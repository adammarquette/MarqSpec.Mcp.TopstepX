using FluentAssertions;
using MarqSpec.Client.ProjectX;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.Telemetry;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.Tests.Venue;

/// <summary>Gated execution — every write path and outcome (gh#710).</summary>
[Trait("Gate", "ADR-0024")]
public sealed class ProjectXExecutionGatewayTests
{
    private const int PracticeAccountId = 100;
    private const int FundedAccountId = 200;
    private const int UnknownAccountId = 300;
    private const int MissingAccountId = 400;
    private const int InactivePracticeAccountId = 500;

    public static IEnumerable<object[]> GateRefusalCases =>
        from verb in Enum.GetValues<WriteVerb>()
        from scenario in Enum.GetValues<GateScenario>()
        select new object[] { verb, scenario };

    [Theory]
    [MemberData(nameof(GateRefusalCases))]
    public async Task Write_DoesNotCallTheClient_WhenTheGateRefuses(WriteVerb verb, GateScenario scenario)
    {
        (ExecutionOrderClient fake, IProjectXApiClient client) = ExecutionOrderClient.Create();
        IExecutionGateway gateway = Gateway(
            client,
            accounts: AccountsFor(scenario),
            enabled: scenario != GateScenario.Disabled,
            killSwitchEngaged: scenario == GateScenario.KillSwitch);

        ExecutionWriteResult result = await InvokeAsync(gateway, verb, AccountIdFor(scenario));

        ClientCalls(fake, verb).Should().Be(0);
        result.Outcome.Should().Be(ExecutionWriteOutcome.NotTransmitted);
        result.GateRefusal.Should().NotBeNull();
    }

    [Fact]
    public async Task PlaceOrder_Acknowledges_WhenTheVenueReturnsSuccessWithOrderId()
    {
        (ExecutionOrderClient fake, IProjectXApiClient client) = ExecutionOrderClient.Create();
        fake.PlaceOrderAnswer = _ => new MarqSpec.Client.ProjectX.Api.Models.PlaceOrderResponse
        {
            Success = true,
            OrderId = 9001,
            ErrorCode = 0,
        };

        IExecutionGateway gateway = Gateway(client, accounts: [PracticeAccount()], enabled: true);

        ExecutionWriteResult result = await gateway.PlaceOrder(SamplePlaceCommand(), CancellationToken.None);

        fake.PlaceOrderCount.Should().Be(1);
        result.Outcome.Should().Be(ExecutionWriteOutcome.BrokerAcknowledged);
        result.VenueErrorCode.Should().BeNull();
        result.OrderId.Should().Be(9001);
    }

    [Fact]
    public async Task PlaceOrder_IsVenueRejected_WhenTheVenueReturnsFailure()
    {
        (ExecutionOrderClient fake, IProjectXApiClient client) = ExecutionOrderClient.Create();
        fake.PlaceOrderAnswer = _ => new MarqSpec.Client.ProjectX.Api.Models.PlaceOrderResponse
        {
            Success = false,
            ErrorCode = 3,
            ErrorMessage = "rejected",
        };

        IExecutionGateway gateway = Gateway(client, accounts: [PracticeAccount()], enabled: true);

        ExecutionWriteResult result = await gateway.PlaceOrder(SamplePlaceCommand(), CancellationToken.None);

        result.Outcome.Should().Be(ExecutionWriteOutcome.VenueRejected);
        result.VenueErrorCode.Should().Be(3);
        result.OrderId.Should().BeNull();
        result.Lifecycle.BrokerAcknowledged.Should().BeFalse();
    }

    [Fact]
    public async Task PlaceOrder_IsNotLive_WhenSuccessArrivesWithoutOrderId()
    {
        (ExecutionOrderClient fake, IProjectXApiClient client) = ExecutionOrderClient.Create();
        fake.PlaceOrderAnswer = _ => new MarqSpec.Client.ProjectX.Api.Models.PlaceOrderResponse
        {
            Success = true,
            OrderId = null,
            ErrorCode = 0,
        };

        IExecutionGateway gateway = Gateway(client, accounts: [PracticeAccount()], enabled: true);

        ExecutionWriteResult result = await gateway.PlaceOrder(SamplePlaceCommand(), CancellationToken.None);

        result.Outcome.Should().Be(ExecutionWriteOutcome.BrokerSuccessWithoutOrderId);
        result.OrderId.Should().BeNull();
        result.VenueErrorCode.Should().BeNull();
        result.Lifecycle.BrokerAcknowledged.Should().BeFalse();
    }

    [Fact]
    public async Task PlaceOrder_ReturnsTransmittedUnknown_WhenTheClientThrowsAfterSend()
    {
        (ExecutionOrderClient fake, IProjectXApiClient client) = ExecutionOrderClient.Create();
        fake.PlaceOrderAnswer = _ => throw new TimeoutException("simulated timeout");

        IExecutionGateway gateway = Gateway(client, accounts: [PracticeAccount()], enabled: true);

        ExecutionWriteResult result = await gateway.PlaceOrder(SamplePlaceCommand(), CancellationToken.None);

        result.Outcome.Should().Be(ExecutionWriteOutcome.TransmittedOutcomeUnknown);
        result.Lifecycle.Transmitted.Should().BeTrue();
        result.Lifecycle.BrokerAcknowledged.Should().BeFalse();
    }

    private static int AccountIdFor(GateScenario scenario) => scenario switch
    {
        GateScenario.Funded => FundedAccountId,
        GateScenario.Unknown => UnknownAccountId,
        GateScenario.AccountNotFound => MissingAccountId,
        GateScenario.Inactive => InactivePracticeAccountId,
        _ => PracticeAccountId,
    };

    private static IReadOnlyList<VenueAccount> AccountsFor(GateScenario scenario) => scenario switch
    {
        GateScenario.AccountNotFound => [PracticeAccount()],
        GateScenario.Funded => [Account(FundedAccountId, AccountStage.Funded)],
        GateScenario.Unknown => [Account(UnknownAccountId, AccountStage.Unknown)],
        GateScenario.Inactive => [Account(InactivePracticeAccountId, AccountStage.Practice, canTrade: false)],
        _ => [PracticeAccount()],
    };

    private static int ClientCalls(ExecutionOrderClient fake, WriteVerb verb) => verb switch
    {
        WriteVerb.Place => fake.PlaceOrderCount,
        WriteVerb.Modify => fake.ModifyOrderCount,
        WriteVerb.Cancel => fake.CancelOrderCount,
        WriteVerb.Close => fake.ClosePositionCount,
        WriteVerb.PartialClose => fake.PartialClosePositionCount,
        _ => throw new ArgumentOutOfRangeException(nameof(verb), verb, null),
    };

    private static Task<ExecutionWriteResult> InvokeAsync(
        IExecutionGateway gateway,
        WriteVerb verb,
        int accountId) =>
        verb switch
        {
            WriteVerb.Place => gateway.PlaceOrder(SamplePlaceCommand(accountId), CancellationToken.None),
            WriteVerb.Modify => gateway.ModifyOrder(
                new ExecutionModifyOrderCommand(accountId, OrderId: 1, Size: 1, LimitPrice: null, StopPrice: null),
                CancellationToken.None),
            WriteVerb.Cancel => gateway.CancelOrder(new ExecutionCancelOrderCommand(accountId, OrderId: 1), CancellationToken.None),
            WriteVerb.Close => gateway.ClosePosition(
                new ExecutionClosePositionCommand(accountId, ContractId: "CON.F.US.EP.Z26"),
                CancellationToken.None),
            WriteVerb.PartialClose => gateway.PartialClosePosition(
                new ExecutionPartialClosePositionCommand(accountId, ContractId: "CON.F.US.EP.Z26", Size: 1),
                CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(verb), verb, null),
        };

    private static IExecutionGateway Gateway(
        IProjectXApiClient client,
        IReadOnlyList<VenueAccount> accounts,
        bool enabled,
        bool killSwitchEngaged = false)
    {
        TradingExecutionGate gate = new(
            Options.Create(new TradingExecutionOptions { Enabled = enabled }),
            new FakeKillSwitch { Engaged = killSwitchEngaged });

        return new ProjectXExecutionGateway(
            gate,
            new StubAccountsGateway(accounts),
            new NullTradingUnknownStageRefusalSignal(),
            client,
            new HostTelemetry());
    }

    private static ExecutionPlaceOrderCommand SamplePlaceCommand(int accountId = PracticeAccountId) =>
        new(
            accountId,
            ContractId: "CON.F.US.EP.Z26",
            OrderType: ExecutionOrderType.Market,
            Side: VenueSide.Buy,
            Size: 1,
            LimitPrice: null,
            StopPrice: null,
            TrailPrice: null);

    private static VenueAccount PracticeAccount() => Account(PracticeAccountId, AccountStage.Practice);

    private static VenueAccount Account(int accountId, AccountStage stage, bool canTrade = true) =>
        new(accountId, stage, CanTrade: canTrade, IsVisible: true, Balance: 50_000m);

    public enum WriteVerb
    {
        Place,
        Modify,
        Cancel,
        Close,
        PartialClose,
    }

    public enum GateScenario
    {
        Disabled,
        KillSwitch,
        Funded,
        Unknown,
        AccountNotFound,
        Inactive,
    }

    private sealed class FakeKillSwitch : ITradingKillSwitch
    {
        public bool Engaged { get; init; }

        public bool IsEngaged => Engaged;
    }

    private sealed class StubAccountsGateway(IReadOnlyList<VenueAccount> accounts) : IMarketDataGateway
    {
        public int GetAccountsCallCount { get; private set; }

        public string VenueId => "stub";

        public Task<IReadOnlyList<VenueAccount>> GetAccountsAsync(
            bool onlyActive,
            CancellationToken cancellationToken)
        {
            GetAccountsCallCount++;
            _ = onlyActive;
            return Task.FromResult(accounts);
        }

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
