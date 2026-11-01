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
using Npgsql;

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

    [Fact]
    public async Task TheAdvisoryLock_IsHeldOnANonPooledConnection_AndASecondTrySkips()
    {
        PostgresBarBackfillLock first = Lock();
        PostgresBarBackfillLock second = Lock();
        first.DedicatedConnectionString.Should().Contain("Pooling=false");
        first.DedicatedConnectionString.Should().Contain("Keepalive=30");

        IBarBackfillLockHold? held = await first.TryAcquireAsync(CancellationToken.None);
        held.Should().NotBeNull();
        try
        {
            (await held!.StillHeldAsync(CancellationToken.None)).Should().BeTrue();
            (await CountGrantedLocksAsync()).Should().Be(1);
            (await second.TryAcquireAsync(CancellationToken.None)).Should().BeNull();
        }
        finally
        {
            if (held is not null)
            {
                await held.DisposeAsync();
            }
        }

        (await CountGrantedLocksAsync()).Should().Be(0);
        IBarBackfillLockHold? after = await second.TryAcquireAsync(CancellationToken.None);
        after.Should().NotBeNull();
        if (after is not null)
        {
            await after.DisposeAsync();
        }
    }

    [Fact]
    public async Task DroppingTheLockConnection_ReleasesTheAdvisoryLock()
    {
        PostgresBarBackfillLock first = Lock();
        IBarBackfillLockHold? acquired = await first.TryAcquireAsync(CancellationToken.None);
        BarBackfillLockHold held = (BarBackfillLockHold)acquired!;
        await held.AbandonAsync();

        (await CountGrantedLocksAsync()).Should().Be(0);
        IBarBackfillLockHold? again = await first.TryAcquireAsync(CancellationToken.None);
        again.Should().NotBeNull();
        if (again is not null)
        {
            await again.DisposeAsync();
        }
    }

    private PostgresBarBackfillLock Lock() =>
        new(_fixture.ConnectionString, NullLogger<PostgresBarBackfillLock>.Instance);

    private async Task<int> CountGrantedLocksAsync()
    {
        await using NpgsqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            """
            SELECT count(*)::int FROM pg_locks
            WHERE locktype = 'advisory'
              AND classid = @classId
              AND objid = @objectId
              AND objsubid = 2
              AND granted
            """,
            connection);
        command.Parameters.AddWithValue("classId", PostgresBarBackfillLock.ClassId);
        command.Parameters.AddWithValue("objectId", PostgresBarBackfillLock.ObjectId);
        return (int)(await command.ExecuteScalarAsync())!;
    }
}
