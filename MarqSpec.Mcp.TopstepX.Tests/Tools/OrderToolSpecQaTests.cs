using System.Diagnostics;
using System.Reflection;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Telemetry;
using MarqSpec.Mcp.TopstepX.Tests.Configuration;
using MarqSpec.Mcp.TopstepX.Tools;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.Tests.Tools;

/// <summary>
/// Spec-driven QA for the five order tools, written from PRD R-4.1, R-5.13 and ADR-0024 sections 4-6 rather
/// than from the engineers' tests. Each case covers a tool, fault or side effect that
/// <c>TradingOrderToolGateTests</c> and <c>TradingRiskGateTests</c> do not. In-process doubles only.
/// </summary>
[Trait("Gate", "ADR-0024")]
[Trait("QA", "gh769")]
[Collection(HostTelemetryCollection.Name)]
public sealed class OrderToolSpecQaTests
{
    private const int Practice = 100;
    private const int OtherPractice = 101;
    private const int Funded = 102;

    private const int UnknownAccount = 103;
    private const string Mes = "CON.F.US.MES.Z26";
    private const long OpenOrderId = 7;

    private static readonly IReadOnlyList<VenueAccount> _accounts =
    [
        new(Practice, AccountStage.Practice, CanTrade: true, IsVisible: true, Balance: 50_000m),
        new(OtherPractice, AccountStage.Practice, CanTrade: true, IsVisible: true, Balance: 50_000m),
        new(Funded, AccountStage.Funded, CanTrade: true, IsVisible: true, Balance: 50_000m),
        new(UnknownAccount, AccountStage.Unknown, CanTrade: true, IsVisible: true, Balance: 50_000m),
    ];

    private static readonly InstrumentRegistry _registry =
        new(Options.Create(new MarketDataOptions { Instruments = "MES" }));

    private readonly QaDailyCap _daily = new();
    private readonly QaGateway _gateway;
    private readonly QaAudit _audit = new();
    private readonly QaKill _kill = new();
    private readonly QaMarket _market = new(_accounts);
    private readonly ProjectXAccountStageResolver _stage = new();

    public OrderToolSpecQaTests() => _gateway = new QaGateway(_daily);

    public static TheoryData<string> AllTools() =>
    [
        TradingMcpTools.PlaceOrderOperation,
        TradingMcpTools.ModifyOrderOperation,
        TradingMcpTools.CancelOrderOperation,
        TradingMcpTools.ClosePositionOperation,
        TradingMcpTools.PartialClosePositionOperation,
    ];

    /// <summary>Tools whose send runs inside the account-day cap lock.</summary>
    public static TheoryData<string> CapLockedTools() =>
    [
        TradingMcpTools.PlaceOrderOperation,
        TradingMcpTools.ModifyOrderOperation,
        TradingMcpTools.PartialClosePositionOperation,
    ];

    /// <summary>
    /// Every tool crossed with each outcome whose telemetry and side effects are not pinned per tool elsewhere.
    /// trading_disabled is left out: #769 pins it for all five tools. The kill switch blocks place and
    /// modify only (gh#770); cancel and close under a halt are pinned below.
    /// </summary>
    public static TheoryData<string, string> ToolByScenario()
    {
        TheoryData<string, string> data = [];
        foreach (string op in AllTools())
        {
            data.Add(op, ExecutionWriteRefusal.AccountNotAllowlisted);
            data.Add(op, ExecutionWriteRefusal.NonPracticeAccount);
            data.Add(op, ExecutionWriteRefusal.IdempotencyKeyTooLong);
            data.Add(op, ExecutionWriteRefusal.Transmitted);
        }

        return data;
    }

    /// <summary>
    /// The three ways the real cap coordinator's lease can fail after the send: the dispose throws a plain
    /// exception, the dispose throws a venue exception, or the dispose returns and the coordinator reports
    /// the failed release (what <c>TradingDailyCapCoordinator</c> does when the unlock fails).
    /// </summary>
    public static TheoryData<string, ReleaseFault> CapLockedToolByReleaseFault()
    {
        TheoryData<string, ReleaseFault> data = [];
        foreach (string op in CapLockedTools())
        {
            data.Add(op, ReleaseFault.Throws);
            data.Add(op, ReleaseFault.ThrowsVenueException);
            data.Add(op, ReleaseFault.ReportedFailed);
        }

        return data;
    }

    public enum ReleaseFault
    {
        None,
        Throws,
        ThrowsVenueException,
        ReportedFailed,
    }

    private static TradingExecutionOptions On(string? allow = "100,101", int perOrder = 5, string deny = "") =>
        new() { Enabled = true, AllowedAccountIds = allow, MaxContractsPerOrder = perOrder, DeniedSymbols = deny };

    // ---------------- R-5.13 telemetry, R-4.1 / ADR-0024 s5 refusal codes and side effects, every tool ----------------
    [Theory]
    [MemberData(nameof(ToolByScenario))]
    public async Task TradingOrderTools_EveryCall_EmitsOneSpanAndOneCounter_AndOnlyTheSideEffectsItsOutcomeAllows(
        string op,
        string expected)
    {
        using HostTelemetry telemetry = new();
        using MetricCollector<long> writes = new(telemetry, HostTelemetry.Name, HostTelemetry.ExecutionWritesInstrument);
        List<Activity> spans = [];
        using ActivityListener listener = Listen(telemetry, spans);

        (TradingExecutionOptions options, int account, string key) = expected switch
        {
            ExecutionWriteRefusal.AccountNotAllowlisted => (On("101"), Practice, "k"),
            ExecutionWriteRefusal.NonPracticeAccount => (On("100,102"), Funded, "k"),
            ExecutionWriteRefusal.IdempotencyKeyTooLong => (On(), Practice, new string('k', 257)),
            _ => (On(), Practice, "k"),
        };

        ToolPayloads.TradingWriteResult result = await Call(Tools(telemetry, options), op, key, account: account);

        IReadOnlyList<TradingAuditWriteRequest> rows = _audit.Snapshot();
        if (expected == ExecutionWriteRefusal.Transmitted)
        {
            result.Status.Should().Be(nameof(TradingAttemptOutcome.Live));
            result.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.NotSafeToRetry);
            _gateway.Total.Should().Be(1);
            rows.Select(r => r.Outcome).Should().Equal(
                TradingAuditOutcomes.Unknown,
                TradingAuditOutcomes.Attempted,
                TradingAuditOutcomes.Live);
            AssertCapLock(op, sends: 1);
        }
        else
        {
            result.Status.Should().Be(nameof(TradingAttemptOutcome.Refused));
            result.RefusalCode.Should().Be(expected);
            result.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.SafeToRetry);
            _gateway.Total.Should().Be(0);
            if (expected == ExecutionWriteRefusal.IdempotencyKeyTooLong)
            {
                rows.Should().BeEmpty("the key is rejected before any audit row (ADR-0024 s5.7)");
                _daily.Acquisitions.Should().Be(0);
            }
            else
            {
                AssertOnlyARefusalRow(rows, expected);
            }

            _daily.HeldNow.Should().Be(0, "a refusal must not leave the account-day lock held");
        }

        AssertOneWrite(writes, spans, op, expected);
    }

    // ---------------- Short positions: close and partial close size against the absolute position ----------------
    [Theory]
    [InlineData(TradingMcpTools.ClosePositionOperation)]
    [InlineData(TradingMcpTools.PartialClosePositionOperation)]
    public async Task TradingOrderTools_ShortPosition_CloseAndPartialClose_Transmit(string op)
    {
        _market.PositionSize = -3;
        using HostTelemetry telemetry = new();

        ToolPayloads.TradingWriteResult result = await Call(Tools(telemetry, On()), op, "short", size: 2);

        result.Status.Should().Be(nameof(TradingAttemptOutcome.Live));
        result.OrderId.Should().NotBeNull();
        _gateway.Total.Should().Be(1);
        if (op == TradingMcpTools.PartialClosePositionOperation)
        {
            _gateway.LastPartialClose.Should().Be(
                new ExecutionPartialClosePositionCommand(Practice, Mes, 2),
                "a partial close of a short sends the requested size against the absolute position, not a signed one");
            _gateway.LastClose.Should().BeNull();
        }
        else
        {
            // The close command carries no side or size; the venue flattens whatever is open.
            _gateway.LastClose.Should().Be(new ExecutionClosePositionCommand(Practice, Mes));
            _gateway.LastPartialClose.Should().BeNull();
        }
    }

    // ---------------- ADR-0024 s5.5: caps and denylist, exact codes ----------------
    [Theory]
    [InlineData(ExecutionWriteRefusal.OrderSizeExceedsLimit)]
    [InlineData(ExecutionWriteRefusal.DailyContractLimitExceeded)]
    [InlineData(ExecutionWriteRefusal.SymbolDenied)]
    public async Task TradingOrderTools_PlaceOrder_CapOrDenylistRefusal_CarriesTheExactCode_AndDoesNotSend(
        string expected)
    {
        TradingExecutionOptions options = expected switch
        {
            ExecutionWriteRefusal.OrderSizeExceedsLimit => On(perOrder: 2),
            ExecutionWriteRefusal.SymbolDenied => On(deny: "MES"),
            _ => On(),
        };
        _daily.ContractsToday = expected == ExecutionWriteRefusal.DailyContractLimitExceeded ? 50 : 0;
        using HostTelemetry telemetry = new();
        using MetricCollector<long> writes = new(telemetry, HostTelemetry.Name, HostTelemetry.ExecutionWritesInstrument);
        List<Activity> spans = [];
        using ActivityListener listener = Listen(telemetry, spans);

        ToolPayloads.TradingWriteResult result =
            await Call(Tools(telemetry, options), TradingMcpTools.PlaceOrderOperation, "k", size: 3);

        result.Status.Should().Be(nameof(TradingAttemptOutcome.Refused));
        result.RefusalCode.Should().Be(expected);
        result.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.SafeToRetry);
        _gateway.Total.Should().Be(0);
        AssertOnlyARefusalRow(_audit.Snapshot(), expected);
        _daily.HeldNow.Should().Be(0);
        AssertOneWrite(writes, spans, TradingMcpTools.PlaceOrderOperation, expected);
    }

    // ---------------- ADR-0024 s5.7: concurrent same key, deterministic overlap ----------------
    // The cap lock is non-blocking here so the seven duplicates reach the claim while the first send is held.
    [Fact]
    public async Task TradingOrderTools_PlaceOrder_SameKeyWhileTheSendIsInFlight_SendsOnce_DuplicatesGetOutcomeUnknown_ALaterCallReplaysTheOrderId()
    {
        _daily.Blocking = false;
        _gateway.HoldPlace = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using HostTelemetry telemetry = new();

        TimeSpan bound = TimeSpan.FromSeconds(10);
        Task<ToolPayloads.TradingWriteResult> first =
            Call(Tools(telemetry, On()), TradingMcpTools.PlaceOrderOperation, "race");
        ToolPayloads.TradingWriteResult[] inFlight;
        try
        {
            await _gateway.PlaceEntered.Task.WaitAsync(bound);

            // Bounded so a broken claim guard fails here instead of hanging behind the held send.
            inFlight = await Task.WhenAll(Enumerable.Range(0, 7)
                .Select(_ => Call(Tools(telemetry, On()), TradingMcpTools.PlaceOrderOperation, "race")))
                .WaitAsync(bound);
            _gateway.Place.Should().Be(1, "the duplicates must not send while the first send is unanswered");
            _audit.Snapshot().Where(r => r.Outcome == TradingAuditOutcomes.Unknown).Should().ContainSingle();
        }
        finally
        {
            _gateway.HoldPlace.TrySetResult();
        }

        ToolPayloads.TradingWriteResult winner = await first.WaitAsync(bound);
        ToolPayloads.TradingWriteResult replay =
            await Call(Tools(telemetry, On()), TradingMcpTools.PlaceOrderOperation, "race").WaitAsync(bound);

        _gateway.Place.Should().Be(1);
        inFlight.Should().OnlyContain(r => r.Status == nameof(TradingAttemptOutcome.Unknown)
            && r.RetrySafety == ToolPayloads.TradingRetrySafety.OutcomeUnknown
            && r.OrderId == null);
        winner.Status.Should().Be(nameof(TradingAttemptOutcome.Live));
        winner.OrderId.Should().NotBeNull();
        replay.Status.Should().Be(nameof(TradingAttemptOutcome.Live));
        replay.OrderId.Should().Be(winner.OrderId);
        _audit.Snapshot().Select(r => r.Outcome).Should().Equal(
            TradingAuditOutcomes.Unknown,
            TradingAuditOutcomes.Attempted,
            TradingAuditOutcomes.Live);
    }

    // ---------------- Duplicate-order bug (#753 round 4), the caller's view, real lease faults ----------------
    // R-5.13: OutcomeUnknown when the send may have happened; ADR: a fresh key per order. A caller that obeys
    // retrySafety retries with a fresh key only on SafeToRetry, so an acked order must never come back that way.
    [Theory]
    [MemberData(nameof(CapLockedToolByReleaseFault))]
    public async Task TradingOrderTools_CapLeaseReleaseFailsAfterTheAck_ACallerFollowingRetrySafety_SendsOnce(
        string op,
        ReleaseFault fault)
    {
        _daily.Fault = fault;
        using HostTelemetry telemetry = new();

        ToolPayloads.TradingWriteResult first = await CallerFollowingRetrySafety(telemetry, op);

        _gateway.Total.Should().Be(1, "an acked write followed by a lease fault must not be SafeToRetry");
        AssertOutcomeUnknown(first, _gateway.LastIssuedId);
        AssertCapLock(op, sends: 1);
        _audit.Snapshot().Should().Contain(r => r.Outcome == TradingAuditOutcomes.Live);
    }

    // #769 pins the throwing span listener for place, cancel and close; these are the other two tools.
    [Theory]
    [InlineData(TradingMcpTools.ModifyOrderOperation)]
    [InlineData(TradingMcpTools.PartialClosePositionOperation)]
    public async Task TradingOrderTools_SpanListenerThrowsAfterTheAck_ACallerFollowingRetrySafety_SendsOnce(string op)
    {
        using HostTelemetry telemetry = new();
        using ActivityListener listener = ThrowingOnStop(telemetry);

        ToolPayloads.TradingWriteResult first = await CallerFollowingRetrySafety(telemetry, op);

        _gateway.Total.Should().Be(1);
        AssertOutcomeUnknown(first, _gateway.LastIssuedId);
        _audit.Snapshot().Should().Contain(r => r.Outcome == TradingAuditOutcomes.Live);
    }

    // ---------------- Unmappable venue outcome: the tool-level result, every tool ----------------
    // The gate maps the unknown enum value to Unknown; this pins what each tool returns and that the caller stops.
    [Theory]
    [MemberData(nameof(AllTools))]
    public async Task TradingOrderTools_UnrecognisedVenueOutcome_IsOutcomeUnknown_AndACallerFollowingRetrySafety_DoesNotResend(
        string op)
    {
        _gateway.Next = new ExecutionWriteResult((ExecutionWriteOutcome)99, null, 200, null, 4242);
        using HostTelemetry telemetry = new();

        ToolPayloads.TradingWriteResult first = await CallerFollowingRetrySafety(telemetry, op);

        _gateway.Total.Should().Be(1);
        first.Status.Should().Be(nameof(TradingAttemptOutcome.Unknown));
        first.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.OutcomeUnknown);
        // The venue's id is passed through for reconciliation, though the status stays Unknown. A replay is not
        // pinned: the id is never stored for this outcome, so a replay returns none (#784).
        first.OrderId.Should().Be(4242);
        _audit.Snapshot().Select(r => r.Outcome).Should().NotContain(TradingAuditOutcomes.Live);
    }

    // ---------------- Control: a lease fault with no send stays a plain refusal ----------------
    [Theory]
    [InlineData(ReleaseFault.Throws)]
    [InlineData(ReleaseFault.ReportedFailed)]
    public async Task TradingOrderTools_PlaceOrder_LeaseFaultWithoutASend_StaysARetryableRefusal(ReleaseFault fault)
    {
        _daily.Fault = fault;
        _daily.ContractsToday = 50;
        using HostTelemetry telemetry = new();

        ToolPayloads.TradingWriteResult result =
            await Call(Tools(telemetry, On()), TradingMcpTools.PlaceOrderOperation, "cap");

        _gateway.Total.Should().Be(0);
        result.Status.Should().Be(nameof(TradingAttemptOutcome.Refused));
        result.RefusalCode.Should().Be(ExecutionWriteRefusal.DailyContractLimitExceeded);
        result.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.SafeToRetry);
        AssertOnlyARefusalRow(_audit.Snapshot(), ExecutionWriteRefusal.DailyContractLimitExceeded);
        _daily.Acquisitions.Should().Be(1);
        _daily.HeldNow.Should().Be(0, "the lease is released even when its dispose faults");
    }

    // ---------------- gh#770: the kill switch blocks new orders and modifies only ----------------
    [Fact]
    public async Task TradingOrderTools_KillSwitch_CancelAndClose_StillSend()
    {
        _kill.IsEngaged = true;
        using HostTelemetry telemetry = new();

        ToolPayloads.TradingWriteResult cancel =
            await Call(Tools(telemetry, On()), TradingMcpTools.CancelOrderOperation, "halt-cancel");
        ToolPayloads.TradingWriteResult close =
            await Call(Tools(telemetry, On()), TradingMcpTools.ClosePositionOperation, "halt-close");
        ToolPayloads.TradingWriteResult partial =
            await Call(Tools(telemetry, On()), TradingMcpTools.PartialClosePositionOperation, "halt-partial");
        ToolPayloads.TradingWriteResult place =
            await Call(Tools(telemetry, On()), TradingMcpTools.PlaceOrderOperation, "halt-place");
        ToolPayloads.TradingWriteResult modify =
            await Call(Tools(telemetry, On()), TradingMcpTools.ModifyOrderOperation, "halt-modify");

        cancel.Status.Should().Be(nameof(TradingAttemptOutcome.Live));
        close.Status.Should().Be(nameof(TradingAttemptOutcome.Live));
        partial.Status.Should().Be(nameof(TradingAttemptOutcome.Live));
        _gateway.LastClose.Should().Be(new ExecutionClosePositionCommand(Practice, Mes));
        _gateway.LastPartialClose.Should().Be(new ExecutionPartialClosePositionCommand(Practice, Mes, 1));
        place.RefusalCode.Should().Be(ExecutionWriteRefusal.KillSwitch);
        modify.RefusalCode.Should().Be(ExecutionWriteRefusal.KillSwitch);
        _gateway.Place.Should().Be(0);
        _gateway.Total.Should().Be(3);
    }

    // ---------------- gh#771: no client order id. OutcomeUnknown plus reconcile is v1. ----------------
    [Fact]
    public void TradingOrderTools_HasNoClientOrderId()
    {
        string[] forbidden = ["clientOrderId", "clientId", "customTag"];
        foreach (MethodInfo method in typeof(TradingOrderTools).GetMethods(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
        {
            if (method.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolAttribute>() is null)
            {
                continue;
            }

            System.Text.Json.JsonElement schema = ModelContextProtocol.Server.McpServerTool.Create(
                method,
                static _ => throw new InvalidOperationException("schema only"),
                new ModelContextProtocol.Server.McpServerToolCreateOptions()).ProtocolTool.InputSchema;
            schema.GetProperty("properties").EnumerateObject()
                .Select(property => property.Name)
                .Should()
                .NotContain(forbidden);
        }
    }

    // ---------------- gh#772: one trading writer per account ----------------
    [Fact]
    public async Task TradingOrderTools_SessionConflicts_SecondReplicaRefusesUntilTheLockIsReleased()
    {
        AccountWriters writers = new();
        using HostTelemetry telemetry = new();

        ToolPayloads.TradingWriteResult held = await Call(
            Tools(telemetry, On(), writers.For(replica: 1)),
            TradingMcpTools.PlaceOrderOperation,
            "writer-a");
        ToolPayloads.TradingWriteResult blocked = await Call(
            Tools(telemetry, On(), writers.For(replica: 2)),
            TradingMcpTools.PlaceOrderOperation,
            "writer-b");
        ToolPayloads.TradingWriteResult otherAccount = await Tools(telemetry, On(), writers.For(replica: 2))
            .SubmitPlace(
            OtherPractice,
            Mes,
            ExecutionOrderType.Limit,
            VenueSide.Buy,
            size: 1,
            "writer-c",
            limitPrice: 6800m);

        held.Status.Should().Be(nameof(TradingAttemptOutcome.Live));
        blocked.Status.Should().Be(nameof(TradingAttemptOutcome.Refused));
        blocked.RefusalCode.Should().Be(ExecutionWriteRefusal.AccountWriterHeld);
        blocked.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.SafeToRetry);
        otherAccount.Status.Should().Be(nameof(TradingAttemptOutcome.Live));
        _gateway.Place.Should().Be(2);

        writers.Release(replica: 1);
        TradingOrderTools after = Tools(telemetry, On(), writers.For(replica: 2));
        ToolPayloads.TradingWriteResult freed =
            await Call(after, TradingMcpTools.CancelOrderOperation, "writer-d");

        freed.Status.Should().Be(nameof(TradingAttemptOutcome.Live));
        _gateway.Total.Should().Be(3);
    }

    [Fact]
    public async Task TradingOrderTools_DryRunAndNonAllowlistedAndUnknownStage_DoNotTakeTheWriterLock()
    {
        AccountWriters writers = new();
        using HostTelemetry telemetry = new();
        ITradingAccountWriterLock replicaB = writers.For(replica: 2);

        ToolPayloads.TradingWriteResult preview = await Tools(telemetry, On(), replicaB).SubmitPlace(
            Practice,
            Mes,
            ExecutionOrderType.Limit,
            VenueSide.Buy,
            size: 1,
            "preview",
            limitPrice: 6800m,
            dryRun: true);
        ToolPayloads.TradingWriteResult notListed = await Tools(telemetry, On(allow: "100"), replicaB)
            .SubmitPlace(
            OtherPractice,
            Mes,
            ExecutionOrderType.Limit,
            VenueSide.Buy,
            size: 1,
            "not-listed",
            limitPrice: 6800m);
        ToolPayloads.TradingWriteResult unknown = await Tools(telemetry, On(), replicaB).SubmitPlace(
            UnknownAccount,
            Mes,
            ExecutionOrderType.Limit,
            VenueSide.Buy,
            size: 1,
            "unknown-stage",
            limitPrice: 6800m);

        preview.Status.Should().Be(nameof(TradingAttemptOutcome.Preview));
        notListed.RefusalCode.Should().Be(ExecutionWriteRefusal.AccountNotAllowlisted);
        unknown.RefusalCode.Should().Be(ExecutionWriteRefusal.UnknownAccountStage);
        writers.AttemptsFor(2).Should().Be(0);

        ToolPayloads.TradingWriteResult live = await Call(
            Tools(telemetry, On(), writers.For(replica: 1)),
            TradingMcpTools.PlaceOrderOperation,
            "still-free");
        live.Status.Should().Be(nameof(TradingAttemptOutcome.Live));
    }

    [Fact]
    public async Task TradingOrderTools_AccountWriterHeld_TagsOnlyOperationRefusalAndReplay()
    {
        AccountWriters writers = new();
        using HostTelemetry taken = new();
        await Call(
            Tools(taken, On(), writers.For(replica: 1)),
            TradingMcpTools.PlaceOrderOperation,
            "held");

        using HostTelemetry telemetry = new();
        using MetricCollector<long> writes = new(
            telemetry, HostTelemetry.Name, HostTelemetry.ExecutionWritesInstrument);
        List<Activity> spans = [];
        using ActivityListener listener = Listen(telemetry, spans);

        ToolPayloads.TradingWriteResult blocked = await Call(
            Tools(telemetry, On(), writers.For(replica: 2)),
            TradingMcpTools.PlaceOrderOperation,
            "blocked");

        blocked.RefusalCode.Should().Be(ExecutionWriteRefusal.AccountWriterHeld);
        _gateway.Place.Should().Be(1);
        IReadOnlyList<CollectedMeasurement<long>> measured = writes.GetMeasurementSnapshot();
        measured.Should().ContainSingle();
        measured[0].Tags.Select(tag => tag.Key).Should().BeEquivalentTo(
            HostTelemetry.OperationTag,
            HostTelemetry.RefusalTag,
            HostTelemetry.ReplayTag);
        measured[0].Tags[HostTelemetry.OperationTag].Should().Be(TradingMcpTools.PlaceOrderOperation);
        measured[0].Tags[HostTelemetry.RefusalTag].Should().Be(ExecutionWriteRefusal.AccountWriterHeld);
        measured[0].Tags[HostTelemetry.ReplayTag].Should().Be(ExecutionWriteReplay.False);

        Activity span = spans.Should().ContainSingle().Subject;
        span.TagObjects.Select(tag => tag.Key).Should().BeEquivalentTo(
            HostTelemetry.OperationTag,
            HostTelemetry.RefusalTag,
            HostTelemetry.ReplayTag);
        span.GetTagItem(HostTelemetry.RefusalTag).Should().Be(ExecutionWriteRefusal.AccountWriterHeld);
    }

    [Fact]
    public async Task TradingOrderTools_WriterLockDroppedBeforeTheSend_DoesNotSend()
    {
        using HostTelemetry telemetry = new();
        ToolPayloads.TradingWriteResult result = await Call(
            Tools(telemetry, On(), new DropBeforeSend()),
            TradingMcpTools.PlaceOrderOperation,
            "dropped");

        result.Status.Should().Be(nameof(TradingAttemptOutcome.Refused));
        result.RefusalCode.Should().Be(ExecutionWriteRefusal.AccountWriterUnavailable);
        _gateway.Place.Should().Be(0);
    }

    // ===================== helpers =====================

    /// <summary>Calls once, then keeps retrying with a FRESH key only while the tool says SafeToRetry.</summary>
    private async Task<ToolPayloads.TradingWriteResult> CallerFollowingRetrySafety(HostTelemetry telemetry, string op)
    {
        ToolPayloads.TradingWriteResult first = await Call(Tools(telemetry, On()), op, "order-0");
        ToolPayloads.TradingWriteResult last = first;
        for (int attempt = 1; attempt < 3 && last.RetrySafety == ToolPayloads.TradingRetrySafety.SafeToRetry; attempt++)
        {
            last = await Call(Tools(telemetry, On()), op, "order-" + attempt);
        }

        return first;
    }

    private void AssertCapLock(string op, int sends)
    {
        bool capLocked = op is TradingMcpTools.PlaceOrderOperation
            or TradingMcpTools.ModifyOrderOperation
            or TradingMcpTools.PartialClosePositionOperation;
        _gateway.SendsInsideCapLock.Should().Be(capLocked ? sends : 0);
        _daily.Acquisitions.Should().Be(capLocked ? sends : 0);
        _daily.HeldNow.Should().Be(0);
    }

    private static void AssertOnlyARefusalRow(IReadOnlyList<TradingAuditWriteRequest> rows, string code)
    {
        TradingAuditWriteRequest row = rows.Should().ContainSingle(
            "a refusal before the send writes its own row and no claim, attempt or live row").Subject;
        row.Outcome.Should().Be(TradingAuditOutcomes.Refused);
        row.VenueErrorCode.Should().Be(code);
        row.OrderId.Should().BeNull();
    }

    private static void AssertOutcomeUnknown(ToolPayloads.TradingWriteResult result, long ackedOrderId)
    {
        result.Status.Should().Be(nameof(TradingAttemptOutcome.Unknown));
        result.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.OutcomeUnknown);
        // A post-send lease-release fault and a throwing span listener both keep the id the gateway
        // acknowledged (gh#780). The no-send refusal stays null.
        result.OrderId.Should().Be(ackedOrderId);
    }

    private static void AssertOneWrite(MetricCollector<long> writes, List<Activity> spans, string op, string reason)
    {
        IReadOnlyList<CollectedMeasurement<long>> measured = writes.GetMeasurementSnapshot();
        measured.Should().ContainSingle();
        measured[0].Value.Should().Be(1);
        measured[0].Tags[HostTelemetry.OperationTag].Should().Be(op);
        measured[0].Tags[HostTelemetry.RefusalTag].Should().Be(reason);

        Activity span = spans.Should().ContainSingle().Subject;
        span.OperationName.Should().Be("execution." + op);
        span.GetTagItem(HostTelemetry.OperationTag).Should().Be(op);
        span.GetTagItem(HostTelemetry.RefusalTag).Should().Be(reason);
    }

    private static ActivityListener Listen(HostTelemetry telemetry, List<Activity> into)
    {
        ActivityListener listener = new()
        {
            ShouldListenTo = source => ReferenceEquals(source, telemetry.Activities),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = into.Add,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static ActivityListener ThrowingOnStop(HostTelemetry telemetry)
    {
        ActivityListener listener = new()
        {
            ShouldListenTo = source => ReferenceEquals(source, telemetry.Activities),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.OperationName.StartsWith("execution.", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("listener failed after the ack");
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    /// <summary>The gate serves one operation, so each call needs a new gate; the doubles behind it are shared.</summary>
    private TradingOrderTools Tools(
        HostTelemetry telemetry,
        TradingExecutionOptions options,
        ITradingAccountWriterLock? writer = null) =>
        new(new TradingRiskGate(
            new TradingExecutionGate(TradingOptionsMonitors.Fixed(options), _kill, _stage),
            _gateway,
            _market,
            _stage,
            _daily,
            _audit,
            new TradingContractDenylist(Options.Create(options), _registry),
            new TradingWriteVenueAccountCache(),
            telemetry,
            writer));

    private static Task<ToolPayloads.TradingWriteResult> Call(
        TradingOrderTools tools,
        string op,
        string key,
        int size = 1,
        int account = Practice) => op switch
        {
            TradingMcpTools.PlaceOrderOperation =>
                tools.SubmitPlace(account, Mes, ExecutionOrderType.Limit, VenueSide.Buy, size, key, limitPrice: 6800m),
            TradingMcpTools.ModifyOrderOperation => tools.SubmitModify(account, OpenOrderId, key, size: size),
            TradingMcpTools.CancelOrderOperation => tools.SubmitCancel(account, OpenOrderId, key),
            TradingMcpTools.ClosePositionOperation => tools.SubmitClose(account, Mes, key),
            TradingMcpTools.PartialClosePositionOperation => tools.SubmitPartialClose(account, Mes, size, key),
            _ => throw new ArgumentOutOfRangeException(nameof(op)),
        };

    private sealed class DropBeforeSend : ITradingAccountWriterLock
    {
        private int _calls;

        public Task<TradingWriterLockTake> TryHoldAsync(int accountId, CancellationToken cancellationToken)
        {
            _calls++;
            return Task.FromResult(_calls == 1
                ? TradingWriterLockTake.Held
                : TradingWriterLockTake.Unavailable);
        }
    }

    private sealed class AccountWriters
    {
        private readonly Dictionary<int, int> _owner = [];

        public ITradingAccountWriterLock For(int replica) => new Replica(this, replica);

        public void Release(int replica)
        {
            int[] held = [.. _owner.Where(pair => pair.Value == replica).Select(pair => pair.Key)];
            foreach (int accountId in held)
            {
                _owner.Remove(accountId);
            }
        }

        private readonly Dictionary<int, int> _attempts = [];

        public int AttemptsFor(int replica) => _attempts.GetValueOrDefault(replica);

        private TradingWriterLockTake Try(int replica, int accountId)
        {
            _attempts[replica] = _attempts.GetValueOrDefault(replica) + 1;
            if (_owner.TryGetValue(accountId, out int owner))
            {
                return owner == replica
                    ? TradingWriterLockTake.Held
                    : TradingWriterLockTake.HeldByOther;
            }

            _owner[accountId] = replica;
            return TradingWriterLockTake.Held;
        }

        private sealed class Replica(AccountWriters owners, int replica) : ITradingAccountWriterLock
        {
            public Task<TradingWriterLockTake> TryHoldAsync(
                int accountId,
                CancellationToken cancellationToken) =>
                Task.FromResult(owners.Try(replica, accountId));
        }
    }

    private sealed class QaKill : ITradingKillSwitch
    {
        public bool IsEngaged { get; set; }
    }

    private sealed class QaGateway(QaDailyCap daily) : IExecutionGateway
    {
        private int _place;
        private int _others;
        private int _insideCapLock;
        private long _nextId = 1000;

        public int Place => _place;

        public int Total => _place + _others;

        /// <summary>Sends that ran while an account-day lease was held.</summary>
        public int SendsInsideCapLock => _insideCapLock;

        /// <summary>The order id assigned to the most recent acknowledgement. The seed until the first send.</summary>
        public long LastIssuedId => Volatile.Read(ref _nextId);

        public ExecutionWriteResult? Next { get; set; }

        public TaskCompletionSource? HoldPlace { get; set; }

        public TaskCompletionSource PlaceEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ExecutionWriteResult> PlaceOrder(ExecutionPlaceOrderCommand command, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _place);
            NoteCapLock();
            PlaceEntered.TrySetResult();
            if (HoldPlace is not null)
            {
                await HoldPlace.Task.ConfigureAwait(false);
            }

            return Result();
        }

        public Task<ExecutionWriteResult> ModifyOrder(ExecutionModifyOrderCommand command, CancellationToken cancellationToken) => Other();

        public Task<ExecutionWriteResult> CancelOrder(ExecutionCancelOrderCommand command, CancellationToken cancellationToken) => Other();

        public ExecutionClosePositionCommand? LastClose { get; private set; }

        public ExecutionPartialClosePositionCommand? LastPartialClose { get; private set; }

        public Task<ExecutionWriteResult> ClosePosition(ExecutionClosePositionCommand command, CancellationToken cancellationToken)
        {
            LastClose = command;
            return Other();
        }

        public Task<ExecutionWriteResult> PartialClosePosition(
            ExecutionPartialClosePositionCommand command,
            CancellationToken cancellationToken)
        {
            LastPartialClose = command;
            return Other();
        }

        private Task<ExecutionWriteResult> Other()
        {
            Interlocked.Increment(ref _others);
            NoteCapLock();
            return Task.FromResult(Result());
        }

        private void NoteCapLock()
        {
            if (daily.HeldNow > 0)
            {
                Interlocked.Increment(ref _insideCapLock);
            }
        }

        private ExecutionWriteResult Result() =>
            Next ?? new ExecutionWriteResult(
                ExecutionWriteOutcome.BrokerAcknowledged,
                GateRefusal: null,
                HttpStatusCode: null,
                VendorErrorCode: null,
                OrderId: Interlocked.Increment(ref _nextId));
    }

    /// <summary>Thread-safe in-memory audit store; uniqueness mirrors the partial unique index.</summary>
    private sealed class QaAudit : ITradingAuditLogWriter
    {
        private readonly List<TradingAuditWriteRequest> _rows = [];

        public IReadOnlyList<TradingAuditWriteRequest> Snapshot()
        {
            lock (_rows)
            {
                return [.. _rows];
            }
        }

        public Task WriteAsync(TradingAuditWriteRequest request, CancellationToken cancellationToken)
        {
            lock (_rows)
            {
                _rows.Add(request);
            }

            return Task.CompletedTask;
        }

        public Task<TradingAuditClaimResult> TryClaimUnknownAsync(
            TradingAuditWriteRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(TradingAuditClaimMemory.TryClaim(_rows, request));
    }

    /// <summary>
    /// Account-day lock double. Fault modes copy the real coordinator: a lease dispose can throw, or it can
    /// return and leave <see cref="LastReleaseFailed"/> set, which is what <c>TradingDailyCapCoordinator</c> does.
    /// </summary>
    private sealed class QaDailyCap : ITradingDailyCapCoordinator
    {
        private readonly SemaphoreSlim _lock = new(1, 1);
        private int _acquisitions;
        private int _held;
        private bool _lastReleaseFailed;

        public int ContractsToday { get; set; }

        public ReleaseFault Fault { get; set; }

        public bool Blocking { get; set; } = true;

        public int Acquisitions => _acquisitions;

        public int HeldNow => Volatile.Read(ref _held);

        public bool LastReleaseFailed => _lastReleaseFailed;

        public async Task<IAsyncDisposable> AcquireAccountDayLockAsync(int accountId, CancellationToken cancellationToken)
        {
            _lastReleaseFailed = false;
            if (Blocking)
            {
                await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            Interlocked.Increment(ref _acquisitions);
            Interlocked.Increment(ref _held);
            return new Lease(this);
        }

        public Task<int> GetConservativeContractTotalTodayUtcAsync(int accountId, CancellationToken cancellationToken) =>
            Task.FromResult(ContractsToday);

        private sealed class Lease(QaDailyCap owner) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                Interlocked.Decrement(ref owner._held);
                if (owner.Blocking)
                {
                    owner._lock.Release();
                }

                switch (owner.Fault)
                {
                    case ReleaseFault.Throws:
                        throw new InvalidOperationException("pg_advisory_unlock failed");
                    case ReleaseFault.ThrowsVenueException:
                        throw new VenueException("pg_advisory_unlock failed");
                    case ReleaseFault.ReportedFailed:
                        owner._lastReleaseFailed = true;
                        break;
                }

                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class QaMarket(IReadOnlyList<VenueAccount> accounts) : IMarketDataGateway
    {
        /// <summary>Signed size of the one open MES position. Positive is long.</summary>
        public int PositionSize { get; set; } = 3;

        public string VenueId => "qa";

        public Task<IReadOnlyList<VenueAccount>> GetAccountsAsync(bool onlyActive, CancellationToken cancellationToken) =>
            Task.FromResult(accounts);

        public Task<IReadOnlyList<VenueContract>> ResolveContractsAsync(InstrumentId instrument, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<VenueContract?> FindContractAsync(InstrumentId instrument, ContractExpiry expiry, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Bar>> GetBarsAsync(string contractId, BarRange window, TimeSpan barSize, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<VenuePosition>> GetOpenPositionsAsync(int accountId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VenuePosition>>(
                [new(Mes, SignedSize: PositionSize, AveragePrice: 6800m, OpenedAt: DateTimeOffset.UnixEpoch)]);

        public Task<IReadOnlyList<VenueOrder>> GetOrdersAsync(int accountId, BarRange? window, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VenueOrder>>(
            [
                new(OpenOrderId, Mes, VenueSide.Buy, Size: 1, FilledSize: 0, VenueOrderStatus.Open,
                    LimitPrice: 6800m, StopPrice: null, FilledPrice: null, CreatedAt: DateTimeOffset.UnixEpoch),
            ]);

        public Task<IReadOnlyList<VenueTrade>> GetTradesAsync(int accountId, BarRange window, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
