using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Telemetry;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.Tests.Configuration;

/// <summary>Central risk gate — stage, caps, dry-run, and audit (gh#711, ADR-0024).</summary>
[Trait("Gate", "ADR-0024")]
public sealed class TradingRiskGateTests
{
    private const int PracticeAccountId = 100;
    private const int FundedAccountId = 200;
    private const int UnknownAccountId = 300;
    private const string PracticeName = "PRAC-V2-00000-00000001";
    private const string FundedName = "EXPRESS-V2-00000-00000004";
    private const string UnknownName = "PRACX-V2-00000-00000005";
    private const string MesContract = "CON.F.US.MES.Z26";
    private const string CleContract = "CON.F.US.CLE.Z26";

    private readonly FakeKillSwitch _killSwitch = new();
    private readonly RecordingUnknownSignal _unknownSignal = new();
    private readonly FakeExecutionGateway _gateway = new();
    private readonly RecordingAuditLog _audit = new();
    private readonly FakeDailyCapCoordinator _daily = new();

    [Fact]
    public async Task TradingRiskGate_RefusesAndSkipsGateway_WhenTradingIsDisabled()
    {
        TradingRiskGate gate = Gate(new TradingExecutionOptions { Enabled = false });

        TradingRiskGateResult result = await gate.SubmitPlace(
            SamplePlace(PracticeAccountId),
            Context(dryRun: false),
            CancellationToken.None);

        _gateway.PlaceOrderCalls.Should().Be(0);
        result.Outcome.Should().Be(TradingAttemptOutcome.Refused);
        result.Refusal.Should().Be(TradingExecutionRefusal.TradingDisabled);
        _audit.Entries.Should().BeEmpty();
        _daily.LockAcquisitions.Should().Be(0);
    }

    [Fact]
    public async Task TradingRiskGate_RefusesAndSkipsGateway_WhenKillSwitchIsEngaged()
    {
        _killSwitch.IsEngaged = true;
        TradingRiskGate gate = Gate(new TradingExecutionOptions { Enabled = true });

        TradingRiskGateResult result = await gate.SubmitPlace(
            SamplePlace(PracticeAccountId),
            Context(dryRun: false),
            CancellationToken.None);

        _gateway.PlaceOrderCalls.Should().Be(0);
        result.Refusal.Should().Be(TradingExecutionRefusal.KillSwitchEngaged);
    }

    [Theory]
    [InlineData(FundedAccountId, FundedName, AccountStage.Funded, false)]
    [InlineData(UnknownAccountId, UnknownName, AccountStage.Unknown, true)]
    public async Task TradingRiskGate_RefusesNonPracticeStages_BeforeGateway(
        int accountId,
        string accountName,
        AccountStage expectedStage,
        bool expectSignal)
    {
        TradingRiskGate gate = Gate(
            new TradingExecutionOptions { Enabled = true },
            accounts: [Account(accountId, expectedStage, accountName)]);

        TradingRiskGateResult result = await gate.SubmitPlace(
            SamplePlace(accountId),
            Context(dryRun: false),
            CancellationToken.None);

        _gateway.PlaceOrderCalls.Should().Be(0);
        result.Outcome.Should().Be(TradingAttemptOutcome.Refused);
        result.ParsedStage.Should().Be(expectedStage);

        if (expectSignal)
        {
            _unknownSignal.RefusedAccountIds.Should().ContainSingle().Which.Should().Be(accountId);
            result.Refusal.Should().Be(TradingExecutionRefusal.UnknownAccountStage(accountId));
        }
        else
        {
            _unknownSignal.RefusedAccountIds.Should().BeEmpty();
            result.Refusal.Should().Be(TradingExecutionRefusal.NonPracticeAccountStage(accountId, expectedStage));
        }
    }

    [Fact]
    public async Task TradingRiskGate_Refuses_WhenAccountIsNotOnTheAllowlist()
    {
        TradingRiskGate gate = Gate(new TradingExecutionOptions
        {
            Enabled = true,
            AllowedAccountIds = "999",
        });

        TradingRiskGateResult result = await gate.SubmitPlace(
            SamplePlace(PracticeAccountId),
            Context(dryRun: false),
            CancellationToken.None);

        _gateway.PlaceOrderCalls.Should().Be(0);
        result.Refusal.Should().Be(TradingExecutionRefusal.AccountNotAllowlisted(PracticeAccountId));
    }

    [Fact]
    public async Task TradingRiskGate_RefusesDeniedSymbol_WhenProductMatchesDenylist()
    {
        TradingRiskGate gate = Gate(
            new TradingExecutionOptions { Enabled = true, DeniedSymbols = "CL" },
            instruments: "MES,CL");

        TradingRiskGateResult result = await gate.SubmitPlace(
            SamplePlace(PracticeAccountId, contractId: CleContract),
            Context(dryRun: false),
            CancellationToken.None);

        result.Outcome.Should().Be(TradingAttemptOutcome.Refused);
        result.Refusal.Should().Be(TradingExecutionRefusal.SymbolDenied("CL"));
    }

    [Fact]
    public async Task TradingRiskGate_RefusesUnmappedContract_WhenInstrumentNotServed()
    {
        TradingRiskGate gate = Gate(new TradingExecutionOptions { Enabled = true });

        TradingRiskGateResult result = await gate.SubmitPlace(
            SamplePlace(PracticeAccountId, contractId: "CON.F.US.EP.Z26"),
            Context(dryRun: false),
            CancellationToken.None);

        result.Refusal!.Value.Code.Should().Be("contract_product_not_mapped"); // struct nullable
    }

    [Fact]
    public async Task TradingRiskGate_RefusesMalformedContractId()
    {
        TradingRiskGate gate = Gate(new TradingExecutionOptions { Enabled = true });

        TradingRiskGateResult result = await gate.SubmitPlace(
            SamplePlace(PracticeAccountId, contractId: "not-a-contract"),
            Context(dryRun: false),
            CancellationToken.None);

        result.Refusal.Should().Be(TradingExecutionRefusal.MalformedContractId("not-a-contract"));
    }

    [Fact]
    public async Task TradingRiskGate_Refuses_WhenCapsWouldBeExceeded()
    {
        TradingRiskGate sizeGate = Gate(new TradingExecutionOptions { Enabled = true, MaxContractsPerOrder = 1 });
        TradingRiskGateResult sizeResult = await sizeGate.SubmitPlace(
            SamplePlace(PracticeAccountId, size: 2),
            Context(dryRun: false),
            CancellationToken.None);
        sizeResult.Refusal.Should().Be(TradingExecutionRefusal.OrderSizeExceedsLimit(2, 1));

        _daily.ContractsToday = 9;
        TradingRiskGate dailyGate = Gate(new TradingExecutionOptions
        {
            Enabled = true,
            MaxContractsPerDay = 10,
        });
        TradingRiskGateResult dailyResult = await dailyGate.SubmitPlace(
            SamplePlace(PracticeAccountId, size: 2),
            Context(dryRun: false),
            CancellationToken.None);
        dailyResult.Refusal.Should().Be(TradingExecutionRefusal.DailyContractLimitExceeded(PracticeAccountId, 9, 2, 10));
    }

    [Fact]
    public async Task TradingRiskGate_SubmitModify_RefusesAndSkipsGateway_WhenTradingIsDisabled()
    {
        TradingRiskGate gate = Gate(new TradingExecutionOptions { Enabled = false });

        TradingRiskGateResult result = await gate.SubmitModify(
            new ExecutionModifyOrderCommand(PracticeAccountId, OrderId: 1, Size: 1, null, null),
            Context(dryRun: false),
            CancellationToken.None);

        _gateway.ModifyOrderCalls.Should().Be(0);
        result.Outcome.Should().Be(TradingAttemptOutcome.Refused);
        result.Refusal.Should().Be(TradingExecutionRefusal.TradingDisabled);
    }

    [Fact]
    public async Task TradingRiskGate_SubmitModify_RefusesAndSkipsGateway_WhenKillSwitchIsEngaged()
    {
        _killSwitch.IsEngaged = true;
        TradingRiskGate gate = Gate(new TradingExecutionOptions { Enabled = true });

        TradingRiskGateResult result = await gate.SubmitModify(
            new ExecutionModifyOrderCommand(PracticeAccountId, OrderId: 1, Size: 1, null, null),
            Context(dryRun: false),
            CancellationToken.None);

        _gateway.ModifyOrderCalls.Should().Be(0);
        result.Refusal.Should().Be(TradingExecutionRefusal.KillSwitchEngaged);
    }

    [Fact]
    public async Task TradingRiskGate_SubmitModify_RefusesNonPracticeStage_BeforeGateway()
    {
        TradingRiskGate gate = Gate(
            new TradingExecutionOptions { Enabled = true },
            accounts: [Account(FundedAccountId, AccountStage.Funded, FundedName)]);

        TradingRiskGateResult result = await gate.SubmitModify(
            new ExecutionModifyOrderCommand(FundedAccountId, OrderId: 1, Size: 1, null, null),
            Context(dryRun: false),
            CancellationToken.None);

        _gateway.ModifyOrderCalls.Should().Be(0);
        result.Outcome.Should().Be(TradingAttemptOutcome.Refused);
        result.Refusal.Should().Be(TradingExecutionRefusal.NonPracticeAccountStage(FundedAccountId, AccountStage.Funded));
    }

    [Fact]
    public async Task TradingRiskGate_DailyCapCountsModifySizeIncreases()
    {
        _daily.ContractsToday = 9;
        TradingRiskGate gate = Gate(
            new TradingExecutionOptions { Enabled = true, MaxContractsPerDay = 10 },
            orders: [OpenOrder(1, MesContract)]);

        TradingRiskGateResult result = await gate.SubmitModify(
            new ExecutionModifyOrderCommand(PracticeAccountId, OrderId: 1, Size: 2, null, null),
            Context(dryRun: false),
            CancellationToken.None);

        result.Outcome.Should().Be(TradingAttemptOutcome.Refused);
        result.Refusal.Should().Be(TradingExecutionRefusal.DailyContractLimitExceeded(PracticeAccountId, 9, 2, 10));
    }

    [Fact]
    public async Task TradingRiskGate_DryRunPreview_DoesNotCallGateway()
    {
        TradingRiskGate gate = Gate(new TradingExecutionOptions { Enabled = true });

        TradingRiskGateResult result = await gate.SubmitPlace(
            SamplePlace(PracticeAccountId),
            Context(dryRun: true),
            CancellationToken.None);

        _gateway.PlaceOrderCalls.Should().Be(0);
        result.Outcome.Should().Be(TradingAttemptOutcome.Preview);
        _audit.Entries.Should().ContainSingle(e => e.Outcome == TradingAuditOutcomes.Preview);
        _audit.Entries[0].Tool.Should().Be(TradingMcpTools.PlaceOrderOperation);
    }

    [Fact]
    public async Task TradingRiskGate_LivePlace_CallsGateway_WhenPracticeAndEnabled()
    {
        _gateway.PlaceOrderResult = ExecutionWriteResultFactory.Acknowledged(orderId: 42);
        TradingRiskGate gate = Gate(new TradingExecutionOptions { Enabled = true });

        TradingRiskGateResult result = await gate.SubmitPlace(
            SamplePlace(PracticeAccountId),
            Context(dryRun: false),
            CancellationToken.None);

        _gateway.PlaceOrderCalls.Should().Be(1);
        result.Outcome.Should().Be(TradingAttemptOutcome.Live);
        _audit.Entries.Should().Contain(e =>
            e.Outcome == TradingAuditOutcomes.Attempted && e.ContractCount == 1);
        _audit.Entries.Should().Contain(e =>
            e.Outcome == TradingAuditOutcomes.Live && e.ContractCount == 0);
        _audit.Entries.Single(e => e.Outcome == TradingAuditOutcomes.Live).UsedNoneToken.Should().BeTrue();
        result.IsRetrySafe.Should().BeFalse();
    }

    [Fact]
    public async Task TradingRiskGate_MapsFailed_WhenVenueRejects()
    {
        _gateway.PlaceOrderResult = ExecutionWriteResultFactory.VenueRejected();
        TradingRiskGate gate = Gate(new TradingExecutionOptions { Enabled = true });

        TradingRiskGateResult result = await gate.SubmitPlace(
            SamplePlace(PracticeAccountId),
            Context(dryRun: false),
            CancellationToken.None);

        result.Outcome.Should().Be(TradingAttemptOutcome.Failed);
        _audit.Entries.Should().Contain(e =>
            e.Outcome == TradingAuditOutcomes.Failed && e.ContractCount == 0);
        result.IsRetrySafe.Should().BeTrue();
    }

    [Fact]
    public async Task TradingRiskGate_MapsUnknown_WhenTransmitOutcomeIsUncertain()
    {
        _gateway.PlaceOrderResult = ExecutionWriteResultFactory.TransmittedOutcomeUnknown();
        TradingRiskGate gate = Gate(new TradingExecutionOptions { Enabled = true });

        TradingRiskGateResult result = await gate.SubmitPlace(
            SamplePlace(PracticeAccountId),
            Context(dryRun: false),
            CancellationToken.None);

        result.Outcome.Should().Be(TradingAttemptOutcome.Unknown);
        _audit.Entries.Should().Contain(e => e.Outcome == TradingAuditOutcomes.Unknown);
        result.IsRetrySafe.Should().BeFalse();
    }

    [Fact]
    public async Task TradingRiskGate_MapsUnknown_WhenBrokerAcknowledgedWithoutOrderId()
    {
        _gateway.PlaceOrderResult = ExecutionWriteResultFactory.BrokerSuccessWithoutOrderId();
        TradingRiskGate gate = Gate(new TradingExecutionOptions { Enabled = true });

        TradingRiskGateResult result = await gate.SubmitPlace(
            SamplePlace(PracticeAccountId),
            Context(dryRun: false),
            CancellationToken.None);

        result.Outcome.Should().Be(TradingAttemptOutcome.Unknown);
        _audit.Entries.Should().Contain(e => e.Outcome == TradingAuditOutcomes.Unknown);
        result.IsRetrySafe.Should().BeFalse();
    }

    [Fact]
    public async Task TradingRiskGate_MapsUnknown_WhenExecutionOutcomeIsUnrecognized()
    {
        _gateway.PlaceOrderResult = ExecutionWriteResultFactory.UnrecognizedOutcome();
        TradingRiskGate gate = Gate(new TradingExecutionOptions { Enabled = true });

        TradingRiskGateResult result = await gate.SubmitPlace(
            SamplePlace(PracticeAccountId),
            Context(dryRun: false),
            CancellationToken.None);

        result.Outcome.Should().Be(TradingAttemptOutcome.Unknown);
        _audit.Entries.Should().Contain(e => e.Outcome == TradingAuditOutcomes.Unknown);
    }

    [Fact]
    public async Task TradingRiskGate_RefusesInactiveAccount_BeforeGateway()
    {
        TradingRiskGate gate = Gate(
            new TradingExecutionOptions { Enabled = true },
            accounts: [Account(PracticeAccountId, AccountStage.Practice, PracticeName, canTrade: false)]);

        TradingRiskGateResult result = await gate.SubmitPlace(
            SamplePlace(PracticeAccountId),
            Context(dryRun: false),
            CancellationToken.None);

        _gateway.PlaceOrderCalls.Should().Be(0);
        result.Refusal.Should().Be(TradingExecutionRefusal.AccountNotActive(PracticeAccountId));
    }

    [Fact]
    public async Task TradingRiskGate_MapsGatewayRefusal_ToRefusedWithAudit()
    {
        _gateway.PlaceOrderResult = ExecutionWriteResultFactory.GateRefused(
            TradingExecutionRefusal.KillSwitchEngaged);
        TradingRiskGate gate = Gate(new TradingExecutionOptions { Enabled = true });

        TradingRiskGateResult result = await gate.SubmitPlace(
            SamplePlace(PracticeAccountId),
            Context(dryRun: false),
            CancellationToken.None);

        result.Outcome.Should().Be(TradingAttemptOutcome.Refused);
        _audit.Entries.Should().Contain(e =>
            e.Outcome == TradingAuditOutcomes.Refused && e.VenueErrorCode == "kill_switch");
    }

    [Fact]
    public async Task TradingRiskGate_WritesUnknownAudit_WhenGatewayThrowsAfterSend()
    {
        _gateway.PlaceOrderException = new InvalidOperationException("timeout");
        TradingRiskGate gate = Gate(new TradingExecutionOptions { Enabled = true });

        Func<Task> act = () => gate.SubmitPlace(
            SamplePlace(PracticeAccountId),
            Context(dryRun: false),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _audit.Entries.Should().Contain(e =>
            e.Outcome == TradingAuditOutcomes.Attempted && e.ContractCount == SamplePlace(PracticeAccountId).Size);
        _audit.Entries.Should().Contain(e =>
            e.Outcome == TradingAuditOutcomes.Unknown
            && e.VenueErrorCode == nameof(InvalidOperationException)
            && e.ContractCount == SamplePlace(PracticeAccountId).Size);
    }

    [Fact]
    public async Task TradingRiskGate_SubmitCancel_AuditsAndCallsGateway()
    {
        _gateway.CancelOrderResult = ExecutionWriteResultFactory.Acknowledged(orderId: null);
        TradingRiskGate gate = Gate(new TradingExecutionOptions { Enabled = true });

        TradingRiskGateResult result = await gate.SubmitCancel(
            new ExecutionCancelOrderCommand(PracticeAccountId, 99),
            Context(dryRun: false),
            CancellationToken.None);

        _gateway.CancelOrderCalls.Should().Be(1);
        result.Outcome.Should().Be(TradingAttemptOutcome.Live);
        _audit.Entries[0].Tool.Should().Be(TradingMcpTools.CancelOrderOperation);
    }

    [Fact]
    public async Task TradingRiskGate_UsesServerDerivedRequestHash_NotCallerValue()
    {
        TradingRiskGate gate = Gate(new TradingExecutionOptions { Enabled = true });
        ExecutionPlaceOrderCommand command = SamplePlace(PracticeAccountId);

        await gate.SubmitPlace(command, Context(dryRun: true), CancellationToken.None);

        _audit.Entries.Should().ContainSingle();
        _audit.Entries[0].RequestHash.Should().Be(TradingRequestHasher.HashPlace(command));
    }

    [Fact]
    public async Task TradingRiskGate_AcquiresDailyCapLock_ForPlace()
    {
        TradingRiskGate gate = Gate(new TradingExecutionOptions { Enabled = true });
        _gateway.PlaceOrderResult = ExecutionWriteResultFactory.Acknowledged(1);

        await gate.SubmitPlace(SamplePlace(PracticeAccountId), Context(dryRun: false), CancellationToken.None);

        _daily.LockAcquisitions.Should().Be(1);
    }

    [Fact]
    public async Task TradingRiskGate_WritesAttemptedBeforeGatewaySend()
    {
        TradingRiskGate gate = Gate(new TradingExecutionOptions { Enabled = true });
        _gateway.PlaceOrderResult = ExecutionWriteResultFactory.Acknowledged(1);
        _gateway.OnPlaceOrder = () =>
        {
            _audit.Entries.Should().ContainSingle(e => e.Outcome == TradingAuditOutcomes.Attempted);
        };

        await gate.SubmitPlace(SamplePlace(PracticeAccountId), Context(dryRun: false), CancellationToken.None);
    }

    [Fact]
    public async Task TradingRiskGate_GatewayRefusal_ReleasesCapChargeAfterWriteAhead()
    {
        _gateway.PlaceOrderResult = ExecutionWriteResultFactory.GateRefused(
            TradingExecutionRefusal.KillSwitchEngaged);
        TradingRiskGate gate = Gate(new TradingExecutionOptions { Enabled = true });
        int placeSize = SamplePlace(PracticeAccountId).Size;

        await gate.SubmitPlace(
            SamplePlace(PracticeAccountId),
            Context(dryRun: false),
            CancellationToken.None);

        int attemptedNet = _audit.Entries
            .Where(e => e.Outcome == TradingAuditOutcomes.Attempted)
            .Sum(e => e.ContractCount);
        attemptedNet.Should().Be(0);
        _audit.Entries.Should().Contain(e => e.Outcome == TradingAuditOutcomes.Refused);
    }

    [Fact]
    public async Task TradingRiskGate_RecordsOutcome_WhenCallerCancelsAfterTransmit()
    {
        using CancellationTokenSource cts = new();
        _gateway.PlaceOrderResult = ExecutionWriteResultFactory.TransmittedOutcomeUnknown();
        _gateway.OnPlaceOrder = () => cts.Cancel();
        TradingRiskGate gate = Gate(new TradingExecutionOptions { Enabled = true });

        TradingRiskGateResult result = await gate.SubmitPlace(
            SamplePlace(PracticeAccountId),
            Context(dryRun: false),
            cts.Token);

        result.Outcome.Should().Be(TradingAttemptOutcome.Unknown);
        _audit.Entries.Should().Contain(e => e.Outcome == TradingAuditOutcomes.Attempted);
        _audit.Entries.Should().Contain(e => e.Outcome == TradingAuditOutcomes.Unknown);
    }

    [Fact]
    public async Task TradingRiskGate_SubmitModify_HoldsDailyCapLockThroughGatewaySend()
    {
        TradingRiskGate gate = Gate(
            new TradingExecutionOptions { Enabled = true },
            orders: [OpenOrder(1, MesContract)]);
        _gateway.OnModifyOrder = () => _daily.LockHeldDuringLastCapSend = _daily.LockActive;

        await gate.SubmitModify(
            new ExecutionModifyOrderCommand(PracticeAccountId, OrderId: 1, Size: 1, null, null),
            Context(dryRun: false),
            CancellationToken.None);

        _daily.LockHeldDuringLastCapSend.Should().BeTrue();
    }

    [Fact]
    public async Task TradingRiskGate_SubmitModify_RefusesDeniedContract_FromOpenOrder()
    {
        TradingRiskGate gate = Gate(
            new TradingExecutionOptions { Enabled = true, DeniedSymbols = "CL" },
            instruments: "MES,CL",
            orders: [OpenOrder(1, CleContract)]);

        TradingRiskGateResult result = await gate.SubmitModify(
            new ExecutionModifyOrderCommand(PracticeAccountId, OrderId: 1, Size: 1, null, null),
            Context(dryRun: false),
            CancellationToken.None);

        result.Refusal.Should().Be(TradingExecutionRefusal.SymbolDenied("CL"));
        _gateway.ModifyOrderCalls.Should().Be(0);
    }

    [Fact]
    public async Task TradingRiskGate_RefusesInvalidDeniedSymbols_WithAudit_NotThrow()
    {
        TradingRiskGate gate = Gate(new TradingExecutionOptions { Enabled = true, DeniedSymbols = "CL,," });

        TradingRiskGateResult result = await gate.SubmitPlace(
            SamplePlace(PracticeAccountId),
            Context(dryRun: false),
            CancellationToken.None);

        result.Outcome.Should().Be(TradingAttemptOutcome.Refused);
        result.Refusal!.Value.Code.Should().Be("trading_configuration_invalid");
        _audit.Entries.Should().ContainSingle(e => e.Outcome == TradingAuditOutcomes.Refused);
    }

    [Fact]
    public async Task TradingRiskGate_DailyCapCountsPartialCloseSize()
    {
        _daily.ContractsToday = 9;
        TradingRiskGate gate = Gate(new TradingExecutionOptions { Enabled = true, MaxContractsPerDay = 10 });
        _gateway.OnPartialClose = () => _daily.LockHeldDuringLastCapSend = _daily.LockActive;

        await gate.SubmitPartialClose(
            new ExecutionPartialClosePositionCommand(PracticeAccountId, MesContract, Size: 2),
            Context(dryRun: false),
            CancellationToken.None);

        _daily.LockAcquisitions.Should().Be(1);
    }

    private TradingRiskGate Gate(
        TradingExecutionOptions options,
        IReadOnlyList<VenueAccount>? accounts = null,
        IReadOnlyList<VenueOrder>? orders = null,
        string instruments = "MES")
    {
        ProjectXAccountStageResolver stageResolver = new();
        TradingExecutionGate executionGate = new(
            Options.Create(options),
            _killSwitch,
            stageResolver);
        InstrumentRegistry registry = new(Options.Create(new MarketDataOptions { Instruments = instruments }));
        TradingContractDenylist denylist = new(Options.Create(options), registry);

        IReadOnlyList<VenueAccount> resolvedAccounts = accounts ?? DefaultAccounts(stageResolver);

        return new TradingRiskGate(
            executionGate,
            _gateway,
            new StubMarketDataGateway(resolvedAccounts, orders ?? []),
            stageResolver,
            _unknownSignal,
            _daily,
            _audit,
            denylist,
            new TradingWriteVenueAccountCache(),
            new HostTelemetry(),
            new InMemoryTradingIdempotencyStore(TimeProvider.System));
    }

    private static VenueOrder OpenOrder(long orderId, string contractId) =>
        new(
            orderId,
            contractId,
            VenueSide.Buy,
            Size: 1,
            FilledSize: 0,
            VenueOrderStatus.Open,
            LimitPrice: 6800m,
            StopPrice: null,
            FilledPrice: null,
            CreatedAt: DateTimeOffset.UtcNow);

    private static IReadOnlyList<VenueAccount> DefaultAccounts(IAccountStageResolver stageResolver) =>
    [
        Account(PracticeAccountId, stageResolver.ResolveFromAccountName(PracticeName), PracticeName),
        Account(FundedAccountId, stageResolver.ResolveFromAccountName(FundedName), FundedName),
        Account(UnknownAccountId, stageResolver.ResolveFromAccountName(UnknownName), UnknownName),
    ];

    private static VenueAccount Account(
        int accountId,
        AccountStage stage,
        string name,
        bool canTrade = true) =>
        new(accountId, stage, canTrade, IsVisible: true, Balance: 50_000m);

    private static ExecutionPlaceOrderCommand SamplePlace(
        int accountId,
        int size = 1,
        string contractId = MesContract) =>
        new(
            accountId,
            contractId,
            ExecutionOrderType.Limit,
            VenueSide.Buy,
            size,
            LimitPrice: 6800m,
            StopPrice: null,
            TrailPrice: null);

    private static TradingRiskAttemptContext Context(bool dryRun) => new(dryRun);

    private sealed class FakeKillSwitch : ITradingKillSwitch
    {
        public bool IsEngaged { get; set; }
    }

    private sealed class StubMarketDataGateway(
        IReadOnlyList<VenueAccount> accounts,
        IReadOnlyList<VenueOrder> orders) : IMarketDataGateway
    {
        public string VenueId => "stub";

        public Task<IReadOnlyList<VenueAccount>> GetAccountsAsync(bool onlyActive, CancellationToken cancellationToken)
        {
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
            CancellationToken cancellationToken)
        {
            _ = accountId;
            _ = window;
            return Task.FromResult(orders);
        }

        public Task<IReadOnlyList<VenueTrade>> GetTradesAsync(
            int accountId,
            BarRange window,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeExecutionGateway : IExecutionGateway
    {
        public int PlaceOrderCalls { get; private set; }

        public int CancelOrderCalls { get; private set; }

        public int ModifyOrderCalls { get; private set; }

        public ExecutionWriteResult PlaceOrderResult { get; set; } =
            ExecutionWriteResultFactory.Acknowledged(orderId: 1);

        public ExecutionWriteResult CancelOrderResult { get; set; } =
            ExecutionWriteResultFactory.Acknowledged(orderId: null);

        public Exception? PlaceOrderException { get; set; }

        public Action? OnPlaceOrder { get; set; }

        public Action? OnPartialClose { get; set; }

        public Action? OnModifyOrder { get; set; }

        public Task<ExecutionWriteResult> PlaceOrder(
            ExecutionPlaceOrderCommand command,
            CancellationToken cancellationToken)
        {
            OnPlaceOrder?.Invoke();
            PlaceOrderCalls++;
            if (PlaceOrderException is not null)
            {
                throw PlaceOrderException;
            }

            return Task.FromResult(PlaceOrderResult);
        }

        public Task<ExecutionWriteResult> ModifyOrder(
            ExecutionModifyOrderCommand command,
            CancellationToken cancellationToken)
        {
            OnModifyOrder?.Invoke();
            ModifyOrderCalls++;
            return Task.FromResult(PlaceOrderResult);
        }

        public Task<ExecutionWriteResult> PartialClosePosition(
            ExecutionPartialClosePositionCommand command,
            CancellationToken cancellationToken)
        {
            OnPartialClose?.Invoke();
            return Task.FromResult(PlaceOrderResult);
        }

        public Task<ExecutionWriteResult> CancelOrder(
            ExecutionCancelOrderCommand command,
            CancellationToken cancellationToken)
        {
            CancelOrderCalls++;
            return Task.FromResult(CancelOrderResult);
        }

        public Task<ExecutionWriteResult> ClosePosition(
            ExecutionClosePositionCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

    }

    private sealed class RecordingAuditLog : ITradingAuditLogWriter
    {
        public List<RecordedAudit> Entries { get; } = [];

        public Task WriteAsync(TradingAuditWriteRequest request, CancellationToken cancellationToken)
        {
            Entries.Add(new RecordedAudit(request, cancellationToken == CancellationToken.None));
            return Task.CompletedTask;
        }
    }

    private sealed record RecordedAudit(TradingAuditWriteRequest Request, bool UsedNoneToken)
    {
        public string Outcome => Request.Outcome;

        public string Tool => Request.Tool;

        public string RequestHash => Request.RequestHash;

        public string ParsedStage => Request.ParsedStage;

        public string? VenueErrorCode => Request.VenueErrorCode;

        public int ContractCount => Request.ContractCount;
    }

    private sealed class FakeDailyCapCoordinator : ITradingDailyCapCoordinator
    {
        public int ContractsToday { get; set; }

        public int LockAcquisitions { get; private set; }

        public bool LockActive { get; private set; }

        public bool LockHeldDuringLastCapSend { get; set; }

        public Task<IAsyncDisposable> AcquireAccountDayLockAsync(int accountId, CancellationToken cancellationToken)
        {
            LockAcquisitions++;
            LockActive = true;
            return Task.FromResult<IAsyncDisposable>(new NoopLock(this));
        }

        public Task<int> GetConservativeContractTotalTodayUtcAsync(
            int accountId,
            CancellationToken cancellationToken) =>
            Task.FromResult(ContractsToday);

        private sealed class NoopLock(FakeDailyCapCoordinator owner) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                owner.LockActive = false;
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class RecordingUnknownSignal : ITradingUnknownStageRefusalSignal
    {
        public List<int> RefusedAccountIds { get; } = [];

        public void UnknownStageWriteRefused(int accountId) => RefusedAccountIds.Add(accountId);
    }

    private static class ExecutionWriteResultFactory
    {
        public static ExecutionWriteResult Acknowledged(long? orderId) =>
            new(
                ExecutionWriteOutcome.BrokerAcknowledged,
                GateRefusal: null,
                HttpStatusCode: null,
                VendorErrorCode: null,
                OrderId: orderId);

        public static ExecutionWriteResult VenueRejected() =>
            new(
                ExecutionWriteOutcome.VenueRejected,
                GateRefusal: null,
                HttpStatusCode: 400,
                VendorErrorCode: 3,
                OrderId: null);

        public static ExecutionWriteResult TransmittedOutcomeUnknown() =>
            new(
                ExecutionWriteOutcome.TransmittedOutcomeUnknown,
                GateRefusal: null,
                HttpStatusCode: null,
                VendorErrorCode: null,
                OrderId: null);

        public static ExecutionWriteResult BrokerSuccessWithoutOrderId() =>
            new(
                ExecutionWriteOutcome.BrokerSuccessWithoutOrderId,
                GateRefusal: null,
                HttpStatusCode: 200,
                VendorErrorCode: null,
                OrderId: null);

        public static ExecutionWriteResult UnrecognizedOutcome() =>
            new(
                (ExecutionWriteOutcome)999,
                GateRefusal: null,
                HttpStatusCode: null,
                VendorErrorCode: null,
                OrderId: null);

        public static ExecutionWriteResult GateRefused(TradingExecutionRefusal refusal) =>
            new(
                ExecutionWriteOutcome.NotTransmitted,
                refusal,
                HttpStatusCode: null,
                VendorErrorCode: null,
                OrderId: null);
    }
}
