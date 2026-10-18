using System.Diagnostics;
using FluentAssertions;
using MarqSpec.Client.ProjectX;
using MarqSpec.Client.ProjectX.Api.Models;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Telemetry;
using MarqSpec.Mcp.TopstepX.Tests.Venue;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.Tests.Configuration;

/// <summary>One execution-write span and counter per logical gate call (gh#710, gh#740 review).</summary>
[Trait("Gate", "ADR-0024")]
[Collection(HostTelemetryCollection.Name)]
public sealed class TradingRiskGateExecutionWriteTelemetryTests
{
    private const int PracticeAccountId = 100;
    private const string PracticeName = "PRAC-V2-00000-00000001";
    private const string MesContract = "CON.F.US.MES.Z26";

    private readonly FakeKillSwitch _killSwitch = new();
    private readonly RecordingUnknownSignal _unknownSignal = new();
    private readonly FakeExecutionGateway _fakeGateway = new();
    private readonly RecordingAuditLog _audit = new();
    private readonly FakeDailyCapCoordinator _daily = new();

    [Fact]
    public async Task TradingRiskGate_SubmitPlace_DryRun_EmitsPreviewTelemetryOnce()
    {
        using HostTelemetry telemetry = new();
        TradingRiskGate gate = Gate(telemetry, new TradingExecutionOptions { Enabled = true });

        await AssertSingleWriteAsync(
            telemetry,
            () => gate.SubmitPlace(
                SamplePlace(),
                new TradingRiskAttemptContext(DryRun: true),
                CancellationToken.None),
            ExecutionOperation.PlaceOrderOperation,
            ExecutionWriteRefusal.Preview);
    }

    [Fact]
    public async Task TradingRiskGate_SubmitPlace_LiveAck_EmitsTransmittedOnce()
    {
        _fakeGateway.PlaceOrderResult = Acknowledged(42);
        using HostTelemetry telemetry = new();
        TradingRiskGate gate = Gate(telemetry, new TradingExecutionOptions { Enabled = true });

        await AssertSingleWriteAsync(
            telemetry,
            () => gate.SubmitPlace(
                SamplePlace(),
                new TradingRiskAttemptContext(DryRun: false),
                CancellationToken.None),
            ExecutionOperation.PlaceOrderOperation,
            ExecutionWriteRefusal.Transmitted);
    }

    [Fact]
    public async Task TradingRiskGate_SubmitPlace_VenueRejected_EmitsVenueRejectedOnce()
    {
        _fakeGateway.PlaceOrderResult = new ExecutionWriteResult(
            ExecutionWriteOutcome.VenueRejected,
            GateRefusal: null,
            HttpStatusCode: 400,
            VendorErrorCode: 3,
            OrderId: null);
        using HostTelemetry telemetry = new();
        TradingRiskGate gate = Gate(telemetry, new TradingExecutionOptions { Enabled = true });

        await AssertSingleWriteAsync(
            telemetry,
            () => gate.SubmitPlace(
                SamplePlace(),
                new TradingRiskAttemptContext(DryRun: false),
                CancellationToken.None),
            ExecutionOperation.PlaceOrderOperation,
            ExecutionWriteRefusal.VenueRejected);
    }

    [Fact]
    public async Task TradingRiskGate_SubmitPlace_ThroughRealGateway_EmitsSingleExecutionWrite()
    {
        (ExecutionOrderClient fake, IProjectXApiClient client) = ExecutionOrderClient.Create();
        fake.PlaceOrderAnswer = (_, _) => Task.FromResult(new PlaceOrderResponse
        {
            Success = true,
            OrderId = 7,
            ErrorCode = 0,
        });

        using HostTelemetry telemetry = new();
        TradingRiskGate gate = GateWithRealGateway(telemetry, client, enabled: true);

        using MetricCollector<long> writes = new(
            telemetry,
            HostTelemetry.Name,
            HostTelemetry.ExecutionWritesInstrument);
        List<Activity> spans = [];
        using ActivityListener listener = Listen(spans, telemetry.Activities);

        TradingRiskGateResult result = await gate.SubmitPlace(
            SamplePlace(),
            new TradingRiskAttemptContext(DryRun: false),
            CancellationToken.None);

        result.Outcome.Should().Be(TradingAttemptOutcome.Live);
        fake.PlaceOrderCount.Should().Be(1);
        writes.GetMeasurementSnapshot().Should().ContainSingle().Which.Tags[HostTelemetry.RefusalTag]
            .Should().Be(ExecutionWriteRefusal.Transmitted);
        spans.Where(span => span.OperationName.StartsWith("execution.", StringComparison.Ordinal))
            .Should()
            .ContainSingle();
    }

    private async Task AssertSingleWriteAsync(
        HostTelemetry telemetry,
        Func<Task<TradingRiskGateResult>> act,
        string expectedOperation,
        string expectedRefusal)
    {
        using MetricCollector<long> writes = new(
            telemetry,
            HostTelemetry.Name,
            HostTelemetry.ExecutionWritesInstrument);
        List<Activity> spans = [];
        using ActivityListener listener = Listen(spans, telemetry.Activities);

        await act();

        IReadOnlyList<CollectedMeasurement<long>> measurements = writes.GetMeasurementSnapshot();
        measurements.Should().ContainSingle();
        measurements[0].Value.Should().Be(1);
        measurements[0].Tags[HostTelemetry.OperationTag].Should().Be(expectedOperation);
        measurements[0].Tags[HostTelemetry.RefusalTag].Should().Be(expectedRefusal);

        Activity span = spans
            .Where(candidate => candidate.OperationName.StartsWith("execution.", StringComparison.Ordinal))
            .Should()
            .ContainSingle()
            .Subject;
        span.OperationName.Should().Be("execution." + expectedOperation);
        span.GetTagItem(HostTelemetry.RefusalTag).Should().Be(expectedRefusal);
    }

    private TradingRiskGate Gate(HostTelemetry telemetry, TradingExecutionOptions options)
    {
        TradingExecutionGate executionGate = new(Options.Create(options), _killSwitch, new ProjectXAccountStageResolver());
        InstrumentRegistry registry = new(Options.Create(new MarketDataOptions { Instruments = "MES" }));
        TradingContractDenylist denylist = new(Options.Create(options), registry);
        ProjectXAccountStageResolver stageResolver = new();

        return new TradingRiskGate(
            executionGate,
            _fakeGateway,
            new StubMarketDataGateway([PracticeAccount(stageResolver)], []),
            stageResolver,
            _unknownSignal,
            _daily,
            _audit,
            denylist,
            new TradingWriteVenueAccountCache(),
            telemetry);
    }

    private TradingRiskGate GateWithRealGateway(
        HostTelemetry telemetry,
        IProjectXApiClient client,
        bool enabled)
    {
        TradingExecutionOptions options = new() { Enabled = enabled };
        TradingExecutionGate executionGate = new(
            Options.Create(options),
            _killSwitch,
            new ProjectXAccountStageResolver());
        InstrumentRegistry registry = new(Options.Create(new MarketDataOptions { Instruments = "MES" }));
        TradingContractDenylist denylist = new(Options.Create(options), registry);
        ProjectXAccountStageResolver stageResolver = new();
        StubMarketDataGateway marketData = new([PracticeAccount(stageResolver)], []);
        TradingWriteVenueAccountCache accountCache = new();
        IExecutionGateway gateway = new ProjectXExecutionGateway(
            executionGate,
            marketData,
            _unknownSignal,
            client,
            accountCache,
            telemetry,
            NullLogger<ProjectXExecutionGateway>.Instance);

        return new TradingRiskGate(
            executionGate,
            gateway,
            marketData,
            stageResolver,
            _unknownSignal,
            _daily,
            _audit,
            denylist,
            accountCache,
            telemetry);
    }

    private static VenueAccount PracticeAccount(IAccountStageResolver stageResolver) =>
        new(PracticeAccountId, stageResolver.ResolveFromAccountName(PracticeName), CanTrade: true, IsVisible: true, Balance: 50_000m);

    private static ExecutionWriteResult Acknowledged(long? orderId) =>
        new(
            ExecutionWriteOutcome.BrokerAcknowledged,
            GateRefusal: null,
            HttpStatusCode: null,
            VendorErrorCode: null,
            OrderId: orderId);

    private static ExecutionPlaceOrderCommand SamplePlace() =>
        new(
            PracticeAccountId,
            MesContract,
            ExecutionOrderType.Market,
            VenueSide.Buy,
            Size: 1,
            LimitPrice: null,
            StopPrice: null,
            TrailPrice: null);

    private static ActivityListener Listen(List<Activity> into, ActivitySource source)
    {
        ActivityListener listener = new()
        {
            ShouldListenTo = candidate => ReferenceEquals(candidate, source),
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = into.Add,
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
        public void UnknownStageWriteRefused(int accountId)
        {
        }
    }

    private sealed class FakeExecutionGateway : IExecutionGateway
    {
        public ExecutionWriteResult PlaceOrderResult { get; set; } = Acknowledged(1);

        public Task<ExecutionWriteResult> PlaceOrder(
            ExecutionPlaceOrderCommand command,
            CancellationToken cancellationToken) =>
            Task.FromResult(PlaceOrderResult);

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
        public Task<IAsyncDisposable> AcquireAccountDayLockAsync(int accountId, CancellationToken cancellationToken) =>
            Task.FromResult<IAsyncDisposable>(new NoopLock());

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
