using System.Diagnostics;
using FluentAssertions;
using MarqSpec.Client.ProjectX;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.Telemetry;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.Tests.Venue;

/// <summary>
/// Adam's acceptance criteria for master enable off: refusal before the vendor, plus OTel (gh#710, #734).
/// </summary>
[Trait("Gate", "ADR-0024")]
public sealed class ProjectXExecutionGatewayTradingRiskGateTelemetryTests
{
    private const int PracticeAccountId = 100;

    public static IEnumerable<object[]> DisabledWriteVerbs =>
        from verb in Enum.GetValues<ProjectXExecutionGatewayTests.WriteVerb>()
        select new object[] { verb };

    [Theory]
    [MemberData(nameof(DisabledWriteVerbs))]
    public async Task TradingRiskGate_WhenTradingDisabled_RefusesBeforeVendorAndEmitsTelemetry(
        ProjectXExecutionGatewayTests.WriteVerb verb)
    {
        (ExecutionOrderClient fake, IProjectXApiClient client) = ExecutionOrderClient.Create();
        using HostTelemetry telemetry = new();
        using MetricCollector<long> writes = new(telemetry, HostTelemetry.Name, HostTelemetry.ExecutionWritesInstrument);

        List<Activity> spans = [];
        using ActivityListener listener = Listen(spans, telemetry.Activities);

        IExecutionGateway gateway = Gateway(client, telemetry, enabled: false);

        ExecutionWriteResult result = await InvokeAsync(gateway, verb, PracticeAccountId);

        ClientCalls(fake, verb).Should().Be(0);
        result.Outcome.Should().Be(ExecutionWriteOutcome.NotTransmitted);
        result.GateRefusal.Should().Be(TradingExecutionRefusal.TradingDisabled);

        CollectedMeasurement<long> measurement = writes.LastMeasurement!;
        measurement.Value.Should().Be(1);
        measurement.Tags[HostTelemetry.OperationTag].Should().Be(ExpectedOperation(verb));
        measurement.Tags[HostTelemetry.RefusalTag].Should().Be(ExecutionWriteRefusal.TradingDisabled);

        Activity span = spans.Should().ContainSingle().Subject;
        span.OperationName.Should().Be("execution." + ExpectedOperation(verb));
        span.GetTagItem(HostTelemetry.RefusalTag).Should().Be(ExecutionWriteRefusal.TradingDisabled);
    }

    private static string ExpectedOperation(ProjectXExecutionGatewayTests.WriteVerb verb) => verb switch
    {
        ProjectXExecutionGatewayTests.WriteVerb.Place => ExecutionOperation.PlaceOrderOperation,
        ProjectXExecutionGatewayTests.WriteVerb.Modify => ExecutionOperation.ModifyOrderOperation,
        ProjectXExecutionGatewayTests.WriteVerb.Cancel => ExecutionOperation.CancelOrderOperation,
        ProjectXExecutionGatewayTests.WriteVerb.Close => ExecutionOperation.ClosePositionOperation,
        ProjectXExecutionGatewayTests.WriteVerb.PartialClose => ExecutionOperation.PartialClosePositionOperation,
        _ => throw new ArgumentOutOfRangeException(nameof(verb), verb, null),
    };

    private static int ClientCalls(ExecutionOrderClient fake, ProjectXExecutionGatewayTests.WriteVerb verb) =>
        verb switch
        {
            ProjectXExecutionGatewayTests.WriteVerb.Place => fake.PlaceOrderCount,
            ProjectXExecutionGatewayTests.WriteVerb.Modify => fake.ModifyOrderCount,
            ProjectXExecutionGatewayTests.WriteVerb.Cancel => fake.CancelOrderCount,
            ProjectXExecutionGatewayTests.WriteVerb.Close => fake.ClosePositionCount,
            ProjectXExecutionGatewayTests.WriteVerb.PartialClose => fake.PartialClosePositionCount,
            _ => throw new ArgumentOutOfRangeException(nameof(verb), verb, null),
        };

    private static Task<ExecutionWriteResult> InvokeAsync(
        IExecutionGateway gateway,
        ProjectXExecutionGatewayTests.WriteVerb verb,
        int accountId) =>
        verb switch
        {
            ProjectXExecutionGatewayTests.WriteVerb.Place => gateway.PlaceOrder(SamplePlaceCommand(accountId), CancellationToken.None),
            ProjectXExecutionGatewayTests.WriteVerb.Modify => gateway.ModifyOrder(
                new ExecutionModifyOrderCommand(accountId, OrderId: 1, Size: 1, LimitPrice: null, StopPrice: null),
                CancellationToken.None),
            ProjectXExecutionGatewayTests.WriteVerb.Cancel => gateway.CancelOrder(
                new ExecutionCancelOrderCommand(accountId, OrderId: 1),
                CancellationToken.None),
            ProjectXExecutionGatewayTests.WriteVerb.Close => gateway.ClosePosition(
                new ExecutionClosePositionCommand(accountId, ContractId: "CON.F.US.EP.Z26"),
                CancellationToken.None),
            ProjectXExecutionGatewayTests.WriteVerb.PartialClose => gateway.PartialClosePosition(
                new ExecutionPartialClosePositionCommand(accountId, ContractId: "CON.F.US.EP.Z26", Size: 1),
                CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(verb), verb, null),
        };

    private static IExecutionGateway Gateway(
        IProjectXApiClient client,
        HostTelemetry telemetry,
        bool enabled)
    {
        TradingExecutionGate gate = new(
            Options.Create(new TradingExecutionOptions { Enabled = enabled }),
            new FakeKillSwitch());

        return new ProjectXExecutionGateway(
            gate,
            new StubAccountsGateway([PracticeAccount()]),
            new NoOpUnknownSignal(),
            client,
            telemetry,
            NullLogger<ProjectXExecutionGateway>.Instance);
    }

    private static ExecutionPlaceOrderCommand SamplePlaceCommand(int accountId) =>
        new(
            accountId,
            ContractId: "CON.F.US.EP.Z26",
            OrderType: ExecutionOrderType.Market,
            Side: VenueSide.Buy,
            Size: 1,
            LimitPrice: null,
            StopPrice: null,
            TrailPrice: null);

    private static VenueAccount PracticeAccount() =>
        new(PracticeAccountId, AccountStage.Practice, CanTrade: true, IsVisible: true, Balance: 50_000m);

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
        public bool IsEngaged => false;
    }

    private sealed class NoOpUnknownSignal : ITradingUnknownStageRefusalSignal
    {
        public void UnknownStageWriteRefused(int accountId)
        {
        }
    }

    private sealed class StubAccountsGateway(IReadOnlyList<VenueAccount> accounts) : IMarketDataGateway
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
            throw new NotSupportedException();

        public Task<IReadOnlyList<VenueTrade>> GetTradesAsync(
            int accountId,
            BarRange window,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
