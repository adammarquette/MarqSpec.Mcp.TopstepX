using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Telemetry;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// A failed cap reversal must not stay tracked and be inserted twice (gh#772).
/// </summary>
[Collection(SeriesStoreCollection.Name)]
[Trait("Gate", "ADR-0024")]
public sealed class TradingWriterRefusalRetryTests(SeriesStoreFixture fixture) : IAsyncLifetime
{
    private const int AccountId = 100;
    private const string ContractId = "CON.F.US.MES.Z26";

    private readonly SeriesStoreFixture _fixture = fixture;

    /// <inheritdoc />
    public Task InitializeAsync() => _fixture.ResetAsync();

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task AFailedSaveAfterTheReversalIsTracked_StillNetsTheCapToZero_WithOneRefusedRow()
    {
        FailNextTrackedSave fail = new();
        await using TopstepXDbContext database = _fixture.CreateContext(fail);
        TradingExecutionOptions options = new()
        {
            Enabled = true,
            AllowedAccountIds = AccountId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        CountingSends gateway = new();
        using HostTelemetry telemetry = new();
        TradingExecutionGate execution = new(
            Options.Create(options),
            new DisengagedKillSwitch(),
            new ProjectXAccountStageResolver());
        TradingRiskGate gate = new(
            execution,
            gateway,
            new PracticeAccount(AccountId),
            new ProjectXAccountStageResolver(),
            new TradingDailyCapCoordinator(database, TimeProvider.System, telemetry),
            new TradingAuditLogWriter(database, TimeProvider.System),
            new TradingContractDenylist(
                Options.Create(options),
                new InstrumentRegistry(Options.Create(new MarketDataOptions { Instruments = "MES" }))),
            new TradingWriteVenueAccountCache(),
            telemetry,
            new ArmOnRecheck(fail));

        TradingRiskGateResult result = await gate.SubmitPlace(
            new ExecutionPlaceOrderCommand(
                AccountId,
                ContractId,
                ExecutionOrderType.Limit,
                VenueSide.Buy,
                Size: 1,
                LimitPrice: 6800m,
                StopPrice: null,
                TrailPrice: null),
            new TradingRiskAttemptContext(DryRun: false, "reversal-retry", RequireIdempotencyKey: true),
            CancellationToken.None);

        result.Outcome.Should().Be(TradingAttemptOutcome.Refused);
        result.Refusal.HasValue.Should().BeTrue();
        result.Refusal.GetValueOrDefault().Code.Should().Be(ExecutionWriteRefusal.AccountWriterHeld);
        fail.SawAddedRow.Should().BeTrue("the first save failed after the reversal was tracked");
        fail.Failures.Should().Be(1);
        gateway.Sends.Should().Be(0);
        database.ChangeTracker.Entries<TradingAuditLogRecord>()
            .Should()
            .NotContain(entry => entry.State == EntityState.Added);

        List<TradingAuditLogRecord> rows = await database.TradingAuditLog
            .AsNoTracking()
            .Where(row => row.AccountId == AccountId)
            .ToListAsync();
        rows.Where(row => row.Outcome == TradingAuditOutcomes.Attempted)
            .Sum(row => row.ContractCount)
            .Should()
            .Be(0);
        rows.Count(row => row.Outcome == TradingAuditOutcomes.Attempted && row.ContractCount < 0)
            .Should()
            .Be(1);
        rows.Count(row => row.Outcome == TradingAuditOutcomes.Refused).Should().Be(1);
    }

    private sealed class FailNextTrackedSave : SaveChangesInterceptor
    {
        private int _armed;

        public int Failures { get; private set; }

        public bool SawAddedRow { get; private set; }

        public void Arm() => _armed = 1;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (_armed > 0)
            {
                _armed--;
                Failures++;
                SawAddedRow = eventData.Context!.ChangeTracker
                    .Entries<TradingAuditLogRecord>()
                    .Any(entry => entry.State == EntityState.Added);
                throw new IOException("save failed after the reversal was tracked");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class ArmOnRecheck(FailNextTrackedSave fail) : ITradingAccountWriterLock
    {
        private int _calls;

        public Task<TradingWriterLockTake> TryHoldAsync(int accountId, CancellationToken cancellationToken)
        {
            _calls++;
            if (_calls == 1)
            {
                return Task.FromResult(TradingWriterLockTake.Held);
            }

            fail.Arm();
            return Task.FromResult(TradingWriterLockTake.HeldByOther);
        }
    }

    private sealed class DisengagedKillSwitch : ITradingKillSwitch
    {
        public bool IsEngaged => false;
    }

    private sealed class CountingSends : IExecutionGateway
    {
        public int Sends { get; private set; }

        public Task<ExecutionWriteResult> PlaceOrder(
            ExecutionPlaceOrderCommand command,
            CancellationToken cancellationToken)
        {
            Sends++;
            throw new InvalidOperationException("the refusal must not send");
        }

        public Task<ExecutionWriteResult> ModifyOrder(
            ExecutionModifyOrderCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ExecutionWriteResult> CancelOrder(
            ExecutionCancelOrderCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ExecutionWriteResult> ClosePosition(
            ExecutionClosePositionCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ExecutionWriteResult> PartialClosePosition(
            ExecutionPartialClosePositionCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class PracticeAccount(int accountId) : IMarketDataGateway
    {
        public string VenueId => "qa";

        public Task<IReadOnlyList<VenueAccount>> GetAccountsAsync(
            bool onlyActive,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VenueAccount>>(
            [
                new(accountId, AccountStage.Practice, CanTrade: true, IsVisible: true, Balance: 50_000m),
            ]);

        public Task<IReadOnlyList<VenueContract>> ResolveContractsAsync(
            InstrumentId instrument,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<VenueContract?> FindContractAsync(
            InstrumentId instrument,
            ContractExpiry expiry,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Bar>> GetBarsAsync(
            string contractId,
            BarRange window,
            TimeSpan barSize,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<VenuePosition>> GetOpenPositionsAsync(
            int accountId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<VenueOrder>> GetOrdersAsync(
            int accountId,
            BarRange? window,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<VenueTrade>> GetTradesAsync(
            int accountId,
            BarRange window,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
