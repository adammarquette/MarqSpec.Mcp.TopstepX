using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Telemetry;
using MarqSpec.Mcp.TopstepX.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// The store read behind the recorder report. The gap rules are pinned against a scripted store; this pins
/// the query, so a reverted <c>GROUP BY</c> or window filter fails here on its own (gh#756).
/// </summary>
public sealed class EfRecorderHealthStoreTests : IDisposable
{
    private const string Venue = "projectx";
    private const string Es = "CON.F.US.EP.U26";

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
            .LastTicksAsync(CancellationToken.None);

        ticks.Should().ContainSingle()
            .Which.Should().Be(new ContractLastTick("ES", Es, later));
    }

    [Fact]
    public async Task CoverageAsync_WhenARangeOverlapsTheWindow_ReturnsThatRange()
    {
        await CoverAsync(_open, At(2026, 8, 18, 9, 0));

        IReadOnlyList<CoverageSlice> slices = await new EfRecorderHealthStore(_database)
            .CoverageAsync(_open, _tuesday, CancellationToken.None);

        slices.Should().ContainSingle()
            .Which.Should().Be(new CoverageSlice("ES", Es, _open, At(2026, 8, 18, 9, 0)));
    }

    [Fact]
    public async Task CoverageAsync_WhenARangeMissesTheWindow_OmitsThatRange()
    {
        await CoverAsync(At(2026, 8, 17, 10, 0), _open);

        IReadOnlyList<CoverageSlice> slices = await new EfRecorderHealthStore(_database)
            .CoverageAsync(_open, _tuesday, CancellationToken.None);

        slices.Should().BeEmpty();
    }

    [Fact]
    public async Task DatabaseConnectedAsync_WhenTheInMemoryStoreIsOpen_ReturnsTrue()
    {
        bool connected = await new EfRecorderHealthStore(_database)
            .DatabaseConnectedAsync(CancellationToken.None);

        connected.Should().BeTrue();
    }

    [Fact]
    public async Task ReadAsync_WhenTheListenIsStillOpen_ReportsNoGap()
    {
        await PrintAsync(_tuesday, 1);
        await CoverAsync(_open, TapeCoverageRecord.StillListeningEnd);
        RecorderHealthService service = Service(listening: true);

        RecorderHealthReport.Report report = await service.ReadAsync(CancellationToken.None);

        report.Gaps.Should().NotContain(gap => gap.Symbol == "ES");
        report.Contracts.Should().Contain(row => row.Symbol == "ES" && row.LastTickUtc == _tuesday);
    }

    [Fact]
    public async Task ReadAsync_WhenListeningStoppedMidSession_ReportsTheGapAfterTheClose()
    {
        DateTimeOffset stopped = At(2026, 8, 18, 9, 0);
        await PrintAsync(stopped, 1);
        await CoverAsync(_open, stopped);
        RecorderHealthService service = Service(listening: false);

        RecorderHealthReport.Report report = await service.ReadAsync(CancellationToken.None);

        report.Gaps.Should().Contain(new RecorderHealthReport.Gap("ES", Es, stopped, _tuesday));
    }

    private RecorderHealthService Service(bool listening)
    {
        TapeAvailabilityHolder tape = new();
        tape.Set(TapeAvailability.ConnectedButNotSubscribed());
        if (listening)
        {
            tape.Set("ES", TapeAvailability.Listening());
            tape.Set("NQ", TapeAvailability.Listening());
        }

        IOptions<MarketDataOptions> options = Options.Create(new MarketDataOptions
        {
            Instruments = "ES,NQ",
            MaxRows = 5_000,
            SessionCloseCentral = "16:00",
        });
        return new RecorderHealthService(
            new EfRecorderHealthStore(_database),
            tape,
            new InstrumentRegistry(options),
            BarSessionCalendar.Parse("16:00", []),
            new FakeTimeProvider(_tuesday),
            new HostTelemetry());
    }

    private async Task PrintAsync(DateTimeOffset when, long sequence)
    {
        _database.Trades.Add(new TradeRecord
        {
            Venue = Venue,
            Instrument = "ES",
            ContractId = Es,
            TradeTimeUtc = when,
            Sequence = sequence,
            Price = 5000m,
            Size = 1,
            Direction = TradeDirection.Buy,
            RecordedAt = _recorded,
        });
        await _database.SaveChangesAsync();
    }

    private async Task CoverAsync(DateTimeOffset start, DateTimeOffset end)
    {
        _database.TapeCoverage.Add(new TapeCoverageRecord
        {
            Venue = Venue,
            Instrument = "ES",
            ContractId = Es,
            RangeStart = start,
            RangeEnd = end,
            RecordedAt = _recorded,
        });
        await _database.SaveChangesAsync();
    }

    private static DateTimeOffset At(int year, int month, int day, int hour, int minute) =>
        MarketClock.FromMarket(new DateOnly(year, month, day), new TimeOnly(hour, minute)).ToUniversalTime();
}
