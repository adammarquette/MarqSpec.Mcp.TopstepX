using System.Data;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// The host rebuild verb: read the tape, project per contract run, write cells, reconcile.
/// </summary>
/// <remarks>
/// <para>
/// Seeded trades, not a live hub. The aggregator's numbers are pinned in
/// <c>FootprintAggregatorTests</c>; this file pins the store-shaped claims — idempotence,
/// reconciliation, an empty tape, the Unknown refusal end to end, and that an uncounted
/// print on another contract does not delete the counted volume (gh#220).
/// </para>
/// <para>
/// <b>This tier, and only this tier.</b> Every claim here is about what the store holds after a
/// pass, so proving any of them now means executing the real <c>UpsertCellsSql</c> — an
/// <c>ON CONFLICT … DO UPDATE</c> no in-memory provider has — and reading back the rows it left.
/// The stand-in that used to serve this suite in the unit tier was a second implementation of the
/// cell write, executed by no production process, and it was deleted (gh#387). What is left runs
/// against a real Postgres or it does not run.
/// </para>
/// <para>
/// <b>The counts these tests assert are the store's own.</b> A pass now returns the rows the
/// statement reports it wrote or revised, plus the rows it removed — where the deleted stand-in
/// returned however many cells the pass had queued. The two disagreed, and the number a caller
/// reads is the store's.
/// </para>
/// </remarks>
[Collection(SeriesStoreCollection.Name)]
public sealed class FootprintProjectorTests : IAsyncLifetime
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

    private readonly SeriesStoreFixture _fixture;
    private readonly TopstepXDbContext _database;

    /// <param name="fixture">The shared container.</param>
    public FootprintProjectorTests(SeriesStoreFixture fixture)
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
        return Task.CompletedTask;
    }

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

        int written = await ProjectAsync(Projector(), _recordedFirst);

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

        await ProjectAsync(Projector(), _recordedFirst);

        FootprintCellRecord cell = (await CellsAsync()).Should().ContainSingle().Subject;
        cell.BuyVolume.Should().Be(5, "Unknown must not be counted as a buy");
        cell.SellVolume.Should().Be(0);
        cell.Price.Should().Be(5000m);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("middle")]
    [InlineData("end")]
    public async Task AnUnknownPrintOnAnotherContract_DoesNotDropTheCountedVolume(string position)
    {
        // 2 + 3 = 5 buy on U26. An Unknown of 100 on Z26 is uncounted; it must not
        // open a contract seam that MixedBuckets then refuses as a splice.
        await SeedAsync(UnknownOnAnotherContract(position));

        await ProjectAsync(Projector(), _recordedFirst);

        FootprintCellRecord cell = (await CellsAsync()).Should().ContainSingle(
            "the known tape still justifies 5; an empty series would read as a bar that did not trade")
            .Subject;
        cell.BuyVolume.Should().Be(5);
        cell.SellVolume.Should().Be(0);
        cell.Price.Should().Be(5000m);
        cell.BucketStart.Should().Be(_bucket1430);
    }

    [Fact]
    public async Task AZeroSizePrintOnAnotherContract_DoesNotDropTheCountedVolume()
    {
        // Same 2 + 3 = 5 on U26. A Sell of size 0 on Z26 is uncounted, same as Unknown.
        await SeedAsync(
            Trade(_bucket1430.AddMinutes(1), 1, 5000m, 2, TradeDirection.Buy, Front),
            Trade(_bucket1430.AddMinutes(2), 2, 5000m, 0, TradeDirection.Sell, Next),
            Trade(_bucket1430.AddMinutes(3), 3, 5000m, 3, TradeDirection.Buy, Front));

        await ProjectAsync(Projector(), _recordedFirst);

        FootprintCellRecord cell = (await CellsAsync()).Should().ContainSingle().Subject;
        cell.BuyVolume.Should().Be(5);
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

        int first = await ProjectAsync(projector, _recordedFirst);

        int second = await ProjectAsync(projector, _recordedLater);

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
        await ProjectAsync(projector, _recordedFirst);

        (await CellsAsync()).Should().HaveCount(2);

        TradeRecord removed = await _database.Trades.SingleAsync(t => t.Sequence == 2);
        _database.Trades.Remove(removed);
        await _database.SaveChangesAsync();

        int changed = await ProjectAsync(projector, _recordedLater);

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
        await ProjectAsync(projector, _recordedFirst);

        (await CellsAsync()).Should().NotBeEmpty();

        _database.Trades.RemoveRange(await _database.Trades.ToListAsync());
        await _database.SaveChangesAsync();

        await ProjectAsync(projector, _recordedLater);

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

        await ProjectAsync(Projector(), _recordedFirst);

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

        await ProjectAsync(Projector(), _recordedFirst);

        FootprintCellRecord? survivor = await _database.FootprintCells.FirstOrDefaultAsync(
            c => c.Venue == venue
                && c.Instrument == instrument
                && c.ResolutionMinutes == resolutionMinutes
                && c.Price == 42m);

        survivor.Should().NotBeNull("a projection of (test, ES, 5) has no standing over another series' rows");
        survivor!.BuyVolume.Should().Be(99);
    }

    /// <summary>
    /// Runs one pass the way the host runs it — inside the transaction the projector demands.
    /// </summary>
    /// <param name="projector">The pass to run.</param>
    /// <param name="now">The instant the pass runs at, stamped on the rows it changes.</param>
    /// <returns>How many rows the pass changed — written, updated, or removed.</returns>
    /// <remarks>
    /// <para>
    /// <c>ProjectAsync</c> refuses outright when the caller holds no transaction, and it is right to: the
    /// cells it writes leave as one statement the store runs as it is sent, while the cells the tape no
    /// longer justifies are removed through the change tracker and wait for <c>SaveChanges</c>. Outside a
    /// transaction the first commits alone, leaving cells standing that the very same pass decided to remove.
    /// </para>
    /// <para>
    /// <b>These tests never ran inside that transaction before.</b> The in-memory provider had none for the
    /// guard to find, so it was skipped and this suite drove a pass in a shape no host process uses (gh#387).
    /// <c>SeriesUnitOfWork</c> is what production wraps it in; it is internal to the host, so the
    /// level it states is restated here — <see cref="IsolationLevel.RepeatableRead"/>, the same
    /// level the concurrency suites next door open by hand for the same reason.
    /// </para>
    /// </remarks>
    private async Task<int> ProjectAsync(FootprintProjector projector, DateTimeOffset now)
    {
        await using IDbContextTransaction transaction = await _database.Database
            .BeginTransactionAsync(IsolationLevel.RepeatableRead, CancellationToken.None);

        int changed = await projector.ProjectAsync(Venue, _es, FiveMinutes, now, CancellationToken.None);
        await _database.SaveChangesAsync();
        await transaction.CommitAsync();

        return changed;
    }

    private async Task SeedAsync(params TradeRecord[] trades)
    {
        _database.Trades.AddRange(trades);
        await _database.SaveChangesAsync();
    }

    /// <summary>Every stored cell for the series, ordered.</summary>
    /// <returns>The cells.</returns>
    /// <remarks>
    /// <b><c>AsNoTracking</c>, and it is load-bearing rather than tidy (gh#387).</b> A projection pass reads
    /// the stored cells untracked and hands the unjustified ones to <c>Remove</c>, which attaches each as
    /// <c>Deleted</c>. A tracking read here puts a <i>different</i> instance of the same key in the identity
    /// map first, and the pass then throws rather than reconciling. The in-memory branch this suite used to
    /// run on read the cells <b>tracked</b>, so the two instances were one and the collision could not arise.
    /// </remarks>
    private async Task<List<FootprintCellRecord>> CellsAsync() =>
        await _database.FootprintCells
            .AsNoTracking()
            .Where(c => c.Venue == Venue && c.Instrument == _es.Symbol && c.ResolutionMinutes == FiveMinutes)
            .OrderBy(c => c.BucketStart)
            .ThenBy(c => c.Price)
            .ToListAsync();

    private static TradeRecord[] UnknownOnAnotherContract(string position) => position switch
    {
        "start" =>
        [
            Trade(_bucket1430.AddMinutes(1), 1, 5000m, 100, TradeDirection.Unknown, Next),
            Trade(_bucket1430.AddMinutes(2), 2, 5000m, 2, TradeDirection.Buy, Front),
            Trade(_bucket1430.AddMinutes(3), 3, 5000m, 3, TradeDirection.Buy, Front),
        ],
        "middle" =>
        [
            Trade(_bucket1430.AddMinutes(1), 1, 5000m, 2, TradeDirection.Buy, Front),
            Trade(_bucket1430.AddMinutes(2), 2, 5000m, 100, TradeDirection.Unknown, Next),
            Trade(_bucket1430.AddMinutes(3), 3, 5000m, 3, TradeDirection.Buy, Front),
        ],
        "end" =>
        [
            Trade(_bucket1430.AddMinutes(1), 1, 5000m, 2, TradeDirection.Buy, Front),
            Trade(_bucket1430.AddMinutes(2), 2, 5000m, 3, TradeDirection.Buy, Front),
            Trade(_bucket1430.AddMinutes(3), 3, 5000m, 100, TradeDirection.Unknown, Next),
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(position)),
    };

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
