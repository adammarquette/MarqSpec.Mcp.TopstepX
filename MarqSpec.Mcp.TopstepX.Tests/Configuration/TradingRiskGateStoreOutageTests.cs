using System.Data.Common;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Telemetry;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.Tests.Configuration;

/// <summary>
/// A store outage on the gate path returns the documented refusal and does not throw (gh#958, ADR-0024).
/// </summary>
[Trait("Gate", "ADR-0024")]
public sealed class TradingRiskGateStoreOutageTests
{
    private const int PracticeAccountId = 100;
    private const string PracticeName = "PRAC-V2-00000-00000001";
    private const string MesContract = "CON.F.US.MES.Z26";

    [Fact]
    public async Task TradingRiskGate_DailyCapLockOutage_RefusesAndDoesNotSend()
    {
        Harness harness = Harness.Live(Fault.DailyLock);

        TradingRiskGateResult result = await harness.Place();

        result.Outcome.Should().Be(TradingAttemptOutcome.Refused);
        result.Refusal.Should().Be(TradingExecutionRefusal.BeforeSendFault);
        harness.Sends.Should().Be(0);
    }

    [Fact]
    public async Task TradingRiskGate_DailyCapReadOutage_RefusesAndDoesNotSend()
    {
        Harness harness = Harness.Live(Fault.DailyRead);

        TradingRiskGateResult result = await harness.Place();

        result.Outcome.Should().Be(TradingAttemptOutcome.Refused);
        result.Refusal.Should().Be(TradingExecutionRefusal.BeforeSendFault);
        harness.Sends.Should().Be(0);
    }

    [Fact]
    public async Task TradingRiskGate_RefusalAuditOutage_StillRefusesTheKillSwitch()
    {
        Harness harness = Harness.Live(Fault.RefusalWrite);
        harness.KillSwitchEngaged = true;

        TradingRiskGateResult result = await harness.Place();

        result.Outcome.Should().Be(TradingAttemptOutcome.Refused);
        result.Refusal.Should().Be(TradingExecutionRefusal.KillSwitchEngaged);
        harness.Sends.Should().Be(0);
    }

    [Fact]
    public async Task TradingRiskGate_ClaimOutage_RefusesAndDoesNotSend()
    {
        Harness harness = Harness.Live(Fault.Claim);

        TradingRiskGateResult result = await harness.Place();

        result.Outcome.Should().Be(TradingAttemptOutcome.Refused);
        result.Refusal.Should().Be(TradingExecutionRefusal.BeforeSendFault);
        harness.Sends.Should().Be(0);
    }

    [Fact]
    public async Task TradingRiskGate_AttemptedWriteOutage_RefusesAndDoesNotSend()
    {
        Harness harness = Harness.Live(Fault.AttemptedWrite);

        TradingRiskGateResult result = await harness.Place();

        result.Outcome.Should().Be(TradingAttemptOutcome.Refused);
        result.Refusal.Should().Be(TradingExecutionRefusal.BeforeSendFault);
        result.IsRetrySafe.Should().BeTrue();
        harness.Sends.Should().Be(0);
    }

    [Fact]
    public async Task TradingRiskGate_PreviewAuditOutage_RefusesInsteadOfPreviewing()
    {
        Harness harness = Harness.Live(Fault.PreviewWrite);

        TradingRiskGateResult result = await harness.Gate.SubmitPlace(
            SamplePlace(),
            new TradingRiskAttemptContext(DryRun: true, IdempotencyKey: "preview-outage"),
            CancellationToken.None);

        result.Outcome.Should().Be(TradingAttemptOutcome.Refused);
        result.Refusal.Should().Be(TradingExecutionRefusal.BeforeSendFault);
        result.Preview.Should().BeNull();
        harness.Sends.Should().Be(0);
    }

    [Fact]
    public async Task TradingRiskGate_WriterLockRefusalAuditOutage_StillRefusesUnavailable()
    {
        Harness harness = Harness.Live(Fault.WriterRefusalWrite, TradingWriterLockTake.Unavailable);

        TradingRiskGateResult result = await harness.Place();

        result.Outcome.Should().Be(TradingAttemptOutcome.Refused);
        result.Refusal!.Value.Code.Should().Be(ExecutionWriteRefusal.AccountWriterUnavailable);
        harness.Sends.Should().Be(0);
    }

    [Fact]
    public async Task TradingRiskGate_WriterLockLookupOutage_RefusesAndDoesNotThrow()
    {
        Harness harness = Harness.Live(
            Fault.Lookup,
            TradingWriterLockTake.Held,
            TradingWriterLockTake.Unavailable);

        TradingRiskGateResult result = await harness.Place();

        result.Outcome.Should().Be(TradingAttemptOutcome.Refused);
        result.Refusal!.Value.Code.Should().Be(ExecutionWriteRefusal.AccountWriterUnavailable);
        harness.Sends.Should().Be(0);
    }

    [Fact]
    public async Task TradingRiskGate_CallerCancellation_Throws()
    {
        Harness harness = Harness.Live(Fault.CancelLock);
        using CancellationTokenSource caller = new();
        caller.Cancel();

        Func<Task> act = () => harness.Gate.SubmitPlace(
            SamplePlace(),
            new TradingRiskAttemptContext(DryRun: false, IdempotencyKey: "cancelled"),
            caller.Token);

        PreSendFaultException thrown = (await act.Should().ThrowAsync<PreSendFaultException>()).Which;
        thrown.InnerException.Should().BeOfType<OperationCanceledException>();
        harness.Sends.Should().Be(0);
    }

    [Fact]
    public async Task TradingRiskGate_ProgrammingError_StillThrows()
    {
        Harness harness = Harness.Live(Fault.ProgrammingClaim);

        Func<Task> act = () => harness.Place();

        PreSendFaultException thrown = (await act.Should().ThrowAsync<PreSendFaultException>()).Which;
        thrown.InnerException.Should().BeOfType<InvalidOperationException>();
        harness.Sends.Should().Be(0);
    }

    private static ExecutionPlaceOrderCommand SamplePlace() =>
        new(
            PracticeAccountId,
            MesContract,
            ExecutionOrderType.Limit,
            VenueSide.Buy,
            Size: 1,
            LimitPrice: 6800m,
            StopPrice: null,
            TrailPrice: null);

    private enum Fault
    {
        None,
        DailyLock,
        DailyRead,
        RefusalWrite,
        Claim,
        AttemptedWrite,
        PreviewWrite,
        WriterRefusalWrite,
        Lookup,
        CancelLock,
        ProgrammingClaim,
    }

    private sealed class Harness
    {
        private readonly Fault _fault;
        private readonly CountingGateway _gateway = new();

        private Harness(Fault fault, params TradingWriterLockTake[] takes)
        {
            _fault = fault;
            ProjectXAccountStageResolver stages = new();
            TradingExecutionOptions options = new() { Enabled = true };
            TradingExecutionGate execution = new(
                TradingOptionsMonitors.Fixed(options),
                new Switch(this),
                stages);
            InstrumentRegistry registry = new(
                Options.Create(new MarketDataOptions { Instruments = "MES" }));
            Gate = new TradingRiskGate(
                execution,
                _gateway,
                new PracticeAccount(stages),
                stages,
                new DailyCap(fault),
                new Audit(fault),
                new TradingContractDenylist(Options.Create(options), registry),
                new TradingWriteVenueAccountCache(),
                new HostTelemetry(),
                takes.Length == 0 ? null : new ScriptedLock(takes));
        }

        public TradingRiskGate Gate { get; }

        public bool KillSwitchEngaged { get; set; }

        public int Sends => _gateway.Sends;

        public static Harness Live(Fault fault, params TradingWriterLockTake[] takes) => new(fault, takes);

        public Task<TradingRiskGateResult> Place() =>
            Gate.SubmitPlace(
                SamplePlace(),
                new TradingRiskAttemptContext(
                    DryRun: false,
                    IdempotencyKey: "store-outage",
                    RequireIdempotencyKey: true),
                CancellationToken.None);

        private sealed class Switch(Harness harness) : ITradingKillSwitch
        {
            public bool IsEngaged => harness.KillSwitchEngaged;
        }
    }

    private sealed class BrokenConnectionException : DbException
    {
        public BrokenConnectionException()
            : base("connection refused")
        {
        }
    }

    private sealed class DailyCap(Fault fault) : ITradingDailyCapCoordinator
    {
        public bool LastReleaseFailed => false;

        public Task<IAsyncDisposable> AcquireAccountDayLockAsync(
            int accountId,
            CancellationToken cancellationToken)
        {
            if (fault == Fault.DailyLock)
            {
                throw new BrokenConnectionException();
            }

            if (fault == Fault.CancelLock)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            return Task.FromResult<IAsyncDisposable>(new Noop());
        }

        public Task<int> GetConservativeContractTotalTodayUtcAsync(
            int accountId,
            CancellationToken cancellationToken)
        {
            if (fault == Fault.DailyRead)
            {
                throw new TimeoutException("command timeout");
            }

            return Task.FromResult(0);
        }

        private sealed class Noop : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class Audit(Fault fault) : ITradingAuditLogWriter
    {
        public Task WriteAsync(TradingAuditWriteRequest request, CancellationToken cancellationToken)
        {
            if (fault == Fault.RefusalWrite && request.Outcome == TradingAuditOutcomes.Refused)
            {
                throw new BrokenConnectionException();
            }

            if (fault == Fault.PreviewWrite && request.Outcome == TradingAuditOutcomes.Preview)
            {
                throw new IOException("connection reset");
            }

            if (fault == Fault.WriterRefusalWrite && request.Outcome == TradingAuditOutcomes.Refused)
            {
                throw new BrokenConnectionException();
            }

            if (fault == Fault.AttemptedWrite && request.Outcome == TradingAuditOutcomes.Attempted)
            {
                throw new TimeoutException("command timeout");
            }

            if (fault == Fault.Lookup
                && request.Outcome == TradingAuditOutcomes.Attempted
                && request.ContractCount < 0)
            {
                throw new BrokenConnectionException();
            }

            return Task.CompletedTask;
        }

        public Task<TradingWriterRefusalStored> FindStoredWriterRefusalAsync(
            Guid finaliseId,
            string refusalCode,
            int reversalCount,
            CancellationToken cancellationToken)
        {
            if (fault == Fault.Lookup)
            {
                throw new BrokenConnectionException();
            }

            return Task.FromResult(TradingWriterRefusalStored.None);
        }

        public Task<TradingAuditClaimResult> TryClaimUnknownAsync(
            TradingAuditWriteRequest request,
            CancellationToken cancellationToken)
        {
            if (fault == Fault.Claim)
            {
                throw new BrokenConnectionException();
            }

            if (fault == Fault.ProgrammingClaim)
            {
                throw new InvalidOperationException("the claim double is misconfigured");
            }

            return Task.FromResult(TradingAuditClaimResult.Claimed());
        }
    }

    private sealed class ScriptedLock(TradingWriterLockTake[] takes) : ITradingAccountWriterLock
    {
        private int _calls;

        public Task<TradingWriterLockTake> TryHoldAsync(int accountId, CancellationToken cancellationToken)
        {
            int index = Math.Min(_calls, takes.Length - 1);
            _calls++;
            return Task.FromResult(takes[index]);
        }
    }

    private sealed class CountingGateway : IExecutionGateway
    {
        public int Sends { get; private set; }

        public Task<ExecutionWriteResult> PlaceOrder(
            ExecutionPlaceOrderCommand command,
            CancellationToken cancellationToken)
        {
            Sends++;
            throw new InvalidOperationException("an outage must not send");
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

    private sealed class PracticeAccount(IAccountStageResolver stages) : IMarketDataGateway
    {
        public string VenueId => "qa";

        public Task<IReadOnlyList<VenueAccount>> GetAccountsAsync(
            bool onlyActive,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VenueAccount>>(
            [
                new(
                    PracticeAccountId,
                    stages.ResolveFromAccountName(PracticeName),
                    CanTrade: true,
                    IsVisible: true,
                    Balance: 50_000m),
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
