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
/// The moment a snapshot reads its indicators at, when it has no bars to take one from (gh#268).
/// </summary>
/// <remarks>
/// <para>
/// With bars, the anchor is the last bar's timestamp and is therefore a property of the data. With none it is
/// <i>now</i> — and <c>now</c> read from the static <see cref="DateTimeOffset.UtcNow"/> made this the one path
/// on the tool surface whose answer depended on when it ran, because nothing could pin it.
/// </para>
/// <para>
/// <b>The two cases below differ in the clock and in nothing else.</b> Same store, same fixture, same
/// arguments; only the <see cref="FakeTimeProvider"/> moves, from after the stored values to before them. That
/// is what makes them evidence about the anchor rather than about the fixture — if the store or the arguments
/// varied too, a change in the answer would have somewhere else to have come from.
/// </para>
/// <para>
/// <b>The map now holds readings rather than bare numbers</b> (gh#286), so the first case also reads the
/// bucket its value came from. That is an added assertion, not a changed contrast: the two cases still take
/// the same store and the same arguments and still differ only in the clock, and each still measures which
/// stored value the anchor reaches — one reaches the last, the other reaches none.
/// </para>
/// <para>
/// The store deliberately holds indicator rows the bar window cannot reach. That is not a contrived state, it
/// is an instrument that has stopped updating: the history is still there, the look-back reaches back four
/// days plus four bar spans per bar asked for (<see cref="ToolGuards.LookbackWindow"/>) and finds nothing, and
/// the zero-bars branch fires with values still sitting behind it. It is the one state where the anchor is
/// observable at all, so it is the state these cases are built in.
/// </para>
/// </remarks>
public sealed class SnapshotClockTests : IDisposable
{
    private const string Contract = "CON.F.US.EP.U26";

    /// <summary>What ATR(3) is over this fixture.</summary>
    /// <remarks>
    /// Eight flat bars, each with a high-low range of exactly two points. Every true range is that same 2 —
    /// <c>H-L</c> is 2, and both gaps to the previous close are 1 — so the Wilder seed is its own mean and
    /// every value after it repeats. Hand-checked, and exact in <c>decimal</c>. A fixture whose expected
    /// number came out of the implementation would pass forever and prove nothing.
    /// </remarks>
    private const decimal ExpectedAtr = 2m;

    private readonly TopstepXDbContext _database;

    public SnapshotClockTests() =>
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
    public async Task ASnapshotWithNoBars_AnchorsItsIndicatorsOnTheInjectedClock()
    {
        // The counterweight, and it has to come first: it establishes that this fixture CAN produce a reading
        // at all. Without it, the null asserted in the second case would be satisfied just as well by a store
        // that was simply empty, a misspelled indicator name or a venue id matching nothing -- and that case
        // would pass while measuring none of what it claims to.
        SnapshotTools snapshot = await ComposeAsync(now: Bucket(0).AddDays(30));

        ToolPayloads.MarketSnapshot payload =
            await snapshot.GetMarketSnapshot("ES", [5], 8, CancellationToken.None);

        ToolPayloads.ResolutionSnapshot slice = payload.PerResolution.Should().ContainSingle().Subject;

        slice.Bars.Should().BeEmpty(
            "the look-back reaches four days back from a clock thirty days past the fixture, so this is the "
            + "zero-bars branch -- the one under test. A slice with bars in it is testing the other branch");

        slice.Indicators["atr"].Should().NotBeNull(
            "the anchor is now, now is after every stored value, and an as-of read takes the last one at or "
            + "before it");

        ToolPayloads.IndicatorReading atr = slice.Indicators["atr"]!;

        atr.Value.Should().Be(ExpectedAtr);

        // Added by gh#286, and it strengthens this case rather than changing what it contrasts: the inputs
        // are still the fixture and one clock. The value above is the fixture's LAST stored bucket, and this
        // clock sits thirty days past Bucket(0) -- so the number is a month old, and until the reading
        // carried its bucket nothing in the payload said so.
        atr.BucketStart.Should().Be(
            Bucket(7),
            "the newest bucket ATR(3) has a row for -- and the anchor is thirty days past Bucket(0), so this "
            + "is a month-old number arriving beside an empty bar list");
    }

    [Fact]
    public async Task ASnapshotWithNoBars_TakesNoValueFromAfterTheInjectedMoment()
    {
        // The case that goes red against DateTimeOffset.UtcNow, and the only one that can: the static clock
        // is real wall time, which is later than this fixture and later than any fixture that will ever be
        // written in the past tense, so it reads exactly the value the pinned moment must not see. "Reads are
        // as-of, never lookahead" (the Coding contract) is a rule about the anchor as much as about the
        // query, and an anchor nothing can move is one nobody can check.
        SnapshotTools snapshot = await ComposeAsync(now: Bucket(0).AddDays(-1));

        ToolPayloads.MarketSnapshot payload =
            await snapshot.GetMarketSnapshot("ES", [5], 8, CancellationToken.None);

        ToolPayloads.ResolutionSnapshot slice = payload.PerResolution.Should().ContainSingle().Subject;

        slice.Bars.Should().BeEmpty("the fixture is a day in this clock's future, so the same branch fires");

        slice.Indicators.Should().ContainKey(
            "atr",
            "every indicator this server computes keeps its key -- an absent key would say the server does "
            + "not compute it, which is a different fact from cannot measure");

        slice.Indicators["atr"].Should().BeNull(
            "nothing had been computed a day before the first bar, and a missing number is missing rather "
            + "than the next one along");
    }

    /// <summary>
    /// Seeds the store, projects the indicators over it, and composes the snapshot around one clock.
    /// </summary>
    /// <param name="now">The moment every part of the composition agrees is <i>now</i>.</param>
    /// <returns>The snapshot tool.</returns>
    /// <remarks>
    /// One <see cref="FakeTimeProvider"/> is shared by the cache, the market-data tool, the reference tool and
    /// the snapshot, because that is how the container wires it — a test that handed the snapshot a clock of
    /// its own could pass with the composition root still giving the real one to everything else.
    /// </remarks>
    private async Task<SnapshotTools> ComposeAsync(DateTimeOffset now)
    {
        for (int i = 0; i < 8; i++)
        {
            _database.Bars.Add(new BarRecord
            {
                Venue = "test",
                Instrument = "ES",
                ResolutionMinutes = 5,
                BucketStart = Bucket(i),
                Open = 100m,
                High = 101m,
                Low = 99m,
                Close = 100m,
                Volume = 1_000,
                ContractId = Contract,
                RecordedAt = SessionStart,
            });
        }

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
        FakeTimeProvider clock = new(now);

        // Serves nothing. The window each case reads is empty of bars in the store AND at the venue, which is
        // what puts the snapshot on its zero-bars branch; a gateway holding bars would fill the window and
        // take both cases off the path under test.
        CountingGateway gateway = new([]);

        IndicatorProjector projector = new(_database, catalog, NullLogger<IndicatorProjector>.Instance);
        await projector.ProjectAsync("test", new InstrumentId("ES"), 5, SessionStart, CancellationToken.None);
        await _database.SaveChangesAsync();

        BarCacheService cache = new(
            _database, gateway, calendar, projector, clock, NullLogger<BarCacheService>.Instance);

        MarketDataTools marketData = new(
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
            Options.Create(new KeyLevelDetectionOptions()));

        ReferenceTools reference = new(
            new InstrumentRegistry(wrapped), calendar, gateway, wrapped, clock);

        return new SnapshotTools(marketData, reference, new IndicatorCatalogNames(catalog), clock);
    }
}
