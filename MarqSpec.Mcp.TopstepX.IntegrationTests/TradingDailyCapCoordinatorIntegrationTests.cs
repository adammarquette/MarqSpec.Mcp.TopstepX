using System.Data.Common;
using System.Diagnostics.Metrics;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Telemetry;
using MarqSpec.Mcp.TopstepX.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>Attempted-row session-day rollups and advisory locking for trading caps (gh#711).</summary>
[Collection(SeriesStoreCollection.Name)]
public sealed class TradingDailyCapCoordinatorIntegrationTests(SeriesStoreFixture fixture) : IAsyncLifetime
{
    private TopstepXDbContext _database = null!;
    private FakeTimeProvider _clock = null!;

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        _database = fixture.CreateContext();
        _clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 18, 15, 0, 0, TimeSpan.Zero));
    }

    public async Task DisposeAsync()
    {
        await _database.DisposeAsync();
    }

    [Fact]
    public async Task TradingDailyCapCoordinator_CountsOnlyAttemptedRows_ForSessionDay()
    {
        using HostTelemetry telemetry = new();
        TradingDailyCapCoordinator coordinator = new(_database, _clock, telemetry);
        DateTimeOffset sessionStart = TradingCapSessionCalendar.SessionDayStartUtc(_clock.GetUtcNow());

        _database.TradingAuditLog.AddRange(
            Row(sessionStart.AddHours(1), TradingAuditOutcomes.Live, 2),
            Row(sessionStart.AddHours(2), TradingAuditOutcomes.Failed, 3),
            Row(sessionStart.AddHours(3), TradingAuditOutcomes.Attempted, 4),
            Row(sessionStart.AddDays(-1), TradingAuditOutcomes.Attempted, 99));
        await _database.SaveChangesAsync();

        int total = await coordinator.GetConservativeContractTotalTodayUtcAsync(
            accountId: 100,
            CancellationToken.None);

        total.Should().Be(4);
    }

    [Fact]
    public async Task TradingDailyCapCoordinator_AdvisoryLock_SerialisesConcurrentSessions()
    {
        await using TopstepXDbContext other = fixture.CreateContext();
        using HostTelemetry telemetry = new();
        TradingDailyCapCoordinator first = new(_database, _clock, telemetry);
        TradingDailyCapCoordinator second = new(other, _clock, telemetry);

        IAsyncDisposable firstLock = await first.AcquireAccountDayLockAsync(100, CancellationToken.None);

        Task<IAsyncDisposable> blocked = second.AcquireAccountDayLockAsync(100, CancellationToken.None);
        Task completed = await Task.WhenAny(blocked, Task.Delay(TimeSpan.FromMilliseconds(200)));
        completed.Should().NotBe(blocked);

        await firstLock.DisposeAsync();
        await using IAsyncDisposable secondLock = await blocked;
        secondLock.Should().NotBeNull();
    }

    [Fact]
    [Trait("Gate", "ADR-0024")]
    public async Task TradingDailyCapCoordinator_Release_SwallowsADisposedConnection_AndCountsIt()
    {
        using HostTelemetry telemetry = new();
        BookkeepingFaultCounter faults = new();
        using MeterListener listener = faults.Listen(telemetry);

        TradingDailyCapCoordinator coordinator = new(_database, _clock, telemetry);
        IAsyncDisposable lease = await coordinator.AcquireAccountDayLockAsync(100, CancellationToken.None);
        DbConnection connection = _database.Database.GetDbConnection();
        await connection.DisposeAsync();

        Func<Task> release = async () => await lease.DisposeAsync();

        await release.Should().NotThrowAsync();
        coordinator.LastReleaseFailed.Should().BeTrue();
        faults.Value.Should().Be(1);
    }

    private static TradingAuditLogRecord Row(DateTimeOffset at, string outcome, int contractCount) =>
        new()
        {
            AttemptedAt = at,
            AccountId = 100,
            ParsedStage = "Practice",
            Tool = TradingMcpTools.PlaceOrderOperation,
            IdempotencyKey = string.Empty,
            RequestHash = new string('b', 64),
            DryRun = false,
            ContractCount = contractCount,
            Outcome = outcome,
        };
}
