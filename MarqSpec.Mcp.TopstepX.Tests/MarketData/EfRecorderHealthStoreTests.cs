using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// The store read behind the recorder report. The gap rules are pinned against a scripted store; this pins
/// the query, so a reverted <c>GROUP BY</c> or window filter fails here on its own (gh#756).
/// </summary>
public sealed class EfRecorderHealthStoreTests : IDisposable
{
    private const string Venue = "projectx";
    private const string Es = "CON.F.US.EP.U26";
    private const string EsJune = "CON.F.US.EP.M26";
    private static readonly string[] _es = ["ES"];

    private static readonly DateTimeOffset _tuesday = At(2026, 8, 18, 9, 30);
    private static readonly DateTimeOffset _open = At(2026, 8, 17, 17, 0);
    private static readonly DateTimeOffset _recorded = At(2026, 8, 18, 9, 30);

    private readonly TopstepXDbContext _database = new(
        new DbContextOptionsBuilder<TopstepXDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task LastTicksAsync_WhenTwoPrintsExist_ReturnsTheLaterOne()
    {
        DateTimeOffset earlier = At(2026, 8, 18, 9, 0);
        DateTimeOffset later = At(2026, 8, 18, 9, 20);
        await PrintAsync(earlier, 1);
        await PrintAsync(later, 2);

        IReadOnlyList<ContractLastTick> ticks = await new EfRecorderHealthStore(_database)
            .LastTicksAsync(_es, _open, _tuesday, CancellationToken.None);

        ticks.Should().ContainSingle()
            .Which.Should().Be(new ContractLastTick("ES", Es, later));
    }

    [Fact]
    public async Task CoverageAsync_WhenARangeOverlapsTheWindow_ReturnsThatRange()
    {
        await CoverAsync(_open, At(2026, 8, 18, 9, 0));

        IReadOnlyList<CoverageSlice> slices = await new EfRecorderHealthStore(_database)
            .CoverageAsync(_es, _open, _tuesday, CancellationToken.None);

        slices.Should().ContainSingle()
            .Which.Should().Be(new CoverageSlice("ES", Es, _open, At(2026, 8, 18, 9, 0)));
    }

    [Fact]
    public async Task CoverageAsync_WhenARangeMissesTheWindow_OmitsThatRange()
    {
        await CoverAsync(At(2026, 8, 17, 10, 0), _open);

        IReadOnlyList<CoverageSlice> slices = await new EfRecorderHealthStore(_database)
            .CoverageAsync(_es, _open, _tuesday, CancellationToken.None);

        slices.Should().BeEmpty();
    }

    [Fact]
    public async Task LastTicksAsync_WhenAPrintFallsOutsideTheWindow_OmitsThatContract()
    {
        await PrintAsync(At(2026, 6, 15, 10, 0), 1, EsJune);
        await PrintAsync(_tuesday, 2, Es);

        IReadOnlyList<ContractLastTick> ticks = await new EfRecorderHealthStore(_database)
            .LastTicksAsync(_es, _open, _tuesday, CancellationToken.None);

        ticks.Should().ContainSingle()
            .Which.ContractId.Should().Be(Es);
    }

    [Fact]
    public async Task CoverageAsync_WhenTheInstrumentIsNotRequested_OmitsThatRange()
    {
        await CoverAsync(_open, _tuesday, "RTY", "CON.F.US.RTY.U26");

        IReadOnlyList<CoverageSlice> slices = await new EfRecorderHealthStore(_database)
            .CoverageAsync(_es, _open, _tuesday, CancellationToken.None);

        slices.Should().BeEmpty();
    }

    [Fact]
    public void Trades_IndexesInstrumentThenTradeTime()
    {
        string? columns = _database.Model.FindEntityType(typeof(TradeRecord))!
            .GetIndexes()
            .Select(index => string.Join(",", index.Properties.Select(property => property.Name)))
            .SingleOrDefault(shape => shape == "Instrument,TradeTimeUtc");

        columns.Should().Be("Instrument,TradeTimeUtc");
    }

    [Fact]
    public async Task DatabaseConnectedAsync_WhenTheInMemoryStoreIsOpen_ReturnsTrue()
    {
        bool connected = await new EfRecorderHealthStore(_database)
            .DatabaseConnectedAsync(CancellationToken.None);

        connected.Should().BeTrue();
    }

    private async Task PrintAsync(DateTimeOffset when, long sequence, string contractId = Es)
    {
        _database.Trades.Add(new TradeRecord
        {
            Venue = Venue,
            Instrument = "ES",
            ContractId = contractId,
            TradeTimeUtc = when,
            Sequence = sequence,
            Price = 5000m,
            Size = 1,
            Direction = TradeDirection.Buy,
            RecordedAt = _recorded,
        });
        await _database.SaveChangesAsync();
    }

    private async Task CoverAsync(
        DateTimeOffset start,
        DateTimeOffset end,
        string instrument = "ES",
        string contractId = Es)
    {
        _database.TapeCoverage.Add(new TapeCoverageRecord
        {
            Venue = Venue,
            Instrument = instrument,
            ContractId = contractId,
            RangeStart = start,
            RangeEnd = end,
            RecordedAt = _recorded,
        });
        await _database.SaveChangesAsync();
    }

    private static DateTimeOffset At(int year, int month, int day, int hour, int minute) =>
        MarketClock.FromMarket(new DateOnly(year, month, day), new TimeOnly(hour, minute)).ToUniversalTime();
}
