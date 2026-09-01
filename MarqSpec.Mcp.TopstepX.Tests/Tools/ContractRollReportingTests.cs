using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Tests.MarketData;
using MarqSpec.Mcp.TopstepX.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MarqSpec.Mcp.TopstepX.Tests.Tools;

/// <summary>
/// What a caller is told when the window it asked for contains a contract roll (gh#42).
/// </summary>
/// <remarks>
/// The rule the whole surface follows: <b>bars are observations and are returned with the seam named; nothing
/// derived from bars is computed across one.</b> Both halves are visible in the payload, because an agent that
/// cannot see the roll will read a 40-point bookkeeping gap as a market event and act on it.
/// </remarks>
public sealed class ContractRollReportingTests : IDisposable
{
    private const string Expiring = "CON.F.US.EP.U26";
    private const string NewFront = "CON.F.US.EP.Z26";

    private readonly TopstepXDbContext _database;

    public ContractRollReportingTests() =>
        _database = new TopstepXDbContext(
            new DbContextOptionsBuilder<TopstepXDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics
                    .InMemoryEventId.TransactionIgnoredWarning))
                .Options);

    public void Dispose() => _database.Dispose();

    private static DateTimeOffset SessionStart =>
        MarketClock.FromMarket(new DateOnly(2026, 8, 18), new TimeOnly(9, 0)).ToUniversalTime();

    private static DateTimeOffset Bucket(int index) => SessionStart.AddMinutes(5 * index);

    [Fact]
    public async Task GetBars_ReportsTheRollBoundaryInThePayload()
    {
        MarketDataTools tools = await BuildAsync(rollAt: 4, total: 8);

        ToolPayloads.BarSeries series =
            await tools.GetBars("ES", 5, Bucket(0), Bucket(8), CancellationToken.None);

        series.Bars.Should().HaveCount(8, "the bars themselves are observations and are still returned");
        series.Contracts.Span.Should().Be(ToolPayloads.ContractSpan.SpansRoll);
        series.Contracts.Segments.Should().HaveCount(2);
        series.Contracts.Segments[0].ContractId.Should().Be(Expiring);
        series.Contracts.Segments[0].FirstBucket.Should().Be(Bucket(0));
        series.Contracts.Segments[0].LastBucket.Should().Be(Bucket(3));
        series.Contracts.Segments[0].BarCount.Should().Be(4);
        series.Contracts.Segments[1].ContractId.Should().Be(NewFront);
        series.Contracts.Segments[1].FirstBucket.Should().Be(Bucket(4));
    }

    [Fact]
    public async Task GetBars_WithinOneContract_ReportsNoRoll()
    {
        // The reporting must distinguish, or it is noise. A window inside one quarter says so.
        MarketDataTools tools = await BuildAsync(rollAt: 4, total: 8);

        ToolPayloads.BarSeries series =
            await tools.GetBars("ES", 5, Bucket(0), Bucket(4), CancellationToken.None);

        series.Contracts.Span.Should().Be(ToolPayloads.ContractSpan.SingleContract);
        series.Contracts.Segments.Should().ContainSingle()
            .Which.ContractId.Should().Be(Expiring);
    }

    [Fact]
    public async Task GetIndicators_ReportsTheRollBoundaryInThePayload()
    {
        // Each stored value is honest on its own — the projection never smoothed across the seam — but the
        // SERIES still crosses it, and reading the two halves as one trend is the mistake to prevent.
        MarketDataTools tools = await BuildAsync(rollAt: 4, total: 8);

        ToolPayloads.IndicatorSeries series =
            await tools.GetIndicators("ES", 5, "atr", Bucket(0), Bucket(8), CancellationToken.None);

        series.Contracts.Span.Should().Be(ToolPayloads.ContractSpan.SpansRoll);
        series.Contracts.Segments.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetIndicatorAt_SaysWhichContractTheValueBelongsTo()
    {
        // Two readings either side of a roll are not comparable, and nothing in a bare number says so.
        MarketDataTools tools = await BuildAsync(rollAt: 4, total: 8);

        ToolPayloads.IndicatorReading reading =
            await tools.GetIndicatorAt("ES", 5, "atr", Bucket(7), CancellationToken.None);

        reading.Value.Should().Be(4m, "the new contract's own range, hand-checked: (4+4+4)/3");
        reading.ContractId.Should().Be(NewFront);
    }

    [Fact]
    public async Task GetKeyLevels_DetectsOnlyOverTheContractInFront()
    {
        // The harm named in gh#42: a level "formed" at a price the current contract has never traded at.
        // Detection is confined to the newest contract segment, and the truncation is stated rather than
        // implied — a caller that asked for 40 bars and silently got 20 would draw the wrong conclusion about
        // how much history supports the levels.
        MarketDataTools tools = await BuildSwingingAsync(rollAt: 20, total: 40);

        ToolPayloads.LevelSet levels = await tools.GetKeyLevels("ES", 5, 40, cancellationToken: CancellationToken.None);

        levels.Contracts.Span.Should().Be(ToolPayloads.ContractSpan.SpansRoll);
        levels.DetectedOverBars.Should().Be(20, "only the bars of the contract in front are eligible");
        levels.Levels.Should().NotBeEmpty("the swinging fixture produces pivots on the new contract");
        levels.Levels.Should().NotContain(
            l => l.FormedAt < Bucket(20),
            "a level from the expiring contract sits at a price the new one has never traded");
        levels.Levels.Should().NotContain(
            l => l.Bottom < 130m,
            "the expiring contract traded around 100 and the new one has never been near it");
    }

    [Fact]
    public async Task GetMarketSnapshot_ReportsTheRollBehindItsLevels_NotOnlyBehindItsBars()
    {
        // gh#42 review, finding 2. The snapshot reads TWO windows of different lengths: barCount bars, and
        // max(barCount, 200) bars for level detection. Ask for eight bars over a store that rolled twenty
        // bars back and they disagree -- the bars are all one contract, the levels were truncated at a seam.
        //
        // Reporting only the bar window let the payload state the weaker fact on the one tool the catalogue
        // tells an agent to reach for first, and dropped detectedOverBars, which PRD R-3.5 requires.
        MarketDataTools marketData = await BuildSwingingAsync(rollAt: 20, total: 40);

        ToolPayloads.MarketSnapshot snapshot = await Snapshot(marketData)
            .GetMarketSnapshot("ES", [5], 8, CancellationToken.None);

        ToolPayloads.ResolutionSnapshot slice = snapshot.PerResolution.Should().ContainSingle().Subject;

        slice.Contracts.Span.Should().Be(
            ToolPayloads.ContractSpan.SingleContract,
            "the eight bars returned are all from the contract in front");

        slice.Levels.Contracts.Span.Should().Be(
            ToolPayloads.ContractSpan.SpansRoll,
            "the levels behind them were detected over a window that crosses the roll");

        slice.Levels.DetectedOverBars.Should().Be(
            20, "and the caller has to be able to see how much history actually supports them");
    }

    [Fact]
    public async Task GetKeyLevels_NullRunBetweenTwoRunsOfTheSameContract_IsUnknown_NotSpansRoll()
    {
        // gh#402. Bars written before migration 20260823074908_AddBarContractId kept ContractId == null
        // forever, and a block of them sitting between two attributed runs of the SAME contract used to be
        // reported exactly like a genuine roll -- SpansRoll -- because ToCoverage treated any run count above
        // one as a roll regardless of whether a run's provenance was ever recorded. There was no roll here:
        // every bar is Expiring. ContractRollDetector's segmentation is UNCHANGED by the fix -- it still
        // reports three runs, and Newest still confines detection to the trailing one -- only the SUMMARY
        // exposed to the caller must stop calling an unattributed seam a roll.
        for (int i = 0; i < 8; i++)
        {
            bool unattributed = i is >= 3 and < 5;
            AddBar(i, close: 100m, halfRange: 1m, contractId: unattributed ? null : Expiring);
        }

        MarketDataTools tools = await ComposeAsync(8);

        ToolPayloads.LevelSet levels =
            await tools.GetKeyLevels("ES", 5, 8, cancellationToken: CancellationToken.None);

        levels.Contracts.Span.Should().Be(
            ToolPayloads.ContractSpan.Unknown,
            "every bar is the Expiring contract -- the seam is missing provenance, not a second contract");
        levels.Contracts.Segments.Should().HaveCount(
            3, "the null run is still its own segment -- the fix reports it honestly, it does not fold it in");
        levels.DetectedOverBars.Should().Be(
            3, "detection still confines to the trailing run -- this fix is about the SIGNAL, not the window");
    }

    [Fact]
    public async Task GetKeyLevels_NullRunBetweenTwoDifferentContracts_IsStillReportedAsSpansRoll()
    {
        // gh#402 review. A genuine roll -- two DIFFERENT recorded contracts -- must not be swallowed by an
        // unattributed run sitting inside the same window. The store KNOWS a roll happened here; a null run
        // elsewhere cannot make that knowledge disappear. An earlier version of this fix checked "any null
        // segment present" BEFORE checking "two different recorded contracts present", so it reported
        // Unknown here -- worse than the original defect, because it teaches a caller to read a genuine
        // bookkeeping gap as market movement, which is the unsafe direction.
        for (int i = 0; i < 8; i++)
        {
            string? contractId = i switch
            {
                < 3 => Expiring,
                < 5 => null,
                _ => NewFront,
            };

            AddBar(i, close: 100m, halfRange: 1m, contractId: contractId);
        }

        MarketDataTools tools = await ComposeAsync(8);

        ToolPayloads.LevelSet levels =
            await tools.GetKeyLevels("ES", 5, 8, cancellationToken: CancellationToken.None);

        levels.Contracts.Span.Should().Be(
            ToolPayloads.ContractSpan.SpansRoll,
            "two DIFFERENT recorded contracts appear in the window -- the store is certain a roll happened, "
            + "and an unattributed run elsewhere must not outrank that certainty");
        levels.Contracts.Segments.Should().HaveCount(3, "the null run is still its own segment");
    }

    [Fact]
    public async Task UnknownProvenance_IsReportedAsUnknown_NotAsNoRoll()
    {
        // gh#42 review, finding 3. Every bar written before this server recorded provenance carries none, so
        // a window over legacy history genuinely may or may not contain a roll. A bool could only say "no",
        // which is a missing fact rendered as a confident negative -- on the field added to stop exactly that.
        for (int i = 0; i < 8; i++)
        {
            AddBar(i, close: 100m, halfRange: 1m, contractId: null);
        }

        MarketDataTools tools = await ComposeAsync(8);

        ToolPayloads.BarSeries series =
            await tools.GetBars("ES", 5, Bucket(0), Bucket(8), CancellationToken.None);

        series.Contracts.Span.Should().Be(ToolPayloads.ContractSpan.Unknown);
        series.Contracts.Segments.Should().ContainSingle()
            .Which.ContractId.Should().BeNull("the absence is what the caller has to be able to see");
    }

    /// <summary>
    /// Eight flat bars — four of the expiring contract, four of the new one — so the arithmetic is exact.
    /// </summary>
    /// <remarks>
    /// Each half has a constant high-low range, 2 points then 4, so every true range inside a segment equals
    /// that range and the Wilder seed is its own mean. Hand-checkable, and exact in <c>decimal</c>.
    /// </remarks>
    private async Task<MarketDataTools> BuildAsync(int rollAt, int total)
    {
        for (int i = 0; i < total; i++)
        {
            bool rolled = i >= rollAt;
            AddBar(
                i,
                close: rolled ? 140m : 100m,
                halfRange: rolled ? 2m : 1m,
                contractId: rolled ? NewFront : Expiring);
        }

        return await ComposeAsync(total);
    }

    /// <summary>
    /// A longer series whose closes zig-zag, so that swing pivots actually form on both sides of the roll.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A monotone ramp produces no pivots at all, and a key-level assertion over a series with no pivots
    /// asserts nothing. The triangle repeats every twelve bars, comfortably wider than the default lookback
    /// of five on each side.
    /// </para>
    /// <para>
    /// The step has to be large relative to the bar's own range or nothing survives the significance floor.
    /// Heikin-Ashi bodies smooth the turn, so a peak's prominence works out at roughly one step while ATR
    /// works out at the step plus the range: a one-point step against a four-point range scores 0.25 and is
    /// filtered out, which is a silently empty fixture rather than a failing one. Four points against a
    /// four-point range scores about 0.67 and clears the 0.5 floor.
    /// </para>
    /// </remarks>
    /// <summary>
    /// A triangle wave over two contracts, with the detection window stated rather than inherited.
    /// </summary>
    /// <param name="rollAt">The bar index the new front contract starts at.</param>
    /// <param name="total">How many bars in total.</param>
    /// <returns>The tool over the seeded store.</returns>
    /// <remarks>
    /// <b>The window is 5 and 5 here, not the shipped 20 and 15, and the fixture is why (gh#245).</b> The
    /// swing repeats every twelve bars, so under a left window of twenty every peak has an EQUAL peak inside
    /// its own window — and a tie is not dominance, so the series yields no pivots at any amplitude. That is
    /// the sawtooth trap <c>LevelMethodCatalogRollTests</c> records, arriving from the other direction: the
    /// fixture cannot carry the shipped window without ceasing to be a repeating swing. These cases are
    /// about <c>R-3.5</c> — that detection stops at the seam — so the window is set to one this shape can
    /// actually hold a pivot in, and stated here rather than assumed.
    /// </remarks>
    private async Task<MarketDataTools> BuildSwingingAsync(int rollAt, int total)
    {
        for (int i = 0; i < total; i++)
        {
            bool rolled = i >= rollAt;
            int phase = i % 12;
            decimal swing = 4m * (phase <= 6 ? phase : 12 - phase);

            AddBar(
                i,
                close: (rolled ? 140m : 100m) + swing,
                halfRange: rolled ? 2m : 1m,
                contractId: rolled ? NewFront : Expiring);
        }

        return await ComposeAsync(
            total,
            new KeyLevelDetectionOptions { PivotLookback = 5, PivotRightLookback = 5 });
    }

    private void AddBar(int index, decimal close, decimal halfRange, string? contractId) =>
        _database.Bars.Add(new BarRecord
        {
            Venue = "test",
            Instrument = "ES",
            ResolutionMinutes = 5,
            BucketStart = Bucket(index),
            Open = close,
            High = close + halfRange,
            Low = close - halfRange,
            Close = close,
            Volume = 1_000,
            ContractId = contractId,
            RecordedAt = SessionStart,
        });

    /// <summary>
    /// Composes the snapshot tool over the same store, so both windows it reads are the ones just seeded.
    /// </summary>
    private SnapshotTools Snapshot(MarketDataTools marketData)
    {
        ReferenceTools reference = new(
            new InstrumentRegistry(_wrapped!), _calendar!, _gateway!, _wrapped!, _clock!);

        return new SnapshotTools(marketData, reference, new IndicatorCatalogNames(_catalog!), _clock!);
    }

    private IOptions<MarketDataOptions>? _wrapped;
    private BarSessionCalendar? _calendar;
    private IndicatorCatalog? _catalog;
    private FakeTimeProvider? _clock;
    private CountingGateway? _gateway;

    private async Task<MarketDataTools> ComposeAsync(int total, KeyLevelDetectionOptions? detection = null)
    {
        await _database.SaveChangesAsync();

        MarketDataOptions options = new()
        {
            Instruments = "ES,NQ",
            MaxRows = 5_000,
            SessionCloseCentral = "16:00",
        };
        IOptions<MarketDataOptions> wrapped = Options.Create(options);

        BarSessionCalendar calendar = BarSessionCalendar.Parse("16:00", []);
        IndicatorCatalog catalog = new(
            Options.Create(new IndicatorOptions { AtrPeriod = 3, RsiPeriod = 3 }), calendar);
        FakeTimeProvider clock = new(Bucket(total).AddHours(2));
        CountingGateway gateway = new([]);

        (_wrapped, _calendar, _catalog, _clock, _gateway) = (wrapped, calendar, catalog, clock, gateway);

        IndicatorProjector projector = new(_database, catalog, NullLogger<IndicatorProjector>.Instance);
        await projector.ProjectAsync("test", new InstrumentId("ES"), 5, SessionStart, CancellationToken.None);
        await _database.SaveChangesAsync();

        BarCacheService cache = new(
            _database, gateway, calendar, projector, clock, NullLogger<BarCacheService>.Instance);

        return new MarketDataTools(
            cache,
            _database,
            new InstrumentRegistry(wrapped),
            catalog,
            new IndicatorCacheService(
                _database, catalog, projector, clock, NullLogger<IndicatorCacheService>.Instance),
            new LevelMethodCatalog(calendar),
            gateway,
            new ToolGuards(wrapped),
            new StoreAvailabilityHolder(),
            clock,
            Options.Create(detection ?? new KeyLevelDetectionOptions()),
            new VolumeProfileService(_database),
            new TapeAvailabilityHolder(),
            new TapeVolumeFrontService(_database, gateway, calendar),
            new FootprintCacheService(
                _database,
                new FootprintProjector(_database, NullLogger<FootprintProjector>.Instance),
                clock,
                NullLogger<FootprintCacheService>.Instance));
    }
}
