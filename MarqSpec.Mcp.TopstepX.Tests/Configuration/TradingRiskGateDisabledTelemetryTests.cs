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
/// Adam's acceptance criteria: master enable off refuses every write with telemetry and no side effects (gh#710).
/// </summary>
[Trait("Gate", "ADR-0024")]
[Collection(HostTelemetryCollection.Name)]
public sealed class TradingRiskGateDisabledTelemetryTests
{
    private const int PracticeAccountId = 100;
    private const string PracticeName = "PRAC-V2-00000-00000001";
    private const string MesContract = "CON.F.US.MES.Z26";

    private readonly FakeKillSwitch _killSwitch = new();
    private readonly RecordingUnknownSignal _unknownSignal = new();
    private readonly FakeExecutionGateway _gateway = new();
    private readonly RecordingAuditLog _audit = new();
    private readonly FakeDailyCapCoordinator _daily = new();

    [Fact]
    public async Task TradingRiskGate_SubmitPlace_RefusesWithTelemetry_WhenTradingIsDisabled()
    {
        await AssertDisabledAsync(
            ExecutionOperation.PlaceOrderOperation,
            gate => gate.SubmitPlace(
                SamplePlace(),
                new TradingRiskAttemptContext(DryRun: false),
                CancellationToken.None),
            () => _gateway.PlaceOrderCalls.Should().Be(0));
    }

    [Fact]
    public async Task TradingRiskGate_SubmitModify_RefusesWithTelemetry_WhenTradingIsDisabled()
    {
        await AssertDisabledAsync(
            ExecutionOperation.ModifyOrderOperation,
            gate => gate.SubmitModify(
                new ExecutionModifyOrderCommand(PracticeAccountId, OrderId: 1, Size: 1, null, null),
                new TradingRiskAttemptContext(DryRun: false),
                CancellationToken.None),
            () => _gateway.ModifyOrderCalls.Should().Be(0));
    }

    [Fact]
    public async Task TradingRiskGate_SubmitCancel_RefusesWithTelemetry_WhenTradingIsDisabled()
    {
        await AssertDisabledAsync(
            ExecutionOperation.CancelOrderOperation,
            gate => gate.SubmitCancel(
                new ExecutionCancelOrderCommand(PracticeAccountId, OrderId: 99),
                new TradingRiskAttemptContext(DryRun: false),
                CancellationToken.None),
            () => _gateway.CancelOrderCalls.Should().Be(0));
    }

    [Fact]
    public async Task TradingRiskGate_SubmitClose_RefusesWithTelemetry_WhenTradingIsDisabled()
    {
        await AssertDisabledAsync(
            ExecutionOperation.ClosePositionOperation,
            gate => gate.SubmitClose(
                new ExecutionClosePositionCommand(PracticeAccountId, MesContract),
                new TradingRiskAttemptContext(DryRun: false),
                CancellationToken.None),
            () => _gateway.ClosePositionCalls.Should().Be(0));
    }

    [Fact]
    public async Task TradingRiskGate_SubmitPartialClose_RefusesWithTelemetry_WhenTradingIsDisabled()
    {
        await AssertDisabledAsync(
            ExecutionOperation.PartialClosePositionOperation,
            gate => gate.SubmitPartialClose(
                new ExecutionPartialClosePositionCommand(PracticeAccountId, MesContract, Size: 1),
                new TradingRiskAttemptContext(DryRun: false),
                CancellationToken.None),
            () => _gateway.PartialClosePositionCalls.Should().Be(0));
    }

    private async Task AssertDisabledAsync(
        string expectedOperation,
        Func<TradingRiskGate, Task<TradingRiskGateResult>> act,
        Action assertGateway)
    {
        using HostTelemetry telemetry = new();
        TradingRiskGate gate = Gate(telemetry, new TradingExecutionOptions { Enabled = false });

        using MetricCollector<long> writes = new(
            telemetry,
            HostTelemetry.Name,
            HostTelemetry.ExecutionWritesInstrument);

        List<Activity> spans = [];
        using ActivityListener listener = Listen(spans, telemetry.Activities);

        TradingRiskGateResult result = await act(gate);

        result.Outcome.Should().Be(TradingAttemptOutcome.Refused);
        result.Refusal.Should().Be(TradingExecutionRefusal.TradingDisabled);
        _audit.Entries.Should().BeEmpty();
        _daily.LockAcquisitions.Should().Be(0);
        assertGateway();

        writes.LastMeasurement!.Value.Should().Be(1);
        writes.LastMeasurement.Tags[HostTelemetry.OperationTag].Should().Be(expectedOperation);
        writes.LastMeasurement.Tags[HostTelemetry.RefusalTag].Should().Be(ExecutionWriteRefusal.TradingDisabled);

        Activity span = spans.Should().ContainSingle().Subject;
        span.OperationName.Should().Be("execution." + expectedOperation);
        span.GetTagItem(HostTelemetry.OperationTag).Should().Be(expectedOperation);
        span.GetTagItem(HostTelemetry.RefusalTag).Should().Be(ExecutionWriteRefusal.TradingDisabled);
    }

    private TradingRiskGate Gate(HostTelemetry telemetry, TradingExecutionOptions options)
    {
        TradingExecutionGate executionGate = new(Options.Create(options), _killSwitch);
        InstrumentRegistry registry = new(Options.Create(new MarketDataOptions { Instruments = "MES" }));
        TradingContractDenylist denylist = new(Options.Create(options), registry);
        ProjectXAccountStageResolver stageResolver = new();

        IReadOnlyList<VenueAccount> accounts =
        [
            new(PracticeAccountId, AccountStage.Practice, CanTrade: true, IsVisible: true, Balance: 50_000m),
        ];

        return new TradingRiskGate(
            executionGate,
            _gateway,
            new StubMarketDataGateway(accounts, []),
            stageResolver,
            _unknownSignal,
            _daily,
            _audit,
            denylist,
            telemetry);
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

    private sealed class FakeKillSwitch : ITradingKillSwitch
    {
        public bool IsEngaged { get; set; }
    }

    private sealed class RecordingUnknownSignal : ITradingUnknownStageRefusalSignal
    {
        public void UnknownStageWriteRefused(int accountId) { }
    }

    private sealed class StubMarketDataGateway(
        IReadOnlyList<VenueAccount> accounts,
        IReadOnlyList<VenueOrder> orders) : IMarketDataGateway
    {
        public string VenueId => "stub";

        public Task<IReadOnlyList<VenueAccount>> GetAccountsAsync(bool onlyActive, CancellationToken cancellationToken) =>
            Task.FromResult(accounts);

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
            Task.FromResult(orders);

        public Task<IReadOnlyList<VenueTrade>> GetTradesAsync(
            int accountId,
            BarRange window,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeExecutionGateway : IExecutionGateway
    {
        public int PlaceOrderCalls { get; private set; }

        public int ModifyOrderCalls { get; private set; }

        public int CancelOrderCalls { get; private set; }

        public int ClosePositionCalls { get; private set; }

        public int PartialClosePositionCalls { get; private set; }

        public Task<ExecutionWriteResult> PlaceOrder(
            ExecutionPlaceOrderCommand command,
            CancellationToken cancellationToken)
        {
            PlaceOrderCalls++;
            return Task.FromResult(Acknowledged());
        }

        public Task<ExecutionWriteResult> ModifyOrder(
            ExecutionModifyOrderCommand command,
            CancellationToken cancellationToken)
        {
            ModifyOrderCalls++;
            return Task.FromResult(Acknowledged());
        }

        public Task<ExecutionWriteResult> CancelOrder(
            ExecutionCancelOrderCommand command,
            CancellationToken cancellationToken)
        {
            CancelOrderCalls++;
            return Task.FromResult(Acknowledged());
        }

        public Task<ExecutionWriteResult> ClosePosition(
            ExecutionClosePositionCommand command,
            CancellationToken cancellationToken)
        {
            ClosePositionCalls++;
            return Task.FromResult(Acknowledged());
        }

        public Task<ExecutionWriteResult> PartialClosePosition(
            ExecutionPartialClosePositionCommand command,
            CancellationToken cancellationToken)
        {
            PartialClosePositionCalls++;
            return Task.FromResult(Acknowledged());
        }

        private static ExecutionWriteResult Acknowledged() =>
            new(
                ExecutionWriteOutcome.BrokerAcknowledged,
                GateRefusal: null,
                HttpStatusCode: null,
                VendorErrorCode: null,
                OrderId: 1);
    }

    private sealed class RecordingAuditLog : ITradingAuditLogWriter
    {
        public List<TradingAuditWriteRequest> Entries { get; } = [];

        public Task WriteAsync(TradingAuditWriteRequest request, CancellationToken cancellationToken)
        {
            Entries.Add(request);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDailyCapCoordinator : ITradingDailyCapCoordinator
    {
        public int LockAcquisitions { get; private set; }

        public Task<IAsyncDisposable> AcquireAccountDayLockAsync(int accountId, CancellationToken cancellationToken)
        {
            LockAcquisitions++;
            return Task.FromResult<IAsyncDisposable>(new NoopLock());
        }

        public Task<int> GetConservativeContractTotalTodayUtcAsync(
            int accountId,
            CancellationToken cancellationToken) =>
            Task.FromResult(0);

        private sealed class NoopLock : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
