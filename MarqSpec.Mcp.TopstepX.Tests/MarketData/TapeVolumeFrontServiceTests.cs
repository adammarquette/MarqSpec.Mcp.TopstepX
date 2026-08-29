using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// The host read: seeded <c>Trades</c> plus a gateway double. The Domain numbers are
/// pinned in <c>TapeVolumeFrontTests</c>; this file pins the two-source claim (gh#219).
/// </summary>
/// <remarks>
/// The recorder is not built and is not edited. Both contracts are written by the test.
/// A test that never creates two contracts cannot prove a roll or a disagreement.
/// </remarks>
public sealed class TapeVolumeFrontServiceTests : IDisposable
{
    private const string Venue = "test";
    private const string Front = "CON.F.US.EP.U26";
    private const string Next = "CON.F.US.EP.Z26";

    private static readonly InstrumentId _es = new("ES");
    private static readonly BarSessionCalendar _calendar = BarSessionCalendar.Parse("16:00", []);
    private static readonly DateTimeOffset _recorded = new(2026, 8, 18, 16, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly _wednesday = new(2026, 8, 19);

    private readonly TopstepXDbContext _database = new(
        new DbContextOptionsBuilder<TopstepXDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task GatewaySaysA_TapeVolumeSaysB_BothAnswersArePresent()
    {
        // Bars would take contracts[0] — U26, marked active. The tape's Wednesday
        // session is Z26 80 to U26 10. Dropping either answer is the defect.
        await SeedAsync(
            Trade(Central(2026, 8, 19, 10, 0), 1, Front, 10, TradeDirection.Buy),
            Trade(Central(2026, 8, 19, 10, 1), 2, Next, 80, TradeDirection.Unknown));

        TapeVolumeFrontRead read = await Service(Gateway(Front, Next)).ReadAsync(_es, CancellationToken.None);

        read.Tape.ActiveContractId.Should().Be(Next);
        read.GatewaySelectedContractId.Should().Be(Front);
        read.GatewayMarkedActiveContractIds.Should().Equal(Front, Next);
        read.Agree.Should().BeFalse();
        read.Used.Should().Be(TapeVolumeFrontRead.UsedTapeVolume);
        read.Why.Should().Contain(Next);
        read.Why.Should().Contain(Front);
        read.Why.Should().Contain("Bars");
        read.Why.Should().Contain("tape");
    }

    [Fact]
    public async Task ARoll_LeavesBothContractsInTheTape()
    {
        // Choosing the front is a read. Filtering at ingest would delete the off-front
        // prints that prove the choice. The service must not write the tape either.
        await SeedAsync(
            Trade(Central(2026, 8, 18, 10, 0), 1, Front, 100, TradeDirection.Buy),
            Trade(Central(2026, 8, 18, 11, 0), 2, Next, 20, TradeDirection.Sell),
            Trade(Central(2026, 8, 19, 10, 0), 3, Front, 30, TradeDirection.Buy),
            Trade(Central(2026, 8, 19, 10, 5), 4, Next, 80, TradeDirection.Buy));

        TapeVolumeFrontRead read = await Service(Gateway(Front)).ReadAsync(_es, CancellationToken.None);

        read.Tape.ActiveContractId.Should().Be(Next);
        read.Tape.Changeover!.SessionDate.Should().Be(_wednesday);
        read.Tape.Changeover.FromContractId.Should().Be(Front);
        read.Tape.Changeover.ToContractId.Should().Be(Next);

        List<string> remaining = await _database.Trades
            .Select(t => t.ContractId)
            .Distinct()
            .OrderBy(id => id)
            .ToListAsync();

        remaining.Should().Equal(Front, Next);
        read.Tape.SessionVolumes.Select(v => v.ContractId).Distinct()
            .Should().BeEquivalentTo([Front, Next]);
    }

    [Fact]
    public async Task WhenTheyAgree_AgreeIsTrue_AndBothAnswersAreStillPresent()
    {
        await SeedAsync(
            Trade(Central(2026, 8, 18, 10, 0), 1, Front, 10, TradeDirection.Buy),
            Trade(Central(2026, 8, 18, 10, 1), 2, Next, 40, TradeDirection.Sell));

        TapeVolumeFrontRead read = await Service(Gateway(Next, Front)).ReadAsync(_es, CancellationToken.None);

        read.Tape.ActiveContractId.Should().Be(Next);
        read.GatewaySelectedContractId.Should().Be(Next);
        read.Agree.Should().BeTrue();
        read.Used.Should().Be(TapeVolumeFrontRead.UsedTapeVolume);
        read.Why.Should().Contain(Next);
    }

    [Fact]
    public async Task EmptyTape_DoesNotSubstituteTheGateway()
    {
        TapeVolumeFrontRead read = await Service(Gateway(Front)).ReadAsync(_es, CancellationToken.None);

        read.Tape.ActiveContractId.Should().BeNull();
        read.GatewaySelectedContractId.Should().Be(Front);
        read.Agree.Should().BeFalse();
        read.Used.Should().Be(TapeVolumeFrontRead.UsedNone);
        read.Why.Should().Contain(Front);
        read.Why.Should().Contain("does not substitute");
    }

    [Fact]
    public async Task AnUnknownPrint_CountsThroughTheStore()
    {
        await SeedAsync(
            Trade(Central(2026, 8, 18, 10, 0), 1, Front, 50, TradeDirection.Buy),
            Trade(Central(2026, 8, 18, 10, 1), 2, Next, 100, TradeDirection.Unknown));

        TapeVolumeFrontRead read = await Service(Gateway(Front)).ReadAsync(_es, CancellationToken.None);

        read.Tape.ActiveContractId.Should().Be(Next);
        read.Tape.SessionVolumes.Should().ContainSingle(v => v.ContractId == Next)
            .Which.Volume.Should().Be(100);
        read.Agree.Should().BeFalse();
    }

    [Fact]
    public async Task AnotherInstrument_DoesNotMoveTheFront()
    {
        await SeedAsync(
            Trade(Central(2026, 8, 18, 10, 0), 1, Front, 5, TradeDirection.Buy),
            Trade(Central(2026, 8, 18, 10, 1), 2, Next, 4, TradeDirection.Sell));

        _database.Trades.Add(new TradeRecord
        {
            Venue = Venue,
            Instrument = "NQ",
            ContractId = "CON.F.US.ENQ.Z26",
            TradeTimeUtc = Central(2026, 8, 18, 10, 2),
            Sequence = 1,
            Price = 18000m,
            Size = 10_000,
            Direction = TradeDirection.Buy,
            RecordedAt = _recorded,
        });
        await _database.SaveChangesAsync();

        TapeVolumeFrontRead read = await Service(Gateway(Front)).ReadAsync(_es, CancellationToken.None);

        read.Tape.ActiveContractId.Should().Be(Front);
        read.Tape.SessionVolumes.Should().OnlyContain(v => v.ContractId == Front || v.ContractId == Next);
    }

    private TapeVolumeFrontService Service(IMarketDataGateway gateway) =>
        new(_database, gateway, _calendar);

    private async Task SeedAsync(params TradeRecord[] trades)
    {
        _database.Trades.AddRange(trades);
        await _database.SaveChangesAsync();
    }

    private static DateTimeOffset Central(int year, int month, int day, int hour, int minute) =>
        MarketClock.FromMarket(new DateOnly(year, month, day), new TimeOnly(hour, minute));

    private static TradeRecord Trade(
        DateTimeOffset when,
        long sequence,
        string contractId,
        long size,
        TradeDirection direction) => new()
        {
            Venue = Venue,
            Instrument = _es.Symbol,
            ContractId = contractId,
            TradeTimeUtc = when,
            Sequence = sequence,
            Price = 5000m,
            Size = size,
            Direction = direction,
            RecordedAt = _recorded,
        };

    /// <summary>Returns the listed contracts in order — <c>[0]</c> is what <c>BarCacheService</c> fetches.</summary>
    private static FixedContractGateway Gateway(params string[] contractIds) =>
        new([.. contractIds.Select(id => new VenueContract(id, _es, IsActive: true, 0.25m, 12.50m))]);

    private sealed class FixedContractGateway(IReadOnlyList<VenueContract> contracts) : IMarketDataGateway
    {
        public string VenueId => Venue;

        public Task<IReadOnlyList<VenueContract>> ResolveContractsAsync(
            InstrumentId instrument,
            CancellationToken cancellationToken) =>
            Task.FromResult(contracts);

        public Task<IReadOnlyList<Bar>> GetBarsAsync(
            string contractId,
            BarRange window,
            TimeSpan barSize,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Bar>>([]);

        public Task<IReadOnlyList<VenueAccount>> GetAccountsAsync(
            bool onlyActive,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VenueAccount>>([]);

        public Task<IReadOnlyList<VenuePosition>> GetOpenPositionsAsync(
            int accountId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VenuePosition>>([]);

        public Task<IReadOnlyList<VenueOrder>> GetOrdersAsync(
            int accountId,
            BarRange? window,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VenueOrder>>([]);

        public Task<IReadOnlyList<VenueTrade>> GetTradesAsync(
            int accountId,
            BarRange window,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VenueTrade>>([]);
    }
}
