using System.Diagnostics;
using System.Reflection;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Telemetry;
using MarqSpec.Mcp.TopstepX.Tools;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol.Server;

namespace MarqSpec.Mcp.TopstepX.Tests.Tools;

/// <summary>
/// The five order tools call <see cref="TradingRiskGate"/> and nothing else (ADR-0024, gh#750).
/// </summary>
[Trait("Gate", "ADR-0024")]
[Collection(HostTelemetryCollection.Name)]
public sealed class TradingOrderToolGateTests
{
    private const int PracticeAccountId = 100;
    private const string MesContract = "CON.F.US.MES.Z26";
    private const long OpenOrderId = 7;

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 24, 0, 0, 0, TimeSpan.Zero));
    private readonly InMemoryTradingIdempotencyStore _store;
    private readonly FakeKillSwitch _killSwitch = new();
    private readonly CountingGateway _gateway = new();
    private readonly RecordingAuditLog _audit = new();
    private readonly FakeDailyCapCoordinator _daily = new();

    public TradingOrderToolGateTests()
    {
        _store = new InMemoryTradingIdempotencyStore(_clock);
    }

    public static TheoryData<string> EveryOrderTool()
    {
        TheoryData<string> data = [];
        data.Add(TradingMcpTools.PlaceOrderOperation);
        data.Add(TradingMcpTools.ModifyOrderOperation);
        data.Add(TradingMcpTools.CancelOrderOperation);
        data.Add(TradingMcpTools.ClosePositionOperation);
        data.Add(TradingMcpTools.PartialClosePositionOperation);
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryOrderTool))]
    public async Task TradingRiskGate_OrderTool_RefusesWithTelemetry_WhenTradingEnabledIsLeftFalse(string operation)
    {
        using HostTelemetry telemetry = new();
        TradingExecutionOptions options = new();
        options.Enabled.Should().BeFalse();
        TradingOrderTools tools = Tools(telemetry, options);

        using MetricCollector<long> writes = ListenWrites(telemetry);
        List<Activity> spans = [];
        using ActivityListener listener = Listen(spans, telemetry.Activities);

        ToolPayloads.TradingWriteResult result = await Call(tools, operation, "disabled-key");

        result.Status.Should().Be(nameof(TradingAttemptOutcome.Refused));
        result.RefusalCode.Should().Be(TradingExecutionRefusal.TradingDisabled.Code);
        result.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.SafeToRetry);
        result.ParsedStage.Should().Be(AccountStage.Unknown);
        _audit.Entries.Should().BeEmpty();
        _daily.LockAcquisitions.Should().Be(0);
        CallsFor(operation).Should().Be(0);
        AssertWrite(writes, spans, operation, ExecutionWriteRefusal.TradingDisabled);
    }

    [Theory]
    [MemberData(nameof(EveryOrderTool))]
    public async Task TradingRiskGate_OrderTool_KillSwitchRefusesWithTelemetry_IncludingCancelAndClose(
        string operation)
    {
        _killSwitch.IsEngaged = true;
        using HostTelemetry telemetry = new();
        TradingOrderTools tools = Tools(telemetry, new TradingExecutionOptions { Enabled = true });

        using MetricCollector<long> writes = ListenWrites(telemetry);
        List<Activity> spans = [];
        using ActivityListener listener = Listen(spans, telemetry.Activities);

        ToolPayloads.TradingWriteResult result = await Call(tools, operation, "halt-key");

        result.Status.Should().Be(nameof(TradingAttemptOutcome.Refused));
        result.RefusalCode.Should().Be(TradingExecutionRefusal.KillSwitchEngaged.Code);
        result.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.SafeToRetry);
        CallsFor(operation).Should().Be(0);
        AssertWrite(writes, spans, operation, ExecutionWriteRefusal.KillSwitch);
    }

    [Theory]
    [MemberData(nameof(EveryOrderTool))]
    public async Task TradingRiskGate_OrderTool_DisabledRefusalWinsOverABlankIdempotencyKey(string operation)
    {
        using HostTelemetry telemetry = new();
        TradingOrderTools tools = Tools(telemetry, new TradingExecutionOptions());

        ToolPayloads.TradingWriteResult result = await Call(tools, operation, "  ");

        result.RefusalCode.Should().Be(TradingExecutionRefusal.TradingDisabled.Code);
        result.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.SafeToRetry);
        _audit.Entries.Should().BeEmpty();
        CallsFor(operation).Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(EveryOrderTool))]
    public async Task TradingRiskGate_OrderTool_RefusesABlankIdempotencyKey_AfterTheMasterSwitch(string operation)
    {
        using HostTelemetry telemetry = new();
        TradingOrderTools tools = Tools(telemetry, new TradingExecutionOptions { Enabled = true });

        using MetricCollector<long> writes = ListenWrites(telemetry);
        List<Activity> spans = [];
        using ActivityListener listener = Listen(spans, telemetry.Activities);

        ToolPayloads.TradingWriteResult result = await Call(tools, operation, "");

        result.Status.Should().Be(nameof(TradingAttemptOutcome.Refused));
        result.RefusalCode.Should().Be(ExecutionWriteRefusal.IdempotencyKeyRequired);
        result.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.SafeToRetry);
        CallsFor(operation).Should().Be(0);
        AssertWrite(writes, spans, operation, ExecutionWriteRefusal.IdempotencyKeyRequired);
    }

    [Theory]
    [MemberData(nameof(EveryOrderTool))]
    public async Task TradingRiskGate_OrderTool_ReplaysTheOriginalResult_WithoutASecondSend(string operation)
    {
        _gateway.Next = Acknowledged(42);
        using HostTelemetry telemetry = new();
        TradingOrderTools tools = Tools(telemetry, new TradingExecutionOptions { Enabled = true });

        using MetricCollector<long> writes = ListenWrites(telemetry);
        List<Activity> spans = [];
        using ActivityListener listener = Listen(spans, telemetry.Activities);

        ToolPayloads.TradingWriteResult first = await Call(tools, operation, "same-key");
        ToolPayloads.TradingWriteResult replay = await Call(tools, operation, "same-key");

        first.Status.Should().Be(nameof(TradingAttemptOutcome.Live));
        first.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.NotSafeToRetry);
        first.OrderId.Should().Be(42);
        replay.Should().Be(first);
        CallsFor(operation).Should().Be(1);
        writes.GetMeasurementSnapshot().Should().HaveCount(2);
        spans.Should().HaveCount(2);
        spans.Should().OnlyContain(span => span.OperationName == "execution." + operation);
    }

    [Theory]
    [MemberData(nameof(EveryOrderTool))]
    public async Task TradingRiskGate_OrderTool_ReportsOutcomeUnknown_AndReplaysIt(string operation)
    {
        _gateway.Next = new ExecutionWriteResult(
            ExecutionWriteOutcome.TransmittedOutcomeUnknown,
            GateRefusal: null,
            HttpStatusCode: null,
            VendorErrorCode: null,
            OrderId: null);
        using HostTelemetry telemetry = new();
        TradingOrderTools tools = Tools(telemetry, new TradingExecutionOptions { Enabled = true });

        ToolPayloads.TradingWriteResult first = await Call(tools, operation, "unknown-key");
        ToolPayloads.TradingWriteResult replay = await Call(tools, operation, "unknown-key");

        first.Status.Should().Be(nameof(TradingAttemptOutcome.Unknown));
        first.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.OutcomeUnknown);
        replay.Should().Be(first);
        CallsFor(operation).Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(EveryOrderTool))]
    public async Task TradingRiskGate_OrderTool_ReportsSafeToRetry_WhenTheVenueRejects(string operation)
    {
        _gateway.Next = new ExecutionWriteResult(
            ExecutionWriteOutcome.VenueRejected,
            GateRefusal: null,
            HttpStatusCode: 400,
            VendorErrorCode: 3,
            OrderId: null);
        using HostTelemetry telemetry = new();
        TradingOrderTools tools = Tools(telemetry, new TradingExecutionOptions { Enabled = true });

        ToolPayloads.TradingWriteResult result = await Call(tools, operation, "rejected-key");

        result.Status.Should().Be(nameof(TradingAttemptOutcome.Failed));
        result.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.SafeToRetry);
        result.ResultCode.Should().Be(3);
        CallsFor(operation).Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(EveryOrderTool))]
    public async Task TradingRiskGate_OrderTool_RefusesADifferentRequest_ForTheSameIdempotencyKey(string operation)
    {
        _gateway.Next = Acknowledged(42);
        using HostTelemetry telemetry = new();
        TradingOrderTools tools = Tools(telemetry, new TradingExecutionOptions { Enabled = true });

        using MetricCollector<long> writes = ListenWrites(telemetry);
        List<Activity> spans = [];
        using ActivityListener listener = Listen(spans, telemetry.Activities);

        await Call(tools, operation, "conflict-key");
        (int size, long orderId, string contractId) = ConflictOf(operation);
        ToolPayloads.TradingWriteResult conflict = await Call(
            tools,
            operation,
            "conflict-key",
            size,
            orderId,
            contractId);

        conflict.Status.Should().Be(nameof(TradingAttemptOutcome.Refused));
        conflict.RefusalCode.Should().Be(ExecutionWriteRefusal.IdempotencyConflict);
        conflict.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.SafeToRetry);
        CallsFor(operation).Should().Be(1);
        writes.GetMeasurementSnapshot().Should().Contain(measurement =>
            measurement.Tags[HostTelemetry.RefusalTag] as string == ExecutionWriteRefusal.IdempotencyConflict);
        spans.Should().Contain(span =>
            span.GetTagItem(HostTelemetry.RefusalTag) as string == ExecutionWriteRefusal.IdempotencyConflict);
    }

    [Fact]
    public async Task TradingRiskGate_PlaceOrder_ReplaysADisabledRefusal_WithoutSending_AfterTradingIsEnabled()
    {
        using HostTelemetry disabledTelemetry = new();
        TradingOrderTools disabled = Tools(disabledTelemetry, new TradingExecutionOptions());
        ToolPayloads.TradingWriteResult refusal = await Call(disabled, TradingMcpTools.PlaceOrderOperation, "held-key");

        using HostTelemetry enabledTelemetry = new();
        TradingOrderTools enabled = Tools(enabledTelemetry, new TradingExecutionOptions { Enabled = true });
        ToolPayloads.TradingWriteResult replay = await Call(enabled, TradingMcpTools.PlaceOrderOperation, "held-key");

        refusal.RefusalCode.Should().Be(TradingExecutionRefusal.TradingDisabled.Code);
        replay.Should().Be(refusal);
        _gateway.PlaceOrderCalls.Should().Be(0);
    }

    [Fact]
    public async Task TradingRiskGate_PlaceOrder_SendsAgain_AfterTheIdempotencyWindow()
    {
        _gateway.Next = Acknowledged(42);
        using HostTelemetry telemetry = new();
        TradingOrderTools tools = Tools(telemetry, new TradingExecutionOptions { Enabled = true });

        await Call(tools, TradingMcpTools.PlaceOrderOperation, "aged-key");
        _clock.Advance(InMemoryTradingIdempotencyStore.Lifetime);
        await Call(tools, TradingMcpTools.PlaceOrderOperation, "aged-key");

        _gateway.PlaceOrderCalls.Should().Be(2);
    }

    [Fact]
    public void TradingRiskGate_OrderTools_PublishTheFiveProtocolNames()
    {
        using HostTelemetry telemetry = new();
        TradingOrderTools tools = Tools(telemetry, new TradingExecutionOptions());

        string[] names = [.. OrderMethods().Select(method => McpServerTool.Create(
            method,
            tools,
            new McpServerToolCreateOptions()).ProtocolTool.Name)];

        names.Should().BeEquivalentTo(
        [
            TradingMcpTools.PlaceOrderOperation,
            TradingMcpTools.ModifyOrderOperation,
            TradingMcpTools.CancelOrderOperation,
            TradingMcpTools.ClosePositionOperation,
            TradingMcpTools.PartialClosePositionOperation,
        ]);
    }

    private TradingOrderTools Tools(HostTelemetry telemetry, TradingExecutionOptions options) =>
        new(Gate(telemetry, options));

    private TradingRiskGate Gate(HostTelemetry telemetry, TradingExecutionOptions options)
    {
        ProjectXAccountStageResolver stageResolver = new();
        TradingExecutionGate executionGate = new(
            Options.Create(options),
            _killSwitch,
            stageResolver);
        InstrumentRegistry registry = new(Options.Create(new MarketDataOptions { Instruments = "MES" }));
        TradingContractDenylist denylist = new(Options.Create(options), registry);
        IReadOnlyList<VenueAccount> accounts =
        [
            new(PracticeAccountId, AccountStage.Practice, CanTrade: true, IsVisible: true, Balance: 50_000m),
        ];
        IReadOnlyList<VenueOrder> orders = [OpenOrder()];

        return new TradingRiskGate(
            executionGate,
            _gateway,
            new StubMarketDataGateway(accounts, orders),
            stageResolver,
            new RecordingUnknownSignal(),
            _daily,
            _audit,
            denylist,
            new TradingWriteVenueAccountCache(),
            telemetry,
            _store);
    }

    private static Task<ToolPayloads.TradingWriteResult> Call(
        TradingOrderTools tools,
        string operation,
        string idempotencyKey,
        int size = 1,
        long orderId = OpenOrderId,
        string contractId = MesContract) =>
        operation switch
        {
            TradingMcpTools.PlaceOrderOperation => tools.SubmitPlace(
                PracticeAccountId,
                contractId,
                ExecutionOrderType.Limit,
                VenueSide.Buy,
                size,
                idempotencyKey,
                limitPrice: 6800m),
            TradingMcpTools.ModifyOrderOperation => tools.SubmitModify(
                PracticeAccountId,
                orderId,
                idempotencyKey,
                size: size),
            TradingMcpTools.CancelOrderOperation => tools.SubmitCancel(
                PracticeAccountId,
                orderId,
                idempotencyKey),
            TradingMcpTools.ClosePositionOperation => tools.SubmitClose(
                PracticeAccountId,
                contractId,
                idempotencyKey),
            TradingMcpTools.PartialClosePositionOperation => tools.SubmitPartialClose(
                PracticeAccountId,
                contractId,
                size,
                idempotencyKey),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown order tool."),
        };

    private int CallsFor(string operation) => operation switch
    {
        TradingMcpTools.PlaceOrderOperation => _gateway.PlaceOrderCalls,
        TradingMcpTools.ModifyOrderOperation => _gateway.ModifyOrderCalls,
        TradingMcpTools.CancelOrderOperation => _gateway.CancelOrderCalls,
        TradingMcpTools.ClosePositionOperation => _gateway.ClosePositionCalls,
        TradingMcpTools.PartialClosePositionOperation => _gateway.PartialClosePositionCalls,
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown order tool."),
    };

    private static (int Size, long OrderId, string ContractId) ConflictOf(string operation) =>
        operation switch
        {
            TradingMcpTools.CancelOrderOperation => (1, 8, MesContract),
            TradingMcpTools.ClosePositionOperation => (1, OpenOrderId, "CON.F.US.MES.H27"),
            _ => (2, OpenOrderId, MesContract),
        };

    private static ExecutionWriteResult Acknowledged(long orderId) =>
        new(
            ExecutionWriteOutcome.BrokerAcknowledged,
            GateRefusal: null,
            HttpStatusCode: null,
            VendorErrorCode: null,
            OrderId: orderId);

    private static MetricCollector<long> ListenWrites(HostTelemetry telemetry) =>
        new(telemetry, HostTelemetry.Name, HostTelemetry.ExecutionWritesInstrument);

    private static void AssertWrite(
        MetricCollector<long> writes,
        IReadOnlyList<Activity> spans,
        string operation,
        string refusal)
    {
        IReadOnlyList<CollectedMeasurement<long>> measurements = writes.GetMeasurementSnapshot();
        measurements.Should().ContainSingle();
        measurements[0].Value.Should().Be(1);
        measurements[0].Tags[HostTelemetry.OperationTag].Should().Be(operation);
        measurements[0].Tags[HostTelemetry.RefusalTag].Should().Be(refusal);

        Activity span = spans.Should().ContainSingle().Subject;
        span.OperationName.Should().Be("execution." + operation);
        span.GetTagItem(HostTelemetry.OperationTag).Should().Be(operation);
        span.GetTagItem(HostTelemetry.RefusalTag).Should().Be(refusal);
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

    private static IEnumerable<MethodInfo> OrderMethods() =>
        typeof(TradingOrderTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null);

    private static VenueOrder OpenOrder() =>
        new(
            OpenOrderId,
            MesContract,
            VenueSide.Buy,
            Size: 1,
            FilledSize: 0,
            VenueOrderStatus.Open,
            LimitPrice: 6800m,
            StopPrice: null,
            FilledPrice: null,
            CreatedAt: DateTimeOffset.UnixEpoch);

    private sealed class FakeKillSwitch : ITradingKillSwitch
    {
        public bool IsEngaged { get; set; }
    }

    private sealed class RecordingUnknownSignal : ITradingUnknownStageRefusalSignal
    {
        public void UnknownStageWriteRefused(int accountId)
        {
        }
    }

    private sealed class CountingGateway : IExecutionGateway
    {
        public int PlaceOrderCalls { get; private set; }

        public int ModifyOrderCalls { get; private set; }

        public int CancelOrderCalls { get; private set; }

        public int ClosePositionCalls { get; private set; }

        public int PartialClosePositionCalls { get; private set; }

        public ExecutionWriteResult Next { get; set; } = Acknowledged(42);

        public Task<ExecutionWriteResult> PlaceOrder(
            ExecutionPlaceOrderCommand command,
            CancellationToken cancellationToken)
        {
            PlaceOrderCalls++;
            return Task.FromResult(Next);
        }

        public Task<ExecutionWriteResult> ModifyOrder(
            ExecutionModifyOrderCommand command,
            CancellationToken cancellationToken)
        {
            ModifyOrderCalls++;
            return Task.FromResult(Next);
        }

        public Task<ExecutionWriteResult> CancelOrder(
            ExecutionCancelOrderCommand command,
            CancellationToken cancellationToken)
        {
            CancelOrderCalls++;
            return Task.FromResult(Next);
        }

        public Task<ExecutionWriteResult> ClosePosition(
            ExecutionClosePositionCommand command,
            CancellationToken cancellationToken)
        {
            ClosePositionCalls++;
            return Task.FromResult(Next);
        }

        public Task<ExecutionWriteResult> PartialClosePosition(
            ExecutionPartialClosePositionCommand command,
            CancellationToken cancellationToken)
        {
            PartialClosePositionCalls++;
            return Task.FromResult(Next);
        }
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

    private sealed class StubMarketDataGateway(
        IReadOnlyList<VenueAccount> accounts,
        IReadOnlyList<VenueOrder> orders) : IMarketDataGateway
    {
        public string VenueId => "stub";

        public Task<IReadOnlyList<VenueAccount>> GetAccountsAsync(
            bool onlyActive,
            CancellationToken cancellationToken) =>
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
}
