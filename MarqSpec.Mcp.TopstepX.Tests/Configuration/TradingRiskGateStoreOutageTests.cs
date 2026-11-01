using System.Data.Common;
using System.Diagnostics;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Telemetry;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
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
    public async Task TradingRiskGate_FinaliseFirstSaveFails_SecondSucceeds_WithCapCharge()
    {
        Harness harness = Harness.Live(
            Fault.None,
            TradingWriterLockTake.Held,
            TradingWriterLockTake.Unavailable);
        harness.Store.FailReversals = 1;

        TradingRiskGateResult result = await harness.Place();

        result.Outcome.Should().Be(TradingAttemptOutcome.Refused);
        result.Refusal!.Value.Code.Should().Be(ExecutionWriteRefusal.AccountWriterUnavailable);
        harness.Store.ReversalAttempts.Should().Be(2);
        harness.Store.Finds.Should().Be(1);
        harness.Sends.Should().Be(0);
    }

    [Fact]
    public async Task TradingRiskGate_FinaliseBothSavesFail_LookupRuns_WithCapCharge()
    {
        Harness harness = Harness.Live(
            Fault.None,
            TradingWriterLockTake.Held,
            TradingWriterLockTake.Unavailable);
        harness.Store.FailReversals = 2;

        TradingRiskGateResult result = await harness.Place();

        result.Outcome.Should().Be(TradingAttemptOutcome.Refused);
        result.Refusal!.Value.Code.Should().Be(ExecutionWriteRefusal.AccountWriterUnavailable);
        harness.Store.ReversalAttempts.Should().Be(2);
        harness.Store.Finds.Should().Be(2);
        harness.Sends.Should().Be(0);
    }

    [Fact]
    public async Task TradingRiskGate_FinaliseFirstSaveFails_SecondSucceeds_WithoutCapCharge()
    {
        Harness harness = Harness.Live(
            Fault.None,
            TradingWriterLockTake.Held,
            TradingWriterLockTake.Unavailable);
        harness.Store.FailRefusals = 1;

        TradingRiskGateResult result = await harness.Cancel();

        result.Outcome.Should().Be(TradingAttemptOutcome.Refused);
        result.Refusal!.Value.Code.Should().Be(ExecutionWriteRefusal.AccountWriterUnavailable);
        harness.Store.RefusalAttempts.Should().Be(2);
        harness.Store.Finds.Should().Be(1);
        harness.Sends.Should().Be(0);
    }

    [Fact]
    public async Task TradingRiskGate_FinaliseBothSavesFail_LookupRuns_WithoutCapCharge()
    {
        Harness harness = Harness.Live(
            Fault.None,
            TradingWriterLockTake.Held,
            TradingWriterLockTake.Unavailable);
        harness.Store.FailRefusals = 2;

        TradingRiskGateResult result = await harness.Cancel();

        result.Outcome.Should().Be(TradingAttemptOutcome.Refused);
        result.Refusal!.Value.Code.Should().Be(ExecutionWriteRefusal.AccountWriterUnavailable);
        harness.Store.RefusalAttempts.Should().Be(2);
        harness.Store.Finds.Should().Be(2);
        harness.Sends.Should().Be(0);
    }

    [Fact]
    public async Task TradingRiskGate_UnsentClaimAuditOutage_StillRefuses()
    {
        Harness harness = Harness.Live(Fault.AttemptedWrite);
        harness.Store.FailRefusals = 1;

        TradingRiskGateResult result = await harness.Place();

        result.Outcome.Should().Be(TradingAttemptOutcome.Refused);
        result.Refusal.Should().Be(TradingExecutionRefusal.BeforeSendFault);
        harness.Store.RefusalAttempts.Should().Be(1);
        harness.Sends.Should().Be(0);
    }

    [Fact]
    public async Task TradingRiskGate_DisabledAfterTheCharge_FailedReversal_LeavesTheCapCharged()
    {
        ReplaceableOptions options = new(new TradingExecutionOptions { Enabled = true });
        Harness harness = Harness.Live(Fault.None, options);
        harness.Store.OnWrite = request =>
        {
            if (request.Outcome == TradingAuditOutcomes.Attempted && request.ContractCount > 0)
            {
                options.Current = new TradingExecutionOptions { Enabled = false };
            }
        };
        harness.Store.FailReversals = 1;

        TradingRiskGateResult result = await harness.Place();

        result.Outcome.Should().Be(TradingAttemptOutcome.Refused);
        result.Refusal.Should().Be(TradingExecutionRefusal.TradingDisabled);
        harness.Store.Rows.Where(row => row.Outcome == TradingAuditOutcomes.Attempted)
            .Sum(row => row.ContractCount)
            .Should()
            .Be(1);
        harness.Sends.Should().Be(0);
    }

    [Fact]
    public async Task TradingRiskGate_AfterSendStoreFault_StaysUnknown()
    {
        Harness harness = Harness.Live(Fault.AfterSend, TradingWriterLockTake.Held);
        harness.AllowSend();

        TradingRiskGateResult result = await harness.Place();

        result.Outcome.Should().Be(TradingAttemptOutcome.Unknown);
        result.WriteResult!.Outcome.Should().Be(ExecutionWriteOutcome.TransmittedOutcomeUnknown);
        harness.Sends.Should().Be(1);
    }

    [Fact]
    public async Task TradingRiskGate_RecheckProbeFault_RefusesUnavailable()
    {
        Harness harness = Harness.Live(Fault.None, new ThrowOnRecheck());

        TradingRiskGateResult result = await harness.Place();

        result.Outcome.Should().Be(TradingAttemptOutcome.Refused);
        result.Refusal!.Value.Code.Should().Be(ExecutionWriteRefusal.AccountWriterUnavailable);
        harness.Sends.Should().Be(0);
    }

    [Fact]
    public async Task TradingRiskGate_ClaimOutage_WritesErrorUnderTheCallerKey()
    {
        Harness harness = Harness.Live(Fault.Claim);

        TradingRiskGateResult result = await harness.Place();

        result.Refusal.Should().Be(TradingExecutionRefusal.BeforeSendFault);
        harness.Store.Rows.Should().Contain(row =>
            row.Outcome == TradingAuditOutcomes.Refused
            && row.VenueErrorCode == ExecutionWriteRefusal.Error
            && row.IdempotencyKey == "store-outage");
        harness.Sends.Should().Be(0);
    }

    [Fact]
    public async Task TradingRiskGate_BeforeSendFault_RecordsTheRefusalSpanAndCounter()
    {
        Harness harness = Harness.Live(Fault.DailyLock);
        using MetricCollector<long> writes = new(
            harness.Telemetry,
            HostTelemetry.Name,
            HostTelemetry.ExecutionWritesInstrument);
        List<Activity> spans = [];
        using ActivityListener listener = Listen(spans, harness.Telemetry.Activities);

        TradingRiskGateResult result = await harness.Place();

        result.Refusal.Should().Be(TradingExecutionRefusal.BeforeSendFault);
        IReadOnlyList<CollectedMeasurement<long>> measurements = writes.GetMeasurementSnapshot();
        measurements.Should().ContainSingle();
        measurements[0].Tags[HostTelemetry.OperationTag].Should().Be(ExecutionOperation.PlaceOrderOperation);
        measurements[0].Tags[HostTelemetry.RefusalTag].Should().Be(ExecutionWriteRefusal.Error);
        measurements[0].Tags[HostTelemetry.ReplayTag].Should().Be(ExecutionWriteReplay.False);
        Activity span = spans.Should().ContainSingle().Subject;
        span.GetTagItem(HostTelemetry.OperationTag).Should().Be(ExecutionOperation.PlaceOrderOperation);
        span.GetTagItem(HostTelemetry.RefusalTag).Should().Be(ExecutionWriteRefusal.Error);
        span.GetTagItem(HostTelemetry.ReplayTag).Should().Be(ExecutionWriteReplay.False);
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
        AfterSend,
    }

    private sealed class Harness
    {
        private readonly CountingGateway _gateway = new();

        private Harness(
            Fault fault,
            IOptionsMonitor<TradingExecutionOptions>? bound,
            ITradingAccountWriterLock? probe,
            TradingWriterLockTake[] takes)
        {
            ProjectXAccountStageResolver stages = new();
            TradingExecutionOptions options = new() { Enabled = true };
            Store = new Audit(fault);
            Telemetry = new HostTelemetry();
            TradingExecutionGate execution = new(
                bound ?? TradingOptionsMonitors.Fixed(options),
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
                Store,
                new TradingContractDenylist(Options.Create(options), registry),
                new TradingWriteVenueAccountCache(),
                Telemetry,
                probe ?? (takes.Length == 0 ? null : new ScriptedLock(takes)));
        }

        public TradingRiskGate Gate { get; }

        public Audit Store { get; }

        public HostTelemetry Telemetry { get; }

        public bool KillSwitchEngaged { get; set; }

        public int Sends => _gateway.Sends;

        public static Harness Live(Fault fault, params TradingWriterLockTake[] takes) =>
            new(fault, null, null, takes);

        public static Harness Live(Fault fault, ITradingAccountWriterLock probe) =>
            new(fault, null, probe, []);

        public static Harness Live(Fault fault, IOptionsMonitor<TradingExecutionOptions> bound) =>
            new(fault, bound, null, []);

        public void AllowSend() =>
            _gateway.PlaceResult = new ExecutionWriteResult(
                ExecutionWriteOutcome.BrokerAcknowledged,
                GateRefusal: null,
                HttpStatusCode: null,
                VendorErrorCode: null,
                OrderId: 7);

        public Task<TradingRiskGateResult> Cancel() =>
            Gate.SubmitCancel(
                new ExecutionCancelOrderCommand(PracticeAccountId, 1),
                new TradingRiskAttemptContext(
                    DryRun: false,
                    IdempotencyKey: "cancel-outage",
                    RequireIdempotencyKey: true),
                CancellationToken.None);

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
        public int ReversalAttempts { get; private set; }

        public int RefusalAttempts { get; private set; }

        public int Finds { get; private set; }

        public int FailReversals { get; set; }

        public int FailRefusals { get; set; }

        public List<TradingAuditWriteRequest> Rows { get; } = [];

        public Action<TradingAuditWriteRequest>? OnWrite { get; set; }

        public Task WriteAsync(TradingAuditWriteRequest request, CancellationToken cancellationToken)
        {
            OnWrite?.Invoke(request);
            if (request.Outcome == TradingAuditOutcomes.Attempted && request.ContractCount < 0)
            {
                ReversalAttempts++;
                if (FailReversals > 0)
                {
                    FailReversals--;
                    throw new BrokenConnectionException();
                }
            }

            if (request.Outcome == TradingAuditOutcomes.Refused)
            {
                RefusalAttempts++;
                if (FailRefusals > 0)
                {
                    FailRefusals--;
                    throw new BrokenConnectionException();
                }
            }

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

            if (fault == Fault.AfterSend && request.Outcome == TradingAuditOutcomes.Live)
            {
                throw new BrokenConnectionException();
            }

            Rows.Add(request);
            return Task.CompletedTask;
        }

        public Task<TradingWriterRefusalStored> FindStoredWriterRefusalAsync(
            Guid finaliseId,
            string refusalCode,
            int reversalCount,
            CancellationToken cancellationToken)
        {
            Finds++;
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

    private sealed class ThrowOnRecheck : ITradingAccountWriterLock
    {
        private int _calls;

        public Task<TradingWriterLockTake> TryHoldAsync(int accountId, CancellationToken cancellationToken)
        {
            _calls++;
            if (_calls > 1)
            {
                throw new InvalidOperationException("writer lock probe failed");
            }

            return Task.FromResult(TradingWriterLockTake.Held);
        }
    }

    private sealed class ReplaceableOptions(TradingExecutionOptions initial)
        : IOptionsMonitor<TradingExecutionOptions>
    {
        public TradingExecutionOptions Current { get; set; } = initial;

        public TradingExecutionOptions CurrentValue => Current;

        public TradingExecutionOptions Get(string? name) => Current;

        public IDisposable OnChange(Action<TradingExecutionOptions, string?> listener) => new Noop();

        private sealed class Noop : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private static ActivityListener Listen(List<Activity> into, ActivitySource source)
    {
        ActivityListener listener = new()
        {
            ShouldListenTo = candidate => ReferenceEquals(candidate, source),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity => into.Add(activity),
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private sealed class CountingGateway : IExecutionGateway
    {
        public int Sends { get; private set; }

        public ExecutionWriteResult? PlaceResult { get; set; }

        public Task<ExecutionWriteResult> PlaceOrder(
            ExecutionPlaceOrderCommand command,
            CancellationToken cancellationToken)
        {
            Sends++;
            if (PlaceResult is not null)
            {
                return Task.FromResult(PlaceResult);
            }

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
