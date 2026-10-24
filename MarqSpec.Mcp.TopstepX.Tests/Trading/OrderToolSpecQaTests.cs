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
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.Tests.Trading;

/// <summary>
/// Independent spec-driven QA for the five order tools (gh#750, PR #753, fix-up PR #769).
/// Derived from PRD R-4.1, R-5.13 and ADR-0024 sections 4-6; not from the engineers' tests.
/// Everything runs against in-process doubles. No vendor client, no network.
/// </summary>
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
    private Exception? _accountsFailure;

    public static TheoryData<string> Tools5() =>
    [
        TradingMcpTools.PlaceOrderOperation,
        TradingMcpTools.ModifyOrderOperation,
        TradingMcpTools.CancelOrderOperation,
        TradingMcpTools.ClosePositionOperation,
        TradingMcpTools.PartialClosePositionOperation,
    ];

    private static TradingExecutionOptions On(string? allow = "100,101", int perOrder = 5, string deny = "") =>
        new() { Enabled = true, AllowedAccountIds = allow, MaxContractsPerOrder = perOrder, DeniedSymbols = deny };

    // ---------- Q01 R-4.1 / R-5.13 / ADR-0024 s5.1: master switch off => no order path ----------
    [Theory, MemberData(nameof(Tools5))]
    public async Task Q01_TradingDisabled_RefusesEveryTool_NoGatewayNoAuditNoCap(string op)
    {
        using HostTelemetry t = new();
        var r = await Call(Tools(t, new TradingExecutionOptions()), op, "k1");
        r.Status.Should().Be("Refused");
        r.RefusalCode.Should().Be("trading_disabled");
        r.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.SafeToRetry);
        _gateway.Total.Should().Be(0);
        _audit.Snapshot().Should().BeEmpty();
        _daily.Acquired.Should().Be(0);
    }

    // ---------- Q02 R-4.1 / ADR s5.2: kill switch blocks all five, including cancel and close ----------
    [Theory, MemberData(nameof(Tools5))]
    public async Task Q02_KillSwitch_RefusesEveryTool_IncludingCancelAndClose(string op)
    {
        _kill.IsEngaged = true;
        using HostTelemetry t = new();
        var r = await Call(Tools(t, On()), op, "k1");
        r.Status.Should().Be("Refused");
        r.RefusalCode.Should().Be("kill_switch");
        _gateway.Total.Should().Be(0);
    }

    // ---------- Q03 ADR s5.3: non-practice account refused even when allowlisted ----------
    [Theory, MemberData(nameof(Tools5))]
    public async Task Q03_NonPracticeAccount_Refused_EvenIfAllowlisted(string op)
    {
        using HostTelemetry t = new();
        var r = await Call(Tools(t, On("100,102")), op, "k1", account: Funded);
        r.Status.Should().Be("Refused");
        _gateway.Total.Should().Be(0);
    }

    // ---------- Q04 ADR s5.4: allowlist miss refused ----------
    [Theory, MemberData(nameof(Tools5))]
    public async Task Q04_AllowlistMiss_Refused_NoSend(string op)
    {
        using HostTelemetry t = new();
        var r = await Call(Tools(t, On("101")), op, "k1");
        r.Status.Should().Be("Refused");
        r.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.SafeToRetry);
        _gateway.Total.Should().Be(0);
    }

    // ---------- Q05 ADR s5.5: per-order cap ----------
    [Fact]
    public async Task Q05_PlaceOverPerOrderCap_Refused_NoSend()
    {
        using HostTelemetry t = new();
        var r = await Call(Tools(t, On(perOrder: 2)), TradingMcpTools.PlaceOrderOperation, "k1", size: 3);
        r.Status.Should().Be("Refused");
        _gateway.Total.Should().Be(0);
    }

    // ---------- Q06 ADR s5.5: daily cap ----------
    [Fact]
    public async Task Q06_PlaceOverDailyCap_Refused_NoSend()
    {
        _daily.ContractsToday = 50;
        using HostTelemetry t = new();
        var r = await Call(Tools(t, On()), TradingMcpTools.PlaceOrderOperation, "k1", size: 1);
        r.Status.Should().Be("Refused");
        _gateway.Total.Should().Be(0);
    }

    // ---------- Q07 ADR s5.5: symbol denylist ----------
    [Fact]
    public async Task Q07_DeniedSymbol_Refused_NoSend()
    {
        using HostTelemetry t = new();
        var r = await Call(Tools(t, On(deny: "MES")), TradingMcpTools.PlaceOrderOperation, "k1");
        r.Status.Should().Be("Refused");
        _gateway.Total.Should().Be(0);
    }

    // ---------- Q08 ADR s4 / s5.6: dryRun previews, never sends, is not a claim ----------
    [Fact]
    public async Task Q08_DryRun_Previews_NoSend_ThenRealSendWithSameKeyStillSendsOnce()
    {
        using HostTelemetry t = new();
        var tools = Tools(t, On());
        var p = await tools.SubmitPlace(Practice, Mes, ExecutionOrderType.Limit, VenueSide.Buy, 1, "k1", limitPrice: 6800m, dryRun: true);
        p.Status.Should().Be("Preview");
        _gateway.Total.Should().Be(0);
        // dryRun is in the request hash, so the real send with the same key is a different hash on the triple.
        // ADR says a preview "is not a claim" => the real send must not be blocked as a replay of the preview.
        var live = await tools.SubmitPlace(Practice, Mes, ExecutionOrderType.Limit, VenueSide.Buy, 1, "k1", limitPrice: 6800m);
        live.Status.Should().Be("Live");
        _gateway.Place.Should().Be(1);
    }

    // ---------- Q09 R-5.13 / ADR s6 idempotency: duplicate key replays, adapter called once ----------
    [Theory, MemberData(nameof(Tools5))]
    public async Task Q09_SameKeySameRequest_Replays_NoSecondSend(string op)
    {
        using HostTelemetry t = new();
        var first = await Call(Tools(t, On()), op, "dup");
        var again = await Call(Tools(t, On()), op, "dup"); // a fresh gate instance = a restart / second replica
        first.Status.Should().Be("Live");
        again.Status.Should().Be("Live");
        again.OrderId.Should().Be(first.OrderId);
        again.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.NotSafeToRetry);
        _gateway.Total.Should().Be(1);
    }

    // ---------- Q10 ADR threat "Key reuse": different request hash on same triple => idempotency_conflict ----------
    [Fact]
    public async Task Q10_SameKeyDifferentRequest_IdempotencyConflict_NoSecondSend()
    {
        using HostTelemetry t = new();
        await Call(Tools(t, On()), TradingMcpTools.PlaceOrderOperation, "dup", size: 1);
        var r = await Call(Tools(t, On()), TradingMcpTools.PlaceOrderOperation, "dup", size: 2);
        r.Status.Should().Be("Refused");
        r.RefusalCode.Should().Be("idempotency_conflict");
        _gateway.Place.Should().Be(1);
    }

    // ---------- Q11 R-5.13: key scoped per account and per tool ----------
    [Fact]
    public async Task Q11_SameKeyOtherAccount_IsADifferentClaim()
    {
        using HostTelemetry t = new();
        await Call(Tools(t, On()), TradingMcpTools.PlaceOrderOperation, "shared");
        var r = await Call(Tools(t, On()), TradingMcpTools.PlaceOrderOperation, "shared", account: OtherPractice);
        r.Status.Should().Be("Live");
        _gateway.Place.Should().Be(2);
    }

    // ---------- Q12 R-5.13: key > 256 chars => idempotency_key_too_long, no row; blank => required, audited ----------
    [Fact]
    public async Task Q12_KeyTooLong_NoRow_NoSend_BlankKeyRefused()
    {
        using HostTelemetry t = new();
        var r = await Call(Tools(t, On()), TradingMcpTools.PlaceOrderOperation, new string('k', 257));
        r.RefusalCode.Should().Be("idempotency_key_too_long");
        _audit.Snapshot().Should().BeEmpty();
        var ok = await Call(Tools(t, On()), TradingMcpTools.PlaceOrderOperation, new string('k', 256));
        ok.Status.Should().Be("Live");
        var blank = await Call(Tools(t, On()), TradingMcpTools.PlaceOrderOperation, " ");
        blank.RefusalCode.Should().Be("idempotency_key_required");
        _gateway.Place.Should().Be(1);
    }

    // ---------- Q13 ADR "Live vs preview": send throws / times out => OutcomeUnknown; same-key retry does not resend ----------
    [Fact]
    public async Task Q13_SendTimesOut_OutcomeUnknown_SameKeyRetryDoesNotResend()
    {
        _gateway.PlaceThrows = new TimeoutException("simulated timeout after the request left");
        using HostTelemetry t = new();
        var r = await Call(Tools(t, On()), TradingMcpTools.PlaceOrderOperation, "t1");
        r.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.OutcomeUnknown);
        r.Status.Should().NotBe("Live");
        _gateway.PlaceThrows = null;
        var retry = await Call(Tools(t, On()), TradingMcpTools.PlaceOrderOperation, "t1");
        retry.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.OutcomeUnknown);
        _gateway.Place.Should().Be(1);
    }

    // ---------- Q14 ADR: success:false / not acked is not Live ----------
    [Fact]
    public async Task Q14_VenueRejection_IsNotLive_AndIsSafeToRetry()
    {
        _gateway.Next = new ExecutionWriteResult(ExecutionWriteOutcome.VenueRejected, null, 200, 2, null);
        using HostTelemetry t = new();
        var r = await Call(Tools(t, On()), TradingMcpTools.PlaceOrderOperation, "rej");
        r.Status.Should().NotBe("Live");
        r.OrderId.Should().BeNull();
        r.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.SafeToRetry);
    }

    // ---------- Q15 ADR / catalogue: fault before the send => retryable error, no side effects ----------
    [Fact]
    public async Task Q15_FaultBeforeSend_SafeToRetry_NoSend()
    {
        _accountsFailure = new HttpRequestException("account read failed");
        using HostTelemetry t = new();
        var r = await Call(Tools(t, On()), TradingMcpTools.PlaceOrderOperation, "pre");
        r.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.SafeToRetry);
        _gateway.Total.Should().Be(0);
    }

    // ---------- Q16 R-5.13 / ADR s5.7: concurrent calls with the same key => exactly one send ----------
    [Fact]
    public async Task Q16_ConcurrentSameKey_ExactlyOneSend()
    {
        _gateway.Delay = TimeSpan.FromMilliseconds(100);
        using HostTelemetry t = new();
        var calls = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => Call(Tools(t, On()), TradingMcpTools.PlaceOrderOperation, "race")))
            .ToArray();
        var results = await Task.WhenAll(calls);
        _gateway.Place.Should().Be(1);
        results.Should().Contain(x => x.Status == "Live"
            && x.RetrySafety == ToolPayloads.TradingRetrySafety.NotSafeToRetry);
        results.Should().NotContain(x => x.RetrySafety == ToolPayloads.TradingRetrySafety.SafeToRetry
            && x.Status != "Refused");
    }

    // ---------- Q17 DUP-ORDER BUG (#753 round 4): unlock fails after ack ----------
    // Spec: "OutcomeUnknown is returned only when the send may have happened. A fault before the send is a
    // retryable error" (R-5.13). An acked order must never come back SafeToRetry, because a model following
    // the documented retry signal sends again with a fresh key (ADR: "A fresh key per order").
    [Fact]
    public async Task Q17_DupOrder_UnlockThrowsAfterAck_RetryPolicyMustNotPlaceASecondOrder()
    {
        _daily.ThrowOnRelease = true;
        using HostTelemetry t = new();
        int placed = await ModelFollowsRetrySafety(() => Tools(t, On()));
        placed.Should().Be(1, "an acknowledged order followed by a bookkeeping fault must not be SafeToRetry");
    }

    // ---------- Q18 DUP-ORDER BUG variant: telemetry span listener throws after ack ----------
    [Fact]
    public async Task Q18_DupOrder_SpanListenerThrowsAfterAck_RetryPolicyMustNotPlaceASecondOrder()
    {
        using HostTelemetry t = new();
        using ActivityListener l = new()
        {
            ShouldListenTo = s => ReferenceEquals(s, t.Activities),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a =>
            {
                if (a.OperationName.StartsWith("execution.", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("listener boom");
                }
            },
        };
        ActivitySource.AddActivityListener(l);
        int placed = await ModelFollowsRetrySafety(() => Tools(t, On()));
        placed.Should().Be(1);
    }

    // ---------- Q19 the single-call shape of the bug, for the matrix ----------
    [Fact]
    public async Task Q19_AckThenBookkeepingFault_ReturnsOutcomeUnknown_NotSafeToRetry()
    {
        _daily.ThrowOnRelease = true;
        using HostTelemetry t = new();
        var r = await Call(Tools(t, On()), TradingMcpTools.PlaceOrderOperation, "ack-then-fault");
        _gateway.Place.Should().Be(1);
        r.RetrySafety.Should().NotBe(ToolPayloads.TradingRetrySafety.SafeToRetry);
        r.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.OutcomeUnknown);
    }

    // ---------- Q20 control: unlock fault BEFORE the send (cap refusal) stays a plain refusal ----------
    [Fact]
    public async Task Q20_UnlockFaultWithoutSend_StaysSafe_NoSend()
    {
        _daily.ThrowOnRelease = true;
        _daily.ContractsToday = 50;
        using HostTelemetry t = new();
        var r = await Call(Tools(t, On()), TradingMcpTools.PlaceOrderOperation, "cap");
        _gateway.Total.Should().Be(0);
        r.RetrySafety.Should().NotBe(ToolPayloads.TradingRetrySafety.NotSafeToRetry);
    }

    // ---------- SpecGaps (documented, not guessed) ----------
    [Fact(Skip = "SpecGap: ADR-0024 s5.1/5.2 marks 'kill switch blocks cancel/close' as PENDING Adam. Q02 pins current behaviour only.")]
    public void S01_KillSwitchFlattenCarveOut() { }

    [Fact(Skip = "SpecGap: no requirement covers a venue-side client order id. customTag is deliberately not exposed (ADR-0008), so a lost ack + caller-chosen fresh key cannot be deduplicated at the venue; the spec relies on OutcomeUnknown + reconcile only.")]
    public void S02_VenueClientOrderId() { }

    [Fact(Skip = "SpecGap: 'session conflicts' (two MCP sessions/replicas trading one account, or an order on a closed session) is not specified beyond the per-(account,tool,key) claim and the account-day cap lock. Cross-key concurrent orders on one account are allowed by spec.")]
    public void S03_SessionConflicts() { }

    // ===================== helpers =====================

    /// <summary>A caller that obeys the catalogue: retry with a FRESH key only on SafeToRetry, up to 3 times.</summary>
    private async Task<int> ModelFollowsRetrySafety(Func<TradingOrderTools> scope)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var r = await Call(scope(), TradingMcpTools.PlaceOrderOperation, "order-" + attempt);
            if (r.RetrySafety != ToolPayloads.TradingRetrySafety.SafeToRetry)
            {
                break;
            }
        }

        return _gateway.Place;
    }

    private TradingOrderTools Tools(HostTelemetry t, TradingExecutionOptions o)
    {
        ProjectXAccountStageResolver stage = new();
        TradingExecutionGate exec = new(TradingOptionsMonitors.Fixed(o), _kill, stage);
        InstrumentRegistry reg = new(Options.Create(new MarketDataOptions { Instruments = "MES" }));
        TradingContractDenylist deny = new(Options.Create(o), reg);
        IReadOnlyList<VenueAccount> accounts =
        [
            new(Practice, AccountStage.Practice, CanTrade: true, IsVisible: true, Balance: 50_000m),
            new(OtherPractice, AccountStage.Practice, CanTrade: true, IsVisible: true, Balance: 50_000m),
            new(Funded, AccountStage.Funded, CanTrade: true, IsVisible: true, Balance: 50_000m),
        ];
        return new TradingOrderTools(new TradingRiskGate(exec, _gateway, new QaMarket(accounts, () => _accountsFailure),
            stage, new QaSignal(), _daily, _audit, deny, new TradingWriteVenueAccountCache(), t));
    }

    private static Task<ToolPayloads.TradingWriteResult> Call(TradingOrderTools tools, string op, string key,
        int size = 1, int account = Practice) => op switch
        {
            TradingMcpTools.PlaceOrderOperation => tools.SubmitPlace(account, Mes, ExecutionOrderType.Limit, VenueSide.Buy, size, key, limitPrice: 6800m),
            TradingMcpTools.ModifyOrderOperation => tools.SubmitModify(account, OpenOrderId, key, size: size),
            TradingMcpTools.CancelOrderOperation => tools.SubmitCancel(account, OpenOrderId, key),
            TradingMcpTools.ClosePositionOperation => tools.SubmitClose(account, Mes, key),
            TradingMcpTools.PartialClosePositionOperation => tools.SubmitPartialClose(account, Mes, size, key),
            _ => throw new ArgumentOutOfRangeException(nameof(op)),
        };

    private sealed class QaKill : ITradingKillSwitch { public bool IsEngaged { get; set; } }

    private sealed class QaSignal : ITradingUnknownStageRefusalSignal { public void UnknownStageWriteRefused(int accountId) { } }

    private sealed class QaGateway : IExecutionGateway
    {
        private int _place, _modify, _cancel, _close, _partial;
        private long _nextId = 1000;
        public int Place => _place;
        public int Total => _place + _modify + _cancel + _close + _partial;
        public ExecutionWriteResult? Next { get; set; }
        public Exception? PlaceThrows { get; set; }
        public TimeSpan Delay { get; set; }

        private async Task<ExecutionWriteResult> Send()
        {
            if (Delay > TimeSpan.Zero) { await Task.Delay(Delay); }
            return Next ?? new ExecutionWriteResult(ExecutionWriteOutcome.BrokerAcknowledged, null, null, null, Interlocked.Increment(ref _nextId));
        }

        public Task<ExecutionWriteResult> PlaceOrder(ExecutionPlaceOrderCommand c, CancellationToken ct)
        {
            Interlocked.Increment(ref _place);
            if (PlaceThrows is not null) { throw PlaceThrows; }
            return Send();
        }

        public Task<ExecutionWriteResult> ModifyOrder(ExecutionModifyOrderCommand c, CancellationToken ct) { Interlocked.Increment(ref _modify); return Send(); }
        public Task<ExecutionWriteResult> CancelOrder(ExecutionCancelOrderCommand c, CancellationToken ct) { Interlocked.Increment(ref _cancel); return Send(); }
        public Task<ExecutionWriteResult> ClosePosition(ExecutionClosePositionCommand c, CancellationToken ct) { Interlocked.Increment(ref _close); return Send(); }
        public Task<ExecutionWriteResult> PartialClosePosition(ExecutionPartialClosePositionCommand c, CancellationToken ct) { Interlocked.Increment(ref _partial); return Send(); }
    }

    /// <summary>Thread-safe in-memory audit store; uniqueness mirrors the partial unique index.</summary>
    private sealed class QaAudit : ITradingAuditLogWriter
    {
        private readonly List<TradingAuditWriteRequest> _rows = [];
        public IReadOnlyList<TradingAuditWriteRequest> Snapshot() { lock (_rows) { return _rows.ToList(); } }
        public Task WriteAsync(TradingAuditWriteRequest r, CancellationToken ct) { lock (_rows) { _rows.Add(r); } return Task.CompletedTask; }
        public Task<TradingAuditClaimResult> TryClaimUnknownAsync(TradingAuditWriteRequest r, CancellationToken ct) =>
            Task.FromResult(TradingAuditClaimMemory.TryClaim(_rows, r));
    }

    private sealed class QaDailyCap : ITradingDailyCapCoordinator
    {
        private readonly SemaphoreSlim _lock = new(1, 1);
        public int Acquired;
        public int ContractsToday { get; set; }
        public bool ThrowOnRelease { get; set; }

        public async Task<IAsyncDisposable> AcquireAccountDayLockAsync(int accountId, CancellationToken ct)
        {
            Interlocked.Increment(ref Acquired);
            await _lock.WaitAsync(ct);
            return new Lease(this);
        }

        public Task<int> GetConservativeContractTotalTodayUtcAsync(int accountId, CancellationToken ct) => Task.FromResult(ContractsToday);

        private sealed class Lease(QaDailyCap o) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                o._lock.Release();
                if (o.ThrowOnRelease) { throw new InvalidOperationException("pg_advisory_unlock failed"); }
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class QaMarket(IReadOnlyList<VenueAccount> accounts, Func<Exception?> fail) : IMarketDataGateway
    {
        public string VenueId => "qa";
        public Task<IReadOnlyList<VenueAccount>> GetAccountsAsync(bool onlyActive, CancellationToken ct) =>
            fail() is Exception e ? throw e : Task.FromResult(accounts);
        public Task<IReadOnlyList<VenueContract>> ResolveContractsAsync(InstrumentId i, CancellationToken ct) => throw new NotSupportedException();
        public Task<VenueContract?> FindContractAsync(InstrumentId i, ContractExpiry e, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<Bar>> GetBarsAsync(string c, BarRange w, TimeSpan s, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<VenuePosition>> GetOpenPositionsAsync(int a, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<VenuePosition>>([new(Mes, SignedSize: 3, AveragePrice: 6800m, OpenedAt: DateTimeOffset.UnixEpoch)]);
        public Task<IReadOnlyList<VenueOrder>> GetOrdersAsync(int a, BarRange? w, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<VenueOrder>>([new(OpenOrderId, Mes, VenueSide.Buy, Size: 1, FilledSize: 0, VenueOrderStatus.Open,
                LimitPrice: 6800m, StopPrice: null, FilledPrice: null, CreatedAt: DateTimeOffset.UnixEpoch)]);
        public Task<IReadOnlyList<VenueTrade>> GetTradesAsync(int a, BarRange w, CancellationToken ct) => throw new NotSupportedException();
    }
}
