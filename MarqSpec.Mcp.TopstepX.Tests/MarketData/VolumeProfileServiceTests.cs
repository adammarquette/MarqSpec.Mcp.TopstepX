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
    public async Task ReadCells_WhenThisProcessIsNotRecording_ServesClosedCoverage()
    {
        // The store tier already serves a closed run when this process is not the recorder.
        // The still-open sentinel is stored beside it and must not extend the window (gh#728).
        DateTimeOffset runStart = new(2026, 10, 16, 13, 30, 0, TimeSpan.Zero);
        DateTimeOffset runEnd = new(2026, 10, 16, 14, 30, 0, TimeSpan.Zero);
        DateTimeOffset askedEnd = new(2026, 10, 16, 15, 30, 0, TimeSpan.Zero);
        TapeAvailabilityHolder tape = new();
        tape.Set(TapeAvailability.NeverStartedBecauseStdio());

        await SeedCoverageAsync(
            Coverage(Next, runStart, runEnd),
            Coverage(Next, runEnd, TapeCoverageRecord.StillListeningEnd));
        await SeedCellsAsync(CellsAcross(runStart));

        FootprintRead read = await new VolumeProfileService(_database, tape).ReadCellsAsync(
            Venue, _es, FiveMinutes, runStart, askedEnd, CancellationToken.None);

        read.Cells.Should().HaveCount(24);
        read.Window.Start.Should().Be(runStart);
        read.Window.End.Should().Be(runEnd);
        read.Window.End.Should().NotBe(askedEnd);
        read.Window.Narrowed.Should().BeTrue();
    }

    [Fact]
    public async Task ReadCells_WhenTheCallerPassesHealth_IgnoresAListeningHolder()
    {
        // The holder says this process is listening, which would keep the sentinel and run
        // the window to the ask. The status passed for the call says it is not. That snapshot
        // wins, or the live flag and the window are two different reads.
        TapeAvailabilityHolder listening = new();
        listening.Set(TapeAvailability.Listening());

        await SeedCoverageAsync(
            Coverage(Next, _fourteen, _sixteen),
            Coverage(Next, _sixteen, TapeCoverageRecord.StillListeningEnd));
        await SeedCellsAsync(Cell(_bucket1430, 5000m, buy: 4, sell: 1));

        FootprintRead read = await new VolumeProfileService(_database, listening).ReadCellsAsync(
            Venue,
            _es,
            FiveMinutes,
            _fourteen,
            _sixteen.AddHours(2),
            CancellationToken.None,
            TapeAvailability.NeverStartedBecauseStdio());

        read.Window.End.Should().Be(_sixteen);
    }

    [Fact]
    public async Task AWindowWithNoTape_Refuses()
    {
        await SeedCellsAsync(Cell(_bucket1430, 5000m, buy: 4, sell: 1));

        Func<Task> read = () => Service().ReadAsync(
            Venue, _es, FiveMinutes, _ten, _sixteen, CancellationToken.None);

        await read.Should().ThrowAsync<NoClosedTapeException>().WithMessage("*no tape*");
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

    [Fact]
    public async Task AggregateClosedCoverage_KeepsAPrintAtTheAlignedStart_AndDropsOneJustBeforeIt()
    {
        // 14:33 on 5-minute bars floors to 14:30. The print at that open counts. One tick
        // earlier is the previous bar. There is no overlap filter on this path, so removing
        // or widening >= window.Start returns that tick, and narrowing it to > drops the open.
        DateTimeOffset askStart = _fourteen.AddMinutes(33);
        DateTimeOffset aligned = _fourteen.AddMinutes(30);
        await SeedCoverageAsync(Coverage(Next, _fourteen, _sixteen));
        await SeedTradesAsync(
            Trade(aligned, 1, Next, 10),
            Trade(aligned.AddTicks(-1), 2, Next, 7));

        FootprintRead read = await Aggregate(askStart, _sixteen);

        read.Window.Start.Should().Be(aligned);
        read.Window.End.Should().Be(_sixteen);
        read.Window.Narrowed.Should().BeFalse();
        read.Cells.Should().ContainSingle()
            .Which.Should().Be(new FootprintCell(_es.Symbol, FiveMinutes, aligned, 5000m, 10, 0));
    }

    [Fact]
    public async Task AggregateClosedCoverage_DropsAPrintAtTheWindowEnd()
    {
        // 16:02 sits inside the 16:00 bar, so the print at that instant would survive an
        // overlap filter. Widening < window.End to <= keeps it. The end is not floored.
        DateTimeOffset end = _sixteen.AddMinutes(2);
        await SeedCoverageAsync(Coverage(Next, _fourteen, _sixteen.AddHours(2)));
        await SeedTradesAsync(
            Trade(_sixteen.AddMinutes(-2), 1, Next, 10),
            Trade(end, 2, Next, 3));

        FootprintRead read = await Aggregate(_fourteen, end);

        read.Window.End.Should().Be(end);
        read.Cells.Should().ContainSingle()
            .Which.Should().Be(new FootprintCell(
                _es.Symbol, FiveMinutes, _sixteen.AddMinutes(-5), 5000m, 10, 0));
    }

    [Fact]
    public async Task AggregateClosedCoverage_DropsAPrintAfterTheWindowEnd()
    {
        // One minute past 16:02 is still the 16:00 bar. <= window.End does not admit it.
        // Removing the upper bound does.
        DateTimeOffset end = _sixteen.AddMinutes(2);
        await SeedCoverageAsync(Coverage(Next, _fourteen, _sixteen.AddHours(2)));
        await SeedTradesAsync(
            Trade(_sixteen.AddMinutes(-2), 1, Next, 10),
            Trade(end.AddMinutes(1), 2, Next, 4));

        FootprintRead read = await Aggregate(_fourteen, end);

        read.Window.End.Should().Be(end);
        read.Cells.Should().ContainSingle()
            .Which.Should().Be(new FootprintCell(
                _es.Symbol, FiveMinutes, _sixteen.AddMinutes(-5), 5000m, 10, 0));
    }

    [Fact]
    public async Task AggregateClosedCoverage_DropsAPrintOnAnotherContract()
    {
        // Same bucket as the kept print. A mixed bucket produces no cell, so removing the
        // contract filter loses the baseline instead of adding the other size.
        await SeedCoverageAsync(Coverage(Next, _fourteen, _sixteen));
        await SeedTradesAsync(
            Trade(_bucket1430.AddMinutes(1), 1, Next, 10),
            Trade(_bucket1430.AddMinutes(2), 2, Front, 99));

        FootprintRead read = await Aggregate(_fourteen, _sixteen);

        read.Window.ContractId.Should().Be(Next);
        read.Cells.Should().ContainSingle()
            .Which.Should().Be(new FootprintCell(_es.Symbol, FiveMinutes, _bucket1430, 5000m, 10, 0));
    }

    [Fact]
    public async Task ReadCellsAndAggregateClosedCoverage_WhenTheAskStartsInsideABar_ReturnTheSameCompleteBar()
    {
        // 14:31 and 14:34 are one 14:30 bar. The stored cell is that whole bar. A clipped
        // in-memory read would keep only the 14:34 print, and an unaligned report would
        // say the window opens at 14:33.
        DateTimeOffset askStart = _fourteen.AddMinutes(33);
        DateTimeOffset bar = _fourteen.AddMinutes(30);
        await SeedEdgeBarAsync(bar);

        FootprintRead stored = await ReadStored(askStart, _sixteen);
        FootprintRead tape = await Aggregate(askStart, _sixteen);

        stored.Window.Start.Should().Be(bar);
        stored.Window.End.Should().Be(_sixteen);
        stored.Window.Narrowed.Should().BeFalse();
        stored.Cells.Should().ContainSingle()
            .Which.Should().Be(new FootprintCell(_es.Symbol, FiveMinutes, bar, 5000m, 30, 0));
        tape.Should().BeEquivalentTo(stored);
    }

    [Fact]
    public async Task ReadCellsAndAggregateClosedCoverage_WhenTheAskStartsOnABar_ReturnTheSameCells()
    {
        DateTimeOffset bar = _fourteen.AddMinutes(30);
        await SeedEdgeBarAsync(bar);

        FootprintRead stored = await ReadStored(bar, _sixteen);
        FootprintRead tape = await Aggregate(bar, _sixteen);

        stored.Window.Start.Should().Be(bar);
        stored.Window.Narrowed.Should().BeFalse();
        tape.Should().BeEquivalentTo(stored);
    }

    private async Task SeedEdgeBarAsync(DateTimeOffset bar)
    {
        await SeedCoverageAsync(Coverage(Next, _fourteen, _sixteen));
        await SeedTradesAsync(
            Trade(bar.AddMinutes(1), 1, Next, 10),
            Trade(bar.AddMinutes(4), 2, Next, 20));
        await SeedCellsAsync(
            Cell(bar.AddMinutes(-5), 5000m, buy: 100, sell: 0),
            Cell(bar, 5000m, buy: 30, sell: 0));
    }

    private Task<FootprintRead> Aggregate(DateTimeOffset start, DateTimeOffset end) =>
        new VolumeProfileService(_database).AggregateClosedCoverageAsync(
            Venue,
            _es,
            FiveMinutes,
            start,
            end,
            CancellationToken.None,
            TapeAvailability.NeverStartedBecauseStdio());

    private Task<FootprintRead> ReadStored(DateTimeOffset start, DateTimeOffset end) =>
        new VolumeProfileService(_database).ReadCellsAsync(
            Venue,
            _es,
            FiveMinutes,
            start,
            end,
            CancellationToken.None,
            TapeAvailability.Listening());

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

    private async Task SeedTradesAsync(params TradeRecord[] trades)
    {
        _database.Trades.AddRange(trades);
        await _database.SaveChangesAsync();
    }

    private static TradeRecord Trade(
        DateTimeOffset when,
        long sequence,
        string contractId,
        long size) => new()
        {
            Venue = Venue,
            Instrument = _es.Symbol,
            ContractId = contractId,
            TradeTimeUtc = when,
            Sequence = sequence,
            Price = 5000m,
            Size = size,
            Direction = TradeDirection.Buy,
            RecordedAt = _recorded,
        };

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

    /// <summary>
    /// Twelve 5-minute buckets, two prices each. 6800.00 carries the volume, so a profile over
    /// these cells has its point of control there.
    /// </summary>
    private static FootprintCellRecord[] CellsAcross(DateTimeOffset runStart)
    {
        FootprintCellRecord[] cells = new FootprintCellRecord[24];
        for (int bucket = 0; bucket < 12; bucket++)
        {
            DateTimeOffset open = runStart.AddMinutes(FiveMinutes * bucket);
            cells[bucket * 2] = Cell(open, 6800.00m, buy: 10, sell: 0);
            cells[(bucket * 2) + 1] = Cell(open, 6800.25m, buy: 1, sell: 0);
        }

        return cells;
    }
}
