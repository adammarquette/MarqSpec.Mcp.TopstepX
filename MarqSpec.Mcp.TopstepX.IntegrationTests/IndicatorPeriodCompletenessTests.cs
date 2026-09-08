using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Telemetry;
using MarqSpec.Mcp.TopstepX.Tests.MarketData;
using MarqSpec.Mcp.TopstepX.Tools;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// What the read-triggered projection's probe serves when the store holds an <c>(Indicator, Period)</c> pair
/// whose values stop short of the bars (gh#531).
/// </summary>
/// <remarks>
/// <para>
/// <b>The probe used to ask the wrong question.</b> It compared the catalogue against the DISTINCT
/// <c>(Indicator, Period)</c> pairs the store holds — an <i>existence</i> test — and a pair whose rows end
/// halfway down the series is present by that test. A read of it then returned every value written up to
/// wherever the projection last reached and nothing after, which is the shape this repository exists to
/// refuse: a series that ends early looks exactly like a market that stopped moving.
/// </para>
/// <para>
/// <b>The question is now completeness, and the boundary is the domain's own.</b>
/// <see cref="IIndicator.WarmupBars"/> already says how many bars a value needs, so a pair is short only when
/// its newest value sits further back than the newest bar a warm-up could legitimately have suppressed. That
/// keeps the honest absences honest — the run of nulls after a contract roll (ADR-0011) is not a gap — while
/// leaving nothing else for the probe to mistake for a complete series.
/// </para>
/// </remarks>
[Collection(SeriesStoreCollection.Name)]
public sealed class IndicatorPeriodCompletenessTests : IAsyncLifetime
{
    private const string Venue = "test";
    private const int Resolution = 5;

    /// <summary>How many buckets the series is warmed to before anything changes.</summary>
    private const int Warmed = 40;

    /// <summary>How many buckets the series holds after it grows.</summary>
    private const int Grown = 70;

    private static readonly InstrumentId _es = new("ES");

    private readonly SeriesStoreFixture _fixture;
    private readonly TopstepXDbContext _database;
    private readonly FakeTimeProvider _clock;
    private readonly HostTelemetry _telemetry = new();

    /// <param name="fixture">The shared container.</param>
    public IndicatorPeriodCompletenessTests(SeriesStoreFixture fixture)
    {
        _fixture = fixture;
        _database = fixture.CreateContext();

        // Past the last bucket the widest fixture uses, so nothing anywhere below is still forming.
        _clock = new FakeTimeProvider(Bucket(Grown).AddMinutes(Resolution));
    }

    /// <inheritdoc />
    public Task InitializeAsync() => _fixture.ResetAsync();

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        _database.Dispose();
        _telemetry.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>A Tuesday mid-session, so every bucket is one the venue owed us.</summary>
    private static DateTimeOffset SessionStart =>
        MarketClock.FromMarket(new DateOnly(2026, 8, 18), new TimeOnly(9, 0)).ToUniversalTime();

    private static DateTimeOffset Bucket(int index) => SessionStart.AddMinutes(Resolution * index);

    /// <summary>
    /// Bars over a half-open index range, drifting irregularly.
    /// </summary>
    /// <param name="fromIndex">The first bucket index.</param>
    /// <param name="toIndexExclusive">One past the last bucket index.</param>
    /// <returns>The bars.</returns>
    /// <remarks>
    /// A tidy ramp produces EMA values that survive rounding to the stored scale intact, so a series built
    /// from one hides more than it shows.
    /// </remarks>
    private static IReadOnlyList<Bar> Bars(int fromIndex, int toIndexExclusive) =>
    [
        .. Enumerable.Range(fromIndex, toIndexExclusive - fromIndex).Select(i =>
        {
            decimal drift = i % 3 == 0 ? 1.37m : i % 3 == 1 ? -0.91m : 2.13m;
            decimal close = 5_000m + (i * drift);
            return new Bar(
                Bucket(i), close, close + 1.25m, close - 0.75m, close, 1_000 + i,
                CountingGateway.DefaultContractId);
        }),
    ];

    // ── The card ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task APairWhoseValuesStopShortOfTheBars_IsReplayedByTheNextRead()
    {
        // THE CARD, REDUCED TO THE STORE STATE IT IS ABOUT. Whatever put it there -- a period removed from
        // configuration and re-added, a fill whose projection a future change forgets to run, a second
        // process writing the same series under a different catalogue -- what the probe sees is one thing:
        // a pair the catalogue computes whose newest value is far behind the newest bar. The probe answered
        // "present, therefore complete" and served the truncated series.
        //
        // Seeded through the fill path first, so the values under the first forty buckets are the ones
        // production writes, and then the tail is written straight to the store -- the one arrangement that
        // separates "the bars are there" from "a projection has run over them". A fill would project, which
        // is precisely the thing that has NOT happened when a series is in this state.
        await FillAsync(Catalog(alsoEmaPeriods: "3"), Warmed);
        await SeedDirectlyAsync(Bars(Warmed, Grown));

        IndicatorCacheService indicators = Cache(Catalog(alsoEmaPeriods: "3"));

        bool projected = await indicators.EnsureProjectedAsync(
            Venue, _es, Resolution, CancellationToken.None);

        projected.Should().BeTrue(
            "the store holds thirty bars newer than the newest (ema, 3) value, which is far more than the "
            + "three a warm-up could account for -- so the pair is short, and a probe that reads 'the pair "
            + "exists' as 'the series is complete' serves a window that ends thirty bars early");

        (await NewestValueAsync("ema", 3)).Should().Be(
            Bucket(Grown - 1),
            "the replay is the whole stored series, so after it the pair reaches the newest bar");
    }

    [Fact]
    public async Task APeriodRemovedAndReAdded_CoversTheBarsWrittenWhileItWasGone_WithNoVendorCall()
    {
        // gh#531's acceptance criterion, in the operator's own terms. Warm with AdditionalEmaPeriods = "3",
        // remove it and fetch more bars, put it back, and read period 3: the series must cover the new bars,
        // and it must cost nothing at the venue -- IndicatorCacheService takes no gateway at all, so the
        // counters below are the second, weaker statement of that.
        await FillAsync(Catalog(alsoEmaPeriods: "3"), Warmed);

        // The operator removes the period. Thirty more buckets arrive while it is gone.
        await FillAsync(Catalog(alsoEmaPeriods: null), Grown);

        // And puts it back.
        CountingGateway gateway = new(Bars(0, Grown));
        IndicatorTools tools = Tools(Catalog(alsoEmaPeriods: "3"), gateway);

        ToolPayloads.IndicatorSeries series = await tools.GetIndicators(
            "ES", Resolution, "ema", Bucket(0), Bucket(Grown), period: 3,
            cancellationToken: CancellationToken.None);

        series.Period.Should().Be(3, "the read must answer under the period it was asked for");

        series.Values.Should().NotBeEmpty(
            "the bars are all cached, so every value is computable without an operator and without the venue");

        series.Values[^1].T.Should().Be(
            Bucket(Grown - 1),
            "the series must reach the newest stored bar. Ending at bucket {0} would be the defect: a window "
            + "that stops where the projection last reached, served as though the market had stopped there",
            Warmed - 1);

        gateway.BarRequests.Should().Be(0, "no bar is missing, so nothing may be fetched");
        gateway.ContractRequests.Should().Be(0, "resolving a contract is a vendor call too");
    }

    [Fact]
    public async Task ACompleteSeries_IsStillNotReplayed()
    {
        // The other half of the completeness question, and the one that decides whether the change is a fix
        // or a permanent replay. A warm series pays the probe and nothing else; a probe that answered
        // "short" for a series that is simply up to date would replay every read forever and the cost would
        // never show up as an error.
        await FillAsync(Catalog(alsoEmaPeriods: "3"), Warmed);

        IndicatorCacheService indicators = Cache(Catalog(alsoEmaPeriods: "3"));

        bool projected = await indicators.EnsureProjectedAsync(
            Venue, _es, Resolution, CancellationToken.None);

        projected.Should().BeFalse(
            "every pair the catalogue computes reaches the newest bar, so there is nothing to replay");
        indicators.Projections.Should().Be(0);
        indicators.Probes.Should().Be(1);
    }

    [Fact]
    public async Task TheWarmUpAfterAContractRoll_IsNotReadAsAGap()
    {
        // THE ABSENCE THAT MUST STAY AN ABSENCE. A roll restarts every warm-up (ADR-0011), so the newest
        // bars legitimately carry no value at all -- and a completeness test keyed on "the newest value is
        // older than the newest bar" would read that as a gap and replay the series on every single read,
        // producing nothing each time. The boundary is the indicator's OWN warm-up, so a run of absences
        // shorter than it is a fact about the bars rather than a hole in the store.
        //
        // Thirty-eight bars under one contract and two under the next, at ema(3): the second run cannot
        // satisfy a three-bar warm-up, so the pair's newest value sits at bucket 37 while the newest bar is
        // at 39. That is the honest answer, and it must cost one probe.
        await SeedDirectlyAsync(BarsAcrossARoll(0, Warmed, rollAt: Warmed - 2));
        await ProjectAsync(Catalog(alsoEmaPeriods: "3"));

        (await NewestValueAsync("ema", 3)).Should().Be(
            Bucket(Warmed - 3),
            "the fixture only pins the probe if the pair really does stop short of the newest bar");

        IndicatorCacheService indicators = Cache(Catalog(alsoEmaPeriods: "3"));

        bool projected = await indicators.EnsureProjectedAsync(
            Venue, _es, Resolution, CancellationToken.None);

        projected.Should().BeFalse(
            "two bars cannot satisfy a three-bar warm-up, so the missing tail is what the bars say rather "
            + "than what the store lost -- and replaying would write nothing, forever, on every read");
    }

    [Fact]
    public async Task APairShortByExactlyItsWarmUp_IsReplayed()
    {
        // THE BOUNDARY FROM THE OTHER SIDE, AT ITS MINIMUM — one bar, and the review of PR #606 is why it is
        // here. `architecture.md` described the boundary as the bucket `WarmupBars` bars behind the newest;
        // the code uses the `WarmupBars`-th newest, which is one bar nearer. Those differ by exactly one
        // bucket, the difference decides a verdict, and until this case nothing in either tier could tell
        // them apart: the looser reading was applied to the probe and the whole suite stayed green.
        //
        // ONE unprojected bar is the sharpest fixture there is for it. It puts EVERY pair's newest value at
        // `tail[1]`, and the tightest boundary in the shipped catalogue is VWAP's — warm-up 1, so `tail[0]`,
        // the newest bar itself, because a warm-up of one may leave no trailing bar without a value at all.
        // VWAP is therefore short by exactly its own warm-up, and every other pair is honestly complete: at
        // `w >= 2` a newest value at `tail[1]` is inside what a warm-up accounts for. So this asserts the
        // boundary rather than the mechanism, and it fails the moment the boundary moves a single bucket.
        await FillAsync(Catalog(alsoEmaPeriods: "3"), Warmed);
        await SeedDirectlyAsync(Bars(Warmed, Warmed + 1));

        (await NewestValueAsync("vwap", 0)).Should().Be(
            Bucket(Warmed - 1),
            "the fixture only pins the boundary if VWAP really is one bucket behind the newest bar — a "
            + "fixture that had already reached bucket {0} would pass while testing nothing",
            Warmed);

        IndicatorCacheService indicators = Cache(Catalog(alsoEmaPeriods: "3"));

        bool projected = await indicators.EnsureProjectedAsync(
            Venue, _es, Resolution, CancellationToken.None);

        projected.Should().BeTrue(
            "VWAP has a value from its session's first bar, so a stored bar carrying none is a gap and not a "
            + "warm-up. One bar is the whole of the difference between the boundary the code uses and the "
            + "one bar looser it is easy to write, and the looser one serves that bar as though the series "
            + "ended before it");

        (await NewestValueAsync("vwap", 0)).Should().Be(
            Bucket(Warmed), "and the replay carries the pair to the newest stored bar");
    }

    [Fact]
    public async Task AWarmUpRunSpanningASessionBreak_IsNotReadAsAGap()
    {
        // THE "IN BARS, NEVER IN TIME" CLAIM, PINNED RATHER THAN ARGUED — the review of PR #606 found it
        // stated in four places and defended by no test, because every other fixture in this file lays its
        // buckets five minutes apart with no break, and on a contiguous series the two forms agree exactly.
        //
        // The threshold the documents name as WRONG is `tail[0] - (w - 1) * resolution`. Buckets are never
        // closer together than the resolution, so that instant is never older than `tail[w - 1]` and the
        // time form can only ever over-replay — it cannot serve a wrong number, which is why this ranks
        // below the boundary case above. What it costs is a series that rolls over a weekend replaying on
        // every read, forever, writing nothing each time, with nothing anywhere reporting it.
        //
        // The arrangement is the roll fixture with one thing added: the last two buckets sit on the NEXT
        // TRADING DAY. `ema(3)`'s newest value is then at `tail[2]`, exactly the boundary — and the two
        // adjacent stored buckets either side of the break are 20 h 55 m apart, where the resolution says
        // five minutes. So `tail[0] - 2 * 5min`, the threshold the time form would use, lands 20 h 50 m
        // later than that value. Counted in bars this is a warm-up; counted in time it is a chasm.
        // (Measured off the store, not computed on paper: the value sits at 2026-08-18T17:05:00Z and
        // `tail[0]` at 2026-08-19T14:05:00Z, so the gap is 1,255 minutes and the shortfall 1,250.)
        await SeedDirectlyAsync(
            BarsAcrossARoll(0, Warmed, rollAt: Warmed - 2, sessionBreakAt: Warmed - 2));
        await ProjectAsync(Catalog(alsoEmaPeriods: "3"));

        (await NewestValueAsync("ema", 3)).Should().Be(
            Bucket(Warmed - 3),
            "the fixture only pins anything if the pair really does stop at the last bucket of the earlier "
            + "session");

        (await NewestBarAsync()).Should().Be(
            Bucket(0).AddDays(1).AddMinutes(Resolution),
            "and the newest bar really is the second bucket of the NEXT day's session — 20 h 55 m past the "
            + "last bucket before the break, where two five-minute steps would put it ten minutes past. "
            + "Without that gap the two forms of the boundary agree and this case tests nothing");

        IndicatorCacheService indicators = Cache(Catalog(alsoEmaPeriods: "3"));

        bool projected = await indicators.EnsureProjectedAsync(
            Venue, _es, Resolution, CancellationToken.None);

        projected.Should().BeFalse(
            "two bars cannot satisfy a three-bar warm-up whichever day they fall on. A threshold measured in "
            + "TIME puts the boundary two five-minute steps behind the newest bar — inside the new session — "
            + "so every value from the previous session reads as a gap, and this series replays on every "
            + "read and writes nothing");
    }

    // ── Scaffolding ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Bars over a half-open index range, split across two contracts at <paramref name="rollAt"/>.
    /// </summary>
    /// <param name="fromIndex">The first bucket index.</param>
    /// <param name="toIndexExclusive">One past the last bucket index.</param>
    /// <param name="rollAt">The bucket index the second contract starts at.</param>
    /// <param name="sessionBreakAt">
    /// The bucket index that opens the <b>next trading day</b>, or <see langword="null"/> for a contiguous
    /// series. A stored series is not contiguous in time — an overnight or a weekend puts hours between two
    /// adjacent buckets — and that difference is the whole of what separates a boundary counted in bars from
    /// one counted in time.
    /// </param>
    /// <returns>The bars.</returns>
    private static IReadOnlyList<Bar> BarsAcrossARoll(
        int fromIndex,
        int toIndexExclusive,
        int rollAt,
        int? sessionBreakAt = null) =>
    [
        .. Bars(fromIndex, toIndexExclusive).Select(bar =>
        {
            Bar rolled = bar.OpenTime < Bucket(rollAt)
                ? bar
                : bar with { ContractId = "CON.F.US.TEST.H27" };

            if (sessionBreakAt is not { } breakAt || rolled.OpenTime < Bucket(breakAt))
            {
                return rolled;
            }

            // Re-laid from the next day's session open, so the buckets after the break are five minutes
            // apart from each other and a day apart from the ones before it.
            return rolled with { OpenTime = rolled.OpenTime.AddDays(1).AddMinutes(-Resolution * breakAt) };
        }),
    ];

    private static BarSessionCalendar Calendar() => BarSessionCalendar.Parse("16:00", []);

    /// <summary>
    /// The catalogue, with EMA at its shipped primary period and optionally at further ones.
    /// </summary>
    /// <param name="alsoEmaPeriods">
    /// Further EMA periods, comma-separated — the <c>Indicators__AdditionalEmaPeriods</c> line the card is
    /// about. <see langword="null"/> is the operator having removed it.
    /// </param>
    /// <returns>The catalogue.</returns>
    /// <remarks>
    /// ATR and RSI are pinned short so a forty-bar series produces values for them at all; EMA's additional
    /// period is the knob these tests move, because moving it is how an operator makes the catalogue and the
    /// store disagree without a code change.
    /// </remarks>
    private static IndicatorCatalog Catalog(string? alsoEmaPeriods) =>
        new(
            Options.Create(new IndicatorOptions
            {
                AtrPeriod = 3,
                RsiPeriod = 3,
                AdditionalEmaPeriods = alsoEmaPeriods,
            }),
            Calendar());

    private static IOptions<MarketDataOptions> MarketData() =>
        Options.Create(new MarketDataOptions
        {
            Instruments = "ES,NQ",
            SessionCloseCentral = "16:00",
        });

    /// <summary>The newest bucket the series holds a bar at.</summary>
    /// <returns>The bucket.</returns>
    private async Task<DateTimeOffset?> NewestBarAsync() =>
        await _database.Bars
            .AsNoTracking()
            .Where(b => b.Venue == Venue
                && b.Instrument == _es.Symbol
                && b.ResolutionMinutes == Resolution)
            .MaxAsync(b => (DateTimeOffset?)b.BucketStart);

    /// <summary>The newest bucket the store holds a value at for one pair, or null if it holds none.</summary>
    /// <param name="indicator">The indicator name.</param>
    /// <param name="period">The period.</param>
    /// <returns>The bucket.</returns>
    private async Task<DateTimeOffset?> NewestValueAsync(string indicator, int period) =>
        await _database.IndicatorValues
            .AsNoTracking()
            .Where(v => v.Venue == Venue
                && v.Instrument == _es.Symbol
                && v.ResolutionMinutes == Resolution
                && v.Indicator == indicator
                && v.Period == period)
            .MaxAsync(v => (DateTimeOffset?)v.BucketStart);

    /// <summary>Fills the store the way the world fills it — through the cache-aside bar read.</summary>
    /// <param name="catalog">The catalogue in force while the bars were written.</param>
    /// <param name="bars">How many buckets to warm.</param>
    private async Task FillAsync(IndicatorCatalog catalog, int bars)
    {
        CountingGateway gateway = new(Bars(0, bars));

        BarCacheService cache = new(
            _database,
            gateway,
            Calendar(),
            new IndicatorProjector(_database, catalog, NullLogger<IndicatorProjector>.Instance, _telemetry),
            new InstrumentRegistry(MarketData()),
            new ContractDirectory(_clock),
            _clock,
            NullLogger<BarCacheService>.Instance,
            _telemetry);

        BarReadResult warmed = await cache.GetBarsAsync(
            _es, Resolution, new BarRange(Bucket(0), Bucket(bars)), CancellationToken.None);

        warmed.Bars.Should().HaveCount(bars, "the rest of each test rests on the store being warm");

        _database.ChangeTracker.Clear();
    }

    /// <summary>Writes bars straight to the store, so no projection has run over them.</summary>
    /// <param name="bars">The bars.</param>
    private async Task SeedDirectlyAsync(IReadOnlyList<Bar> bars)
    {
        foreach (Bar bar in bars)
        {
            _database.Bars.Add(new BarRecord
            {
                Venue = Venue,
                Instrument = _es.Symbol,
                ResolutionMinutes = Resolution,
                BucketStart = bar.OpenTime,
                Open = bar.Open,
                High = bar.High,
                Low = bar.Low,
                Close = bar.Close,
                Volume = bar.Volume,
                ContractId = bar.ContractId,
                RecordedAt = SessionStart,
            });
        }

        await _database.SaveChangesAsync();
        _database.ChangeTracker.Clear();
    }

    /// <summary>Runs one whole-series projection, in the transaction production runs it in.</summary>
    /// <param name="catalog">The catalogue to project.</param>
    private async Task ProjectAsync(IndicatorCatalog catalog)
    {
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction seed = await _database.Database
            .BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, CancellationToken.None);

        await new IndicatorProjector(_database, catalog, NullLogger<IndicatorProjector>.Instance, _telemetry)
            .ProjectAsync(Venue, _es, Resolution, _clock.GetUtcNow(), CancellationToken.None);

        await _database.SaveChangesAsync();
        await seed.CommitAsync(CancellationToken.None);
        _database.ChangeTracker.Clear();
    }

    private IndicatorCacheService Cache(IndicatorCatalog catalog) =>
        new(
            _database,
            catalog,
            new IndicatorProjector(_database, catalog, NullLogger<IndicatorProjector>.Instance, _telemetry),
            _clock,
            NullLogger<IndicatorCacheService>.Instance,
            _telemetry);

    private IndicatorTools Tools(IndicatorCatalog catalog, CountingGateway gateway) =>
        new(
            new InstrumentResolver(new InstrumentRegistry(MarketData()), new StoreAvailabilityHolder()),
            _database,
            catalog,
            Cache(catalog),
            gateway,
            new ToolGuards(MarketData()));
}
