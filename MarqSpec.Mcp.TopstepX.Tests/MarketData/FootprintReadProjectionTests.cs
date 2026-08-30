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
using ModelContextProtocol;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// A covered tape with no stored cells is projected on the next footprint or volume-profile
/// read, without the venue (gh#366).
/// </summary>
/// <remarks>
/// The trigger is on-read, the same shape as <see cref="IndicatorCacheService"/> (ADR-0014).
/// Ingest is not taken: <c>TradeTapeRecorder</c> still writes <c>Trades</c> only.
/// The venue is unreachable from this path by construction — the cache service takes no gateway.
/// </remarks>
public sealed class FootprintReadProjectionTests : IDisposable
{
    private const string Venue = "test";
    private const string Front = "CON.F.US.EP.U26";
    private const string Next = "CON.F.US.EP.Z26";
    private const int FiveMinutes = 5;

    private static readonly InstrumentId _es = new("ES");

    private static readonly DateTimeOffset _fourteen = new(2026, 8, 18, 14, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _sixteen = new(2026, 8, 18, 16, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _bucket1430 = new(2026, 8, 18, 14, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _recorded = new(2026, 8, 18, 15, 0, 0, TimeSpan.Zero);

    private readonly TopstepXDbContext _database = new(
        new DbContextOptionsBuilder<TopstepXDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private readonly FakeTimeProvider _clock = new(_sixteen);
    private readonly TapeAvailabilityHolder _tape = new();
    private readonly CountingGateway _gateway = new([]);

    public FootprintReadProjectionTests() => _tape.Set(TapeAvailability.Listening());

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task CoveredTapeWithNoCells_ServesFootprintCellsOnTheRead()
    {
        // 2 + 3 = 5 buy at 5000 in 14:30; 4 sell at 5000.25. Coverage overlaps; no cells yet.
        await SeedTapeAsync(
            Trade(_bucket1430.AddMinutes(1), 1, 5000.00m, 2, TradeDirection.Buy),
            Trade(_bucket1430.AddMinutes(2), 2, 5000.00m, 3, TradeDirection.Buy),
            Trade(_bucket1430.AddMinutes(3), 3, 5000.25m, 4, TradeDirection.Sell));

        _gateway.ResetCounters();

        ToolPayloads.FootprintSeries payload = await Tools().GetFootprint(
            "ES", FiveMinutes, _fourteen, _sixteen, CancellationToken.None);

        payload.Cells.Should().BeEquivalentTo(
        [
            new ToolPayloads.FootprintCellPoint(_bucket1430, 5000.00m, 5, 0),
            new ToolPayloads.FootprintCellPoint(_bucket1430, 5000.25m, 0, 4),
        ]);
        _gateway.BarRequests.Should().Be(0, "the tape is already stored; nothing may be fetched");
    }

    [Fact]
    public async Task CoveredTapeWithNoCells_ServesAVolumeProfileOnTheRead()
    {
        await SeedTapeAsync(
            Trade(_bucket1430.AddMinutes(1), 1, 5000.00m, 2, TradeDirection.Buy),
            Trade(_bucket1430.AddMinutes(2), 2, 5000.00m, 3, TradeDirection.Buy),
            Trade(_bucket1430.AddMinutes(3), 3, 5000.25m, 4, TradeDirection.Sell));

        ToolPayloads.VolumeProfileSeries payload = await Tools().GetVolumeProfile(
            "ES", FiveMinutes, _fourteen, _sixteen, CancellationToken.None);

        payload.PointOfControl.Should().Be(5000.00m);
        payload.TotalVolume.Should().Be(9);
        payload.ByPrice.Should().BeEquivalentTo(
        [
            new ToolPayloads.VolumeAtPricePoint(5000.00m, 5),
            new ToolPayloads.VolumeAtPricePoint(5000.25m, 4),
        ]);
    }

    [Fact]
    public async Task ASecondPassOverTheSameTape_IsAnEmptyDiff()
    {
        await SeedTapeAsync(
            Trade(_bucket1430.AddMinutes(1), 1, 5000m, 2, TradeDirection.Buy),
            Trade(_bucket1430.AddMinutes(2), 2, 5000.25m, 4, TradeDirection.Sell));

        await Tools().GetFootprint("ES", FiveMinutes, _fourteen, _sixteen, CancellationToken.None);

        DateTimeOffset firstStamp = await _database.FootprintCells
            .Select(c => c.RecordedAt)
            .Distinct()
            .SingleAsync();

        _clock.Advance(TimeSpan.FromHours(1));

        int changed = await new FootprintProjector(_database, NullLogger<FootprintProjector>.Instance)
            .ProjectAsync(Venue, _es, FiveMinutes, _clock.GetUtcNow(), CancellationToken.None);
        await _database.SaveChangesAsync();

        changed.Should().Be(0, "a replay over the same tape reproduces the same numbers, so it rewrites none");

        List<DateTimeOffset> stamps = await _database.FootprintCells
            .Select(c => c.RecordedAt)
            .Distinct()
            .ToListAsync();

        stamps.Should().ContainSingle().Which.Should().Be(firstStamp);
    }

    [Fact]
    public async Task ABucketWhoseCountedPrintsSpanTwoContracts_ProducesNoCell()
    {
        // 10 buy on the front month and 5 sell on the next, same 5-minute window, same price.
        // Merging them would report 10/5 as the bar's footprint (ADR-0011).
        await SeedTapeAsync(
            Trade(_bucket1430.AddMinutes(1), 1, 5000m, 10, TradeDirection.Buy, Front),
            Trade(_bucket1430.AddMinutes(2), 2, 5000m, 5, TradeDirection.Sell, Next));

        Func<Task> footprint = () => Tools().GetFootprint(
            "ES", FiveMinutes, _fourteen, _sixteen, CancellationToken.None);
        Func<Task> profile = () => Tools().GetVolumeProfile(
            "ES", FiveMinutes, _fourteen, _sixteen, CancellationToken.None);

        await footprint.Should().ThrowAsync<McpException>();
        await profile.Should().ThrowAsync<McpException>();

        (await _database.FootprintCells.CountAsync()).Should().Be(0,
            "a spliced bucket must produce no cell — an empty list would look like a quiet market, "
            + "and a merged 10/5 would be a wrong number");
    }

    private MarketDataTools Tools()
    {
        IOptions<MarketDataOptions> options = Options.Create(new MarketDataOptions
        {
            Instruments = "ES,NQ",
            MaxRows = 5_000,
            SessionCloseCentral = "16:00",
        });

        BarSessionCalendar calendar = BarSessionCalendar.Parse("16:00", []);
        IndicatorCatalog catalog = new(
            Options.Create(new IndicatorOptions { AtrPeriod = 3, RsiPeriod = 3 }), calendar);
        IndicatorProjector projector = new(_database, catalog, NullLogger<IndicatorProjector>.Instance);

        return new MarketDataTools(
            new BarCacheService(
                _database, _gateway, calendar, projector, _clock, NullLogger<BarCacheService>.Instance),
            _database,
            new InstrumentRegistry(options),
            catalog,
            new IndicatorCacheService(
                _database, catalog, projector, _clock, NullLogger<IndicatorCacheService>.Instance),
            new LevelMethodCatalog(calendar),
            _gateway,
            new ToolGuards(options),
            new StoreAvailabilityHolder(),
            _clock,
            Options.Create(new KeyLevelDetectionOptions()),
            new VolumeProfileService(_database),
            _tape);
    }

    private async Task SeedTapeAsync(params TradeRecord[] trades)
    {
        _database.Trades.AddRange(trades);
        _database.TapeCoverage.Add(new TapeCoverageRecord
        {
            Venue = Venue,
            Instrument = _es.Symbol,
            ContractId = Front,
            RangeStart = _fourteen,
            RangeEnd = _sixteen,
            RecordedAt = _recorded,
        });
        await _database.SaveChangesAsync();
    }

    private static TradeRecord Trade(
        DateTimeOffset when,
        long sequence,
        decimal price,
        long size,
        TradeDirection direction,
        string contractId = Front) => new()
        {
            Venue = Venue,
            Instrument = _es.Symbol,
            ContractId = contractId,
            TradeTimeUtc = when,
            Sequence = sequence,
            Price = price,
            Size = size,
            Direction = direction,
            RecordedAt = _recorded,
        };
}
