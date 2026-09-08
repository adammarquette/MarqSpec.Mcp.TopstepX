using System.Data;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Telemetry;
using MarqSpec.Mcp.TopstepX.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// The projection over a <b>session</b> series — the same code path the resolution series has always used,
/// reached through a <see cref="SeriesKey"/> instead of a bar size (gh#501).
/// </summary>
/// <remarks>
/// <para>
/// <b>Integration tier, and there is nowhere else.</b> Every claim here turns on the real write: one
/// <c>ON CONFLICT … DO UPDATE</c> against <c>SessionIndicatorValues</c>, a <c>RepeatableRead</c> transaction
/// around it, and the <c>numeric(18,8)</c> column whose scale is what makes a confirming rebuild an empty
/// diff at all (gh#37, gh#387).
/// </para>
/// <para>
/// <b>The store is filled through <see cref="SessionBarService"/> rather than seeded.</b> A hand-seeded
/// session row is a row whose provenance nothing checked, and the property under test — that the projection
/// replays to the same numbers — is about the rows the product actually writes.
/// </para>
/// <para>
/// <b>The neutrality half is asserted here too.</b>
/// <c>AResolutionProjection_EmitsTheSameTelemetryTags</c> pins that routing a resolution series through the
/// key-taking overload leaves <c>mcp.indicator.projections</c> tagged exactly as it was: every panel already
/// built on it reads those three tag keys, and a fourth would retire the stored series in every backend
/// scraping it.
/// </para>
/// </remarks>
[Collection(SeriesStoreCollection.Name)]
public sealed class SessionIndicatorProjectionTests : IAsyncLifetime
{
    private static readonly InstrumentId _es = new("ES");

    /// <summary>The regular session: 08:30–15:00 Central off 30-minute bars — thirteen buckets a day.</summary>
    private static readonly SessionDefinition _rth = new("rth", new TimeOnly(8, 30), new TimeOnly(15, 0), 30);

    /// <summary>
    /// Six consecutive trading days, Tuesday to the Tuesday after.
    /// </summary>
    /// <remarks>
    /// <b>Six because a session series is one bar a day.</b> ATR(3) and RSI(3) — the periods
    /// <see cref="ConcurrencyHarness.Catalog"/> configures — first measure on the fourth bar, so six dates
    /// leave three values apiece: enough that "one row per (indicator, period, date)" is a claim about a
    /// series rather than about a single row. The default MACD signal warm-up is thirty-four, so that member
    /// correctly measures nothing here, which is the honest answer rather than a gap (`R-2.3`).
    /// </remarks>
    private static readonly DateOnly[] _tradeDates =
    [
        new(2026, 8, 18),
        new(2026, 8, 19),
        new(2026, 8, 20),
        new(2026, 8, 21),
        new(2026, 8, 24),
        new(2026, 8, 25),
    ];

    private readonly SeriesStoreFixture _fixture;
    private readonly TopstepXDbContext _database;

    /// <param name="fixture">The shared container.</param>
    public SessionIndicatorProjectionTests(SeriesStoreFixture fixture)
    {
        _fixture = fixture;
        _database = fixture.CreateContext();
    }

    /// <inheritdoc />
    public Task InitializeAsync() => _fixture.ResetAsync();

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        _database.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>A week past the last session, so every fetched range is settled history.</summary>
    private static DateTimeOffset SettledNow => new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ASessionFill_ProjectsIndicatorsInTheSameUnitOfWork()
    {
        // ONE CALL AND NOTHING ELSE. A session fill used to write SessionBars and leave
        // SessionIndicatorValues empty until something else projected -- the first read, or an operator
        // running `rebuild-indicators` -- so a freshly derived session answered its first indicator read by
        // replaying the whole series. The projection now runs inside the fill's own unit of work, between the
        // save and the read-back, which is what makes the bars and the values those bars justify commit
        // together or not at all (gh#501).
        string venue = await FillAsync();

        IReadOnlyList<SessionIndicatorValueRecord> rows = await StoredValuesAsync(venue);

        rows.Should().NotBeEmpty(
            "the fill projected the session series it had just written, with nothing else asked to");

        // The session vocabulary, from the fill rather than from a separate pass: the catalogue minus the
        // session-anchored vwap a one-bar session has no distribution to weight (ADR-0022).
        rows.Select(r => r.Indicator).Should().Contain("atr").And.Contain("rsi");
        rows.Select(r => r.Indicator).Should().NotContain("vwap");

        // Keyed on the session bar's own opening instant, so every value the fill wrote sits on a bar the
        // same unit of work committed.
        HashSet<DateTimeOffset> openings = [.. (await StoredBarsAsync(venue)).Select(b => b.OpenUtc)];
        openings.Should().HaveCount(_tradeDates.Length);
        rows.Select(r => r.BucketStart).Should().OnlyContain(bucket => openings.Contains(bucket));
    }

    [Fact]
    public async Task AWarmSessionRead_DoesNotReproject()
    {
        // THE COST OF THE FILL-TIME PROJECTION IS PAID ONLY WHEN THE PASS CHANGED THE SERIES. Every
        // get_session_bars with one closed date enters the unit of work, so an unconditional projection made
        // a warm read for a single trade date recompute the whole session series -- an empty diff, but one
        // paid on every call, over a series that grows with the store. The resolution path has always gated
        // its own projection on `written > 0`; this is the same rule with the two other ways a session pass
        // can change the series added to it.
        string venue = await FillAsync();

        IReadOnlyList<DateTimeOffset> stamped = [.. (await StoredValuesAsync(venue)).Select(v => v.RecordedAt)];
        stamped.Should().NotBeEmpty("the fill projected, which is what makes a SECOND read the warm one");

        // A LATER CLOCK, so a pass that did rewrite these rows would stamp a different RecordedAt on them
        // and be visible. Nothing else about the ask changes: the same six dates, the same definition.
        SessionBarReadResult second =
            await Sessions(venue, SettledNow.AddDays(1)).GetAsync(_es, _rth, _tradeDates, CancellationToken.None);

        second.Bars.Should().HaveCount(_tradeDates.Length, "the second read is served from the store");
        _database.ChangeTracker.Clear();

        (await StoredValuesAsync(venue)).Select(v => v.RecordedAt).Should().BeEquivalentTo(
            stamped, "no value was rewritten, so no RecordedAt moved");

        // AND THE OBSERVATION THAT ACTUALLY DISCRIMINATES. RecordedAt not moving cannot tell "did not
        // project" from "projected and agreed" -- a confirming pass writes nothing either way. Emptying the
        // table first can: a third read over the same unchanged series must leave it empty, because it must
        // not project at all.
        await _database.SessionIndicatorValues.ExecuteDeleteAsync();
        _database.ChangeTracker.Clear();

        await Sessions(venue, SettledNow.AddDays(2)).GetAsync(_es, _rth, _tradeDates, CancellationToken.None);
        _database.ChangeTracker.Clear();

        (await StoredValuesAsync(venue)).Should().BeEmpty(
            "a read that upserted no bar, reconciled none away and discarded none must not recompute the "
            + "whole session series' indicators");
    }

    [Fact]
    public async Task ADiscardOnlyPass_StillProjects()
    {
        // THE THIRD WAY A PASS CHANGES THE SERIES, and the one a `written > 0` gate copied straight from the
        // resolution path would miss. Step (a) discards every stored row whose (WindowCentral,
        // BaseResolutionMinutes) provenance disagrees with the definition standing today -- unscoped by date,
        // because a changed definition invalidates the whole series -- and a pass that does only that has
        // upserted nothing and reconciled nothing. Its values are now standing over bars that no longer
        // exist, which is precisely what the projection's reconcile removes.
        //
        // ARRANGED BY SEEDING THE MISMATCHED ROW rather than by handing GetAsync a changed definition, and
        // that is what ISOLATES the term: a changed definition also fails to re-derive the dates asked about,
        // so `stale` fires too and the test would pass on either gate. Seeding one row under an old
        // provenance, on a trade date OUTSIDE the ask, leaves the six asked-about bars unchanged (nothing to
        // upsert) and derived (nothing stale) while step (a) still has something to discard.
        string venue = await FillAsync();

        DateOnly orphanDate = new(2026, 8, 17);
        DateTimeOffset orphanOpen = RthBucket(orphanDate, 0);

        await SeedAsync(venue, orphanDate, orphanOpen);

        (await StoredValuesAsync(venue)).Should().Contain(
            v => v.BucketStart == orphanOpen, "the orphan value is what the pass has to reconcile away");

        SessionBarReadResult served =
            await Sessions(venue, SettledNow.AddDays(1)).GetAsync(_es, _rth, _tradeDates, CancellationToken.None);

        served.Bars.Should().HaveCount(_tradeDates.Length, "the six asked-about sessions are unchanged");
        _database.ChangeTracker.Clear();

        IReadOnlyList<SessionIndicatorValueRecord> after = await StoredValuesAsync(venue);

        after.Should().NotContain(
            v => v.BucketStart == orphanOpen,
            "step (a) discarded the bar this value described, so the pass changed the series and had to "
            + "project -- and the reconcile removes a value no bar justifies");
        after.Should().NotBeEmpty("the six sessions the store still holds keep theirs");
    }

    [Fact]
    public async Task ASessionSeries_ProjectsTheCatalogueMinusVwap()
    {
        string venue = await FillAsync();
        SeriesKey key = new SeriesKey.Session(venue, _es.Symbol, _rth.Name);

        // THE FILL ALREADY PROJECTED THIS SERIES (gh#501), so the pass below would confirm rather than write
        // and this test would be measuring an empty diff instead of a projection. Emptied so the claim is
        // about what one pass over these session bars produces.
        await _database.SessionIndicatorValues.ExecuteDeleteAsync();

        int changed = await ProjectOnePassAsync(key, SettledNow);
        changed.Should().BePositive("six session bars satisfy the short warm-ups the catalogue is built with");

        IReadOnlyList<SessionIndicatorValueRecord> rows = await StoredValuesAsync(venue);
        rows.Should().NotBeEmpty();

        // SESSION-ANCHORED VWAP IS NEVER WRITTEN. A session bar is the whole session, so there is no
        // intra-session volume distribution to weight and the value would be that bar's own typical price
        // wearing an average's clothes (ADR-0022). `vwap-rolling` is a window over N bars and is a real
        // number on any series, so it stays.
        rows.Select(r => r.Indicator).Should().NotContain("vwap");
        rows.Select(r => r.Indicator).Should().Contain("atr");
        rows.Select(r => r.Indicator).Should().Contain("rsi");

        // KEYED ON OpenUtc, NOT ON THE TRADE DATE. SessionBars keys on the trade date because a session's UTC
        // bounds move with daylight saving; a value is keyed by the instant the bar it describes begins, the
        // way every other projection in the store is, and the unique (Venue, Instrument, Session, OpenUtc)
        // index is what makes exactly one bar answer for any of these.
        HashSet<DateTimeOffset> openings = [.. (await StoredBarsAsync(venue)).Select(b => b.OpenUtc)];
        openings.Should().HaveCount(_tradeDates.Length);
        rows.Select(r => r.BucketStart).Should().OnlyContain(bucket => openings.Contains(bucket));

        rows.Select(r => (r.Indicator, r.Period, r.BucketStart)).Should().OnlyHaveUniqueItems(
            "(Indicator, Period, BucketStart) is the storage key below the series");

        rows.Should().OnlyContain(r => r.Session == "rth" && r.Venue == venue && r.Instrument == "ES");
    }

    [Fact]
    public async Task AConfirmingRebuild_ProducesAnEmptyDiff_OverSessionBars()
    {
        // ADR-0006, on the second series kind. The rounding to the column's own scale is what makes this
        // true, and it was false for a whole phase on the resolution series because nothing projected twice.
        string venue = await FillAsync();
        SeriesKey key = new SeriesKey.Session(venue, _es.Symbol, _rth.Name);

        // Emptied for the reason ASessionSeries_ProjectsTheCatalogueMinusVwap states: the fill projects now,
        // so without this the "first" pass below would already be the confirming one and the claim would be
        // that two confirming passes agree -- which is a weaker thing to have proven.
        await _database.SessionIndicatorValues.ExecuteDeleteAsync();

        int first = await ProjectOnePassAsync(key, SettledNow);
        int second = await ProjectOnePassAsync(key, SettledNow.AddHours(1));

        first.Should().BePositive("the first pass has values to write");
        second.Should().Be(0, "nothing changed, so a rebuild must produce an empty diff");
    }

    [Fact]
    public async Task Rebuild_WalksSessionSeriesToo()
    {
        // The rebuild verb is the repair an operator reaches for, and a session series it cannot see is a
        // series nothing can repair. `rebuild-indicators` discards the result, so the per-series log line is
        // the operator-visible output — and it has to name the SESSION, not a resolution the series has not
        // got.
        string venue = await FillAsync();

        // Both series unprojected, so both are rewritten. The fill projects BOTH in its own unit of work --
        // the base 30-minute series and, since gh#501's last slice, the session series too -- so both tables
        // are emptied here. Clearing only one would leave that series confirming rather than rewritten, and
        // the walk's second series would stop being observable.
        await _database.IndicatorValues.ExecuteDeleteAsync();
        await _database.SessionIndicatorValues.ExecuteDeleteAsync();

        CapturingLogger<IndicatorRebuilder> logger = new();

        IndicatorRebuildResult result = await Rebuilder(logger).RebuildAsync(null, CancellationToken.None);

        result.ValuesChanged.Should().BePositive();
        result.SeriesRewritten.Should().Be(2, "one resolution series and one session series were rewritten");

        logger.Messages.Should().Contain(
            message => message.Contains("ES rth", StringComparison.Ordinal),
            "the log names the series the way SeriesUnitOfWork does");
        logger.Messages.Should().Contain(
            message => message.Contains("ES 30m", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AConfirmingRebuild_OverAStoreTheFillProjected_IsAnEmptyDiff()
    {
        // THE REBUILD VERB'S OWN EMPTY DIFF, over BOTH series at once and over rows nothing in this test
        // wrote by hand. One fill leaves the store holding a 30-minute resolution series and an `rth` session
        // series, each already projected by the unit of work that wrote its bars; `rebuild-indicators` then
        // walks both and must change nothing. (0, 0) is the only answer that says so: the first number is
        // values written plus removed across every series, the second is how many series were rewritten.
        //
        // NOT THE SAME CLAIM AS AConfirmingRebuild_ProducesAnEmptyDiff_OverSessionBars, which is one
        // PROJECTOR pass repeated over the session series alone. This one is a level up and wider: it goes
        // through the rebuilder's own enumeration, so it also covers the resolution half and the walk that
        // finds the two series in the first place. A rebuilder that enumerated a session series twice, or
        // normalised an instrument differently on the way in, is red here and green there.
        //
        // (Named for what it measures. It used to be called `…_OverASeriesProjectedBeforeTheRefactor`, from
        // when the point was that rows written through the old (venue, instrument, int, now, ct) signature
        // were confirmed through the key-taking one. That distinction is gone: the old signature forwards to
        // the same body, so there is one path and nothing left to compare it against.)
        await FillAsync();

        IndicatorRebuildResult result = await Rebuilder(NullLogger<IndicatorRebuilder>.Instance)
            .RebuildAsync(null, CancellationToken.None);

        result.Should().Be(new IndicatorRebuildResult(0, 0));
    }

    [Fact]
    public async Task AResolutionProjection_EmitsTheSameTelemetryTags()
    {
        string venue = await FillAsync();
        await _database.IndicatorValues.ExecuteDeleteAsync();

        using HostTelemetry telemetry = new();
        using MetricCollector<long> projections = new(
            telemetry, HostTelemetry.Name, HostTelemetry.IndicatorProjectionsInstrument);

        await ProjectOnePassAsync(
            new SeriesKey.Resolution(venue, _es.Symbol, _rth.BaseResolutionMinutes), SettledNow, telemetry);

        IReadOnlyList<CollectedMeasurement<long>> measured = projections.GetMeasurementSnapshot();
        measured.Should().NotBeEmpty();

        foreach (CollectedMeasurement<long> measurement in measured)
        {
            measurement.Value.Should().BePositive();
            measurement.Tags.Keys.Should().BeEquivalentTo(
                [HostTelemetry.IndicatorTag, HostTelemetry.SymbolTag, HostTelemetry.ResolutionTag],
                "a resolution projection's tags are what every existing panel is built on");
            measurement.Tags[HostTelemetry.SymbolTag].Should().Be("ES");
            measurement.Tags[HostTelemetry.ResolutionTag].Should().Be(_rth.BaseResolutionMinutes);
        }
    }

    [Fact]
    public async Task ASessionProjection_TagsTheSessionRatherThanAResolution()
    {
        string venue = await FillAsync();

        // A CONFIRMING PASS RECORDS NOTHING, by design -- the meter counts what a pass decided to write. The
        // fill already projected this series, so without emptying the table there would be no measurement to
        // read the tags off at all.
        await _database.SessionIndicatorValues.ExecuteDeleteAsync();

        using HostTelemetry telemetry = new();
        using MetricCollector<long> projections = new(
            telemetry, HostTelemetry.Name, HostTelemetry.IndicatorProjectionsInstrument);

        await ProjectOnePassAsync(
            new SeriesKey.Session(venue, _es.Symbol, _rth.Name), SettledNow, telemetry);

        IReadOnlyList<CollectedMeasurement<long>> measured = projections.GetMeasurementSnapshot();
        measured.Should().NotBeEmpty();

        foreach (CollectedMeasurement<long> measurement in measured)
        {
            measurement.Tags[HostTelemetry.SessionTag].Should().Be("rth");
            measurement.Tags.Keys.Should().NotContain(HostTelemetry.ResolutionTag);
        }
    }

    // ── The two tools, served ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetSessionIndicators_ReturnsOneValuePerTradeDate_AfterWarmup_WithContracts()
    {
        string venue = await FillAsync();

        ToolPayloads.SessionIndicatorSeries series = await Tools(venue, ConcurrencyHarness.Catalog())
            .GetSessionIndicators(
                "ES", "rth", "atr", WindowStart, WindowEnd, cancellationToken: CancellationToken.None);

        series.Symbol.Should().Be("ES");
        series.Session.Should().Be("rth");
        series.Indicator.Should().Be("atr");
        series.Period.Should().Be(3, "the primary configured period answers when period is omitted");

        // ONE VALUE PER TRADE DATE THAT HAS ONE, and the warm-up dates are ABSENT rather than zero. ATR(3)
        // first measures on the fourth session bar, so of six stored sessions three carry a value -- and the
        // first three carry none, which is a fact about the series' length and not about the market.
        series.Values.Select(v => v.TradeDate).Should().Equal(
            new DateOnly(2026, 8, 21), new DateOnly(2026, 8, 24), new DateOnly(2026, 8, 25));

        // `t` is the session's own opening instant, carried beside the trade date because the two are not
        // interchangeable: the UTC bounds of a session move with the offset.
        series.Values.Select(v => v.T).Should().Equal(
            RthBucket(new DateOnly(2026, 8, 21), 0),
            RthBucket(new DateOnly(2026, 8, 24), 0),
            RthBucket(new DateOnly(2026, 8, 25), 0));

        series.Values.Should().OnlyContain(v => v.V > 0m, "an ATR over a moving ramp is a positive range");

        series.Contracts.Span.Should().Be(
            ToolPayloads.ContractSpan.SingleContract,
            "all six sessions came from the one contract the fill listed");
        series.Contracts.Segments.Should().ContainSingle()
            .Which.ContractId.Should().Be(ConcurrencyHarness.ContractId);
    }

    [Fact]
    public async Task GetSessionIndicatorAt_NeverReturnsASessionStillOpenAtTheMoment()
    {
        string venue = await FillAsync();

        // Monday 24 August at 10:00 Central -- 15:00Z, inside that day's rth session, which runs 08:30 to
        // 15:00 Central. Monday's session bar and its atr value are BOTH stored, because the fill covered
        // every date in the list; the only thing that can keep Monday out of this answer is the comparison
        // against the session's CLOSE.
        DateTimeOffset mondayMidSession = new(2026, 8, 24, 15, 0, 0, TimeSpan.Zero);

        (await StoredValuesAsync(venue)).Should().Contain(
            v => v.Indicator == "atr" && v.BucketStart == RthBucket(new DateOnly(2026, 8, 24), 0),
            "otherwise the as-of comparison below would have nothing to exclude and would prove nothing");

        ToolPayloads.SessionIndicatorReading reading = await Tools(venue, ConcurrencyHarness.Catalog())
            .GetSessionIndicatorAt(
                "ES", "rth", "atr", mondayMidSession, cancellationToken: CancellationToken.None);

        // FRIDAY'S, not Monday's. A session in progress has no final high, low or close, so its indicator
        // value is a number the market has not finished producing -- reading it as of a moment inside it is
        // the lookahead the whole as-of rule exists to refuse.
        reading.TradeDate.Should().Be(new DateOnly(2026, 8, 21));
        reading.BucketStart.Should().Be(RthBucket(new DateOnly(2026, 8, 21), 0));
        reading.Value.Should().NotBeNull();
        reading.ContractId.Should().Be(ConcurrencyHarness.ContractId);
    }

    [Fact]
    public async Task ASessionIndicatorRead_DoesNotServeOrProjectOverMismatchedProvenance()
    {
        // ADR-0022 §4. SessionBarService discards rows whose (WindowCentral, BaseResolutionMinutes)
        // disagree with the standing definition and restates that pair on read-back, so mismatched bars
        // are never served. The indicator path used to key only on (Venue, Instrument, Session): store
        // holds rth under an older provenance, the operator changes the definition and restarts, and a
        // caller who never re-ran get_session_bars receives ordinary-looking atr/rsi labeled as today's
        // rth — a number today's definition cannot reproduce.
        //
        // ARRANGED BY SEEDING ONLY THE MISMATCHED SERIES — no get_session_bars under the standing
        // definition — which is exactly the call order the failure needs. SeedAsync already plants
        // BaseResolutionMinutes = 60 against today's half-hourly rth.
        string venue = ConcurrencyHarness.Venue();

        foreach (DateOnly tradeDate in _tradeDates)
        {
            await SeedAsync(venue, tradeDate, RthBucket(tradeDate, 0));
        }

        int seeded = await _database.SessionIndicatorValues.CountAsync(v => v.Venue == venue);
        seeded.Should().Be(
            _tradeDates.Length, "one atr value per mismatched bar, and nothing else in this venue");

        ToolPayloads.SessionIndicatorSeries series = await Tools(venue, ConcurrencyHarness.Catalog())
            .GetSessionIndicators(
                "ES", "rth", "atr", WindowStart, WindowEnd, cancellationToken: CancellationToken.None);

        series.Values.Should().BeEmpty(
            "mismatched provenance is not today's rth — those atr points must not arrive as ordinary "
            + "current-session answers");

        ToolPayloads.SessionIndicatorReading reading = await Tools(venue, ConcurrencyHarness.Catalog())
            .GetSessionIndicatorAt(
                "ES", "rth", "atr", WindowEnd, cancellationToken: CancellationToken.None);

        reading.Value.Should().BeNull("the as-of join is the same bar query the windowed read uses");
        reading.TradeDate.Should().BeNull();

        IReadOnlyList<SessionIndicatorValueRecord> after = await StoredValuesAsync(venue);
        after.Should().HaveCount(
            seeded,
            "EnsureProjectedAsync must not treat the retired OHLC as the standing series and project "
            + "the catalogue over it");
        after.Should().OnlyContain(
            v => v.Indicator == "atr" && v.Period == 3,
            "the only values left are the ones the seed planted — a projection would have written rsi too");
    }

    [Fact]
    public async Task GetSessionIndicators_ReplaysOnFirstRead_WhenTheCatalogueOutranTheStore()
    {
        // The session flavour of AnIndicatorTheStoreHasNoValuesFor_IsProjectedOnTheNextRead_WithNoVendorCall.
        // The fill projected this series under the shipped test catalogue, so the store holds (rsi, 3); the
        // read below arrives under a catalogue that computes (rsi, 5), which nothing has ever written. The
        // membership of IndicatorCatalog is fixed at compile time and only the PERIODS are configurable, so
        // moving one is exactly what "the catalogue outran the store" looks like from the store.
        string venue = await FillAsync();

        int barsBefore = await _database.Bars.CountAsync();

        IndicatorCatalog wider = new(
            Options.Create(new IndicatorOptions { AtrPeriod = 3, RsiPeriod = 5 }),
            ConcurrencyHarness.Calendar());

        ToolPayloads.SessionIndicatorSeries series = await Tools(venue, wider)
            .GetSessionIndicators(
                "ES", "rth", "rsi", WindowStart, WindowEnd, cancellationToken: CancellationToken.None);

        series.Period.Should().Be(5, "the read answers under the period the catalogue is configured for");
        series.Values.Should().NotBeEmpty(
            "the session bars are already stored, so the value is computable without an operator running "
            + "rebuild-indicators -- an absence here would be an artefact of when computation happened "
            + "rather than a fact about the market");

        // NO BAR WAS FETCHED, stated against the store rather than against a counter: a fetch writes bars,
        // and this read left the base series exactly the size the fill left it. The stronger half of the
        // claim is structural and is pinned elsewhere -- neither SessionIndicatorTools nor
        // IndicatorCacheService holds a gateway at all (MarketDataToolBoundaryTests, VenueFailureReportingTests).
        (await _database.Bars.CountAsync()).Should().Be(barsBefore);
    }

    /// <summary>The window every served read here asks over — the six trade dates, whole.</summary>
    private static DateTimeOffset WindowStart => new(2026, 8, 18, 0, 0, 0, TimeSpan.Zero);

    /// <summary>One day past the last session, so every one of the six lies wholly inside.</summary>
    private static DateTimeOffset WindowEnd => new(2026, 8, 26, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The tool type under test, over this suite's store and one private venue.</summary>
    /// <param name="venue">The venue the fill wrote under.</param>
    /// <param name="catalog">The catalogue in force for this read.</param>
    /// <returns>The tools.</returns>
    /// <remarks>
    /// The gateway is handed in holding <b>no bars at all</b>, and it is never called: this type reads it
    /// once for its venue id in the constructor and keeps no client. A read that tried to fetch would
    /// therefore have nothing to fetch, so every value these tests see came out of the store.
    /// </remarks>
    private SessionIndicatorTools Tools(string venue, IndicatorCatalog catalog)
    {
        IOptions<MarketDataOptions> options = Options.Create(new MarketDataOptions
        {
            Instruments = "ES,NQ",
            MaxRows = 5_000,
            SessionCloseCentral = "16:00",
        });

        SessionCatalog sessions = new(options, ConcurrencyHarness.Calendar());
        IndicatorProjector projector = new(
            _database, catalog, NullLogger<IndicatorProjector>.Instance, ConcurrencyHarness.Telemetry, sessions);

        return new SessionIndicatorTools(
            new InstrumentResolver(new InstrumentRegistry(options), new StoreAvailabilityHolder()),
            _database,
            catalog,
            new IndicatorCacheService(
                _database,
                catalog,
                projector,
                new FakeTimeProvider(SettledNow),
                NullLogger<IndicatorCacheService>.Instance,
                ConcurrencyHarness.Telemetry,
                sessions: sessions),
            sessions,
            ConcurrencyHarness.Calendar(),
            new SeriesGateway(venue, []),
            new ToolGuards(options));
    }

    /// <summary>Runs one projection pass the way every call site in the product runs one.</summary>
    /// <param name="key">The series.</param>
    /// <param name="now">The instant the pass runs at, stamped on the rows it changes.</param>
    /// <param name="telemetry">The meter to record under, when a test reads it.</param>
    /// <returns>How many rows the pass changed.</returns>
    private async Task<int> ProjectOnePassAsync(
        SeriesKey key, DateTimeOffset now, HostTelemetry? telemetry = null)
    {
        IndicatorProjector projector = new(
            _database, ConcurrencyHarness.Catalog(), NullLogger<IndicatorProjector>.Instance,
            telemetry ?? ConcurrencyHarness.Telemetry, ConcurrencyHarness.Sessions());

        await using IDbContextTransaction transaction = await _database.Database
            .BeginTransactionAsync(IsolationLevel.RepeatableRead, CancellationToken.None);

        int changed = await projector.ProjectAsync(key, now, CancellationToken.None);

        await _database.SaveChangesAsync();
        await transaction.CommitAsync();

        _database.ChangeTracker.Clear();

        return changed;
    }

    /// <summary>The rebuild verb over this test's store.</summary>
    /// <param name="logger">The logger, when a test reads what the walk said it did.</param>
    /// <returns>The rebuilder.</returns>
    private IndicatorRebuilder Rebuilder(ILogger<IndicatorRebuilder> logger) =>
        new(
            _database,
            ConcurrencyHarness.Projector(_database),
            ConcurrencyHarness.Registry(),
            new FakeTimeProvider(SettledNow.AddDays(1)),
            logger);

    /// <summary>
    /// Drives one session fill through <see cref="SessionBarService"/>, filling both series at once.
    /// </summary>
    /// <returns>The private venue id the rows are keyed under.</returns>
    /// <remarks>
    /// One call writes the 30-minute base bars <b>and</b> their indicator values (the bar fill projects in
    /// its own unit of work), and the six session bars over them. So the store this leaves is exactly the two
    /// series a rebuild has to walk.
    /// </remarks>
    private async Task<string> FillAsync()
    {
        string venue = ConcurrencyHarness.Venue();

        SessionBarReadResult result =
            await Sessions(venue, SettledNow).GetAsync(_es, _rth, _tradeDates, CancellationToken.None);

        result.Bars.Should().HaveCount(
            _tradeDates.Length, "every trade date's whole session was served, so every one is stored");

        _database.ChangeTracker.Clear();

        return venue;
    }

    /// <summary>
    /// Seeds one session bar built under an <b>older definition</b>, and one indicator value standing over it.
    /// </summary>
    /// <param name="venue">The venue the rows are keyed under.</param>
    /// <param name="tradeDate">The trade date the seeded bar carries.</param>
    /// <param name="openUtc">When that session opened — the key the value is written under.</param>
    /// <returns>The running seed.</returns>
    /// <remarks>
    /// <para>
    /// The provenance pair is deliberately wrong for the definition standing today — the same window off
    /// <b>hourly</b> base bars rather than half-hourly ones — which is exactly the row step (a) exists to
    /// discard. The trade date sits outside the ask, so discarding it is the <i>only</i> thing the pass under
    /// test does.
    /// </para>
    /// <para>
    /// <b>The tracker is cleared afterwards</b>, because the rows every other statement in this suite writes
    /// are raw SQL the tracker never sees: a seeded instance left in the identity map is what a later read
    /// would hand back in preference to the row the store actually holds (gh#387).
    /// </para>
    /// </remarks>
    private async Task SeedAsync(string venue, DateOnly tradeDate, DateTimeOffset openUtc)
    {
        _database.SessionBars.Add(new SessionBarRecord
        {
            Venue = venue,
            Instrument = _es.Symbol,
            Session = _rth.Name,
            TradeDate = tradeDate,
            OpenUtc = openUtc,
            CloseUtc = openUtc.AddHours(6.5),
            Open = 5_000m,
            High = 5_010m,
            Low = 4_990m,
            Close = 5_005m,
            Volume = 130,
            ContractId = ConcurrencyHarness.ContractId,
            BaseResolutionMinutes = 60,
            BaseBucketCount = 7,
            WindowCentral = "08:30-15:00",
            RecordedAt = SettledNow,
        });

        _database.SessionIndicatorValues.Add(new SessionIndicatorValueRecord
        {
            Venue = venue,
            Instrument = _es.Symbol,
            Session = _rth.Name,
            Indicator = "atr",
            Period = 3,
            BucketStart = openUtc,
            Value = 20m,
            RecordedAt = SettledNow,
        });

        await _database.SaveChangesAsync();
        _database.ChangeTracker.Clear();
    }

    /// <summary>The session-bar reader, over this suite's store and one venue.</summary>
    /// <param name="venue">The venue the rows are keyed under.</param>
    /// <param name="now">The instant the read runs at.</param>
    /// <returns>The service.</returns>
    /// <remarks>
    /// Built per call rather than held: a second read of the same series is the thing several cases here
    /// measure, and it has to go through a service whose per-scope state started empty, exactly as a second
    /// MCP request would.
    /// </remarks>
    private SessionBarService Sessions(string venue, DateTimeOffset now)
    {
        FakeTimeProvider clock = new(now);
        SeriesGateway gateway = new(venue, RthBars());

        BarCacheService bars = ConcurrencyHarness.Cache(_database, venue, RthBars(), now);

        return new SessionBarService(
            _database,
            bars,
            gateway,
            ConcurrencyHarness.Calendar(),
            ConcurrencyHarness.Projector(_database),
            clock,
            NullLogger<SessionBarService>.Instance);
    }

    /// <summary>Every trade date's thirteen 30-minute <c>rth</c> buckets, as a ramp the venue will serve.</summary>
    /// <returns>The bars, ascending.</returns>
    /// <remarks>
    /// Deliberately irregular across dates rather than one repeated ramp: a series whose sessions all close
    /// at the same number gives RSI and ATR values that survive rounding intact, and the empty-diff claim
    /// would hold even with the gh#37 rounding defect present.
    /// </remarks>
    private static IReadOnlyList<Bar> RthBars() =>
    [
        .. _tradeDates.SelectMany((tradeDate, day) => Enumerable.Range(0, 13).Select(i =>
        {
            decimal drift = day % 3 == 0 ? 1.37m : day % 3 == 1 ? -0.91m : 2.13m;
            decimal open = 5000m + (day * 7m * drift) + i;

            return new Bar(
                RthBucket(tradeDate, i),
                open,
                open + 5m,
                open - 5m,
                open + 1m,
                10,
                ConcurrencyHarness.ContractId);
        })),
    ];

    /// <summary>The instant one <c>rth</c> bucket opens. In August, Central is CDT, so 08:30 is 13:30Z.</summary>
    /// <param name="tradeDate">The trade date.</param>
    /// <param name="index">The bucket index.</param>
    /// <returns>The opening instant.</returns>
    private static DateTimeOffset RthBucket(DateOnly tradeDate, int index) =>
        new DateTimeOffset(tradeDate.Year, tradeDate.Month, tradeDate.Day, 13, 30, 0, TimeSpan.Zero)
            .AddMinutes(30 * index);

    /// <summary>The session bars one venue holds, read through a second context.</summary>
    /// <param name="venue">The private venue id.</param>
    /// <returns>The rows, ascending by trade date.</returns>
    private async Task<IReadOnlyList<SessionBarRecord>> StoredBarsAsync(string venue)
    {
        await using TopstepXDbContext verify = _fixture.CreateContext();
        return await verify.SessionBars
            .AsNoTracking()
            .Where(s => s.Venue == venue)
            .OrderBy(s => s.OpenUtc)
            .ToListAsync();
    }

    /// <summary>
    /// The session indicator values one venue holds, read through a second context.
    /// </summary>
    /// <param name="venue">The private venue id.</param>
    /// <returns>The rows.</returns>
    /// <remarks>
    /// A second context, and <c>AsNoTracking</c>: the write is raw SQL the change tracker never sees, so
    /// re-reading through the context that ran it would hand back whatever the identity map is holding
    /// (gh#103).
    /// </remarks>
    private async Task<IReadOnlyList<SessionIndicatorValueRecord>> StoredValuesAsync(string venue)
    {
        await using TopstepXDbContext verify = _fixture.CreateContext();
        return await verify.SessionIndicatorValues
            .AsNoTracking()
            .Where(v => v.Venue == venue)
            .ToListAsync();
    }
}
