using System.Data;
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
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// What the three indicator read paths serve when a stored value's bar is gone (gh#577).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the repository's central failure mode in its purest form.</b> gh#571 stopped an orphaned value
/// <i>surviving</i> a bar delete; it did not stop one being <b>served</b> in the window before a pass runs,
/// and no read runs that pass — <see cref="IndicatorCacheService.EnsureProjectedAsync"/> returns early at
/// zero bars, so a series whose bars are all gone is never reprojected on read. Measured on PR #574's head:
/// <c>get_indicators</c> returned <b>37 ATR points over zero bars</b>, <c>get_indicator_at</c> returned
/// <b>65.32947503</b> with <c>contract=null</c>, and <c>get_market_snapshot</c> published that same number
/// beside <c>bars: []</c>. Nothing failed and nothing said anything was wrong.
/// </para>
/// <para>
/// <b>The decision these cases pin is "serve nothing", not "refuse".</b> A read serves a stored value only
/// where the store still holds the bar at its bucket. That is an <i>absence</i>, which `R-2.3` already makes
/// every caller read as cannot-measure, rather than an error — an error would be the caller's to fix and this
/// is the store's condition, and refusing a whole window because one bucket in it lost its bar would be an
/// answer far larger than the fault.
/// </para>
/// <para>
/// <b>The snapshot's LEFT join argued the other way, and the case it argued for is still served.</b> Its
/// comment said an inner join "would turn a known number with unknown provenance into cannot-measure". That
/// describes <c>ContractId</c> being null on a bar that <i>exists</i> — and
/// <see cref="BarRecord.ContractId"/> is nullable, so a join on <c>BucketStart</c> still matches that bar and
/// still reports the unknown contract. <c>AValueWhoseBarRecordedNoContract_IsStillServed</c> is that half,
/// measured rather than argued. What the join actually decided was the other case, where the bar row is
/// absent entirely — and there the number is not known-with-unknown-provenance, it is unreproducible
/// (ADR-0006 §3): no pass recomputes it, so nothing can confirm or correct it.
/// </para>
/// <para>
/// <b>Nothing here sweeps.</b> ADR-0006's 2026-09-07 update keeps a read out of the delete path deliberately,
/// and <c>TheOrphanedRowsAreStillInTheStore_TheReadJustDoesNotServeThem</c> says so: the rows stand until
/// <c>rebuild-indicators</c> or a fill visits the series, exactly as gh#571 left them.
/// </para>
/// </remarks>
[Collection(SeriesStoreCollection.Name)]
public sealed class IndicatorOrphanReadTests : IAsyncLifetime
{
    private const string Venue = "test";
    private const int Resolution = 5;

    /// <summary>How many bars the fixture stores before anything is deleted.</summary>
    /// <remarks>
    /// Enough that ATR(3) and RSI(3) are warm for most of the series and the measured shape has something to
    /// be wrong about: the probe's report of "37 ATR points over zero bars" is only alarming because there
    /// were points to return.
    /// </remarks>
    private const int Bars = 40;

    /// <summary>How many bars survive the partial delete.</summary>
    private const int Kept = 30;

    private const string Contract = "CON.F.US.EP.Z26";

    private static readonly InstrumentId _es = new("ES");

    private readonly SeriesStoreFixture _fixture;
    private readonly TopstepXDbContext _database;
    private readonly HostTelemetry _telemetry = new();

    /// <param name="fixture">The shared container.</param>
    public IndicatorOrphanReadTests(SeriesStoreFixture fixture)
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
        _telemetry.Dispose();
        return Task.CompletedTask;
    }

    private static DateTimeOffset SessionStart =>
        MarketClock.FromMarket(new DateOnly(2026, 8, 18), new TimeOnly(9, 0)).ToUniversalTime();

    private static DateTimeOffset Bucket(int index) => SessionStart.AddMinutes(Resolution * index);

    [Fact]
    public async Task ASeriesWhoseBarsAreAllDeleted_ServesNoPointFromGetIndicators()
    {
        // The first measured probe. Every bar of the series is deleted -- a base revision, or reselect-bars
        // replacing a window that turns out to be everything stored -- and the ATR rows stand, because there
        // is no foreign key (ADR-0011 §2) and no read runs the pass that would sweep them.
        (IndicatorTools indicators, _) = await ComposeAsync();
        await DeleteBarsAsync(from: 0);

        ToolPayloads.IndicatorSeries series = await indicators.GetIndicators(
            "ES", Resolution, "atr", Bucket(0), Bucket(Bars + 1), cancellationToken: CancellationToken.None);

        series.Values.Should().BeEmpty(
            "every one of these values was computed from bars the store no longer holds, and a number nothing "
            + "can reproduce is not a reading -- it is the plausible answer this repository exists to refuse");

        series.Contracts.Segments.Should().BeEmpty(
            "and the coverage block already said so: zero bars underneath the window it was asked about");
    }

    [Fact]
    public async Task ASeriesWhoseBarsAreAllDeleted_CannotMeasureFromGetIndicatorAt()
    {
        // The second measured probe: 65.32947503 with contract=null. The null contract was the only hint
        // anything was wrong, and ToolPayloads documents that field as meaning the BAR's provenance was never
        // recorded -- so it actively told the caller a bar was there.
        (IndicatorTools indicators, _) = await ComposeAsync();
        await DeleteBarsAsync(from: 0);

        ToolPayloads.IndicatorReading reading = await indicators.GetIndicatorAt(
            "ES", Resolution, "atr", Bucket(Bars), cancellationToken: CancellationToken.None);

        reading.Value.Should().BeNull(
            "cannot-measure is the honest answer over bars that are gone, and it is the one `R-2.3` already "
            + "tells every caller to refuse to conclude from");

        reading.BucketStart.Should().BeNull("a reading with no value has no bucket either");
        reading.ContractId.Should().BeNull();
    }

    [Fact]
    public async Task ASeriesWhoseBarsAreAllDeleted_PublishesCannotMeasureInTheSnapshot()
    {
        // The third, added by the reviewer of the probe: atr = 65.32947503 and rsi = 54.29857597, both with
        // contract=null, in a slice whose own `bars` array was EMPTY. The payload contradicted itself and
        // nothing in it said which half to believe.
        (_, SnapshotTools snapshot) = await ComposeAsync();
        await DeleteBarsAsync(from: 0);

        ToolPayloads.MarketSnapshot payload =
            await snapshot.GetMarketSnapshot("ES", [Resolution], Bars, CancellationToken.None);

        ToolPayloads.ResolutionSnapshot slice = payload.PerResolution.Should().ContainSingle().Subject;

        slice.Bars.Should().BeEmpty("this is the zero-bars slice the numbers were published beside");

        slice.Indicators.Should().ContainKey("atr", "every catalogue name is keyed unconditionally");

        slice.Indicators["atr"].Should().BeNull(
            "the map's own null is cannot-measure, and that is what a value over deleted bars is");

        slice.Indicators["rsi"].Should().BeNull("the same, for the second number the reviewer measured");

        slice.Indicators.Values.Should().OnlyContain(
            reading => reading == null,
            "a slice over zero bars can measure nothing at all, whatever rows are left standing behind it");
    }

    [Fact]
    public async Task APartialDelete_StillServesEveryValueTheRemainingBarsJustify()
    {
        // "Serve nothing" is per VALUE, not per series -- otherwise a single deleted bucket would blank a
        // year of history, which is the over-large answer the decision explicitly rejects. The tail is
        // orphaned and the head is not, so the head must still read exactly as it did.
        (IndicatorTools indicators, _) = await ComposeAsync();
        await DeleteBarsAsync(from: Kept);

        ToolPayloads.IndicatorSeries series = await indicators.GetIndicators(
            "ES", Resolution, "atr", Bucket(0), Bucket(Bars + 1), cancellationToken: CancellationToken.None);

        series.Values.Should().NotBeEmpty("the first thirty bars are still there and still justify their values");

        series.Values.Select(v => v.T).Should().OnlyContain(
            t => t < Bucket(Kept), "and nothing past the last surviving bar is served");

        // The as-of read falls BACK to the newest justified bucket rather than reporting cannot-measure,
        // which is the same fallback a contract seam already produces (R-2.7) and is why the servability
        // filter has to sit under the as-of ordering rather than over its answer.
        ToolPayloads.IndicatorReading reading = await indicators.GetIndicatorAt(
            "ES", Resolution, "atr", Bucket(Bars), cancellationToken: CancellationToken.None);

        reading.Value.Should().NotBeNull();
        reading.BucketStart.Should().Be(
            Bucket(Kept - 1), "the newest bucket at or before the moment that still has a bar");
        reading.ContractId.Should().Be(Contract);
    }

    [Fact]
    public async Task AValueWhoseBarRecordedNoContract_IsStillServed()
    {
        // THE SNAPSHOT COMMENT'S OWN CASE, and it is untouched. "A known number with unknown provenance"
        // is a bar that EXISTS whose ContractId was never recorded -- BarRecord.ContractId is nullable, and
        // the join is on BucketStart -- so the number is served and the unknown contract is reported. The
        // LEFT join was never what protected this; it only ever decided the case where the bar row itself is
        // absent, and there the provenance is not the thing that is unknown.
        (IndicatorTools indicators, SnapshotTools snapshot) = await ComposeAsync(contractId: null);

        ToolPayloads.IndicatorReading reading = await indicators.GetIndicatorAt(
            "ES", Resolution, "atr", Bucket(Bars), cancellationToken: CancellationToken.None);

        reading.Value.Should().NotBeNull("the bars are there, so the number is reproducible from them");
        reading.ContractId.Should().BeNull("and the provenance genuinely is unknown, which is what null says");

        ToolPayloads.MarketSnapshot payload =
            await snapshot.GetMarketSnapshot("ES", [Resolution], Bars, CancellationToken.None);

        ToolPayloads.ResolutionSnapshot slice = payload.PerResolution.Should().ContainSingle().Subject;

        slice.Indicators["atr"].Should().NotBeNull(
            "the batched read has to keep the same case the single-purpose one does, or the two disagree");
        slice.Indicators["atr"]!.ContractId.Should().BeNull();
    }

    [Fact]
    public async Task TheOrphanedRowsAreStillInTheStore_TheReadJustDoesNotServeThem()
    {
        // A read still does not sweep (ADR-0006, 2026-09-07). This card changes what is SERVED, not what is
        // stored: wiring the delete into a read would let it delete on the strength of a catalogue it never
        // projected with, and gh#571 shipped no migration on the argument that rebuild-indicators is the
        // remedy. That argument is not contradicted here -- it is only made safe to wait for.
        (IndicatorTools indicators, _) = await ComposeAsync();
        await DeleteBarsAsync(from: 0);

        await indicators.GetIndicators(
            "ES", Resolution, "atr", Bucket(0), Bucket(Bars + 1), cancellationToken: CancellationToken.None);

        (await _database.IndicatorValues.AsNoTracking().CountAsync()).Should().BeGreaterThan(
            0, "the rows stand until a pass visits the series, and a read is not a pass");
    }

    // ── Scaffolding ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Deletes every bar from an index onward.</summary>
    /// <param name="from">The first bar index to delete.</param>
    private async Task DeleteBarsAsync(int from)
    {
        DateTimeOffset cut = Bucket(from);

        _database.Bars.RemoveRange(
            await _database.Bars.Where(b => b.BucketStart >= cut).ToListAsync());

        await _database.SaveChangesAsync();
        _database.ChangeTracker.Clear();
    }

    /// <summary>
    /// Seeds the bars, projects the indicators over them, and composes the two tools that read them back.
    /// </summary>
    /// <param name="contractId">
    /// The contract every seeded bar carries, or <see langword="null"/> for bars whose provenance was never
    /// recorded.
    /// </param>
    /// <returns>The single-purpose tool and the snapshot, out of ONE wiring.</returns>
    /// <remarks>
    /// One <see cref="IndicatorTools"/> instance serves both, exactly as the composition root wires it: two
    /// independently built tools could agree by having been handed the same fixture twice while disagreeing
    /// about the same one.
    /// </remarks>
    private async Task<(IndicatorTools Indicators, SnapshotTools Snapshot)> ComposeAsync(
        string? contractId = Contract)
    {
        // Bars that drift irregularly, so ATR and RSI land on values with real precision rather than on
        // numbers a flat series would make indistinguishable from a default.
        for (int i = 0; i < Bars; i++)
        {
            decimal drift = i % 3 == 0 ? 1.37m : i % 3 == 1 ? -0.91m : 2.13m;
            decimal close = 5_000m + (i * drift);

            _database.Bars.Add(new BarRecord
            {
                Venue = Venue,
                Instrument = _es.Symbol,
                ResolutionMinutes = Resolution,
                BucketStart = Bucket(i),
                Open = close,
                High = close + 1.25m,
                Low = close - 0.75m,
                Close = close,
                Volume = 1_000 + i,
                ContractId = contractId,
                RecordedAt = SessionStart,
            });
        }

        await _database.SaveChangesAsync();
        _database.ChangeTracker.Clear();

        MarketDataOptions options = new()
        {
            Instruments = "ES,NQ",
            MaxRows = 5_000,
            SessionCloseCentral = "16:00",
        };
        IOptions<MarketDataOptions> wrapped = Options.Create(options);

        BarSessionCalendar calendar = BarSessionCalendar.Parse("16:00", []);

        // Short periods, so forty bars leave ATR and RSI warm over almost the whole series. The shipped 14
        // and 20 would leave so few points that "no points" and "few points" stop being distinguishable.
        IndicatorCatalog catalog = new(
            Options.Create(new IndicatorOptions { AtrPeriod = 3, RsiPeriod = 3 }), calendar);

        FakeTimeProvider clock = new(Bucket(Bars).AddHours(2));

        // Serves nothing, so every read below is answered from the store alone. A gateway holding bars would
        // refill the window a case had just emptied -- and, worse, a fill projects, which would sweep the very
        // rows these cases are about.
        CountingGateway gateway = new([]);

        IndicatorProjector projector =
            new(_database, catalog, NullLogger<IndicatorProjector>.Instance, _telemetry);

        // Wrapped in the transaction production uses: the projector refuses to run outside one, because it
        // writes values with a statement the store runs as it is sent while its removals wait for SaveChanges.
        await using (IDbContextTransaction seed = await _database.Database
            .BeginTransactionAsync(IsolationLevel.RepeatableRead, CancellationToken.None))
        {
            await projector.ProjectAsync(Venue, _es, Resolution, SessionStart, CancellationToken.None);
            await _database.SaveChangesAsync();
            await seed.CommitAsync(CancellationToken.None);
        }

        _database.ChangeTracker.Clear();

        BarCacheService cache = new(
            _database,
            gateway,
            calendar,
            projector,
            new InstrumentRegistry(wrapped),
            new ContractDirectory(clock),
            clock,
            NullLogger<BarCacheService>.Instance,
            _telemetry);

        InstrumentResolver resolver = new(new InstrumentRegistry(wrapped), new StoreAvailabilityHolder());
        ToolGuards guards = new(wrapped);

        IndicatorTools indicators = new(
            resolver,
            _database,
            catalog,
            new IndicatorCacheService(
                _database, catalog, projector, clock, NullLogger<IndicatorCacheService>.Instance, _telemetry),
            gateway,
            guards);

        SnapshotTools snapshot = new(
            new BarTools(resolver, cache, guards, clock),
            indicators,
            new KeyLevelTools(
                resolver,
                _database,
                catalog,
                new LevelMethodCatalog(calendar),
                gateway,
                guards,
                new VolumeProfileService(_database),
                Options.Create(new KeyLevelDetectionOptions())),
            new ReferenceTools(new InstrumentRegistry(wrapped), calendar, gateway, wrapped, clock),
            new IndicatorCatalogNames(catalog),
            clock);

        return (indicators, snapshot);
    }
}
