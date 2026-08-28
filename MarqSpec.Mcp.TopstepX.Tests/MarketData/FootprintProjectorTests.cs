using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// The host rebuild verb: read the tape, project per contract run, write cells, reconcile.
/// </summary>
/// <remarks>
/// Seeded trades, not a live hub. The aggregator's numbers are pinned in
/// <c>FootprintAggregatorTests</c>; this file pins the store-shaped claims — idempotence,
/// reconciliation, an empty tape, and the Unknown refusal end to end (gh#220).
/// </remarks>
public sealed class FootprintProjectorTests : IDisposable
{
    private const string Venue = "test";
    private const string Front = "CON.F.US.EP.U26";
    private const string Next = "CON.F.US.EP.Z26";
    private const int FiveMinutes = 5;

    private static readonly InstrumentId _es = new("ES");

    private static readonly DateTimeOffset _bucket1430 = new(2026, 8, 18, 14, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _bucket1435 = new(2026, 8, 18, 14, 35, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _recordedFirst = new(2026, 8, 18, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _recordedLater = new(2026, 8, 18, 16, 0, 0, TimeSpan.Zero);

    private readonly TopstepXDbContext _database = new(
        new DbContextOptionsBuilder<TopstepXDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    public void Dispose() => _database.Dispose();

    private FootprintProjector Projector() =>
        new(_database, NullLogger<FootprintProjector>.Instance);

    [Fact]
    public async Task SeededPrints_WriteHandCheckedCells()
    {
        // 2 + 3 = 5 buy at 5000 in 14:30; 4 sell at 5000.25; 1 buy at 5000 in 14:35.
        await SeedAsync(
            Trade(_bucket1430.AddMinutes(1), 1, 5000.00m, 2, TradeDirection.Buy),
            Trade(_bucket1430.AddMinutes(2), 2, 5000.00m, 3, TradeDirection.Buy),
            Trade(_bucket1430.AddMinutes(3), 3, 5000.25m, 4, TradeDirection.Sell),
            Trade(_bucket1435, 4, 5000.00m, 1, TradeDirection.Buy));

        int written = await Projector().ProjectAsync(Venue, _es, FiveMinutes, _recordedFirst, CancellationToken.None);
        await _database.SaveChangesAsync();

        written.Should().Be(3);
        (await CellsAsync()).Should().BeEquivalentTo(
        [
            Cell(_bucket1430, 5000.00m, buy: 5, sell: 0, _recordedFirst),
            Cell(_bucket1430, 5000.25m, buy: 0, sell: 4, _recordedFirst),
            Cell(_bucket1435, 5000.00m, buy: 1, sell: 0, _recordedFirst),
        ]);
    }

    [Fact]
    public async Task AnUnknownPrint_DoesNotMoveTheStoredCell()
    {
        await SeedAsync(
            Trade(_bucket1430.AddMinutes(1), 1, 5000m, 2, TradeDirection.Buy),
            Trade(_bucket1430.AddMinutes(2), 2, 5000m, 3, TradeDirection.Buy),
            Trade(_bucket1430.AddMinutes(2).AddSeconds(30), 3, 5000m, 100, TradeDirection.Unknown));

        await Projector().ProjectAsync(Venue, _es, FiveMinutes, _recordedFirst, CancellationToken.None);
        await _database.SaveChangesAsync();

        FootprintCellRecord cell = (await CellsAsync()).Should().ContainSingle().Subject;
        cell.BuyVolume.Should().Be(5, "Unknown must not be counted as a buy");
        cell.SellVolume.Should().Be(0);
        cell.Price.Should().Be(5000m);
    }

    [Fact]
    public async Task AConfirmingRebuild_WritesNothing_AndLeavesRecordedAtAlone()
    {
        await SeedAsync(
            Trade(_bucket1430.AddMinutes(1), 1, 5000m, 2, TradeDirection.Buy),
            Trade(_bucket1430.AddMinutes(2), 2, 5000.25m, 4, TradeDirection.Sell));

        FootprintProjector projector = Projector();

        int first = await projector.ProjectAsync(Venue, _es, FiveMinutes, _recordedFirst, CancellationToken.None);
        await _database.SaveChangesAsync();

        int second = await projector.ProjectAsync(Venue, _es, FiveMinutes, _recordedLater, CancellationToken.None);
        await _database.SaveChangesAsync();

        first.Should().Be(2);
        second.Should().Be(0, "nothing changed, so a rebuild must produce an empty diff");

        List<DateTimeOffset> stamps = await _database.FootprintCells
            .Select(c => c.RecordedAt)
            .Distinct()
            .ToListAsync();

        stamps.Should().ContainSingle().Which.Should().Be(_recordedFirst);
    }

    [Fact]
    public async Task ASecondPassAfterAPrintIsRemoved_DropsTheUnjustifiedCell()
    {
        await SeedAsync(
            Trade(_bucket1430.AddMinutes(1), 1, 5000m, 2, TradeDirection.Buy),
            Trade(_bucket1435, 2, 5001m, 3, TradeDirection.Sell));

        FootprintProjector projector = Projector();
        await projector.ProjectAsync(Venue, _es, FiveMinutes, _recordedFirst, CancellationToken.None);
        await _database.SaveChangesAsync();

        (await CellsAsync()).Should().HaveCount(2);

        TradeRecord removed = await _database.Trades.SingleAsync(t => t.Sequence == 2);
        _database.Trades.Remove(removed);
        await _database.SaveChangesAsync();

        int changed = await projector.ProjectAsync(Venue, _es, FiveMinutes, _recordedLater, CancellationToken.None);
        await _database.SaveChangesAsync();

        changed.Should().Be(1, "the cell the removed print justified must be deleted, not left behind");

        List<FootprintCellRecord> remaining = await CellsAsync();
        remaining.Should().ContainSingle();
        remaining[0].Price.Should().Be(5000m);
        remaining[0].BuyVolume.Should().Be(2);
        remaining[0].BucketStart.Should().Be(_bucket1430);
    }

    [Fact]
    public async Task AnEmptyTape_YieldsEmptyCells_NotAFabricatedProfile()
    {
        await SeedAsync(
            Trade(_bucket1430.AddMinutes(1), 1, 5000m, 2, TradeDirection.Buy));

        FootprintProjector projector = Projector();
        await projector.ProjectAsync(Venue, _es, FiveMinutes, _recordedFirst, CancellationToken.None);
        await _database.SaveChangesAsync();

        (await CellsAsync()).Should().NotBeEmpty();

        _database.Trades.RemoveRange(await _database.Trades.ToListAsync());
        await _database.SaveChangesAsync();

        await projector.ProjectAsync(Venue, _es, FiveMinutes, _recordedLater, CancellationToken.None);
        await _database.SaveChangesAsync();

        (await CellsAsync()).Should().BeEmpty(
            "an empty tape is an absence, not a 0/0 profile at every price");
    }

    [Fact]
    public async Task ARollInsideOneBucket_ProducesNoCellForThatBucket()
    {
        // 10 buy on the front month and 5 sell on the next, same 5-minute window, same price.
        // Merging them would report 10/5 as the bar's footprint.
        await SeedAsync(
            Trade(_bucket1430.AddMinutes(1), 1, 5000m, 10, TradeDirection.Buy, Front),
            Trade(_bucket1430.AddMinutes(2), 2, 5000m, 5, TradeDirection.Sell, Next));

        await Projector().ProjectAsync(Venue, _es, FiveMinutes, _recordedFirst, CancellationToken.None);
        await _database.SaveChangesAsync();

        (await CellsAsync()).Should().BeEmpty();
    }

    [Theory]
    [InlineData("nq", 5, "test")]
    [InlineData("ES", 60, "test")]
    [InlineData("ES", 5, "other")]
    public async Task Reconciling_ReachesOnlyTheSeriesItProjected(
        string instrument,
        int resolutionMinutes,
        string venue)
    {
        await SeedAsync(
            Trade(_bucket1430.AddMinutes(1), 1, 5000m, 2, TradeDirection.Buy));

        _database.FootprintCells.Add(new FootprintCellRecord
        {
            Venue = venue,
            Instrument = instrument,
            ResolutionMinutes = resolutionMinutes,
            BucketStart = _bucket1430,
            Price = 42m,
            BuyVolume = 99,
            SellVolume = 1,
            RecordedAt = _recordedFirst,
        });
        await _database.SaveChangesAsync();

        await Projector().ProjectAsync(Venue, _es, FiveMinutes, _recordedFirst, CancellationToken.None);
        await _database.SaveChangesAsync();

        FootprintCellRecord? survivor = await _database.FootprintCells.FirstOrDefaultAsync(
            c => c.Venue == venue
                && c.Instrument == instrument
                && c.ResolutionMinutes == resolutionMinutes
                && c.Price == 42m);

        survivor.Should().NotBeNull("a projection of (test, ES, 5) has no standing over another series' rows");
        survivor!.BuyVolume.Should().Be(99);
    }

    private async Task SeedAsync(params TradeRecord[] trades)
    {
        _database.Trades.AddRange(trades);
        await _database.SaveChangesAsync();
    }

    private async Task<List<FootprintCellRecord>> CellsAsync() =>
        await _database.FootprintCells
            .Where(c => c.Venue == Venue && c.Instrument == _es.Symbol && c.ResolutionMinutes == FiveMinutes)
            .OrderBy(c => c.BucketStart)
            .ThenBy(c => c.Price)
            .ToListAsync();

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
            RecordedAt = _recordedFirst,
        };

    private static FootprintCellRecord Cell(
        DateTimeOffset bucket,
        decimal price,
        long buy,
        long sell,
        DateTimeOffset recordedAt) => new()
        {
            Venue = Venue,
            Instrument = _es.Symbol,
            ResolutionMinutes = FiveMinutes,
            BucketStart = bucket,
            Price = price,
            BuyVolume = buy,
            SellVolume = sell,
            RecordedAt = recordedAt,
        };
}
