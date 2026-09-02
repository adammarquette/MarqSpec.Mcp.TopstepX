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
/// The served half of the bucket-span guard suite — the request it lets <b>through</b> (gh#387).
/// </summary>
/// <remarks>
/// <para>
/// <c>MarqSpec.Mcp.TopstepX.Tests.Tools.BucketSpanGuardTests</c> is almost entirely a suite about refusal,
/// and a refusal never reaches a store: the span is judged at the tool boundary, nothing is fetched and
/// nothing is written. Those cases stay in the unit tier, where they cost no container and are none the
/// weaker for it. This one is the other half of the same claim — that the guard does not <i>over</i>-reject —
/// and it can only be made by <b>serving</b> an ordinary request, which fills the series for real.
/// </para>
/// <para>
/// Serving it executes the write path: the <c>ON CONFLICT … DO UPDATE</c> bar upsert (<c>UpsertBarsSql</c>)
/// and the coverage ledger write (<c>RecordCoverageSql</c>), inside the unit of work's <c>RepeatableRead</c>
/// transaction. The unit tier used to run all three against <c>Microsoft.EntityFrameworkCore.InMemory</c>,
/// which has none of them, by carrying a <i>second</i> implementation of every write in product code that
/// existed only to serve that provider. Those are deleted, so this is now the only tier where the request can
/// be answered at all — see <see cref="SeriesStoreFixture"/>.
/// </para>
/// <para>
/// <b>The two halves belong together and are meant to be read together.</b> Someone opening the guard suite
/// upstairs and finding only refusals would reasonably conclude the "does not over-reject" coverage had been
/// deleted rather than moved, which is why that class points here by name and this one points back.
/// </para>
/// </remarks>
[Collection(SeriesStoreCollection.Name)]
public sealed class BucketSpanGuardServedReadTests : IAsyncLifetime
{
    private const string Contract = "CON.F.US.EP.Z26";
    private const int SeededBars = 40;

    private readonly SeriesStoreFixture _fixture;
    private readonly TopstepXDbContext _database;
    private readonly BarCacheService _cache;
    private readonly FakeTimeProvider _clock;

    /// <summary>Builds the store context and the cache the served read is answered from.</summary>
    /// <param name="fixture">The shared container.</param>
    public BucketSpanGuardServedReadTests(SeriesStoreFixture fixture)
    {
        _fixture = fixture;
        _database = fixture.CreateContext();

        BarSessionCalendar calendar = BarSessionCalendar.Parse("16:00", []);
        IndicatorCatalog catalog = new(
            Options.Create(new IndicatorOptions { AtrPeriod = 3, RsiPeriod = 3 }), calendar);
        _clock = new FakeTimeProvider(Bucket(SeededBars).AddHours(2));
        CountingGateway gateway = new([]);

        IndicatorProjector projector = new(_database, catalog, NullLogger<IndicatorProjector>.Instance);
        _cache = new BarCacheService(
            _database, gateway, calendar, projector, _clock, NullLogger<BarCacheService>.Instance);
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
    public async Task AnOrdinaryReadStillAnswers()
    {
        // The other half of the acceptance criterion: nothing changes for a request that was always fine.
        BarTools tools = WithRowCap(5_000);

        ToolPayloads.BarSeries series = await tools.GetLatestBars("ES", 5, 10, CancellationToken.None);

        series.Bars.Should().HaveCount(10, "forty five-minute bars were seeded and ten were asked for");
    }

    /// <summary>Builds the bar tools against a given row cap.</summary>
    /// <param name="maxRows">The cap to build against.</param>
    /// <returns>The tools.</returns>
    /// <remarks>
    /// The unit-tier fixture reaches this through a <c>Family</c> record, because its reflection sweep has to
    /// map whatever declaring type it lands on to an instance. Nothing here sweeps — one served read through
    /// <c>get_latest_bars</c> is the whole class — so this composes the single tool type that read drives and
    /// stops, rather than standing up five unrelated ones against a real store.
    /// </remarks>
    private BarTools WithRowCap(int maxRows)
    {
        IOptions<MarketDataOptions> capped = Options.Create(new MarketDataOptions
        {
            Instruments = "ES,NQ",
            MaxRows = maxRows,
            SessionCloseCentral = "16:00",
        });

        return new BarTools(
            new InstrumentResolver(new InstrumentRegistry(capped), new StoreAvailabilityHolder()),
            _cache,
            new ToolGuards(capped),
            _clock);
    }
}
