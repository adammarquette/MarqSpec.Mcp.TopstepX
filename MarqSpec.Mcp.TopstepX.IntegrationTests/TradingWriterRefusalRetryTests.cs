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

    [Fact]
    public async Task TwoIdenticalUnkeyedRefusals_EachReverseTheCap_AndEachWriteRefused()
    {
        await using TopstepXDbContext database = _fixture.CreateContext();
        CountingSends gateway = new();
        using HostTelemetry telemetry = new();
        RefuseOnRecheck probe = new();

        TradingRiskGateResult first = await Place(database, telemetry, gateway, probe, idempotencyKey: "");
        TradingRiskGateResult second = await Place(database, telemetry, gateway, probe, idempotencyKey: "");

        first.Outcome.Should().Be(TradingAttemptOutcome.Refused);
        second.Outcome.Should().Be(TradingAttemptOutcome.Refused);
        gateway.Sends.Should().Be(0);

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
            .Be(2);
        rows.Count(row => row.Outcome == TradingAuditOutcomes.Refused).Should().Be(2);
    }

    [Fact]
    public async Task AConcurrentUnkeyedRefusal_DoesNotSatisfyThisAttemptRetry()
    {
        InsertLookalikeThenFail fail = new(_fixture);
        await using TopstepXDbContext database = _fixture.CreateContext(fail);
        CountingSends gateway = new();
        using HostTelemetry telemetry = new();

        TradingRiskGateResult result = await Place(
            database,
            telemetry,
            gateway,
            new ArmOnRecheck(fail),
            idempotencyKey: "");

        result.Outcome.Should().Be(TradingAttemptOutcome.Refused);
        gateway.Sends.Should().Be(0);
        fail.Inserted.Should().BeTrue();

        List<TradingAuditLogRecord> rows = await database.TradingAuditLog
            .AsNoTracking()
            .Where(row => row.AccountId == AccountId)
            .ToListAsync();
        List<TradingAuditLogRecord> ours = rows
            .Where(row => row.FinaliseId != InsertLookalikeThenFail.LookalikeId)
            .ToList();
        ours.Where(row => row.Outcome == TradingAuditOutcomes.Attempted)
            .Sum(row => row.ContractCount)
            .Should()
            .Be(0);
        ours.Count(row => row.Outcome == TradingAuditOutcomes.Attempted && row.ContractCount < 0)
            .Should()
            .Be(1);
        ours.Count(row => row.Outcome == TradingAuditOutcomes.Refused).Should().Be(1);
    }

    [Fact]
    public async Task AppendReversalAndRefusal_RejectsAnotherPendingAddedRow()
    {
        await using TopstepXDbContext database = _fixture.CreateContext();
        database.TradingAuditLog.Add(new TradingAuditLogRecord
        {
            AttemptedAt = DateTimeOffset.UnixEpoch,
            AccountId = AccountId,
            ParsedStage = "Practice",
            Tool = "place_order",
            IdempotencyKey = "",
            RequestHash = new string('a', 64),
            DryRun = false,
            Outcome = TradingAuditOutcomes.Refused,
            ContractCount = 0,
            VenueErrorCode = "account_writer_held",
        });
        TradingAuditLogWriter writer = new(database, TimeProvider.System);
        TradingAuditWriteRequest reversal = Audit(TradingAuditOutcomes.Attempted, contractCount: -1);
        TradingAuditWriteRequest refusal = Audit(TradingAuditOutcomes.Refused, contractCount: 0);

        Func<Task> save = () => writer.AppendReversalAndRefusalAsync(
            reversal,
            refusal,
            Guid.NewGuid(),
            CancellationToken.None);

        string accountText = AccountId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        (await save.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().NotContain(accountText)
            .And.Contain("Added")
            .And.Contain(nameof(TradingAuditLogRecord));
        database.ChangeTracker.Entries<TradingAuditLogRecord>()
            .Count(entry => entry.State == EntityState.Added)
            .Should()
            .Be(1);

        await using TopstepXDbContext fresh = _fixture.CreateContext();
        (await fresh.TradingAuditLog.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task AppendReversalAndRefusal_RejectsAnotherPendingModifiedRow()
    {
        await using TopstepXDbContext database = _fixture.CreateContext();
        TradingAuditLogRecord saved = new()
        {
            AttemptedAt = DateTimeOffset.UnixEpoch,
            AccountId = AccountId,
            ParsedStage = "Practice",
            Tool = "place_order",
            IdempotencyKey = "saved",
            RequestHash = new string('c', 64),
            DryRun = false,
            Outcome = TradingAuditOutcomes.Attempted,
            ContractCount = 1,
        };
        database.TradingAuditLog.Add(saved);
        await database.SaveChangesAsync();
        saved.ContractCount = 2;
        database.Entry(saved).State = EntityState.Modified;

        TradingAuditLogWriter writer = new(database, TimeProvider.System);
        Func<Task> save = () => writer.AppendReversalAndRefusalAsync(
            Audit(TradingAuditOutcomes.Attempted, contractCount: -1),
            Audit(TradingAuditOutcomes.Refused, contractCount: 0),
            Guid.NewGuid(),
            CancellationToken.None);

        string accountText = AccountId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        (await save.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().NotContain(accountText)
            .And.Contain("Modified")
            .And.Contain(nameof(TradingAuditLogRecord));

        await using TopstepXDbContext fresh = _fixture.CreateContext();
        List<TradingAuditLogRecord> rows = await fresh.TradingAuditLog.AsNoTracking().ToListAsync();
        rows.Should().ContainSingle();
        rows[0].ContractCount.Should().Be(1);
        rows[0].Outcome.Should().Be(TradingAuditOutcomes.Attempted);
    }

    private TradingRiskGate PlaceGate(
        TopstepXDbContext database,
        HostTelemetry telemetry,
        CountingSends gateway,
        ITradingAccountWriterLock probe)
    {
        TradingExecutionOptions options = new()
        {
            Enabled = true,
            AllowedAccountIds = AccountId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        return new TradingRiskGate(
            new TradingExecutionGate(
                Options.Create(options),
                new DisengagedKillSwitch(),
                new ProjectXAccountStageResolver()),
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
            probe);
    }

    private Task<TradingRiskGateResult> Place(
        TopstepXDbContext database,
        HostTelemetry telemetry,
        CountingSends gateway,
        ITradingAccountWriterLock probe,
        string idempotencyKey) =>
        PlaceGate(database, telemetry, gateway, probe).SubmitPlace(
            new ExecutionPlaceOrderCommand(
                AccountId,
                ContractId,
                ExecutionOrderType.Limit,
                VenueSide.Buy,
                Size: 1,
                LimitPrice: 6800m,
                StopPrice: null,
                TrailPrice: null),
            new TradingRiskAttemptContext(DryRun: false, idempotencyKey, RequireIdempotencyKey: false),
            CancellationToken.None);

    private static TradingAuditWriteRequest Audit(string outcome, int contractCount) =>
        new(
            AccountId,
            "Practice",
            "place_order",
            IdempotencyKey: "",
            RequestHash: new string('b', 64),
            DryRun: false,
            outcome,
            contractCount,
            VenueErrorCode: "account_writer_held");

    private sealed class RefuseOnRecheck : ITradingAccountWriterLock
    {
        private int _calls;

        public Task<TradingWriterLockTake> TryHoldAsync(int accountId, CancellationToken cancellationToken)
        {
            _calls++;
            TradingWriterLockTake take = _calls % 2 == 1
                ? TradingWriterLockTake.Held
                : TradingWriterLockTake.HeldByOther;
            return Task.FromResult(take);
        }
    }

    private interface IArmableSave
    {
        void Arm();
    }

    private sealed class FailNextTrackedSave : SaveChangesInterceptor, IArmableSave
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

    private sealed class InsertLookalikeThenFail(SeriesStoreFixture fixture)
        : SaveChangesInterceptor, IArmableSave
    {
        public static readonly Guid LookalikeId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        private int _armed;

        public bool Inserted { get; private set; }

        public void Arm() => _armed = 1;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (_armed > 0)
            {
                _armed--;
                string hash = TradingRequestHasher.HashPlace(
                    new ExecutionPlaceOrderCommand(
                        AccountId,
                        ContractId,
                        ExecutionOrderType.Limit,
                        VenueSide.Buy,
                        Size: 1,
                        LimitPrice: 6800m,
                        StopPrice: null,
                        TrailPrice: null),
                    dryRun: false);
                await using TopstepXDbContext other = fixture.CreateContext();
                other.TradingAuditLog.Add(Lookalike(TradingAuditOutcomes.Attempted, -1, hash));
                other.TradingAuditLog.Add(Lookalike(TradingAuditOutcomes.Refused, 0, hash));
                await other.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                Inserted = true;
                throw new IOException("save failed after a concurrent unkeyed refusal");
            }

            return await base.SavingChangesAsync(eventData, result, cancellationToken)
                .ConfigureAwait(false);
        }

        private static TradingAuditLogRecord Lookalike(string outcome, int contractCount, string hash) =>
            new()
            {
                AttemptedAt = DateTimeOffset.UnixEpoch,
                AccountId = AccountId,
                ParsedStage = "Practice",
                Tool = TradingMcpTools.PlaceOrderOperation,
                IdempotencyKey = "",
                RequestHash = hash,
                DryRun = false,
                Outcome = outcome,
                ContractCount = contractCount,
                VenueErrorCode = ExecutionWriteRefusal.AccountWriterHeld,
                FinaliseId = LookalikeId,
            };
    }

    private sealed class ArmOnRecheck(IArmableSave fail) : ITradingAccountWriterLock
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
