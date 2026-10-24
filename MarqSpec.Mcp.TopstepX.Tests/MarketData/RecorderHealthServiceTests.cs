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
/// The report built over the EF store. The query shape lives in <see cref="EfRecorderHealthStoreTests"/>;
/// these two say what that query means once the session and the listen are applied (gh#756).
/// </summary>
public sealed class RecorderHealthServiceTests : IDisposable
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

    private readonly List<HostTelemetry> _telemetry = [];

    public void Dispose()
    {
        foreach (HostTelemetry telemetry in _telemetry)
        {
            telemetry.Dispose();
        }

        _database.Dispose();
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

        HostTelemetry telemetry = new();
        _telemetry.Add(telemetry);
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
            telemetry,
            new RecorderHealthMemo());
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
