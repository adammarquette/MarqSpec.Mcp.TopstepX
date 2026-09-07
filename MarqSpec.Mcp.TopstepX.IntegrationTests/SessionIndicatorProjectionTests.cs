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
    public async Task Rebuild_IsAnEmptyDiff_OverASeriesProjectedBeforeTheRefactor()
    {
        // THE REFACTOR'S OWN REGRESSION. The bar fill writes IndicatorValues through the pre-refactor
        // signature — the (venue, instrument, int, now, ct) overload the cache-aside path still calls — and
        // the rebuild then confirms those rows through the key path. A key path that queried, rounded, keyed
        // or ordered even slightly differently would rewrite them, and (0, 0) is the only answer that says
        // it does not.
        string venue = await FillAsync();

        await ProjectOnePassAsync(new SeriesKey.Session(venue, _es.Symbol, _rth.Name), SettledNow);

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

        IndicatorProjector projector = new(_database, catalog, NullLogger<IndicatorProjector>.Instance);

        return new SessionIndicatorTools(
            new InstrumentResolver(new InstrumentRegistry(options), new StoreAvailabilityHolder()),
            _database,
            catalog,
            new IndicatorCacheService(
                _database,
                catalog,
                projector,
                new FakeTimeProvider(SettledNow),
                NullLogger<IndicatorCacheService>.Instance),
            new SessionCatalog(options, ConcurrencyHarness.Calendar()),
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
            _database, ConcurrencyHarness.Catalog(), NullLogger<IndicatorProjector>.Instance, telemetry);

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
        FakeTimeProvider clock = new(SettledNow);
        SeriesGateway gateway = new(venue, RthBars());

        BarCacheService bars = new(
            _database,
            gateway,
            ConcurrencyHarness.Calendar(),
            ConcurrencyHarness.Projector(_database),
            clock,
            NullLogger<BarCacheService>.Instance);

        SessionBarService sessions = new(
            _database,
            bars,
            gateway,
            ConcurrencyHarness.Calendar(),
            ConcurrencyHarness.Projector(_database),
            clock,
            NullLogger<SessionBarService>.Instance);

        SessionBarReadResult result =
            await sessions.GetAsync(_es, _rth, _tradeDates, CancellationToken.None);

        result.Bars.Should().HaveCount(
            _tradeDates.Length, "every trade date's whole session was served, so every one is stored");

        _database.ChangeTracker.Clear();

        return venue;
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
