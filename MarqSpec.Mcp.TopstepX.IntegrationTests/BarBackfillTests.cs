using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Telemetry;
using MarqSpec.Mcp.TopstepX.Tests.MarketData;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>A backfill pass writes bars once through the real cache (gh#899).</summary>
[Collection(SeriesStoreCollection.Name)]
public sealed class BarBackfillTests(SeriesStoreFixture fixture) : IAsyncLifetime
{
    private static readonly InstrumentId _es = new("ES");

    private static DateTimeOffset SessionStart =>
        MarketClock.FromMarket(new DateOnly(2026, 8, 18), new TimeOnly(9, 0)).ToUniversalTime();

    private readonly SeriesStoreFixture _fixture = fixture;
    private readonly TopstepXDbContext _database = fixture.CreateContext();
    private readonly HostTelemetry _telemetry = new();

    /// <inheritdoc />
    public Task InitializeAsync() => _fixture.ResetAsync();

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        _database.Dispose();
        _telemetry.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task APass_WritesBarsOnce_AndASecondPassDoesNotAskAgain()
    {
        DateTimeOffset now = SessionStart.AddHours(1);
        IReadOnlyList<Bar> bars =
        [
            .. Enumerable.Range(0, 12).Select(i =>
                new Bar(SessionStart.AddMinutes(5 * i), 100m, 101m, 99m, 100.5m, 10)),
        ];
        CountingGateway gateway = new(bars);
        FakeTimeProvider clock = new(now);
        BarSessionCalendar calendar = BarSessionCalendar.Parse("16:00", []);
        IndicatorCatalog catalog = new(
            Options.Create(new IndicatorOptions { AtrPeriod = 3, RsiPeriod = 3 }),
            calendar);
        BarCacheService cache = new(
            _database,
            gateway,
            calendar,
            new IndicatorProjector(
                _database,
                catalog,
                NullLogger<IndicatorProjector>.Instance,
                _telemetry),
            new InstrumentRegistry(Options.Create(new MarketDataOptions { Instruments = "ES" })),
            new ContractDirectory(clock),
            clock,
            NullLogger<BarCacheService>.Instance,
            _telemetry);
        BarBackfillDriver driver = new(
            new BarCacheBackfillAdapter(cache),
            Options.Create(new BarBackfillOptions
            {
                Enabled = true,
                Instruments = "ES",
                ResolutionsMinutes = "5",
                NotBeforeUtc = SessionStart,
            }),
            new InstrumentRegistry(Options.Create(new MarketDataOptions { Instruments = "ES" })),
            clock,
            NullLogger<BarBackfillDriver>.Instance);

        BarBackfillReport first = await driver.RunAsync(CancellationToken.None);
        int written = await _database.Bars.CountAsync(row => row.Instrument == _es.Symbol);
        gateway.ResetCounters();
        BarBackfillReport second = await driver.RunAsync(CancellationToken.None);

        first.Ran.Should().BeTrue();
        first.VenueRequests.Should().BeGreaterThan(0);
        written.Should().BeGreaterThan(0);
        second.VenueRequests.Should().Be(0);
        gateway.BarRequests.Should().Be(0);
        (await _database.Bars.CountAsync(row => row.Instrument == _es.Symbol)).Should().Be(written);
    }
}
