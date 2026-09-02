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
/// The served half of the resolution guard suite — the resolution it lets <b>through</b> (gh#387).
/// </summary>
/// <remarks>
/// <para>
/// <c>MarqSpec.Mcp.TopstepX.Tests.Tools.ResolutionGuardTests</c> is a suite about refusal: a non-positive
/// <c>resolutionMinutes</c>, one past the ceiling, a mixed set, a count whose look-back reaches before the
/// calendar. None of those reaches a store — the value is judged at the tool boundary, before the first
/// venue page and before the first write — so every one of them stays in the unit tier, container-free. This
/// case is the opposite claim, that a resolution which is <i>fine</i> still answers, and the only way to make
/// it is to <b>serve</b> the read.
/// </para>
/// <para>
/// Serving it executes the write path: the <c>ON CONFLICT … DO UPDATE</c> bar upsert (<c>UpsertBarsSql</c>)
/// and the coverage ledger write (<c>RecordCoverageSql</c>), inside the unit of work's <c>RepeatableRead</c>
/// transaction. The unit tier used to run all three against <c>Microsoft.EntityFrameworkCore.InMemory</c>,
/// which has none of them, at the price of a <i>second</i> implementation of every write kept in product
/// code solely to serve that provider. Those are deleted, so the read now has exactly one store it can be
/// answered from — see <see cref="SeriesStoreFixture"/>.
/// </para>
/// <para>
/// <b>The two halves are one suite and read as one.</b> A guard proven only to refuse is a guard nobody has
/// checked for over-reach, so the refusing class upstairs names this one and this one names it back rather
/// than letting the served case look deleted.
/// </para>
/// </remarks>
[Collection(SeriesStoreCollection.Name)]
public sealed class ResolutionGuardServedReadTests : IAsyncLifetime
{
    private const string Contract = "CON.F.US.EP.Z26";
    private const int SeededBars = 40;

    private readonly SeriesStoreFixture _fixture;
    private readonly TopstepXDbContext _database;
    private readonly BarTools _bars;

    /// <summary>Builds the store context and the bar tools the served read goes through.</summary>
    /// <param name="fixture">The shared container.</param>
    public ResolutionGuardServedReadTests(SeriesStoreFixture fixture)
    {
        _fixture = fixture;
        _database = fixture.CreateContext();

        IOptions<MarketDataOptions> options = Options.Create(new MarketDataOptions
        {
            Instruments = "ES,NQ",
            MaxRows = 5_000,
            SessionCloseCentral = "16:00",
        });

        BarSessionCalendar calendar = BarSessionCalendar.Parse("16:00", []);
        IndicatorCatalog catalog = new(
            Options.Create(new IndicatorOptions { AtrPeriod = 3, RsiPeriod = 3 }), calendar);
        FakeTimeProvider clock = new(Bucket(SeededBars).AddHours(2));
        CountingGateway gateway = new([]);

        IndicatorProjector projector = new(_database, catalog, NullLogger<IndicatorProjector>.Instance);
        BarCacheService cache = new(
            _database, gateway, calendar, projector, clock, NullLogger<BarCacheService>.Instance);

        // Only the bar tools, unlike the unit-tier fixture. That one builds all six tool types because its
        // reflection sweep can land on any of them; the single test here drives get_latest_bars and nothing
        // else, and standing the other five up against a real store would buy nothing.
        InstrumentResolver resolver = new(new InstrumentRegistry(options), new StoreAvailabilityHolder());

        _bars = new BarTools(resolver, cache, new ToolGuards(options), clock);
    }

    private static DateTimeOffset SessionStart =>
        MarketClock.FromMarket(new DateOnly(2026, 8, 18), new TimeOnly(9, 0)).ToUniversalTime();

    private static DateTimeOffset Bucket(int index) => SessionStart.AddMinutes(5 * index);

    /// <inheritdoc />
    /// <remarks>
    /// <b>Empty the store first, then seed it.</b> The unit-tier fixture seeded in its constructor because
    /// <c>UseInMemoryDatabase(Guid.NewGuid())</c> handed it a store nobody else could have written to. Here
    /// the container is shared, so the emptying has to happen first — and xUnit runs the constructor before
    /// this, so a seed left up there would be truncated away before the test ever saw it.
    /// </remarks>
    public async Task InitializeAsync()
    {
        await _fixture.ResetAsync();

        for (int i = 0; i < SeededBars; i++)
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
    }

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        _database.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task AValidResolution_StillAnswers()
    {
        // The other half of the acceptance criterion: nothing changes for a resolution that is fine.
        ToolPayloads.BarSeries series =
            await _bars.GetLatestBars("ES", 5, 10, CancellationToken.None);

        series.ResolutionMinutes.Should().Be(5);
        series.Bars.Should().HaveCount(10, "forty five-minute bars were seeded and ten were asked for");
    }
}
