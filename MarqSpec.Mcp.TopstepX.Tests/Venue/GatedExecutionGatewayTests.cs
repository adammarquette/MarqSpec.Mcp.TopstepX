using FluentAssertions;
using MarqSpec.Client.ProjectX;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.Telemetry;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.Tests.Venue;

/// <summary>Gated execution — master switch, practice-only stage, and broker ack (gh#710).</summary>
public sealed class GatedExecutionGatewayTests
{
    private const int PracticeAccountId = 100;
    private const int FundedAccountId = 200;
    private const int UnknownAccountId = 300;

    /// <summary>Master enable off refuses before any venue order call.</summary>
    [Fact]
    public async Task PlaceOrder_DoesNotCallTheClient_WhenTradingIsDisabled()
    {
        (ExecutionOrderClient fake, IProjectXApiClient client) = ExecutionOrderClient.Create();
        GatedExecutionGateway gateway = Gateway(
            client,
            accounts: [PracticeAccount(PracticeAccountId)],
            enabled: false);

        ExecutionWriteResult result = await gateway.PlaceOrderAsync(
            SamplePlaceCommand(PracticeAccountId),
            CancellationToken.None);

        fake.PlaceOrderCount.Should().Be(0);
        result.Lifecycle.Should().Be(ExecutionWriteLifecycle.NotTransmitted);
        result.GateRefusal.Should().Be(TradingExecutionRefusal.TradingDisabled);
    }

    /// <summary>Evaluation or funded stages refuse before any venue order call.</summary>
    [Theory]
    [InlineData(FundedAccountId, AccountStage.Funded)]
    [InlineData(UnknownAccountId, AccountStage.Unknown)]
    public async Task PlaceOrder_DoesNotCallTheClient_WhenStageIsNotPractice(
        int accountId,
        AccountStage stage)
    {
        (ExecutionOrderClient fake, IProjectXApiClient client) = ExecutionOrderClient.Create();
        RecordingUnknownSignal signal = new();
        GatedExecutionGateway gateway = Gateway(
            client,
            accounts: [Account(accountId, stage)],
            enabled: true,
            unknownSignal: signal);

        ExecutionWriteResult result = await gateway.PlaceOrderAsync(
            SamplePlaceCommand(accountId),
            CancellationToken.None);

        fake.PlaceOrderCount.Should().Be(0);
        result.Lifecycle.Should().Be(ExecutionWriteLifecycle.NotTransmitted);
        result.GateRefusal.Should().NotBeNull();

        if (stage == AccountStage.Unknown)
        {
            signal.RefusedAccountIds.Should().ContainSingle().Which.Should().Be(accountId);
            result.GateRefusal.Should().Be(TradingExecutionRefusal.UnknownAccountStage(accountId));
        }
        else
        {
            signal.RefusedAccountIds.Should().BeEmpty();
            result.GateRefusal.Should().Be(TradingExecutionRefusal.NonPracticeAccountStage(accountId, stage));
        }
    }

    /// <summary>Practice account with enable on transmits and maps broker ack.</summary>
    [Fact]
    public async Task PlaceOrder_Acknowledges_WhenTheVenueReturnsSuccess()
    {
        (ExecutionOrderClient fake, IProjectXApiClient client) = ExecutionOrderClient.Create();
        fake.PlaceOrderAnswer = _ => new MarqSpec.Client.ProjectX.Api.Models.PlaceOrderResponse
        {
            Success = true,
            OrderId = 9001,
            ErrorCode = 0,
        };

        GatedExecutionGateway gateway = Gateway(
            client,
            accounts: [PracticeAccount(PracticeAccountId)],
            enabled: true);

        ExecutionWriteResult result = await gateway.PlaceOrderAsync(
            SamplePlaceCommand(PracticeAccountId),
            CancellationToken.None);

        fake.PlaceOrderCount.Should().Be(1);
        result.Lifecycle.Should().Be(ExecutionWriteLifecycle.Acknowledged);
        result.GateRefusal.Should().BeNull();
        result.OrderId.Should().Be(9001);
    }

    /// <summary>Venue success=false is transmitted but not broker-acked — not live.</summary>
    [Fact]
    public async Task PlaceOrder_IsNotAcknowledged_WhenTheVenueReturnsFailure()
    {
        (ExecutionOrderClient fake, IProjectXApiClient client) = ExecutionOrderClient.Create();
        fake.PlaceOrderAnswer = _ => new MarqSpec.Client.ProjectX.Api.Models.PlaceOrderResponse
        {
            Success = false,
            ErrorCode = 3,
            ErrorMessage = "rejected",
        };

        GatedExecutionGateway gateway = Gateway(
            client,
            accounts: [PracticeAccount(PracticeAccountId)],
            enabled: true);

        ExecutionWriteResult result = await gateway.PlaceOrderAsync(
            SamplePlaceCommand(PracticeAccountId),
            CancellationToken.None);

        fake.PlaceOrderCount.Should().Be(1);
        result.Lifecycle.Should().Be(ExecutionWriteLifecycle.PendingOrRejected);
        result.Lifecycle.BrokerAcknowledged.Should().BeFalse();
        result.Lifecycle.Transmitted.Should().BeTrue();
        result.OrderId.Should().BeNull();
        result.VenueErrorCode.Should().Be(3);
    }

    private static GatedExecutionGateway Gateway(
        IProjectXApiClient client,
        IReadOnlyList<VenueAccount> accounts,
        bool enabled,
        ITradingUnknownStageRefusalSignal? unknownSignal = null)
    {
        TradingExecutionGate gate = new(
            Options.Create(new TradingExecutionOptions { Enabled = enabled }),
            new FakeKillSwitch());

        StubAccountsGateway marketData = new(accounts);
        HostTelemetry telemetry = new();
        ProjectXExecutionGateway adapter = new(client, telemetry);

        return new GatedExecutionGateway(
            gate,
            marketData,
            adapter,
            unknownSignal ?? new NullTradingUnknownStageRefusalSignal());
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

    private static VenueAccount PracticeAccount(int accountId) => Account(accountId, AccountStage.Practice);

    private static VenueAccount Account(int accountId, AccountStage stage) =>
        new(accountId, stage, CanTrade: true, IsVisible: true, Balance: 50_000m);

    private sealed class FakeKillSwitch : ITradingKillSwitch
    {
        public bool IsEngaged => false;
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

    private sealed class RecordingUnknownSignal : ITradingUnknownStageRefusalSignal
    {
        public List<int> RefusedAccountIds { get; } = [];

        public void UnknownStageWriteRefused(int accountId) => RefusedAccountIds.Add(accountId);
    }
}
