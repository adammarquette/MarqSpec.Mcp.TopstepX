using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// A read of an indicator the store holds no values for projects it from the bars already cached, without
/// an operator and without the venue (gh#246).
/// </summary>
/// <remarks>
/// <para>
/// <b>What "an indicator was added to the catalogue" looks like from the store.</b> The membership of
/// <see cref="IndicatorCatalog"/> is fixed at compile time and only the <i>periods</i> are configurable, so
/// the only way an operator can make the catalogue outrun the store without a code change is to move a
/// period — and the two are the same condition once stored: a <c>(Indicator, Period)</c> pair the catalogue
/// computes for which <c>IndicatorValues</c> holds no row. Nothing in the store can tell them apart, and the
/// trigger under test reads the store. These tests therefore move a period, and that is not a weaker case
/// than adding an indicator; it is the same one, reachable from configuration.
/// </para>
/// <para>
/// <b>The venue is unreachable from this path by construction</b> — <see cref="IndicatorCacheService"/> takes
/// no <see cref="Venue.IMarketDataGateway"/> at all, the same statement <see cref="IndicatorRebuilder"/>
/// makes. The counter assertions below are the second, weaker check; the constructor is the first.
/// </para>
/// </remarks>
public sealed class IndicatorReadProjectionTests : IDisposable
{
    private const int Resolution = 5;
    private const int SeededBars = 40;

    private static readonly InstrumentId _es = new("ES");

    private readonly TopstepXDbContext _database;
    private readonly CountingGateway _gateway;
    private readonly FakeTimeProvider _clock;

    public IndicatorReadProjectionTests()
    {
        _database = new TopstepXDbContext(
            new DbContextOptionsBuilder<TopstepXDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);

        _gateway = new CountingGateway(Bars(0, SeededBars));

        // After the last seeded bucket closes, so nothing in the window is still forming.
        _clock = new FakeTimeProvider(Bucket(SeededBars).AddMinutes(Resolution));
    }

    public void Dispose() => _database.Dispose();

    /// <summary>A Tuesday mid-session, so every bucket is one the venue owed us.</summary>
    private static DateTimeOffset SessionStart =>
        MarketClock.FromMarket(new DateOnly(2026, 8, 18), new TimeOnly(9, 0)).ToUniversalTime();

    private static DateTimeOffset Bucket(int index) => SessionStart.AddMinutes(Resolution * index);

    /// <summary>
    /// Bars over a half-open index range, drifting irregularly.
    /// </summary>
    /// <remarks>
    /// A tidy ramp produces RSI values like 100 and 50 that survive rounding to the stored scale intact, so a
    /// series built from one hides more than it shows.
    /// </remarks>
    private static IReadOnlyList<Bar> Bars(int fromIndex, int toIndexExclusive) =>
    [
        .. Enumerable.Range(fromIndex, toIndexExclusive - fromIndex).Select(i =>
        {
            decimal drift = i % 3 == 0 ? 1.37m : i % 3 == 1 ? -0.91m : 2.13m;
            decimal close = 5_000m + (i * drift);
            return new Bar(Bucket(i), close, close + 1.25m, close - 0.75m, close, 1_000 + i, "CON.F.US.TEST.Z26");
        }),
    ];

    /// <summary>
    /// Bars over a half-open index range, split across two contracts at <paramref name="rollAt"/>.
    /// </summary>
    /// <param name="fromIndex">The first bucket index.</param>
    /// <param name="toIndexExclusive">One past the last bucket index.</param>
    /// <param name="rollAt">The bucket index the second contract starts at.</param>
    /// <returns>The bars.</returns>
    /// <remarks>
    /// The quarter after the first contract, because a roll is what actually puts two contracts on one
    /// series — an arbitrary second string would exercise the same segmentation while describing something
    /// that cannot happen.
    /// </remarks>
    private static IReadOnlyList<Bar> BarsAcrossARoll(int fromIndex, int toIndexExclusive, int rollAt) =>
    [
        .. Bars(fromIndex, toIndexExclusive).Select(bar => bar.OpenTime < Bucket(rollAt)
            ? bar
            : bar with { ContractId = "CON.F.US.TEST.H27" }),
    ];

    // ── The card ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnIndicatorTheStoreHasNoValuesFor_IsProjectedOnTheNextRead_WithNoVendorCall()
    {
        await WarmAsync(Catalog(rsiPeriod: 3));

        // The catalogue now wants RSI at 5. Nothing has ever written (rsi, 5) for this series, and before
        // gh#246 the only thing that ever would was an operator running `rebuild-indicators`.
        MarketDataTools tools = Tools(Catalog(rsiPeriod: 5));
        _gateway.ResetCounters();

        ToolPayloads.IndicatorSeries series = await tools.GetIndicators(
            "ES", Resolution, "rsi", Bucket(0), Bucket(SeededBars), CancellationToken.None);

        series.Period.Should().Be(5, "the read must answer under the period the catalogue is configured for");
        series.Values.Should().NotBeEmpty(
            "the bars are already cached, so the value is computable without an operator and without the "
            + "venue -- an absence here would be an artefact of when computation happened rather than a fact "
            + "about the market");
        _gateway.BarRequests.Should().Be(0, "no bar is missing, so nothing may be fetched");
        _gateway.ContractRequests.Should().Be(0, "resolving a contract is a vendor call too");
    }

    [Fact]
    public async Task TheReadTriggeredProjection_ProducesWhatARebuildWouldHave()
    {
        // Reproducibility is the property ADR-0006 protects, and the trigger change must leave it exactly
        // where it was: what a read computes and what a replay computes are the same numbers, so a rebuild
        // immediately after a read-triggered projection is an EMPTY DIFF.
        await WarmAsync(Catalog(rsiPeriod: 3));

        IndicatorCatalog wider = Catalog(rsiPeriod: 5);
        MarketDataTools tools = Tools(wider);

        ToolPayloads.IndicatorSeries fromRead = await tools.GetIndicators(
            "ES", Resolution, "rsi", Bucket(0), Bucket(SeededBars), CancellationToken.None);

        int changed = await new IndicatorProjector(_database, wider, NullLogger<IndicatorProjector>.Instance)
            .ProjectAsync("test", _es, Resolution, _clock.GetUtcNow(), CancellationToken.None);
        await _database.SaveChangesAsync();

        changed.Should().Be(0, "a replay over the same bars reproduces the same numbers, so it rewrites none");
        fromRead.Values.Should().NotBeEmpty("otherwise the empty diff above would prove nothing");
    }

    [Fact]
    public async Task AnIndicatorTheStoreAlreadyCovers_IsNotReprojected()
    {
        await WarmAsync(Catalog(rsiPeriod: 3));

        IndicatorCacheService indicators = Cache(Catalog(rsiPeriod: 3));

        bool projected = await indicators.EnsureProjectedAsync(
            "test", _es, Resolution, CancellationToken.None);

        projected.Should().BeFalse(
            "every pair the catalogue computes already has values, so a read must cost the probe and nothing "
            + "more -- a warm series that replays itself on every call is the cost this card was told to bound");
        indicators.Projections.Should().Be(0);
        indicators.Probes.Should().Be(1);
    }

    [Fact]
    public async Task ASeriesShorterThanTheWarmUp_IsNotProjected_AndTheAbsenceStands()
    {
        // A missing number is missing, never a default -- and never a reason to replay on every read either.
        // Six bars cannot satisfy MACD's signal warm-up of 34, so the absence is a fact about the bars. A
        // trigger keyed on "this pair has no rows" alone would project this series forever and never write a
        // value, because there is no value to write.
        await WarmAsync(Catalog(rsiPeriod: 3), bars: 6);

        IndicatorCacheService indicators = Cache(Catalog(rsiPeriod: 3));

        bool projected = await indicators.EnsureProjectedAsync(
            "test", _es, Resolution, CancellationToken.None);

        projected.Should().BeFalse(
            "no pair the store lacks is one these bars could satisfy, so there is nothing to compute");

        MarketDataTools tools = Tools(Catalog(rsiPeriod: 3));
        ToolPayloads.IndicatorSeries series = await tools.GetIndicators(
            "ES", Resolution, "macd-signal", Bucket(0), Bucket(6), CancellationToken.None);

        series.Values.Should().BeEmpty("thirty-four bars are needed and six are stored");
    }

    [Fact]
    public async Task ASeriesWithNoBars_IsNotProjected()
    {
        IndicatorCacheService indicators = Cache(Catalog(rsiPeriod: 3));

        bool projected = await indicators.EnsureProjectedAsync(
            "test", _es, Resolution, CancellationToken.None);

        projected.Should().BeFalse(
            "an unknown instrument, or one nothing has ever fetched, must not open a transaction to compute "
            + "nothing from no bars");
    }

    [Fact]
    public async Task OneSeriesIsProbedOncePerScope_HoweverManyIndicatorsAreRead()
    {
        // get_market_snapshot asks GetIndicatorAt once per indicator per resolution -- eleven times over the
        // same series -- and every one of those would otherwise re-ask the store whether the series is
        // complete. The scope is the request, and within it a series that was complete stays complete:
        // nothing writes a bar without projecting over it in the same unit of work.
        await WarmAsync(Catalog(rsiPeriod: 3));

        IndicatorCacheService indicators = Cache(Catalog(rsiPeriod: 3));

        for (int i = 0; i < 11; i++)
        {
            await indicators.EnsureProjectedAsync("test", _es, Resolution, CancellationToken.None);
        }

        indicators.Probes.Should().Be(1, "the answer is memoised for the life of the scope");
    }

    [Fact]
    public async Task GetIndicatorAt_ProjectsToo_NotOnlyGetIndicators()
    {
        // Both indicator reads are on the trigger, and get_indicator_at is the one get_market_snapshot uses.
        // A trigger wired to only one of them would leave the composed read -- the tool an agent actually
        // reaches for -- reporting cannot-measure over bars that measure perfectly well.
        await WarmAsync(Catalog(rsiPeriod: 3));

        MarketDataTools tools = Tools(Catalog(rsiPeriod: 5));
        _gateway.ResetCounters();

        ToolPayloads.IndicatorReading reading = await tools.GetIndicatorAt(
            "ES", Resolution, "rsi", Bucket(SeededBars), CancellationToken.None);

        reading.Value.Should().NotBeNull();
        _gateway.BarRequests.Should().Be(0);
    }

    [Fact]
    public async Task ASeriesWhoseEveryContractRunIsShorterThanTheWarmUp_ReplaysOnEveryRead()
    {
        // THE RESIDUE OF THE WARM-UP BOUND, PINNED RATHER THAN ASSERTED -- and it is reachable with ONE roll,
        // not with the implausible number the first version of this claim said. The bound counts the WHOLE
        // stored series while warm-up restarts at every contract seam, and the stored series is whatever was
        // fetched rather than a complete contract run: BarCacheService writes only the outstanding buckets,
        // and ContractRollDetector.Segment splits purely on a ContractId change. So two ordinary get_bars
        // windows either side of one quarterly roll are enough.
        //
        // Twelve bars under each of two contracts, at the SHIPPED periods. Seven pairs pass `WarmupBars <= 24`
        // -- atr(14) and rsi(14) at 15, sma(20), ema(20) and the three Bollinger bands at 20 -- and not one of
        // them can ever be produced from a twelve-bar run. Only vwap, whose warm-up is 1, gets rows.
        //
        // Nothing is WRONG here: the pass writes nothing, the absences are the honest answer, and the series
        // is by construction tiny. What it costs is a transaction and a replay on every read, forever. It is
        // recorded rather than guarded, and this test is what stops that decision being a guess.
        // Written straight to the store. A history call answers for exactly ONE contract and CountingGateway
        // stamps every bar it serves with the one it resolved, as a real gateway must (ADR-0011) -- so two
        // contracts cannot reach the store through a single fill. Two fills either side of the roll is what
        // puts them there, and this is that state.
        await SeedDirectlyAsync(BarsAcrossARoll(0, 24, 12));

        IndicatorCacheService first = Cache(ShippedCatalog());
        IndicatorCacheService second = Cache(ShippedCatalog());
        IndicatorCacheService third = Cache(ShippedCatalog());

        bool one = await first.EnsureProjectedAsync("test", _es, Resolution, CancellationToken.None);
        bool two = await second.EnsureProjectedAsync("test", _es, Resolution, CancellationToken.None);
        bool three = await third.EnsureProjectedAsync("test", _es, Resolution, CancellationToken.None);

        new[] { one, two, three }.Should().AllBeEquivalentTo(
            true,
            "each read finds the same seven pairs missing, replays the series, and produces none of them -- "
            + "so the next read finds them missing again. ADR-0014 and IndicatorCacheService both describe "
            + "this, and both said it needed more rolls than a quarterly contract can have; it needs one");

        (await StoredPairsAsync()).Should().Equal(
            [("vwap", 0)],
            "the only warm-up a twelve-bar run satisfies is VWAP's, which is 1. If this ever grows, the "
            + "series stops re-replaying and the paragraph this test pins is describing nothing");
    }

    // ── Scaffolding ──────────────────────────────────────────────────────────────────────────────────

    private static BarSessionCalendar Calendar() => BarSessionCalendar.Parse("16:00", []);

    /// <summary>
    /// The catalogue, with RSI at a chosen period.
    /// </summary>
    /// <param name="rsiPeriod">The RSI period this catalogue computes at.</param>
    /// <returns>The catalogue.</returns>
    /// <remarks>
    /// ATR is pinned short so a forty-bar series produces values at all; RSI is the knob these tests move,
    /// because moving it is how a pair the store has never held becomes one the catalogue computes.
    /// </remarks>
    /// <summary>
    /// The catalogue at the periods this server actually ships, so warm-ups are the real ones.
    /// </summary>
    /// <returns>The catalogue.</returns>
    /// <remarks>
    /// The other tests here pin ATR short so a forty-bar series produces values at all. The residue test
    /// needs the opposite — warm-ups long enough that a twelve-bar contract run cannot satisfy them — and
    /// the shipped defaults already are, so it uses them rather than inventing a period nobody runs.
    /// </remarks>
    private static IndicatorCatalog ShippedCatalog() =>
        new(Options.Create(new IndicatorOptions()), Calendar());

    /// <summary>Writes bars straight to the store, bypassing the fill path and its single contract.</summary>
    /// <param name="bars">The bars.</param>
    private async Task SeedDirectlyAsync(IReadOnlyList<Bar> bars)
    {
        foreach (Bar bar in bars)
        {
            _database.Bars.Add(new BarRecord
            {
                Venue = "test",
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
    }

    /// <summary>The distinct <c>(Indicator, Period)</c> pairs the store holds for the seeded series.</summary>
    /// <returns>The pairs, ordered.</returns>
    private async Task<IReadOnlyList<(string Indicator, int Period)>> StoredPairsAsync()
    {
        var held = await _database.IndicatorValues
            .Where(v => v.Venue == "test"
                && v.Instrument == _es.Symbol
                && v.ResolutionMinutes == Resolution)
            .Select(v => new { v.Indicator, v.Period })
            .Distinct()
            .ToListAsync();

        return [.. held.Select(v => (v.Indicator, v.Period)).OrderBy(p => p.Item1, StringComparer.Ordinal)];
    }

    private static IndicatorCatalog Catalog(int rsiPeriod) =>
        new(
            Options.Create(new IndicatorOptions { AtrPeriod = 3, RsiPeriod = rsiPeriod }),
            Calendar());

    private static IOptions<MarketDataOptions> MarketData() =>
        Options.Create(new MarketDataOptions
        {
            Instruments = "ES,NQ",
            SessionCloseCentral = "16:00",
        });

    /// <summary>Fills the store the way the world fills it — through the cache-aside bar read.</summary>
    /// <param name="catalog">The catalogue in force while the bars were written.</param>
    /// <param name="bars">How many buckets to warm.</param>
    private async Task WarmAsync(IndicatorCatalog catalog, int bars = SeededBars)
    {
        BarCacheService cache = new(
            _database,
            _gateway,
            Calendar(),
            new IndicatorProjector(_database, catalog, NullLogger<IndicatorProjector>.Instance),
            _clock,
            NullLogger<BarCacheService>.Instance);

        BarReadResult warmed = await cache.GetBarsAsync(
            _es, Resolution, new BarRange(Bucket(0), Bucket(bars)), CancellationToken.None);

        warmed.Bars.Should().HaveCount(bars, "the rest of each test rests on the store being warm");
    }

    private IndicatorCacheService Cache(IndicatorCatalog catalog) =>
        new(_database, catalog, new IndicatorProjector(_database, catalog, NullLogger<IndicatorProjector>.Instance),
            _clock, NullLogger<IndicatorCacheService>.Instance);

    private MarketDataTools Tools(IndicatorCatalog catalog) =>
        new(
            new BarCacheService(
                _database,
                _gateway,
                Calendar(),
                new IndicatorProjector(_database, catalog, NullLogger<IndicatorProjector>.Instance),
                _clock,
                NullLogger<BarCacheService>.Instance),
            _database,
            new InstrumentRegistry(MarketData()),
            catalog,
            Cache(catalog),
            new LevelMethodCatalog(Calendar()),
            _gateway,
            new ToolGuards(MarketData()),
            new StoreAvailabilityHolder(),
            _clock,
            Options.Create(new KeyLevelDetectionOptions()));
}
