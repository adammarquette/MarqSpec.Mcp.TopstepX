using System.Diagnostics;
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

namespace MarqSpec.Mcp.TopstepX.Tests.Trading;

/// <summary>
/// Independent spec-driven QA for the five order tools (gh#750, PR #753, fix-up PR #769). Derived from PRD
/// R-4.1, R-5.13 and ADR-0024 sections 4-6, not from the engineers' tests, and kept to what
/// <c>TradingOrderToolGateTests</c> and <c>TradingRiskGateTests</c> do not already pin. In-process doubles
/// only: no vendor client, no network, no real orders.
/// </summary>
[Trait("Gate", "ADR-0024")]
[Trait("QA", "gh769")]
[Collection(HostTelemetryCollection.Name)]
public sealed class OrderToolSpecQaTests
{
    private const int Practice = 100;
    private const int OtherPractice = 101;
    private const int Funded = 102;
    private const string Mes = "CON.F.US.MES.Z26";
    private const long OpenOrderId = 7;

    private readonly QaGateway _gateway = new();
    private readonly QaAudit _audit = new();
    private readonly QaDailyCap _daily = new();
    private readonly QaKill _kill = new();

    public static TheoryData<string> AllTools() =>
    [
        TradingMcpTools.PlaceOrderOperation,
        TradingMcpTools.ModifyOrderOperation,
        TradingMcpTools.CancelOrderOperation,
        TradingMcpTools.ClosePositionOperation,
        TradingMcpTools.PartialClosePositionOperation,
    ];

    /// <summary>Every tool crossed with a refusal or a transmit, and the code R-5.13 telemetry must carry.</summary>
    public static TheoryData<string, string> ToolByScenario()
    {
        TheoryData<string, string> data = [];
        foreach (string op in new[]
                 {
                     TradingMcpTools.PlaceOrderOperation,
                     TradingMcpTools.ModifyOrderOperation,
                     TradingMcpTools.CancelOrderOperation,
                     TradingMcpTools.ClosePositionOperation,
                     TradingMcpTools.PartialClosePositionOperation,
                 })
        {
            data.Add(op, ExecutionWriteRefusal.TradingDisabled);
            data.Add(op, ExecutionWriteRefusal.AccountNotAllowlisted);
            data.Add(op, ExecutionWriteRefusal.NonPracticeAccount);
            data.Add(op, ExecutionWriteRefusal.IdempotencyKeyTooLong);
            data.Add(op, ExecutionWriteRefusal.Transmitted);
        }

        return data;
    }

    public static TheoryData<string, bool> ToolByCleanupFault()
    {
        TheoryData<string, bool> data = [];
        foreach (string op in AllTools())
        {
            data.Add(op, false);
            data.Add(op, true);
        }

        return data;
    }

    /// <summary>Tools the kill switch is decided for. Cancel and close are pending Adam (#770, S01).</summary>
    public static TheoryData<string> KillSwitchDecidedTools() =>
    [
        TradingMcpTools.PlaceOrderOperation,
        TradingMcpTools.ModifyOrderOperation,
        TradingMcpTools.PartialClosePositionOperation,
    ];

    /// <summary>Tools whose send runs inside the account-day cap lock.</summary>
    public static TheoryData<string> CapLockedTools() =>
    [
        TradingMcpTools.PlaceOrderOperation,
        TradingMcpTools.ModifyOrderOperation,
        TradingMcpTools.PartialClosePositionOperation,
    ];

    private static TradingExecutionOptions On(string? allow = "100,101", int perOrder = 5, string deny = "") =>
        new() { Enabled = true, AllowedAccountIds = allow, MaxContractsPerOrder = perOrder, DeniedSymbols = deny };

    // ---------------- R-5.13 telemetry + R-4.1 / ADR-0024 s5 refusal codes, every tool ----------------
    [Theory]
    [MemberData(nameof(ToolByScenario))]
    public async Task TradingOrderTools_EveryCall_EmitsOneSpanAndOneCounter_TaggedWithOperationAndReason(
        string op,
        string expected)
    {
        using HostTelemetry telemetry = new();
        using MetricCollector<long> writes = new(telemetry, HostTelemetry.Name, HostTelemetry.ExecutionWritesInstrument);
        List<Activity> spans = [];
        using ActivityListener listener = Listen(telemetry, spans);

        (TradingExecutionOptions options, int account, string key) = expected switch
        {
            ExecutionWriteRefusal.TradingDisabled => (new TradingExecutionOptions(), Practice, "k"),
            ExecutionWriteRefusal.AccountNotAllowlisted => (On("101"), Practice, "k"),
            ExecutionWriteRefusal.NonPracticeAccount => (On("100,102"), Funded, "k"),
            ExecutionWriteRefusal.IdempotencyKeyTooLong => (On(), Practice, new string('k', 257)),
            _ => (On(), Practice, "k"),
        };

        ToolPayloads.TradingWriteResult result = await Call(Tools(telemetry, options), op, key, account: account);

        if (expected == ExecutionWriteRefusal.Transmitted)
        {
            result.Status.Should().Be(nameof(TradingAttemptOutcome.Live));
            result.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.NotSafeToRetry);
            _gateway.Total.Should().Be(1);
        }
        else
        {
            result.Status.Should().Be(nameof(TradingAttemptOutcome.Refused));
            result.RefusalCode.Should().Be(expected);
            result.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.SafeToRetry);
            _gateway.Total.Should().Be(0);
        }

        if (expected is ExecutionWriteRefusal.TradingDisabled or ExecutionWriteRefusal.IdempotencyKeyTooLong)
        {
            _audit.Snapshot().Should().BeEmpty();
        }

        AssertOneWrite(writes, spans, op, expected);
    }

    // ---------------- ADR-0024 s5.2: kill switch, decided tools only ----------------
    [Theory]
    [MemberData(nameof(KillSwitchDecidedTools))]
    public async Task TradingOrderTools_KillSwitch_RefusesPlaceModifyAndPartialClose_WithTelemetry(string op)
    {
        _kill.IsEngaged = true;
        using HostTelemetry telemetry = new();
        using MetricCollector<long> writes = new(telemetry, HostTelemetry.Name, HostTelemetry.ExecutionWritesInstrument);
        List<Activity> spans = [];
        using ActivityListener listener = Listen(telemetry, spans);

        ToolPayloads.TradingWriteResult result = await Call(Tools(telemetry, On()), op, "k");

        result.RefusalCode.Should().Be(ExecutionWriteRefusal.KillSwitch);
        _gateway.Total.Should().Be(0);
        AssertOneWrite(writes, spans, op, ExecutionWriteRefusal.KillSwitch);
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
        AssertOneWrite(writes, spans, TradingMcpTools.PlaceOrderOperation, expected);
    }

    // ---------------- ADR-0024 s5.7: concurrent same key, deterministic overlap ----------------
    [Fact]
    public async Task TradingOrderTools_PlaceOrder_SameKeyWhileTheSendIsInFlight_SendsOnce_AndReplaysTheSameOrderId()
    {
        _daily.Blocking = false;
        _gateway.HoldPlace = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using HostTelemetry telemetry = new();

        Task<ToolPayloads.TradingWriteResult> first =
            Call(Tools(telemetry, On()), TradingMcpTools.PlaceOrderOperation, "race");
        await _gateway.PlaceEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The first send is provably in flight: the claim exists and the venue has not answered.
        ToolPayloads.TradingWriteResult[] inFlight = await Task.WhenAll(Enumerable.Range(0, 7)
            .Select(_ => Call(Tools(telemetry, On()), TradingMcpTools.PlaceOrderOperation, "race")));

        _gateway.HoldPlace.SetResult();
        ToolPayloads.TradingWriteResult winner = await first;
        ToolPayloads.TradingWriteResult replay =
            await Call(Tools(telemetry, On()), TradingMcpTools.PlaceOrderOperation, "race");

        _gateway.Place.Should().Be(1);
        inFlight.Should().OnlyContain(r => r.Status == nameof(TradingAttemptOutcome.Unknown)
            && r.RetrySafety == ToolPayloads.TradingRetrySafety.OutcomeUnknown
            && r.OrderId == null);
        winner.Status.Should().Be(nameof(TradingAttemptOutcome.Live));
        winner.OrderId.Should().NotBeNull();
        replay.Status.Should().Be(nameof(TradingAttemptOutcome.Live));
        replay.OrderId.Should().Be(winner.OrderId);
        _gateway.Place.Should().Be(1);
    }

    // ---------------- Duplicate-order bug (#753 round 4), the caller's view ----------------
    // R-5.13: OutcomeUnknown when the send may have happened; ADR: a fresh key per order. A caller that obeys
    // retrySafety retries with a fresh key only on SafeToRetry, so an acked order must never come back that way.
    [Theory]
    [MemberData(nameof(CapLockedTools))]
    public async Task TradingOrderTools_UnlockFailsAfterTheAck_ACallerFollowingRetrySafety_SendsOnce(string op)
    {
        _daily.ThrowOnRelease = true;
        using HostTelemetry telemetry = new();

        ToolPayloads.TradingWriteResult first = await CallerFollowingRetrySafety(telemetry, op);

        _gateway.Total.Should().Be(1, "an acked write followed by a bookkeeping fault must not be SafeToRetry");
        AssertOutcomeUnknown(first);
    }

    [Theory]
    [MemberData(nameof(AllTools))]
    public async Task TradingOrderTools_SpanListenerThrowsAfterTheAck_ACallerFollowingRetrySafety_SendsOnce(string op)
    {
        using HostTelemetry telemetry = new();
        using ActivityListener listener = ThrowingOnStop(telemetry);

        ToolPayloads.TradingWriteResult first = await CallerFollowingRetrySafety(telemetry, op);

        _gateway.Total.Should().Be(1);
        AssertOutcomeUnknown(first);
    }

    // ---------------- M9: the gate's post-send catch (cleanup step throws after the ack) ----------------
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TradingOrderTools_PlaceOrder_CleanupThrowsAfterTheAck_IsOutcomeUnknown_NotRetryable(bool venueFault)
    {
        _daily.ThrowFromReleaseCheck = venueFault
            ? new VenueException("cap release check failed")
            : new InvalidOperationException("cap release check failed");
        using HostTelemetry telemetry = new();

        ToolPayloads.TradingWriteResult first = await CallerFollowingRetrySafety(telemetry, TradingMcpTools.PlaceOrderOperation);

        _gateway.Place.Should().Be(1);
        AssertOutcomeUnknown(first);
    }

    [Theory]
    [MemberData(nameof(ToolByCleanupFault))]
    public async Task TradingOrderTools_CleanupFaultAfterTheAck_IsOutcomeUnknown_ForEveryTool(string op, bool venueFault)
    {
        using HostTelemetry telemetry = new();
        TradingRiskGate gate = Gate(telemetry, On());
        gate.CleanupFaultForTest = venueFault
            ? new VenueException("cleanup failed after the send")
            : new InvalidOperationException("cleanup failed after the send");

        ToolPayloads.TradingWriteResult first = await Call(new TradingOrderTools(gate), op, "order-0");
        if (first.RetrySafety == ToolPayloads.TradingRetrySafety.SafeToRetry)
        {
            await Call(Tools(telemetry, On()), op, "order-1");
        }

        _gateway.Total.Should().Be(1);
        AssertOutcomeUnknown(first);
    }

    // ---------------- Unmappable venue outcome after the send ----------------
    [Theory]
    [MemberData(nameof(AllTools))]
    public async Task TradingOrderTools_UnrecognisedVenueOutcomeAfterTheSend_IsOutcomeUnknown_AndDoesNotResend(string op)
    {
        _gateway.Next = new ExecutionWriteResult((ExecutionWriteOutcome)99, null, 200, null, 4242);
        using HostTelemetry telemetry = new();

        ToolPayloads.TradingWriteResult first = await CallerFollowingRetrySafety(telemetry, op);

        _gateway.Total.Should().Be(1);
        first.Status.Should().Be(nameof(TradingAttemptOutcome.Unknown));
        first.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.OutcomeUnknown);
    }

    // ---------------- M2 / M6: the tool's last-resort catch ----------------
    // The gate classifies every fault it sees, so this catch is reachable only from the tool's run step.
    [Fact]
    public async Task TradingOrderTools_RunStep_UnclassifiedException_IsOutcomeUnknown_NeverSafeToRetry()
    {
        ToolPayloads.TradingWriteResult result = await TradingOrderTools.RunAsync(
            Task.FromException<TradingRiskGateResult>(new InvalidOperationException("unclassified")));

        result.Status.Should().Be(nameof(TradingAttemptOutcome.Unknown));
        result.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.OutcomeUnknown);
        result.RefusalCode.Should().BeNull();
        result.ResultCode.Should().Be(ToolPayloads.UnclassifiedFaultResultCode);
        result.OrderId.Should().BeNull();
    }

    // ---------------- Control: a cleanup fault with no send stays a plain refusal ----------------
    [Fact]
    public async Task TradingOrderTools_PlaceOrder_UnlockFailsWithoutASend_StaysARetryableRefusal()
    {
        _daily.ThrowOnRelease = true;
        _daily.ContractsToday = 50;
        using HostTelemetry telemetry = new();

        ToolPayloads.TradingWriteResult result =
            await Call(Tools(telemetry, On()), TradingMcpTools.PlaceOrderOperation, "cap");

        _gateway.Total.Should().Be(0);
        result.Status.Should().Be(nameof(TradingAttemptOutcome.Refused));
        result.RefusalCode.Should().Be(ExecutionWriteRefusal.DailyContractLimitExceeded);
        result.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.SafeToRetry);
    }

    // ---------------- Spec gaps ----------------
    [Fact(Skip = "SpecGap #770: whether the kill switch keeps blocking cancel_order and close_position is pending Adam (ADR-0024 s5.1-5.2).")]
    public void TradingOrderTools_KillSwitch_CancelAndClose_PendingDecision()
    {
    }

    [Fact(Skip = "SpecGap #771: no venue-side client order id; customTag is not exposed (ADR-0008), so a lost ack plus a fresh key relies on OutcomeUnknown and reconcile only.")]
    public void TradingOrderTools_VenueSideClientOrderId_Unspecified()
    {
    }

    [Fact(Skip = "SpecGap #772: session conflicts (two MCP sessions or replicas on one account, or an order into a closed session) are not specified beyond the per-key claim and the account-day cap lock.")]
    public void TradingOrderTools_SessionConflicts_Unspecified()
    {
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

    private static void AssertOutcomeUnknown(ToolPayloads.TradingWriteResult result)
    {
        result.Status.Should().Be(nameof(TradingAttemptOutcome.Unknown));
        result.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.OutcomeUnknown);
        result.OrderId.Should().BeNull();
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

    private TradingOrderTools Tools(HostTelemetry telemetry, TradingExecutionOptions options) =>
        new(Gate(telemetry, options));

    private TradingRiskGate Gate(HostTelemetry telemetry, TradingExecutionOptions options)
    {
        ProjectXAccountStageResolver stage = new();
        TradingExecutionGate execution = new(TradingOptionsMonitors.Fixed(options), _kill, stage);
        InstrumentRegistry registry = new(Options.Create(new MarketDataOptions { Instruments = "MES" }));
        TradingContractDenylist denylist = new(Options.Create(options), registry);
        IReadOnlyList<VenueAccount> accounts =
        [
            new(Practice, AccountStage.Practice, CanTrade: true, IsVisible: true, Balance: 50_000m),
            new(OtherPractice, AccountStage.Practice, CanTrade: true, IsVisible: true, Balance: 50_000m),
            new(Funded, AccountStage.Funded, CanTrade: true, IsVisible: true, Balance: 50_000m),
        ];
        return new TradingRiskGate(
            execution,
            _gateway,
            new QaMarket(accounts),
            stage,
            new QaSignal(),
            _daily,
            _audit,
            denylist,
            new TradingWriteVenueAccountCache(),
            telemetry);
    }

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

    private sealed class QaKill : ITradingKillSwitch
    {
        public bool IsEngaged { get; set; }
    }

    private sealed class QaSignal : ITradingUnknownStageRefusalSignal
    {
        public void UnknownStageWriteRefused(int accountId)
        {
        }
    }

    private sealed class QaGateway : IExecutionGateway
    {
        private int _place;
        private int _others;
        private long _nextId = 1000;

        public int Place => _place;

        public int Total => _place + _others;

        public ExecutionWriteResult? Next { get; set; }

        public TaskCompletionSource? HoldPlace { get; set; }

        public TaskCompletionSource PlaceEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ExecutionWriteResult> PlaceOrder(ExecutionPlaceOrderCommand command, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _place);
            PlaceEntered.TrySetResult();
            if (HoldPlace is not null)
            {
                await HoldPlace.Task.ConfigureAwait(false);
            }

            return Result();
        }

        public Task<ExecutionWriteResult> ModifyOrder(ExecutionModifyOrderCommand command, CancellationToken cancellationToken) => Other();

        public Task<ExecutionWriteResult> CancelOrder(ExecutionCancelOrderCommand command, CancellationToken cancellationToken) => Other();

        public Task<ExecutionWriteResult> ClosePosition(ExecutionClosePositionCommand command, CancellationToken cancellationToken) => Other();

        public Task<ExecutionWriteResult> PartialClosePosition(
            ExecutionPartialClosePositionCommand command,
            CancellationToken cancellationToken) => Other();

        private Task<ExecutionWriteResult> Other()
        {
            Interlocked.Increment(ref _others);
            return Task.FromResult(Result());
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

    private sealed class QaDailyCap : ITradingDailyCapCoordinator
    {
        private readonly SemaphoreSlim _lock = new(1, 1);

        public int ContractsToday { get; set; }

        public bool ThrowOnRelease { get; set; }

        public bool Blocking { get; set; } = true;

        /// <summary>Thrown when the gate asks whether the last release failed, i.e. from its cleanup step.</summary>
        public Exception? ThrowFromReleaseCheck { get; set; }

        public bool LastReleaseFailed => ThrowFromReleaseCheck is Exception fault ? throw fault : false;

        public async Task<IAsyncDisposable> AcquireAccountDayLockAsync(int accountId, CancellationToken cancellationToken)
        {
            if (Blocking)
            {
                await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return new Lease(this);
        }

        public Task<int> GetConservativeContractTotalTodayUtcAsync(int accountId, CancellationToken cancellationToken) =>
            Task.FromResult(ContractsToday);

        private sealed class Lease(QaDailyCap owner) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                if (owner.Blocking)
                {
                    owner._lock.Release();
                }

                return owner.ThrowOnRelease
                    ? throw new InvalidOperationException("pg_advisory_unlock failed")
                    : ValueTask.CompletedTask;
            }
        }
    }

    private sealed class QaMarket(IReadOnlyList<VenueAccount> accounts) : IMarketDataGateway
    {
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
                [new(Mes, SignedSize: 3, AveragePrice: 6800m, OpenedAt: DateTimeOffset.UnixEpoch)]);

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
