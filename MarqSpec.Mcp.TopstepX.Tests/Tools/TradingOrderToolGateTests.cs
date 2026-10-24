using System.Diagnostics;
using System.Net.Http;
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

    private readonly FakeKillSwitch _killSwitch = new();
    private readonly CountingGateway _gateway = new();
    private readonly RecordingAuditLog _audit = new();
    private readonly FakeDailyCapCoordinator _daily = new();
    private Exception? _accountsFailure;

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
        TradingExecutionOptions options = new() { Enabled = true };
        TradingOrderTools tools = Tools(telemetry, options);

        using MetricCollector<long> writes = ListenWrites(telemetry);
        List<Activity> spans = [];
        using ActivityListener listener = Listen(spans, telemetry.Activities);

        ToolPayloads.TradingWriteResult first = await Call(tools, operation, "same-key");
        ToolPayloads.TradingWriteResult replay = await Call(Tools(telemetry, options), operation, "same-key");

        first.Status.Should().Be(nameof(TradingAttemptOutcome.Live));
        first.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.NotSafeToRetry);
        first.OrderId.Should().Be(42);
        replay.Should().Be(first);
        CallsFor(operation).Should().Be(1);
        writes.GetMeasurementSnapshot().Should().HaveCount(2);
        writes.GetMeasurementSnapshot()[1].Tags[HostTelemetry.ReplayTag].Should().Be(ExecutionWriteReplay.True);
        writes.GetMeasurementSnapshot()[0].Tags[HostTelemetry.ReplayTag].Should().Be(ExecutionWriteReplay.False);
        spans.Should().HaveCount(2);
        spans[1].GetTagItem(HostTelemetry.ReplayTag).Should().Be(ExecutionWriteReplay.True);
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
        TradingExecutionOptions options = new() { Enabled = true };
        TradingOrderTools tools = Tools(telemetry, options);

        ToolPayloads.TradingWriteResult first = await Call(tools, operation, "unknown-key");
        ToolPayloads.TradingWriteResult replay = await Call(Tools(telemetry, options), operation, "unknown-key");

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
        TradingExecutionOptions options = new() { Enabled = true };
        TradingOrderTools tools = Tools(telemetry, options);

        using MetricCollector<long> writes = ListenWrites(telemetry);
        List<Activity> spans = [];
        using ActivityListener listener = Listen(spans, telemetry.Activities);

        await Call(tools, operation, "conflict-key");
        (int size, long orderId, string contractId) = ConflictOf(operation);
        ToolPayloads.TradingWriteResult conflict = await Call(
            Tools(telemetry, options),
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
    public async Task TradingRiskGate_PlaceOrder_DoesNotReplayADisabledRefusal_AfterTradingIsEnabled()
    {
        using HostTelemetry disabledTelemetry = new();
        TradingOrderTools disabled = Tools(disabledTelemetry, new TradingExecutionOptions());
        ToolPayloads.TradingWriteResult refusal = await Call(disabled, TradingMcpTools.PlaceOrderOperation, "held-key");

        using HostTelemetry enabledTelemetry = new();
        TradingOrderTools enabled = Tools(enabledTelemetry, new TradingExecutionOptions { Enabled = true });
        _gateway.Next = Acknowledged(42);
        ToolPayloads.TradingWriteResult sent = await Call(enabled, TradingMcpTools.PlaceOrderOperation, "held-key");

        refusal.RefusalCode.Should().Be(TradingExecutionRefusal.TradingDisabled.Code);
        refusal.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.SafeToRetry);
        sent.Status.Should().Be(nameof(TradingAttemptOutcome.Live));
        _gateway.PlaceOrderCalls.Should().Be(1);
        _audit.Entries.Should().NotContain(entry =>
            entry.Outcome == TradingAuditOutcomes.Unknown && entry.IdempotencyKey == "held-key"
            && entry.RequestHash == string.Empty);
    }

    [Fact]
    public async Task TradingRiskGate_PlaceOrder_ReplaysFromTheAuditStore_AfterANewGateInstance()
    {
        _gateway.Next = Acknowledged(42);
        using HostTelemetry firstTelemetry = new();
        TradingOrderTools first = Tools(firstTelemetry, new TradingExecutionOptions { Enabled = true });
        ToolPayloads.TradingWriteResult sent = await Call(first, TradingMcpTools.PlaceOrderOperation, "durable-key");

        using HostTelemetry secondTelemetry = new();
        TradingOrderTools restarted = Tools(secondTelemetry, new TradingExecutionOptions { Enabled = true });
        ToolPayloads.TradingWriteResult replay = await Call(
            restarted,
            TradingMcpTools.PlaceOrderOperation,
            "durable-key");

        sent.OrderId.Should().Be(42);
        replay.Should().Be(sent);
        _gateway.PlaceOrderCalls.Should().Be(1);
    }

    [Fact]
    public async Task TradingRiskGate_PlaceOrder_ThrowingSend_ThenTheSameKey_SendsZeroMoreTimes()
    {
        _gateway.PlaceException = new VenueException("the venue dropped the socket");
        using HostTelemetry telemetry = new();
        TradingOrderTools tools = Tools(telemetry, new TradingExecutionOptions { Enabled = true });

        ToolPayloads.TradingWriteResult first = await Call(tools, TradingMcpTools.PlaceOrderOperation, "thrown-key");
        _gateway.PlaceException = null;
        _gateway.Next = Acknowledged(99);
        using HostTelemetry restartedTelemetry = new();
        TradingOrderTools restarted = Tools(restartedTelemetry, new TradingExecutionOptions { Enabled = true });
        ToolPayloads.TradingWriteResult retry = await Call(
            restarted,
            TradingMcpTools.PlaceOrderOperation,
            "thrown-key");

        first.Status.Should().Be(nameof(TradingAttemptOutcome.Unknown));
        first.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.OutcomeUnknown);
        retry.Status.Should().Be(nameof(TradingAttemptOutcome.Unknown));
        retry.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.OutcomeUnknown);
        _gateway.PlaceOrderCalls.Should().Be(1);
    }

    [Fact]
    public async Task TradingRiskGate_PlaceOrder_ThrowingSend_ReturnsStructuredOutcomeUnknown()
    {
        _gateway.PlaceException = new InvalidOperationException("the socket timed out after the send");
        using HostTelemetry telemetry = new();
        TradingOrderTools tools = Tools(telemetry, new TradingExecutionOptions { Enabled = true });

        ToolPayloads.TradingWriteResult result = await tools.SubmitPlace(
            PracticeAccountId,
            MesContract,
            ExecutionOrderType.Market,
            VenueSide.Buy,
            1,
            "send-threw");

        result.Status.Should().Be(nameof(TradingAttemptOutcome.Unknown));
        result.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.OutcomeUnknown);
        result.RefusalCode.Should().BeNull();
        _gateway.PlaceOrderCalls.Should().Be(1);
    }

    [Fact]
    public async Task TradingRiskGate_PlaceOrder_AuditWriteAfterAck_ReturnsStructuredOutcomeUnknown()
    {
        _gateway.Next = Acknowledged(77);
        _audit.ThrowOnLiveWrite = true;
        using HostTelemetry telemetry = new();
        TradingOrderTools tools = Tools(telemetry, new TradingExecutionOptions { Enabled = true });

        ToolPayloads.TradingWriteResult result = await tools.SubmitPlace(
            PracticeAccountId,
            MesContract,
            ExecutionOrderType.Market,
            VenueSide.Buy,
            1,
            "ack-then-audit");

        result.Status.Should().Be(nameof(TradingAttemptOutcome.Unknown));
        result.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.OutcomeUnknown);
        result.OrderId.Should().BeNull();
        _gateway.PlaceOrderCalls.Should().Be(1);
        _audit.Entries.Should().NotContain(entry => entry.Outcome == TradingAuditOutcomes.Live);
    }

    [Fact]
    public async Task TradingRiskGate_PlaceOrder_VenueReadBeforeSend_IsARetryableError()
    {
        _accountsFailure = new VenueException("the account list did not answer");
        using HostTelemetry telemetry = new();
        TradingOrderTools tools = Tools(telemetry, new TradingExecutionOptions { Enabled = true });

        ToolPayloads.TradingWriteResult result = await tools.SubmitPlace(
            PracticeAccountId,
            MesContract,
            ExecutionOrderType.Market,
            VenueSide.Buy,
            1,
            "read-fault");

        result.Status.Should().Be(nameof(TradingAttemptOutcome.Refused));
        result.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.SafeToRetry);
        result.RefusalCode.Should().Be(ExecutionWriteRefusal.Error);
        _audit.Entries.Should().BeEmpty();
        _gateway.PlaceOrderCalls.Should().Be(0);
    }

    [Fact]
    public async Task TradingRiskGate_PlaceOrder_HttpFaultBeforeSend_IsAStructuredRetryableError()
    {
        _accountsFailure = new HttpRequestException("the venue connection failed");
        using HostTelemetry telemetry = new();
        TradingOrderTools tools = Tools(telemetry, new TradingExecutionOptions { Enabled = true });

        ToolPayloads.TradingWriteResult result = await tools.SubmitPlace(
            PracticeAccountId,
            MesContract,
            ExecutionOrderType.Market,
            VenueSide.Buy,
            1,
            "http-fault");

        result.Status.Should().Be(nameof(TradingAttemptOutcome.Refused));
        result.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.SafeToRetry);
        result.RefusalCode.Should().Be(ExecutionWriteRefusal.Error);
        _gateway.PlaceOrderCalls.Should().Be(0);
    }

    [Fact]
    public async Task TradingRiskGate_PlaceOrder_DatabaseFaultBeforeSend_IsAStructuredRetryableError()
    {
        _audit.ThrowBeforeClaim = true;
        using HostTelemetry telemetry = new();
        TradingOrderTools tools = Tools(telemetry, new TradingExecutionOptions { Enabled = true });

        ToolPayloads.TradingWriteResult result = await tools.SubmitPlace(
            PracticeAccountId,
            MesContract,
            ExecutionOrderType.Market,
            VenueSide.Buy,
            1,
            "store-fault");

        result.Status.Should().Be(nameof(TradingAttemptOutcome.Refused));
        result.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.SafeToRetry);
        result.RefusalCode.Should().Be(ExecutionWriteRefusal.Error);
        _gateway.PlaceOrderCalls.Should().Be(0);
    }

    [Fact]
    public async Task TradingRiskGate_PlaceOrder_AllowlistRefusal_ThenAThrowingSend_ReplaysOutcomeUnknown()
    {
        using HostTelemetry blockedTelemetry = new();
        TradingOrderTools blocked = Tools(
            blockedTelemetry,
            new TradingExecutionOptions { Enabled = true, AllowedAccountIds = "999" });

        ToolPayloads.TradingWriteResult refused = await blocked.SubmitPlace(
            PracticeAccountId,
            MesContract,
            ExecutionOrderType.Market,
            VenueSide.Buy,
            1,
            "K");

        refused.RefusalCode.Should().Be(ExecutionWriteRefusal.AccountNotAllowlisted);
        refused.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.SafeToRetry);
        _gateway.PlaceOrderCalls.Should().Be(0);

        _gateway.PlaceException = new InvalidOperationException("the socket timed out after the send");
        using HostTelemetry allowedTelemetry = new();
        TradingOrderTools allowed = Tools(
            allowedTelemetry,
            new TradingExecutionOptions
            {
                Enabled = true,
                AllowedAccountIds = PracticeAccountId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });

        ToolPayloads.TradingWriteResult thrown = await allowed.SubmitPlace(
            PracticeAccountId,
            MesContract,
            ExecutionOrderType.Market,
            VenueSide.Buy,
            1,
            "K");

        thrown.Status.Should().Be(nameof(TradingAttemptOutcome.Unknown));
        thrown.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.OutcomeUnknown);

        _gateway.PlaceException = null;
        _gateway.Next = Acknowledged(99);
        TradingOrderTools replayTools = Tools(allowedTelemetry, new TradingExecutionOptions
        {
            Enabled = true,
            AllowedAccountIds = PracticeAccountId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        ToolPayloads.TradingWriteResult replay = await replayTools.SubmitPlace(
            PracticeAccountId,
            MesContract,
            ExecutionOrderType.Market,
            VenueSide.Buy,
            1,
            "K");

        replay.Status.Should().Be(nameof(TradingAttemptOutcome.Unknown));
        replay.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.OutcomeUnknown);
        replay.RefusalCode.Should().BeNull();
        _gateway.PlaceOrderCalls.Should().Be(1);
    }

    [Fact]
    public async Task TradingRiskGate_PlaceOrder_ConflictRefusalAfterTheClaim_DoesNotHideOutcomeUnknown()
    {
        _gateway.PlaceException = new InvalidOperationException("the send threw");
        using HostTelemetry telemetry = new();
        TradingExecutionOptions options = new() { Enabled = true };
        TradingOrderTools tools = Tools(telemetry, options);

        ToolPayloads.TradingWriteResult thrown = await tools.SubmitPlace(
            PracticeAccountId,
            MesContract,
            ExecutionOrderType.Market,
            VenueSide.Buy,
            1,
            "K");
        ToolPayloads.TradingWriteResult conflict = await Tools(telemetry, options).SubmitPlace(
            PracticeAccountId,
            MesContract,
            ExecutionOrderType.Market,
            VenueSide.Buy,
            2,
            "K");
        _gateway.PlaceException = null;
        ToolPayloads.TradingWriteResult replay = await Tools(telemetry, options).SubmitPlace(
            PracticeAccountId,
            MesContract,
            ExecutionOrderType.Market,
            VenueSide.Buy,
            1,
            "K");

        thrown.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.OutcomeUnknown);
        conflict.RefusalCode.Should().Be(ExecutionWriteRefusal.IdempotencyConflict);
        replay.Status.Should().Be(nameof(TradingAttemptOutcome.Unknown));
        replay.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.OutcomeUnknown);
        replay.RefusalCode.Should().BeNull();
        _gateway.PlaceOrderCalls.Should().Be(1);
    }

    [Fact]
    public async Task TradingRiskGate_PlaceOrder_TimeoutBeforeSend_IsAStructuredRetryableError()
    {
        _accountsFailure = new TimeoutException("the account read timed out");
        using HostTelemetry telemetry = new();
        TradingOrderTools tools = Tools(telemetry, new TradingExecutionOptions { Enabled = true });

        ToolPayloads.TradingWriteResult result = await tools.SubmitPlace(
            PracticeAccountId,
            MesContract,
            ExecutionOrderType.Market,
            VenueSide.Buy,
            1,
            "read-timeout");

        result.Status.Should().Be(nameof(TradingAttemptOutcome.Refused));
        result.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.SafeToRetry);
        result.RefusalCode.Should().Be(ExecutionWriteRefusal.Error);
        _gateway.PlaceOrderCalls.Should().Be(0);
    }

    [Fact]
    public async Task TradingRiskGate_PlaceOrder_TimeoutDuringSend_ReturnsStructuredOutcomeUnknown()
    {
        _gateway.PlaceException = new TimeoutException("the send timed out");
        using HostTelemetry telemetry = new();
        TradingOrderTools tools = Tools(telemetry, new TradingExecutionOptions { Enabled = true });

        ToolPayloads.TradingWriteResult result = await tools.SubmitPlace(
            PracticeAccountId,
            MesContract,
            ExecutionOrderType.Market,
            VenueSide.Buy,
            1,
            "send-timeout");

        result.Status.Should().Be(nameof(TradingAttemptOutcome.Unknown));
        result.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.OutcomeUnknown);
        result.RefusalCode.Should().BeNull();
        _gateway.PlaceOrderCalls.Should().Be(1);
    }

    [Fact]
    public async Task TradingRiskGate_PlaceOrder_UnexpectedExceptionBeforeSend_IsAStructuredRetryableError()
    {
        _accountsFailure = new InvalidOperationException("the account read failed for another reason");
        using HostTelemetry telemetry = new();
        TradingOrderTools tools = Tools(telemetry, new TradingExecutionOptions { Enabled = true });

        ToolPayloads.TradingWriteResult result = await tools.SubmitPlace(
            PracticeAccountId,
            MesContract,
            ExecutionOrderType.Market,
            VenueSide.Buy,
            1,
            "read-other");

        result.Status.Should().Be(nameof(TradingAttemptOutcome.Refused));
        result.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.SafeToRetry);
        result.RefusalCode.Should().Be(ExecutionWriteRefusal.Error);
        _gateway.PlaceOrderCalls.Should().Be(0);
    }

    [Fact]
    public async Task TradingRiskGate_PlaceOrder_NpgsqlExceptionBeforeSend_IsAStructuredRetryableError()
    {
        _accountsFailure = new Npgsql.NpgsqlException("the store connection dropped");
        using HostTelemetry telemetry = new();
        TradingOrderTools tools = Tools(telemetry, new TradingExecutionOptions { Enabled = true });

        ToolPayloads.TradingWriteResult result = await tools.SubmitPlace(
            PracticeAccountId,
            MesContract,
            ExecutionOrderType.Market,
            VenueSide.Buy,
            1,
            "npgsql-read");

        result.Status.Should().Be(nameof(TradingAttemptOutcome.Refused));
        result.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.SafeToRetry);
        result.RefusalCode.Should().Be(ExecutionWriteRefusal.Error);
        _gateway.PlaceOrderCalls.Should().Be(0);
    }

    [Fact]
    public async Task TradingRiskGate_PlaceOrder_ThrowingUnlockAfterAck_ReturnsOutcomeUnknown()
    {
        _gateway.Next = Acknowledged(77);
        _daily.ThrowOnDispose = true;
        using HostTelemetry telemetry = new();
        using MetricCollector<long> faults = ListenFaults(telemetry);
        TradingOrderTools tools = Tools(telemetry, new TradingExecutionOptions { Enabled = true });

        ToolPayloads.TradingWriteResult result = await tools.SubmitPlace(
            PracticeAccountId,
            MesContract,
            ExecutionOrderType.Market,
            VenueSide.Buy,
            1,
            "unlock-after-ack");

        result.Status.Should().Be(nameof(TradingAttemptOutcome.Unknown));
        result.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.OutcomeUnknown);
        result.RefusalCode.Should().BeNull();
        result.OrderId.Should().BeNull();
        _gateway.PlaceOrderCalls.Should().Be(1);
        _audit.Entries.Should().Contain(entry => entry.Outcome == TradingAuditOutcomes.Live);
        faults.LastMeasurement!.Value.Should().Be(1);
    }

    [Fact]
    public async Task TradingRiskGate_PlaceOrder_ThrowingSpanListenerAfterAck_ReturnsOutcomeUnknown()
    {
        _gateway.Next = Acknowledged(77);
        using HostTelemetry telemetry = new();
        using MetricCollector<long> faults = ListenFaults(telemetry);
        using ActivityListener listener = new()
        {
            ShouldListenTo = source => ReferenceEquals(source, telemetry.Activities),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _ => throw new InvalidOperationException("span listener failed"),
        };
        ActivitySource.AddActivityListener(listener);
        TradingOrderTools tools = Tools(telemetry, new TradingExecutionOptions { Enabled = true });

        ToolPayloads.TradingWriteResult result = await tools.SubmitPlace(
            PracticeAccountId,
            MesContract,
            ExecutionOrderType.Market,
            VenueSide.Buy,
            1,
            "span-after-ack");

        result.Status.Should().Be(nameof(TradingAttemptOutcome.Unknown));
        result.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.OutcomeUnknown);
        result.RefusalCode.Should().BeNull();
        result.OrderId.Should().BeNull();
        _gateway.PlaceOrderCalls.Should().Be(1);
        _audit.Entries.Should().Contain(entry => entry.Outcome == TradingAuditOutcomes.Live);
        faults.LastMeasurement!.Value.Should().Be(1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public async Task TradingRiskGate_PlaceOrder_RefusesANonPositiveSize_BeforeAnyAuditRow(int size)
    {
        using HostTelemetry telemetry = new();
        TradingOrderTools tools = Tools(telemetry, new TradingExecutionOptions { Enabled = true });

        ToolPayloads.TradingWriteResult result = await tools.SubmitPlace(
            PracticeAccountId,
            MesContract,
            ExecutionOrderType.Market,
            VenueSide.Buy,
            size,
            "size-key");

        result.RefusalCode.Should().Be(ExecutionWriteRefusal.SizeNotPositive);
        result.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.SafeToRetry);
        _audit.Entries.Should().BeEmpty();
        _gateway.PlaceOrderCalls.Should().Be(0);
    }

    [Fact]
    public async Task TradingRiskGate_PlaceOrder_RefusesALimitMissingItsPrice_BeforeAnyAuditRow()
    {
        using HostTelemetry telemetry = new();
        TradingOrderTools tools = Tools(telemetry, new TradingExecutionOptions { Enabled = true });

        ToolPayloads.TradingWriteResult result = await tools.SubmitPlace(
            PracticeAccountId,
            MesContract,
            ExecutionOrderType.Limit,
            VenueSide.Buy,
            1,
            "price-key");

        result.RefusalCode.Should().Be(ExecutionWriteRefusal.PriceRequired);
        _audit.Entries.Should().BeEmpty();
        _gateway.PlaceOrderCalls.Should().Be(0);
    }

    [Fact]
    public async Task TradingRiskGate_PlaceOrder_RefusesANonPositivePrice_BeforeAnyAuditRow()
    {
        using HostTelemetry telemetry = new();
        TradingOrderTools tools = Tools(telemetry, new TradingExecutionOptions { Enabled = true });

        ToolPayloads.TradingWriteResult result = await tools.SubmitPlace(
            PracticeAccountId,
            MesContract,
            ExecutionOrderType.Market,
            VenueSide.Buy,
            1,
            "neg-price-key",
            limitPrice: -1m);

        result.RefusalCode.Should().Be(ExecutionWriteRefusal.PriceNotPositive);
        _audit.Entries.Should().BeEmpty();
        _gateway.PlaceOrderCalls.Should().Be(0);
    }

    [Fact]
    public async Task TradingRiskGate_PlaceOrder_RefusesAStopMissingItsPrice_BeforeAnyAuditRow()
    {
        using HostTelemetry telemetry = new();
        TradingOrderTools tools = Tools(telemetry, new TradingExecutionOptions { Enabled = true });

        ToolPayloads.TradingWriteResult result = await tools.SubmitPlace(
            PracticeAccountId,
            MesContract,
            ExecutionOrderType.Stop,
            VenueSide.Sell,
            1,
            "stop-key");

        result.RefusalCode.Should().Be(ExecutionWriteRefusal.PriceRequired);
        _audit.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task TradingRiskGate_ModifyOrder_RefusesANonPositiveSize_AndRunsTheCapWhenSizeIsSet()
    {
        _daily.ContractsToday = 10;
        using HostTelemetry telemetry = new();
        TradingOrderTools tools = Tools(
            telemetry,
            new TradingExecutionOptions { Enabled = true, MaxContractsPerDay = 10 });

        ToolPayloads.TradingWriteResult negative = await tools.SubmitModify(
            PracticeAccountId,
            OpenOrderId,
            "mod-neg",
            size: -3);

        negative.RefusalCode.Should().Be(ExecutionWriteRefusal.SizeNotPositive);
        _audit.Entries.Should().BeEmpty();
        _gateway.ModifyOrderCalls.Should().Be(0);

        TradingOrderTools capped = Tools(
            telemetry,
            new TradingExecutionOptions { Enabled = true, MaxContractsPerDay = 10 });
        ToolPayloads.TradingWriteResult overCap = await capped.SubmitModify(
            PracticeAccountId,
            OpenOrderId,
            "mod-cap",
            size: 1);

        overCap.RefusalCode.Should().Be(ExecutionWriteRefusal.DailyContractLimitExceeded);
        _gateway.ModifyOrderCalls.Should().Be(0);
        _audit.Entries.Should().NotContain(entry => entry.ContractCount < 0);
    }

    [Fact]
    public async Task TradingRiskGate_PartialClose_RefusesWhenTheSizeExceedsTheOpenPosition()
    {
        using HostTelemetry telemetry = new();
        TradingOrderTools tools = Tools(telemetry, new TradingExecutionOptions { Enabled = true });

        ToolPayloads.TradingWriteResult result = await tools.SubmitPartialClose(
            PracticeAccountId,
            MesContract,
            size: 4,
            idempotencyKey: "too-big");

        result.RefusalCode.Should().Be(ExecutionWriteRefusal.PartialCloseExceedsPosition);
        _audit.Entries.Should().BeEmpty();
        _gateway.PartialClosePositionCalls.Should().Be(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task TradingRiskGate_PartialClose_RefusesANonPositiveSize_AndDoesNotLowerTheDailyCap(int size)
    {
        _daily.ContractsToday = 4;
        using HostTelemetry telemetry = new();
        TradingOrderTools tools = Tools(telemetry, new TradingExecutionOptions { Enabled = true });

        ToolPayloads.TradingWriteResult result = await tools.SubmitPartialClose(
            PracticeAccountId,
            MesContract,
            size,
            "flat-size");

        result.RefusalCode.Should().Be(ExecutionWriteRefusal.SizeNotPositive);
        result.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.SafeToRetry);
        _audit.Entries.Should().BeEmpty();
        _audit.Entries.Should().NotContain(entry => entry.ContractCount < 0);
        _gateway.PartialClosePositionCalls.Should().Be(0);
        _daily.LockAcquisitions.Should().Be(0);
        _daily.ContractsToday.Should().Be(4);
    }

    [Fact]
    public async Task TradingRiskGate_PlaceOrder_RefusesAnOverlongIdempotencyKey_BeforeAnyAuditRow()
    {
        using HostTelemetry telemetry = new();
        TradingOrderTools tools = Tools(telemetry, new TradingExecutionOptions { Enabled = true });
        string key = new('k', TradingAuditClaims.MaxKeyLength + 1);

        ToolPayloads.TradingWriteResult result = await tools.SubmitPlace(
            PracticeAccountId,
            MesContract,
            ExecutionOrderType.Market,
            VenueSide.Buy,
            1,
            key);

        result.RefusalCode.Should().Be(ExecutionWriteRefusal.IdempotencyKeyTooLong);
        result.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.SafeToRetry);
        _audit.Entries.Should().BeEmpty();
        _gateway.PlaceOrderCalls.Should().Be(0);
    }

    [Fact]
    public void OrderToolDescriptions_WarnThatTheStakesAreReal_AndNameTheRealReadTools()
    {
        foreach (MethodInfo method in OrderMethods())
        {
            string description = method.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()!.Description;
            description.Should().Contain("the stakes are real");
            description.Should().Contain("Non-practice accounts are refused.");
            description.Should().Contain("Never retry OutcomeUnknown");
            description.Should().Contain("get_orders");
            description.Should().Contain("get_positions");
            description.Should().Contain("get_trades");
            description.Should().NotContain("list_open_orders");
            description.Should().NotContain("list_positions");
        }
    }

    [Fact]
    public void EveryToolNamedInADescription_IsARegisteredTool()
    {
        List<MethodInfo> methods = [];
        foreach (Type type in typeof(TradingOrderTools).Assembly.GetTypes().Where(type => type.IsClass))
        {
            methods.AddRange(type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null));
        }

        HashSet<string> registered = [];
        List<string> descriptions = [];
        foreach (MethodInfo method in methods)
        {
            McpServerTool tool = McpServerTool.Create(
                method,
                _ => throw new InvalidOperationException("description scan does not invoke the tool"),
                new McpServerToolCreateOptions());
            registered.Add(tool.ProtocolTool.Name);
            if (tool.ProtocolTool.Description is { Length: > 0 } description)
            {
                descriptions.Add(description);
            }

            foreach (ParameterInfo parameter in method.GetParameters())
            {
                string? parameterDescription = parameter
                    .GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description;
                if (parameterDescription is { Length: > 0 })
                {
                    descriptions.Add(parameterDescription);
                }
            }
        }

        System.Text.RegularExpressions.Regex names = new(
            @"\b[a-z][a-z0-9]*(?:_[a-z0-9]+)+\b",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        foreach (string description in descriptions)
        {
            foreach (System.Text.RegularExpressions.Match match in names.Matches(description))
            {
                registered.Should().Contain(
                    match.Value,
                    because: "a description names " + match.Value + " and that tool must be registered");
            }
        }
    }

    [Fact]
    public async Task TradingRiskGate_PlaceOrder_IncludesDryRunInTheRequestHash()
    {
        using HostTelemetry telemetry = new();
        TradingOrderTools tools = Tools(telemetry, new TradingExecutionOptions { Enabled = true });
        ExecutionPlaceOrderCommand command = new(
            PracticeAccountId,
            MesContract,
            ExecutionOrderType.Limit,
            VenueSide.Buy,
            1,
            6800m,
            null,
            null);

        await tools.SubmitPlace(
            command.AccountId,
            command.ContractId,
            command.OrderType,
            command.Side,
            command.Size,
            "hash-key",
            command.LimitPrice,
            dryRun: true);

        _audit.Entries.Should().Contain(entry =>
            entry.Outcome == TradingAuditOutcomes.Preview
            && entry.RequestHash == TradingRequestHasher.HashPlace(command, dryRun: true));
        _audit.Entries.Should().NotContain(entry =>
            entry.RequestHash == TradingRequestHasher.HashPlace(command, dryRun: false));
        _gateway.PlaceOrderCalls.Should().Be(0);
    }

    [Fact]
    public async Task TradingRiskGate_PlaceOrder_SameKeyOnAnotherAccount_IsADifferentClaim()
    {
        _gateway.Next = Acknowledged(42);
        using HostTelemetry telemetry = new();
        TradingRiskGate gate = Gate(telemetry, new TradingExecutionOptions { Enabled = true });
        ExecutionPlaceOrderCommand first = new(
            PracticeAccountId,
            MesContract,
            ExecutionOrderType.Market,
            VenueSide.Buy,
            1,
            null,
            null,
            null);
        ExecutionPlaceOrderCommand second = first with { AccountId = 101 };

        await gate.SubmitPlace(first, new TradingRiskAttemptContext(false, "shared-key"), CancellationToken.None);
        TradingRiskGateResult otherAccount = await Gate(telemetry, new TradingExecutionOptions { Enabled = true })
            .SubmitPlace(
            second,
            new TradingRiskAttemptContext(false, "shared-key"),
            CancellationToken.None);

        otherAccount.Outcome.Should().Be(TradingAttemptOutcome.Live);
        _gateway.PlaceOrderCalls.Should().Be(2);
    }

    [Fact]
    public async Task TradingRiskGate_SecondOperationOnTheSameInstance_ThrowsBeforeAnyVendorCall()
    {
        using HostTelemetry telemetry = new();
        TradingRiskGate gate = Gate(telemetry, new TradingExecutionOptions());
        ExecutionPlaceOrderCommand command = new(
            PracticeAccountId,
            MesContract,
            ExecutionOrderType.Market,
            VenueSide.Buy,
            1,
            null,
            null,
            null);

        TradingRiskGateResult first = await gate.SubmitPlace(
            command,
            new TradingRiskAttemptContext(false, "first-key"),
            CancellationToken.None);

        first.Outcome.Should().Be(TradingAttemptOutcome.Refused);
        _gateway.PlaceOrderCalls.Should().Be(0);

        Func<Task> second = () => gate.SubmitModify(
            new ExecutionModifyOrderCommand(PracticeAccountId, OpenOrderId, Size: 1, null, null),
            new TradingRiskAttemptContext(false, "second-key"),
            CancellationToken.None);

        await second.Should().ThrowAsync<InvalidOperationException>();
        _gateway.PlaceOrderCalls.Should().Be(0);
        _gateway.ModifyOrderCalls.Should().Be(0);
        _gateway.CancelOrderCalls.Should().Be(0);
        _gateway.ClosePositionCalls.Should().Be(0);
        _gateway.PartialClosePositionCalls.Should().Be(0);
    }

    [Fact]
    public async Task TradingOrderTools_UnexpectedException_IsOutcomeUnknown_NeverSafeToRetry()
    {
        ToolPayloads.TradingWriteResult result = await TradingOrderTools.RunAsync(
            Task.FromException<TradingRiskGateResult>(new InvalidOperationException("unclassified")));

        result.Status.Should().Be(nameof(TradingAttemptOutcome.Unknown));
        result.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.OutcomeUnknown);
        result.RefusalCode.Should().Be(ExecutionWriteRefusal.Error);
        result.OrderId.Should().BeNull();
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
            TradingOptionsMonitors.Fixed(options),
            _killSwitch,
            stageResolver);
        InstrumentRegistry registry = new(Options.Create(new MarketDataOptions { Instruments = "MES" }));
        TradingContractDenylist denylist = new(Options.Create(options), registry);
        IReadOnlyList<VenueAccount> accounts =
        [
            new(PracticeAccountId, AccountStage.Practice, CanTrade: true, IsVisible: true, Balance: 50_000m),
            new(101, AccountStage.Practice, CanTrade: true, IsVisible: true, Balance: 50_000m),
        ];
        IReadOnlyList<VenueOrder> orders = [OpenOrder()];

        return new TradingRiskGate(
            executionGate,
            _gateway,
            new StubMarketDataGateway(accounts, orders, () => _accountsFailure),
            stageResolver,
            new RecordingUnknownSignal(),
            _daily,
            _audit,
            denylist,
            new TradingWriteVenueAccountCache(),
            telemetry);
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

    private static MetricCollector<long> ListenFaults(HostTelemetry telemetry) =>
        new(telemetry, HostTelemetry.Name, HostTelemetry.BookkeepingFaultsInstrument);

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

        public Exception? PlaceException { get; set; }

        public Task<ExecutionWriteResult> PlaceOrder(
            ExecutionPlaceOrderCommand command,
            CancellationToken cancellationToken)
        {
            PlaceOrderCalls++;
            if (PlaceException is not null)
            {
                throw PlaceException;
            }

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

        public bool ThrowOnLiveWrite { get; set; }

        public bool ThrowBeforeClaim { get; set; }

        public Task WriteAsync(TradingAuditWriteRequest request, CancellationToken cancellationToken)
        {
            if (ThrowOnLiveWrite && request.Outcome == TradingAuditOutcomes.Live)
            {
                throw new Microsoft.EntityFrameworkCore.DbUpdateException("the audit write failed after the ack");
            }

            Entries.Add(request);
            return Task.CompletedTask;
        }

        public Task<TradingAuditClaimResult> TryClaimUnknownAsync(
            TradingAuditWriteRequest request,
            CancellationToken cancellationToken)
        {
            if (ThrowBeforeClaim)
            {
                throw new Microsoft.EntityFrameworkCore.DbUpdateException("the store rejected the claim");
            }

            _ = cancellationToken;
            return Task.FromResult(TradingAuditClaimMemory.TryClaim(Entries, request));
        }
    }

    private sealed class FakeDailyCapCoordinator : ITradingDailyCapCoordinator
    {
        public int LockAcquisitions { get; private set; }

        public int ContractsToday { get; set; }

        public bool ThrowOnDispose { get; set; }

        public Task<IAsyncDisposable> AcquireAccountDayLockAsync(int accountId, CancellationToken cancellationToken)
        {
            LockAcquisitions++;
            return Task.FromResult<IAsyncDisposable>(new NoopLock(ThrowOnDispose));
        }

        public Task<int> GetConservativeContractTotalTodayUtcAsync(
            int accountId,
            CancellationToken cancellationToken) =>
            Task.FromResult(ContractsToday);

        private sealed class NoopLock(bool throwOnDispose) : IAsyncDisposable
        {
            public ValueTask DisposeAsync() =>
                throwOnDispose
                    ? throw new InvalidOperationException("advisory unlock failed")
                    : ValueTask.CompletedTask;
        }
    }

    private sealed class StubMarketDataGateway(
        IReadOnlyList<VenueAccount> accounts,
        IReadOnlyList<VenueOrder> orders,
        Func<Exception?>? accountsFailure = null) : IMarketDataGateway
    {
        public string VenueId => "stub";

        public Task<IReadOnlyList<VenueAccount>> GetAccountsAsync(
            bool onlyActive,
            CancellationToken cancellationToken)
        {
            _ = onlyActive;
            _ = cancellationToken;
            if (accountsFailure?.Invoke() is Exception failure)
            {
                throw failure;
            }

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
            CancellationToken cancellationToken)
        {
            _ = accountId;
            _ = cancellationToken;
            IReadOnlyList<VenuePosition> positions =
            [
                new(MesContract, SignedSize: 3, AveragePrice: 6800m, OpenedAt: DateTimeOffset.UnixEpoch),
            ];
            return Task.FromResult(positions);
        }

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
