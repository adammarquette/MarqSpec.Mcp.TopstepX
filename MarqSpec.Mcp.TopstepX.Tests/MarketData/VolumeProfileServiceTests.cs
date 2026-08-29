using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// The host read: seed <c>TapeCoverage</c> and cells the way gh#220 proved cells on fixtures.
/// </summary>
/// <remarks>
/// The recorder is not built. These rows are written by the test, not by a hub subscription.
/// Domain numbers are pinned in <c>VolumeProfileTests</c>; this file pins the store-shaped
/// claims — the window comes from the ledger, a roll is confined, no tape refuses (gh#221).
/// </remarks>
public sealed class VolumeProfileServiceTests : IDisposable
{
    private const string Venue = "test";
    private const string Front = "CON.F.US.EP.U26";
    private const string Next = "CON.F.US.EP.Z26";
    private const int FiveMinutes = 5;

    private static readonly InstrumentId _es = new("ES");

    private static readonly DateTimeOffset _ten = new(2026, 8, 18, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _twelve = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _fourteen = new(2026, 8, 18, 14, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _sixteen = new(2026, 8, 18, 16, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _bucket1030 = new(2026, 8, 18, 10, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _bucket1430 = new(2026, 8, 18, 14, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _recorded = new(2026, 8, 18, 16, 0, 0, TimeSpan.Zero);

    private readonly TopstepXDbContext _database = new(
        new DbContextOptionsBuilder<TopstepXDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    public void Dispose() => _database.Dispose();

    private VolumeProfileService Service() => new(_database);

    [Fact]
    public async Task AWindowSpanningARoll_UsesOnlyTheFrontContractCells_AndReportsTheCoveredWindow()
    {
        // U26 printed 1000 at 100 before the roll. Z26 printed 10 at 140 after it.
        // A spliced profile would put the POC at 100 — a price Z26 never traded.
        await SeedCoverageAsync(
            Coverage(Front, _ten, _fourteen),
            Coverage(Next, _fourteen, _sixteen));
        await SeedCellsAsync(
            Cell(_bucket1030, 100m, buy: 1000, sell: 0),
            Cell(_bucket1430, 140m, buy: 10, sell: 0));

        VolumeProfileRead read = await Service().ReadAsync(
            Venue, _es, FiveMinutes, _ten, _sixteen, CancellationToken.None);

        read.Window.ContractId.Should().Be(Next);
        read.Window.Start.Should().Be(_fourteen);
        read.Window.End.Should().Be(_sixteen);
        read.Window.Narrowed.Should().BeTrue();
        read.Profile.PointOfControl.Should().Be(140m);
        read.Profile.TotalVolume.Should().Be(10);
    }

    [Fact]
    public async Task TheReportedWindow_ComesFromTapeCoverage_NotTheAsk()
    {
        await SeedCoverageAsync(Coverage(Next, _fourteen, _sixteen));
        await SeedCellsAsync(Cell(_bucket1430, 5000m, buy: 4, sell: 1));

        VolumeProfileRead read = await Service().ReadAsync(
            Venue, _es, FiveMinutes, _ten, _sixteen, CancellationToken.None);

        read.Window.Start.Should().Be(_fourteen);
        read.Window.End.Should().Be(_sixteen);
        read.Window.Narrowed.Should().BeTrue();
        read.Profile.PointOfControl.Should().Be(5000m);
        read.Profile.TotalVolume.Should().Be(5);
    }

    [Fact]
    public async Task AListeningHole_UsesOnlyTheNewestRun_AndDoesNotTakeTheMorningPoc()
    {
        // Same contract, hole at [12:00, 14:00). 1000 at 100 in the morning run would
        // steal the POC if the host still unioned both intervals under an envelope.
        await SeedCoverageAsync(
            Coverage(Next, _ten, _twelve),
            Coverage(Next, _fourteen, _sixteen));
        await SeedCellsAsync(
            Cell(_bucket1030, 100m, buy: 1000, sell: 0),
            Cell(_bucket1430, 140m, buy: 10, sell: 0));

        VolumeProfileRead read = await Service().ReadAsync(
            Venue, _es, FiveMinutes, _ten, _sixteen, CancellationToken.None);

        read.Window.Start.Should().Be(_fourteen);
        read.Window.End.Should().Be(_sixteen);
        read.Window.Narrowed.Should().BeTrue();
        read.Profile.PointOfControl.Should().Be(140m);
        read.Profile.TotalVolume.Should().Be(10);
    }

    [Fact]
    public async Task AWindowWithNoTape_Refuses()
    {
        await SeedCellsAsync(Cell(_bucket1430, 5000m, buy: 4, sell: 1));

        Func<Task> read = () => Service().ReadAsync(
            Venue, _es, FiveMinutes, _ten, _sixteen, CancellationToken.None);

        await read.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no tape*");
    }

    [Fact]
    public async Task AWindowBeforeRecordingBegan_Refuses_AndNamesTheEarliestCoveredTime()
    {
        await SeedCoverageAsync(Coverage(Next, _fourteen, _sixteen));
        await SeedCellsAsync(Cell(_bucket1430, 5000m, buy: 4, sell: 1));

        Func<Task> read = () => Service().ReadAsync(
            Venue, _es, FiveMinutes, _ten, _twelve, CancellationToken.None);

        await read.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*earliest*")
            .WithMessage("*" + _fourteen.ToString("O") + "*");
    }

    [Fact]
    public async Task CoverageWithoutCells_Refuses_RatherThanReturningAnEmptyProfile()
    {
        await SeedCoverageAsync(Coverage(Next, _fourteen, _sixteen));

        Func<Task> read = () => Service().ReadAsync(
            Venue, _es, FiveMinutes, _fourteen, _sixteen, CancellationToken.None);

        await read.Should().ThrowAsync<ArgumentException>().WithMessage("*volume*");
    }

    private async Task SeedCoverageAsync(params TapeCoverageRecord[] rows)
    {
        _database.TapeCoverage.AddRange(rows);
        await _database.SaveChangesAsync();
    }

    private async Task SeedCellsAsync(params FootprintCellRecord[] cells)
    {
        _database.FootprintCells.AddRange(cells);
        await _database.SaveChangesAsync();
    }

    private static TapeCoverageRecord Coverage(
        string contractId,
        DateTimeOffset start,
        DateTimeOffset end) => new()
        {
            Venue = Venue,
            Instrument = _es.Symbol,
            ContractId = contractId,
            RangeStart = start,
            RangeEnd = end,
            RecordedAt = _recorded,
        };

    private static FootprintCellRecord Cell(
        DateTimeOffset bucket,
        decimal price,
        long buy,
        long sell) => new()
        {
            Venue = Venue,
            Instrument = _es.Symbol,
            ResolutionMinutes = FiveMinutes,
            BucketStart = bucket,
            Price = price,
            BuyVolume = buy,
            SellVolume = sell,
            RecordedAt = _recorded,
        };
}
