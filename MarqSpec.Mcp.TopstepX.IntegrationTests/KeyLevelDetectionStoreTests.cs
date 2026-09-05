using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Tests.MarketData;
using MarqSpec.Mcp.TopstepX.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// The one <c>get_key_levels</c> case that is answered through <c>get_market_snapshot</c> — a configured
/// pivot lookback the snapshot's own fixed window cannot satisfy, which must explain itself rather than
/// take the server down (gh#244, gh#245).
/// </summary>
/// <remarks>
/// <para>
/// <b>Split out of <c>KeyLevelDetectionPlumbingTests</c> by gh#387.</b> Every other case in that class either
/// refuses at the tool boundary or reads levels straight off the seeded bars, and none of them reaches a
/// write — so they still run in the unit tier with no container. This one composes the whole snapshot, which
/// reads its indicators through <c>IndicatorCacheService</c>, and a served read there is a
/// <i>write</i>: the on-read projection goes out as the real <c>UpsertValuesSql</c>, an
/// <c>ON CONFLICT … DO UPDATE</c>, inside the <c>RepeatableRead</c> transaction <c>SeriesUnitOfWork</c> now
/// always opens.
/// </para>
/// <para>
/// <b>Why it could not stay.</b> That write used to have a second implementation —
/// <c>IndicatorProjector.WriteInMemory</c> — which existed only so the unit tier's provider could pretend,
/// was executed by no production process, and disagreed with the real path about what a write count meant.
/// It was deleted, and the projector now throws unconditionally when there is no current transaction.
/// </para>
/// <para>
/// The claim is unchanged, and it is the blocking review finding this file exists for: a refusal a caller
/// cannot act on is an outage, not a refusal. <c>get_market_snapshot</c> exposes neither
/// <c>pivotLookback</c> nor <c>lookbackBars</c>, so bounding the lookback against the requested window made
/// the server boot clean and then fail every snapshot call with advice about two arguments the tool does not
/// have.
/// </para>
/// </remarks>
[Collection(SeriesStoreCollection.Name)]
public sealed class KeyLevelDetectionStoreTests(SeriesStoreFixture fixture) : IAsyncLifetime
{
    private const string Contract = "CON.F.US.EP.U26";

    /// <summary>How many bars the fixture seeds, and the look-back every case asks for.</summary>
    private const int Bars = 21;

    private readonly SeriesStoreFixture _fixture = fixture;
    private readonly TopstepXDbContext _database = fixture.CreateContext();

    /// <inheritdoc />
    public Task InitializeAsync() => _fixture.ResetAsync();

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        _database.Dispose();
        return Task.CompletedTask;
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────────────
    //  THE FIXTURE — one contract, 21 five-minute bars, two shoulders and a peak.
    //
    //  Each bar is written around a midpoint: open = mid - 1, close = mid + 1, high = mid + 5, low = mid - 5.
    //  The body is two points wide and the wicks reach four points past it either side, so the three sources
    //  read three different prices off the same bar. The open is NOT the previous close, which is what lets
    //  `Body` find anything at all (gh#247).
    //
    //    i:     0    1    2    3    4    5    6    7    8    9   10   11   12   13   14   15   16   17   18   19   20
    //    mid: 100  104  108  112  116  120  116  112  116  120  124  128  132  136  140  136  132  128  124  120  116
    //
    //  Two structures, deliberately at different scales:
    //    i = 5   a shoulder — dominates two bars either side, but bar 10 is higher, so it dies at lookback 5.
    //    i = 7   the mirrored support shoulder — bar 2 is lower, so it dies at lookback 5 too.
    //    i = 14  the peak — dominates five bars either side, so it survives both lookbacks.
    //
    //  TRUE RANGE IS EXACTLY 10 ON EVERY BAR, which is what makes every number below exact. With a step of
    //  four: high - low = 10; |high - prevClose| = |±4 + 4| is 8 or 0; |low - prevClose| = |±4 - 6| is 2 or
    //  10. So ATR(3) seeds at 10 and Wilder's smoothing keeps it there. A zone is 10 × multiple wide, and a
    //  prominence of 4 scores 0.4.
    // ──────────────────────────────────────────────────────────────────────────────────────────────────
    private static readonly int[] _midpoints =
        [100, 104, 108, 112, 116, 120, 116, 112, 116, 120, 124, 128, 132, 136, 140, 136, 132, 128, 124, 120, 116];

    private static DateTimeOffset SessionStart =>
        MarketClock.FromMarket(new DateOnly(2026, 8, 18), new TimeOnly(9, 0)).ToUniversalTime();

    private static DateTimeOffset Bucket(int index) => SessionStart.AddMinutes(5 * index);

    /// <summary>The detection defaults these tests run under unless a case says otherwise.</summary>
    /// <param name="source">The configured pivot source.</param>
    /// <param name="pivotLookback">The configured pivot lookback.</param>
    /// <param name="zoneAtrMultiple">The configured zone width, in ATR multiples.</param>
    /// <param name="minSignificance">The configured significance floor, in ATR multiples.</param>
    /// <returns>The options.</returns>
    /// <param name="pivotRightLookback">
    /// The configured right-hand confirmation window, or null to mirror <paramref name="pivotLookback"/>.
    /// </param>
    /// <param name="maxZoneWidthPercent">The configured width cap, as a percentage of a zone's midpoint.</param>
    /// <param name="maxLevels">The configured level cap.</param>
    /// <remarks>
    /// <para>
    /// <b><see cref="KeyLevelDetectionOptions.MinSignificance"/> is zero here, deliberately.</b> Every pivot
    /// in the fixture scores exactly 0.4 — a prominence of four against an ATR of ten — so the shipped floor
    /// of 0.5 would filter all of them and the case below would be asserting about an empty list for the
    /// wrong reason. The floor's own plumbing is pinned in the unit tier, by the pair that turns it up and
    /// watches the levels go.
    /// </para>
    /// <para>
    /// <b>The two caps are effectively off here for the same reason, and the right window MIRRORS the left
    /// unless a case names it.</b> The fixture is twenty-one bars around a price of 100 with an ATR of ten —
    /// a tenth of price, where a real five-minute future is nearer a thousandth — so the shipped 2.5% width
    /// cap would delete every zone in it, and the shipped 20/15 window would not fit the series at all. The
    /// mirror keeps every derivation readable as "dominates N bars either side", which is how the fixture's
    /// own comment states them.
    /// </para>
    /// </remarks>
    private static KeyLevelDetectionOptions Detection(
        PivotSource source = PivotSource.HighLow,
        int pivotLookback = 5,
        decimal zoneAtrMultiple = 0.5m,
        decimal minSignificance = 0m,
        int? pivotRightLookback = null,
        decimal maxZoneWidthPercent = 100m,
        int maxLevels = 1_000) =>
        new()
        {
            Source = source.ToString(),
            PivotLookback = pivotLookback,
            ZoneAtrMultiple = zoneAtrMultiple,
            MinSignificance = minSignificance,
            PivotRightLookback = pivotRightLookback ?? pivotLookback,
            MaxZoneWidthPercent = maxZoneWidthPercent,
            MaxLevels = maxLevels,
        };

    [Fact]
    public async Task AConfiguredLookbackTheSnapshotsFixedWindowCannotSatisfy_DoesNotBreakTheSnapshot()
    {
        // THE BLOCKING REVIEW FINDING. `get_market_snapshot` detects over a fixed `max(barCount, 200)` and
        // exposes NEITHER `pivotLookback` NOR `lookbackBars`. A configured lookback of 100 is legal on its
        // own range -- `[Range(1, 1_000)]`, and options validation passes it -- so bounding the lookback
        // against the requested window made the server boot clean and then fail EVERY snapshot call, with
        // advice to change two arguments this tool does not have.
        //
        // A refusal a caller cannot act on is an outage, not a refusal. So the snapshot answers, and the
        // level set explains itself.
        (_, SnapshotTools snapshot) = Compose(Detection(pivotLookback: 100));

        ToolPayloads.MarketSnapshot result =
            await snapshot.GetMarketSnapshot("ES", [5], 100, CancellationToken.None);

        ToolPayloads.LevelSet levels = result.PerResolution.Should().ContainSingle().Subject.Levels;

        levels.Levels.Should().BeEmpty();
        levels.DetectedOverBars.Should().Be(Bars);
        levels.Detection.PivotLookback.Should().Be(100);
    }

    // ── Composition ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the market-data tools <b>and the composed snapshot over the same store</b>.
    /// </summary>
    /// <param name="detection">The detection defaults to build against.</param>
    /// <returns>Both tools.</returns>
    /// <remarks>
    /// The snapshot is composed here rather than in its own fixture because it is the tool that reaches
    /// <c>GetKeyLevels</c> with a window <i>it</i> chose — a fixed <c>max(barCount, 200)</c> — and with
    /// neither detection argument. A bound on the requested window is invisible from
    /// <c>get_key_levels</c>'s own tests and fatal here, which is how the earlier revision got through.
    /// </remarks>
    private (KeyLevelTools KeyLevels, SnapshotTools Snapshot) Compose(KeyLevelDetectionOptions detection)
    {
        if (!_database.Bars.Any())
        {
            Seed();
        }

        IOptions<MarketDataOptions> market = Options.Create(new MarketDataOptions
        {
            Instruments = "ES,NQ",
            MaxRows = 5_000,
            SessionCloseCentral = "16:00",
        });

        BarSessionCalendar calendar = BarSessionCalendar.Parse("16:00", []);

        // ATR(3), so a value exists from bar 3 onward and every pivot in the fixture is scaled by a real
        // one. At the shipped 14 the two shoulders would carry no ATR and be SKIPPED -- silently, since a
        // pivot with no scale yields no zone -- and the lookback cases would be asserting about that instead.
        IndicatorCatalog indicators = new(
            Options.Create(new IndicatorOptions { AtrPeriod = 3 }), calendar);

        CountingGateway gateway = new([]);
        FakeTimeProvider clock = new(Bucket(Bars).AddHours(2));

        IndicatorProjector projector = new(_database, indicators, NullLogger<IndicatorProjector>.Instance);

        BarCacheService cache = new(
            _database, gateway, calendar, projector, clock, NullLogger<BarCacheService>.Instance);

        InstrumentResolver resolver = new(new InstrumentRegistry(market), new StoreAvailabilityHolder());
        ToolGuards guards = new(market);

        KeyLevelTools keyLevels = new(
            resolver,
            _database,
            indicators,
            new LevelMethodCatalog(calendar),
            gateway,
            guards,
            new VolumeProfileService(_database),
            Options.Create(detection));

        // gh#246's read-projection seam. get_key_levels does not touch it -- it computes ATR from the bars
        // it just loaded rather than from the store, which is why KeyLevelTools no longer takes an
        // IndicatorCacheService at all (gh#414) -- but the snapshot composed below DOES read indicators
        // through it, so it is built here for IndicatorTools rather than dropped.
        IndicatorTools indicatorTools = new(
            resolver,
            _database,
            indicators,
            new IndicatorCacheService(
                _database, indicators, projector, clock, NullLogger<IndicatorCacheService>.Instance),
            gateway,
            guards);

        SnapshotTools snapshot = new(
            new BarTools(resolver, cache, guards, clock),
            indicatorTools,
            keyLevels,
            new ReferenceTools(new InstrumentRegistry(market), calendar, gateway, market, clock),
            new IndicatorCatalogNames(indicators),
            clock);

        return (keyLevels, snapshot);
    }

    private void Seed()
    {
        for (int i = 0; i < _midpoints.Length; i++)
        {
            decimal mid = _midpoints[i];
            _database.Bars.Add(new BarRecord
            {
                Venue = "test",
                Instrument = "ES",
                ResolutionMinutes = 5,
                BucketStart = Bucket(i),
                Open = mid - 1m,
                High = mid + 5m,
                Low = mid - 5m,
                Close = mid + 1m,
                Volume = 1_000,
                ContractId = Contract,
                RecordedAt = SessionStart,
            });
        }

        _database.SaveChanges();
    }
}
