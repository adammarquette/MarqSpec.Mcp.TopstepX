using FluentAssertions;
using MarqSpec.Client.ProjectX;
using MarqSpec.Client.ProjectX.Exceptions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.Telemetry;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.Extensions.Logging.Abstractions;
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
        RecordingUnknownSignal unknownSignal = new();
        (ExecutionOrderClient fake, IProjectXApiClient client) = ExecutionOrderClient.Create();
        StubAccountsGateway accountsGateway = new(AccountsFor(scenario));
        IExecutionGateway gateway = Gateway(
            client,
            accountsGateway,
            unknownSignal,
            enabled: scenario != GateScenario.Disabled,
            killSwitchEngaged: scenario == GateScenario.KillSwitch);

        int accountId = AccountIdFor(scenario);
        ExecutionWriteResult result = await InvokeAsync(gateway, verb, accountId);

        ClientCalls(fake, verb).Should().Be(0);
        result.Outcome.Should().Be(ExecutionWriteOutcome.NotTransmitted);
        result.GateRefusal.Should().Be(ExpectedRefusal(scenario, accountId));
        result.IsRetrySafe.Should().BeTrue();

        if (scenario == GateScenario.Unknown)
        {
            unknownSignal.Invocations.Should().Be(1);
        }
        else
        {
            unknownSignal.Invocations.Should().Be(0);
        }
    }

    [Fact]
    public async Task PlaceOrder_RequestsOnlyActiveAccountsFromTheMarketDataGateway()
    {
        (ExecutionOrderClient fake, IProjectXApiClient client) = ExecutionOrderClient.Create();
        StubAccountsGateway accountsGateway = new([PracticeAccount()]);
        IExecutionGateway gateway = Gateway(
            client,
            accountsGateway,
            new RecordingUnknownSignal(),
            enabled: true,
            killSwitchEngaged: false);

        await gateway.PlaceOrder(SamplePlaceCommand(), CancellationToken.None);

        accountsGateway.LastOnlyActive.Should().BeTrue();
        fake.PlaceOrderCount.Should().Be(1);
    }

    [Fact]
    public async Task PlaceOrder_Acknowledges_WhenTheVenueReturnsSuccessWithOrderId()
    {
        (ExecutionOrderClient fake, IProjectXApiClient client) = ExecutionOrderClient.Create();
        fake.PlaceOrderAnswer = (_, _) => Task.FromResult(new MarqSpec.Client.ProjectX.Api.Models.PlaceOrderResponse
        {
            Success = true,
            OrderId = 9001,
            ErrorCode = 0,
        });

        IExecutionGateway gateway = Gateway(client, [PracticeAccount()], enabled: true);

        ExecutionWriteResult result = await gateway.PlaceOrder(SamplePlaceCommand(), CancellationToken.None);

        fake.PlaceOrderCount.Should().Be(1);
        result.Outcome.Should().Be(ExecutionWriteOutcome.BrokerAcknowledged);
        result.VendorErrorCode.Should().BeNull();
        result.HttpStatusCode.Should().BeNull();
        result.OrderId.Should().Be(9001);
        result.IsRetrySafe.Should().BeFalse();
    }

    [Fact]
    public async Task PlaceOrder_IsVenueRejected_WhenTheVenueReturnsFailure()
    {
        (ExecutionOrderClient fake, IProjectXApiClient client) = ExecutionOrderClient.Create();
        fake.PlaceOrderAnswer = (_, _) => Task.FromResult(new MarqSpec.Client.ProjectX.Api.Models.PlaceOrderResponse
        {
            Success = false,
            ErrorCode = 3,
            ErrorMessage = "rejected",
        });

        IExecutionGateway gateway = Gateway(client, [PracticeAccount()], enabled: true);

        ExecutionWriteResult result = await gateway.PlaceOrder(SamplePlaceCommand(), CancellationToken.None);

        result.Outcome.Should().Be(ExecutionWriteOutcome.VenueRejected);
        result.VendorErrorCode.Should().Be(3);
        result.HttpStatusCode.Should().BeNull();
        result.OrderId.Should().BeNull();
        result.IsRetrySafe.Should().BeTrue();
    }

    [Fact]
    public async Task PlaceOrder_IsNotLive_WhenSuccessArrivesWithoutOrderId()
    {
        (ExecutionOrderClient fake, IProjectXApiClient client) = ExecutionOrderClient.Create();
        fake.PlaceOrderAnswer = (_, _) => Task.FromResult(new MarqSpec.Client.ProjectX.Api.Models.PlaceOrderResponse
        {
            Success = true,
            OrderId = null,
            ErrorCode = 0,
        });

        IExecutionGateway gateway = Gateway(client, [PracticeAccount()], enabled: true);

        ExecutionWriteResult result = await gateway.PlaceOrder(SamplePlaceCommand(), CancellationToken.None);

        result.Outcome.Should().Be(ExecutionWriteOutcome.BrokerSuccessWithoutOrderId);
        result.OrderId.Should().BeNull();
        result.VendorErrorCode.Should().BeNull();
        result.HttpStatusCode.Should().BeNull();
        result.IsRetrySafe.Should().BeFalse();
    }

    [Fact]
    public async Task PlaceOrder_ReturnsTransmittedUnknown_WhenTheGuardedCallTimesOut()
    {
        (ExecutionOrderClient fake, IProjectXApiClient client) = ExecutionOrderClient.Create();
        fake.PlaceOrderAnswer = (_, _) => throw new TimeoutException("simulated timeout");

        IExecutionGateway gateway = Gateway(client, [PracticeAccount()], enabled: true);

        ExecutionWriteResult result = await gateway.PlaceOrder(SamplePlaceCommand(), CancellationToken.None);

        result.Outcome.Should().Be(ExecutionWriteOutcome.TransmittedOutcomeUnknown);
        result.IsRetrySafe.Should().BeFalse();
        fake.PlaceOrderCount.Should().Be(1);
    }

    [Fact]
    public async Task PlaceOrder_ReturnsTransmittedUnknown_WhenTheCallerCancelsMidFlight()
    {
        using CancellationTokenSource cancellation = new();
        (ExecutionOrderClient fake, IProjectXApiClient client) = ExecutionOrderClient.Create();
        fake.PlaceOrderAnswer = async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new MarqSpec.Client.ProjectX.Api.Models.PlaceOrderResponse { Success = true, OrderId = 1 };
        };

        IExecutionGateway gateway = Gateway(client, [PracticeAccount()], enabled: true);

        Task<ExecutionWriteResult> pending = gateway.PlaceOrder(SamplePlaceCommand(), cancellation.Token);
        await Task.Delay(50);
        await cancellation.CancelAsync();

        ExecutionWriteResult result = await pending;

        result.Outcome.Should().Be(ExecutionWriteOutcome.TransmittedOutcomeUnknown);
        result.IsRetrySafe.Should().BeFalse();
        fake.PlaceOrderCount.Should().Be(1);
    }

    [Fact]
    public async Task PlaceOrder_ReturnsNotTransmitted_WhenRequestMappingFailsBeforeAnyVenueCall()
    {
        (ExecutionOrderClient fake, IProjectXApiClient client) = ExecutionOrderClient.Create();
        IExecutionGateway gateway = Gateway(client, [PracticeAccount()], enabled: true);

        ExecutionPlaceOrderCommand invalid = SamplePlaceCommand() with
        {
            OrderType = (ExecutionOrderType)999,
        };

        ExecutionWriteResult result = await gateway.PlaceOrder(invalid, CancellationToken.None);

        result.Outcome.Should().Be(ExecutionWriteOutcome.NotTransmitted);
        result.GateRefusal.Should().BeNull();
        result.IsRetrySafe.Should().BeTrue();
        fake.PlaceOrderCount.Should().Be(0);
    }

    [Fact]
    public async Task PlaceOrder_ReturnsNotTransmitted_WhenTheGuardTranslates401()
    {
        (ExecutionOrderClient fake, IProjectXApiClient client) = ExecutionOrderClient.Create();
        fake.PlaceOrderAnswer = (_, _) => throw new ProjectXApiException("vendor secret", 401);

        IExecutionGateway gateway = Gateway(client, [PracticeAccount()], enabled: true);

        ExecutionWriteResult result = await gateway.PlaceOrder(SamplePlaceCommand(), CancellationToken.None);

        result.Outcome.Should().Be(ExecutionWriteOutcome.NotTransmitted);
        result.GateRefusal.Should().BeNull();
        result.IsRetrySafe.Should().BeTrue();
        fake.PlaceOrderCount.Should().Be(1);
    }

    [Fact]
    public async Task PlaceOrder_ReturnsNotTransmitted_WhenTheGuardTranslatesAuthenticationFailure()
    {
        (ExecutionOrderClient fake, IProjectXApiClient client) = ExecutionOrderClient.Create();
        fake.PlaceOrderAnswer = (_, _) => throw new AuthenticationException("vendor secret");

        IExecutionGateway gateway = Gateway(client, [PracticeAccount()], enabled: true);

        ExecutionWriteResult result = await gateway.PlaceOrder(SamplePlaceCommand(), CancellationToken.None);

        result.Outcome.Should().Be(ExecutionWriteOutcome.NotTransmitted);
        result.HttpStatusCode.Should().BeNull();
        result.VendorErrorCode.Should().BeNull();
        result.IsRetrySafe.Should().BeTrue();
        fake.PlaceOrderCount.Should().Be(1);
    }

    [Fact]
    public async Task PlaceOrder_ReturnsVenueRejected_WhenTheGuardTranslatesA4xxApiException()
    {
        (ExecutionOrderClient fake, IProjectXApiClient client) = ExecutionOrderClient.Create();
        fake.PlaceOrderAnswer = (_, _) => throw new ProjectXApiException("vendor secret", 403);

        IExecutionGateway gateway = Gateway(client, [PracticeAccount()], enabled: true);

        ExecutionWriteResult result = await gateway.PlaceOrder(SamplePlaceCommand(), CancellationToken.None);

        result.Outcome.Should().Be(ExecutionWriteOutcome.VenueRejected);
        result.HttpStatusCode.Should().Be(403);
        result.VendorErrorCode.Should().BeNull();
        result.IsRetrySafe.Should().BeTrue();
    }

    [Fact]
    public async Task PlaceOrder_ReturnsTransmittedUnknown_WhenTheGuardTranslatesA5xxApiException()
    {
        (ExecutionOrderClient fake, IProjectXApiClient client) = ExecutionOrderClient.Create();
        fake.PlaceOrderAnswer = (_, _) => throw new ProjectXApiException("vendor secret", 503);

        IExecutionGateway gateway = Gateway(client, [PracticeAccount()], enabled: true);

        ExecutionWriteResult result = await gateway.PlaceOrder(SamplePlaceCommand(), CancellationToken.None);

        result.Outcome.Should().Be(ExecutionWriteOutcome.TransmittedOutcomeUnknown);
        result.IsRetrySafe.Should().BeFalse();
    }

    [Theory]
    [InlineData(WriteVerb.Modify)]
    [InlineData(WriteVerb.Cancel)]
    [InlineData(WriteVerb.Close)]
    [InlineData(WriteVerb.PartialClose)]
    public async Task NonPlaceVerb_Acknowledges_WhenTheVenueReturnsSuccess(WriteVerb verb)
    {
        (ExecutionOrderClient fake, IProjectXApiClient client) = ExecutionOrderClient.Create();
        IExecutionGateway gateway = Gateway(client, [PracticeAccount()], enabled: true);

        ExecutionWriteResult result = await InvokeAsync(gateway, verb, PracticeAccountId);

        result.Outcome.Should().Be(ExecutionWriteOutcome.BrokerAcknowledged);
        ClientCalls(fake, verb).Should().Be(1);
    }

    [Theory]
    [InlineData(WriteVerb.Modify)]
    [InlineData(WriteVerb.Cancel)]
    [InlineData(WriteVerb.Close)]
    [InlineData(WriteVerb.PartialClose)]
    public async Task NonPlaceVerb_IsVenueRejected_WhenTheVenueReturnsFailure(WriteVerb verb)
    {
        (ExecutionOrderClient fake, IProjectXApiClient client) = ExecutionOrderClient.Create();
        ConfigureFailure(fake, verb);

        IExecutionGateway gateway = Gateway(client, [PracticeAccount()], enabled: true);

        ExecutionWriteResult result = await InvokeAsync(gateway, verb, PracticeAccountId);

        result.Outcome.Should().Be(ExecutionWriteOutcome.VenueRejected);
        result.VendorErrorCode.Should().Be(7);
        result.HttpStatusCode.Should().BeNull();
        result.IsRetrySafe.Should().BeTrue();
    }

    [Fact]
    public async Task ModifyOrder_ReturnsTransmittedUnknown_WhenTheGuardTranslatesHttpFailure()
    {
        (ExecutionOrderClient fake, IProjectXApiClient client) = ExecutionOrderClient.Create();
        fake.ModifyOrderAnswer = (_, _) => throw new HttpRequestException("network");

        IExecutionGateway gateway = Gateway(client, [PracticeAccount()], enabled: true);

        ExecutionWriteResult result = await gateway.ModifyOrder(
            new ExecutionModifyOrderCommand(PracticeAccountId, OrderId: 1, Size: 1, LimitPrice: null, StopPrice: null),
            CancellationToken.None);

        result.Outcome.Should().Be(ExecutionWriteOutcome.TransmittedOutcomeUnknown);
        result.IsRetrySafe.Should().BeFalse();
        fake.ModifyOrderCount.Should().Be(1);
    }

    [Fact]
    public async Task CancelOrder_ReturnsVenueRejected_WhenTheGuardTranslatesA4xxApiException()
    {
        (ExecutionOrderClient fake, IProjectXApiClient client) = ExecutionOrderClient.Create();
        fake.CancelOrderAnswer = (_, _, _) => throw new ProjectXApiException("vendor secret", 400);

        IExecutionGateway gateway = Gateway(client, [PracticeAccount()], enabled: true);

        ExecutionWriteResult result = await gateway.CancelOrder(
            new ExecutionCancelOrderCommand(PracticeAccountId, OrderId: 1),
            CancellationToken.None);

        result.Outcome.Should().Be(ExecutionWriteOutcome.VenueRejected);
        result.HttpStatusCode.Should().Be(400);
        result.VendorErrorCode.Should().BeNull();
    }

    public static IEnumerable<object[]> MidFlightCancelVerbs =>
    [
        [WriteVerb.Modify],
        [WriteVerb.Cancel],
        [WriteVerb.Close],
        [WriteVerb.PartialClose],
    ];

    [Theory]
    [MemberData(nameof(MidFlightCancelVerbs))]
    public async Task Write_ReturnsTransmittedUnknown_WhenTheCallerCancelsMidFlight(WriteVerb verb)
    {
        using CancellationTokenSource cancellation = new();
        (ExecutionOrderClient fake, IProjectXApiClient client) = ExecutionOrderClient.Create();
        ConfigureMidFlightDelay(fake, verb);

        IExecutionGateway gateway = Gateway(client, [PracticeAccount()], enabled: true);

        Task<ExecutionWriteResult> pending = InvokeAsync(gateway, verb, PracticeAccountId, cancellation.Token);
        await Task.Delay(50);
        await cancellation.CancelAsync();

        ExecutionWriteResult result = await pending;

        result.Outcome.Should().Be(ExecutionWriteOutcome.TransmittedOutcomeUnknown);
        ClientCalls(fake, verb).Should().Be(1);
    }

    [Fact]
    public async Task ClosePosition_ReturnsTransmittedUnknown_WhenTheGuardedCallTimesOut()
    {
        (ExecutionOrderClient fake, IProjectXApiClient client) = ExecutionOrderClient.Create();
        fake.ClosePositionAnswer = (_, _, _) => throw new TimeoutException("simulated timeout");

        IExecutionGateway gateway = Gateway(client, [PracticeAccount()], enabled: true);

        ExecutionWriteResult result = await gateway.ClosePosition(
            new ExecutionClosePositionCommand(PracticeAccountId, ContractId: "CON.F.US.EP.Z26"),
            CancellationToken.None);

        result.Outcome.Should().Be(ExecutionWriteOutcome.TransmittedOutcomeUnknown);
        fake.ClosePositionCount.Should().Be(1);
    }

    [Fact]
    public async Task PartialClosePosition_ReturnsTransmittedUnknown_WhenTheGuardedCallTimesOut()
    {
        (ExecutionOrderClient fake, IProjectXApiClient client) = ExecutionOrderClient.Create();
        fake.PartialClosePositionAnswer = (_, _, _, _) => throw new TimeoutException("simulated timeout");

        IExecutionGateway gateway = Gateway(client, [PracticeAccount()], enabled: true);

        ExecutionWriteResult result = await gateway.PartialClosePosition(
            new ExecutionPartialClosePositionCommand(PracticeAccountId, ContractId: "CON.F.US.EP.Z26", Size: 1),
            CancellationToken.None);

        result.Outcome.Should().Be(ExecutionWriteOutcome.TransmittedOutcomeUnknown);
        fake.PartialClosePositionCount.Should().Be(1);
    }

    [Fact]
    public async Task PlaceOrder_Rethrows_WhenCancelledBeforeTransmitStarts()
    {
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        (ExecutionOrderClient fake, IProjectXApiClient client) = ExecutionOrderClient.Create();
        IExecutionGateway gateway = Gateway(client, [PracticeAccount()], enabled: true);

        Func<Task> act = () => gateway.PlaceOrder(SamplePlaceCommand(), cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        fake.PlaceOrderCount.Should().Be(0);
    }

    private static void ConfigureMidFlightDelay(ExecutionOrderClient fake, WriteVerb verb)
    {
        switch (verb)
        {
            case WriteVerb.Modify:
                fake.ModifyOrderAnswer = async (_, token) =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return new MarqSpec.Client.ProjectX.Api.Models.ModifyOrderResponse { Success = true };
                };
                break;
            case WriteVerb.Cancel:
                fake.CancelOrderAnswer = async (_, _, token) =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return new MarqSpec.Client.ProjectX.Api.Models.CancelOrderResponse { Success = true };
                };
                break;
            case WriteVerb.Close:
                fake.ClosePositionAnswer = async (_, _, token) =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return new MarqSpec.Client.ProjectX.Api.Models.ClosePositionResponse { Success = true };
                };
                break;
            case WriteVerb.PartialClose:
                fake.PartialClosePositionAnswer = async (_, _, _, token) =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return new MarqSpec.Client.ProjectX.Api.Models.PartialClosePositionResponse { Success = true };
                };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(verb), verb, null);
        }
    }

    private static void ConfigureFailure(ExecutionOrderClient fake, WriteVerb verb)
    {
        switch (verb)
        {
            case WriteVerb.Modify:
                fake.ModifyOrderAnswer = (_, _) => Task.FromResult(new MarqSpec.Client.ProjectX.Api.Models.ModifyOrderResponse
                {
                    Success = false,
                    ErrorCode = 7,
                    ErrorMessage = "no",
                });
                break;
            case WriteVerb.Cancel:
                fake.CancelOrderAnswer = (_, _, _) => Task.FromResult(new MarqSpec.Client.ProjectX.Api.Models.CancelOrderResponse
                {
                    Success = false,
                    ErrorCode = 7,
                    ErrorMessage = "no",
                });
                break;
            case WriteVerb.Close:
                fake.ClosePositionAnswer = (_, _, _) => Task.FromResult(new MarqSpec.Client.ProjectX.Api.Models.ClosePositionResponse
                {
                    Success = false,
                    ErrorCode = 7,
                    ErrorMessage = "no",
                });
                break;
            case WriteVerb.PartialClose:
                fake.PartialClosePositionAnswer = (_, _, _, _) =>
                    Task.FromResult(new MarqSpec.Client.ProjectX.Api.Models.PartialClosePositionResponse
                    {
                        Success = false,
                        ErrorCode = 7,
                        ErrorMessage = "no",
                    });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(verb), verb, null);
        }
    }

    private static TradingExecutionRefusal ExpectedRefusal(GateScenario scenario, int accountId) => scenario switch
    {
        GateScenario.Disabled => TradingExecutionRefusal.TradingDisabled,
        GateScenario.KillSwitch => TradingExecutionRefusal.KillSwitchEngaged,
        GateScenario.Funded => TradingExecutionRefusal.NonPracticeAccountStage(accountId, AccountStage.Funded),
        GateScenario.Unknown => TradingExecutionRefusal.UnknownAccountStage(accountId),
        GateScenario.AccountNotFound => TradingExecutionRefusal.AccountNotFound(accountId),
        GateScenario.Inactive => TradingExecutionRefusal.AccountNotActive(accountId),
        _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null),
    };

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
        int accountId,
        CancellationToken cancellationToken = default) =>
        verb switch
        {
            WriteVerb.Place => gateway.PlaceOrder(SamplePlaceCommand(accountId), cancellationToken),
            WriteVerb.Modify => gateway.ModifyOrder(
                new ExecutionModifyOrderCommand(accountId, OrderId: 1, Size: 1, LimitPrice: null, StopPrice: null),
                cancellationToken),
            WriteVerb.Cancel => gateway.CancelOrder(new ExecutionCancelOrderCommand(accountId, OrderId: 1), cancellationToken),
            WriteVerb.Close => gateway.ClosePosition(
                new ExecutionClosePositionCommand(accountId, ContractId: "CON.F.US.EP.Z26"),
                cancellationToken),
            WriteVerb.PartialClose => gateway.PartialClosePosition(
                new ExecutionPartialClosePositionCommand(accountId, ContractId: "CON.F.US.EP.Z26", Size: 1),
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(verb), verb, null),
        };

    private static IExecutionGateway Gateway(
        IProjectXApiClient client,
        IReadOnlyList<VenueAccount> accounts,
        bool enabled = true,
        bool killSwitchEngaged = false) =>
        Gateway(
            client,
            new StubAccountsGateway(accounts),
            new RecordingUnknownSignal(),
            enabled,
            killSwitchEngaged);

    private static IExecutionGateway Gateway(
        IProjectXApiClient client,
        StubAccountsGateway accountsGateway,
        RecordingUnknownSignal unknownSignal,
        bool enabled,
        bool killSwitchEngaged)
    {
        TradingExecutionGate gate = new(
            Options.Create(new TradingExecutionOptions { Enabled = enabled }),
            new FakeKillSwitch { Engaged = killSwitchEngaged });

        return new ProjectXExecutionGateway(
            gate,
            accountsGateway,
            unknownSignal,
            client,
            new HostTelemetry(),
            NullLogger<ProjectXExecutionGateway>.Instance);
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

    private sealed class RecordingUnknownSignal : ITradingUnknownStageRefusalSignal
    {
        public int Invocations { get; private set; }

        public void UnknownStageWriteRefused(int accountId) => Invocations++;
    }

    private sealed class StubAccountsGateway(IReadOnlyList<VenueAccount> accounts) : IMarketDataGateway
    {
        public bool? LastOnlyActive { get; private set; }

        public string VenueId => "stub";

        public Task<IReadOnlyList<VenueAccount>> GetAccountsAsync(
            bool onlyActive,
            CancellationToken cancellationToken)
        {
            LastOnlyActive = onlyActive;
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
