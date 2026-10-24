using System.ComponentModel;
using System.Net.Http;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Telemetry;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using Npgsql;

namespace MarqSpec.Mcp.TopstepX.Tools;

/// <summary>
/// Venue write tools. Every call goes through <see cref="TradingRiskGate"/> and nowhere else (ADR-0024).
/// </summary>
/// <remarks>
/// Method names are <c>Submit*</c> on purpose. The venue order identifiers are confined to the execution
/// seam; these tools name the gate's entry points and publish <c>place_order</c> and its siblings via
/// <see cref="McpServerToolAttribute.Name"/>. There is no <see cref="McpServerToolTypeAttribute"/>: the
/// composition root registers this type only when trading is enabled, so a disabled server does not list
/// the tools. Each method still refuses when trading is disabled, which is the second layer if a call
/// arrives anyway. The kill switch blocks cancel and close on the same path. There is no carve-out.
/// A fault from a read before the send is a retryable error. <see cref="TradingAttemptOutcome.Unknown"/>
/// is returned only when the send may already have happened.
/// </remarks>
public sealed class TradingOrderTools(TradingRiskGate gate)
{
    private const string Stakes =
        "This writes on a live practice account and the stakes are real. Non-practice accounts are refused. "
        + "Use a fresh idempotencyKey for each order; the key is scoped to this account and this tool, and "
        + "the longest key is 256 characters. Never retry OutcomeUnknown; reconcile via get_orders, "
        + "get_positions and get_trades. ";

    private readonly TradingRiskGate _gate = gate;

    /// <summary>Places an order when the trading gate permits.</summary>
    /// <param name="accountId">The venue account id.</param>
    /// <param name="contractId">The venue contract id.</param>
    /// <param name="orderType">The order type.</param>
    /// <param name="side">Buy or sell.</param>
    /// <param name="size">Contract size.</param>
    /// <param name="idempotencyKey">Caller-supplied key. A replay returns the original result.</param>
    /// <param name="limitPrice">Limit price, when the type uses one.</param>
    /// <param name="stopPrice">Stop price, when the type uses one.</param>
    /// <param name="trailPrice">Trail price, when the type uses one.</param>
    /// <param name="dryRun">When true, preview only.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The gate result, including the retry-safety signal.</returns>
    [McpServerTool(
        Name = TradingMcpTools.PlaceOrderOperation,
        ReadOnly = false,
        Idempotent = false,
        Destructive = true,
        Title = "Place order")]
    [Description(
        Stakes
        + "Places an order only through the trading risk gate. Trading__Enabled defaults to false and refuses "
        + "this call before any vendor send, and the kill switch blocks it the same way. idempotencyKey is "
        + "required; a replay of that key returns the stored transmit and does not send again. status is "
        + "Preview, Refused, Live, Failed, or Unknown. Live means the broker acknowledged the order. "
        + "retrySafety is SafeToRetry, NotSafeToRetry, or OutcomeUnknown. refusalCode, resultCode and "
        + "orderId are omitted when this call has nothing to report for them.")]
    public Task<ToolPayloads.TradingWriteResult> SubmitPlace(
        [Description("The venue account id from list_accounts. Practice accounts only; a non-practice account is refused.")] int accountId,
        [Description("The venue contract id, e.g. CON.F.US.MES.Z26.")] string contractId,
        [Description("Order type: Limit, Market, Stop, StopLimit, TrailingStop, JoinBid, JoinAsk.")]
        ExecutionOrderType orderType,
        [Description("Buy or Sell. Unknown is refused.")] VenueSide side,
        [Description("Contract size, a positive number of contracts.")] int size,
        [Description("Caller-supplied idempotency key, at most 256 characters, scoped to this account and place_order. Use a fresh key per order.")]
        string idempotencyKey,
        [Description("Limit price when the order type uses one. Omit otherwise.")] decimal? limitPrice = null,
        [Description("Stop price when the order type uses one. Omit otherwise.")] decimal? stopPrice = null,
        [Description("Trail price when the order type uses one. Omit otherwise.")] decimal? trailPrice = null,
        [Description("When true, run the same gates and return a preview. Omit for false.")] bool dryRun = false,
        CancellationToken cancellationToken = default) =>
        RunAsync(
            _gate.SubmitPlace(
                new ExecutionPlaceOrderCommand(
                    accountId,
                    contractId,
                    orderType,
                    side,
                    size,
                    limitPrice,
                    stopPrice,
                    trailPrice),
                Attempt(dryRun, idempotencyKey),
                cancellationToken));

    /// <summary>Modifies an open order when the trading gate permits.</summary>
    /// <param name="accountId">The venue account id.</param>
    /// <param name="orderId">The order id.</param>
    /// <param name="idempotencyKey">Caller-supplied key. A replay returns the original result.</param>
    /// <param name="size">New size, when changing it.</param>
    /// <param name="limitPrice">New limit price, when changing it.</param>
    /// <param name="stopPrice">New stop price, when changing it.</param>
    /// <param name="dryRun">When true, preview only.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The gate result, including the retry-safety signal.</returns>
    [McpServerTool(
        Name = TradingMcpTools.ModifyOrderOperation,
        ReadOnly = false,
        Idempotent = false,
        Destructive = true,
        Title = "Modify order")]
    [Description(
        Stakes
        + "Modifies an open order only through the trading risk gate. Trading__Enabled defaults to false and "
        + "refuses this call before any vendor send, and the kill switch blocks it the same way. "
        + "idempotencyKey is required; a replay of that key returns the stored transmit and does not send "
        + "again. A supplied size is checked against the per-order and daily caps, and the full new size is "
        + "what the daily cap counts. status is Preview, "
        + "Refused, Live, Failed, or Unknown. retrySafety is SafeToRetry, NotSafeToRetry, or OutcomeUnknown. "
        + "refusalCode, resultCode and orderId are omitted when this call has nothing to report for them.")]
    public Task<ToolPayloads.TradingWriteResult> SubmitModify(
        [Description("The venue account id from list_accounts. Practice accounts only; a non-practice account is refused.")] int accountId,
        [Description("The order id to modify.")] long orderId,
        [Description("Caller-supplied idempotency key, at most 256 characters, scoped to this account and modify_order. Use a fresh key per order.")]
        string idempotencyKey,
        [Description("New size. Omit to leave the size unchanged.")] int? size = null,
        [Description("New limit price. Omit to leave it unchanged.")] decimal? limitPrice = null,
        [Description("New stop price. Omit to leave it unchanged.")] decimal? stopPrice = null,
        [Description("When true, run the same gates and return a preview. Omit for false.")] bool dryRun = false,
        CancellationToken cancellationToken = default) =>
        RunAsync(
            _gate.SubmitModify(
                new ExecutionModifyOrderCommand(accountId, orderId, size, limitPrice, stopPrice),
                Attempt(dryRun, idempotencyKey),
                cancellationToken));

    /// <summary>Cancels an order when the trading gate permits.</summary>
    /// <param name="accountId">The venue account id.</param>
    /// <param name="orderId">The order id.</param>
    /// <param name="idempotencyKey">Caller-supplied key. A replay returns the original result.</param>
    /// <param name="dryRun">When true, preview only.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The gate result, including the retry-safety signal.</returns>
    [McpServerTool(
        Name = TradingMcpTools.CancelOrderOperation,
        ReadOnly = false,
        Idempotent = false,
        Destructive = true,
        Title = "Cancel order")]
    [Description(
        Stakes
        + "Cancels an order only through the trading risk gate. Trading__Enabled defaults to false and refuses "
        + "this call before any vendor send. The kill switch blocks cancel too; there is no carve-out. "
        + "idempotencyKey is required; a replay of that key returns the stored transmit and does not send "
        + "again. status is Preview, Refused, Live, Failed, or Unknown. retrySafety is SafeToRetry, "
        + "NotSafeToRetry, or OutcomeUnknown. refusalCode, resultCode and orderId are omitted when this "
        + "call has nothing to report for them.")]
    public Task<ToolPayloads.TradingWriteResult> SubmitCancel(
        [Description("The venue account id from list_accounts. Practice accounts only; a non-practice account is refused.")] int accountId,
        [Description("The order id to cancel.")] long orderId,
        [Description("Caller-supplied idempotency key, at most 256 characters, scoped to this account and cancel_order. Use a fresh key per order.")]
        string idempotencyKey,
        [Description("When true, run the same gates and return a preview. Omit for false.")] bool dryRun = false,
        CancellationToken cancellationToken = default) =>
        RunAsync(
            _gate.SubmitCancel(
                new ExecutionCancelOrderCommand(accountId, orderId),
                Attempt(dryRun, idempotencyKey),
                cancellationToken));

    /// <summary>Flattens a position when the trading gate permits.</summary>
    /// <param name="accountId">The venue account id.</param>
    /// <param name="contractId">The contract to flatten.</param>
    /// <param name="idempotencyKey">Caller-supplied key. A replay returns the original result.</param>
    /// <param name="dryRun">When true, preview only.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The gate result, including the retry-safety signal.</returns>
    [McpServerTool(
        Name = TradingMcpTools.ClosePositionOperation,
        ReadOnly = false,
        Idempotent = false,
        Destructive = true,
        Title = "Close position")]
    [Description(
        Stakes
        + "Flattens a position only through the trading risk gate. Trading__Enabled defaults to false and "
        + "refuses this call before any vendor send. The kill switch blocks close too; there is no carve-out. "
        + "idempotencyKey is required; a replay of that key returns the stored transmit and does not send "
        + "again. status is Preview, Refused, Live, Failed, or Unknown. retrySafety is SafeToRetry, "
        + "NotSafeToRetry, or OutcomeUnknown. refusalCode, resultCode and orderId are omitted when this "
        + "call has nothing to report for them.")]
    public Task<ToolPayloads.TradingWriteResult> SubmitClose(
        [Description("The venue account id from list_accounts. Practice accounts only; a non-practice account is refused.")] int accountId,
        [Description("The contract id to flatten, e.g. CON.F.US.MES.Z26.")] string contractId,
        [Description("Caller-supplied idempotency key, at most 256 characters, scoped to this account and close_position. Use a fresh key per order.")]
        string idempotencyKey,
        [Description("When true, run the same gates and return a preview. Omit for false.")] bool dryRun = false,
        CancellationToken cancellationToken = default) =>
        RunAsync(
            _gate.SubmitClose(
                new ExecutionClosePositionCommand(accountId, contractId),
                Attempt(dryRun, idempotencyKey),
                cancellationToken));

    /// <summary>Partially closes a position when the trading gate permits.</summary>
    /// <param name="accountId">The venue account id.</param>
    /// <param name="contractId">The contract to reduce.</param>
    /// <param name="size">Contracts to close.</param>
    /// <param name="idempotencyKey">Caller-supplied key. A replay returns the original result.</param>
    /// <param name="dryRun">When true, preview only.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The gate result, including the retry-safety signal.</returns>
    [McpServerTool(
        Name = TradingMcpTools.PartialClosePositionOperation,
        ReadOnly = false,
        Idempotent = false,
        Destructive = true,
        Title = "Partial close position")]
    [Description(
        Stakes
        + "Reduces a position only through the trading risk gate. Trading__Enabled defaults to false and "
        + "refuses this call before any vendor send. The kill switch blocks a partial close too; there is "
        + "no carve-out. The size is checked against the open position before any audit row. idempotencyKey "
        + "is required; a replay of that key returns the stored transmit and does not send again. status is "
        + "Preview, Refused, Live, Failed, or Unknown. retrySafety is SafeToRetry, NotSafeToRetry, or "
        + "OutcomeUnknown. refusalCode, resultCode and orderId are omitted when this call has nothing to "
        + "report for them.")]
    public Task<ToolPayloads.TradingWriteResult> SubmitPartialClose(
        [Description("The venue account id from list_accounts. Practice accounts only; a non-practice account is refused.")] int accountId,
        [Description("The contract id, e.g. CON.F.US.MES.Z26.")] string contractId,
        [Description("Contracts to close, a positive number no larger than the open position.")] int size,
        [Description("Caller-supplied idempotency key, at most 256 characters, scoped to this account and partial_close_position. Use a fresh key per order.")]
        string idempotencyKey,
        [Description("When true, run the same gates and return a preview. Omit for false.")] bool dryRun = false,
        CancellationToken cancellationToken = default) =>
        RunAsync(
            _gate.SubmitPartialClose(
                new ExecutionPartialClosePositionCommand(accountId, contractId, size),
                Attempt(dryRun, idempotencyKey),
                cancellationToken));

    private static TradingRiskAttemptContext Attempt(bool dryRun, string idempotencyKey) =>
        new(dryRun, idempotencyKey, RequireIdempotencyKey: true);

    private static async Task<ToolPayloads.TradingWriteResult> RunAsync(Task<TradingRiskGateResult> attempt)
    {
        try
        {
            TradingRiskGateResult result = await attempt.ConfigureAwait(false);
            return ToolPayloads.FromGate(result);
        }
        catch (VenueException)
        {
            return BeforeSendFault();
        }
        catch (HttpRequestException)
        {
            return BeforeSendFault();
        }
        catch (TimeoutException)
        {
            return BeforeSendFault();
        }
        catch (DbUpdateException)
        {
            return BeforeSendFault();
        }
        catch (NpgsqlException)
        {
            return BeforeSendFault();
        }
        catch (Exception)
        {
            // Anything that escaped the gate was a read or a store fault before the send. A throw from the
            // send, including a timeout, is caught there and returned as OutcomeUnknown.
            return BeforeSendFault();
        }
    }

    /// <summary>
    /// A read or a store fault that reached the tool happened before the send. The venue was not touched
    /// by this failure, so the caller may retry. Database and HTTP errors stay inside this result.
    /// </summary>
    private static ToolPayloads.TradingWriteResult BeforeSendFault() =>
        new(
            nameof(TradingAttemptOutcome.Refused),
            ToolPayloads.TradingRetrySafety.SafeToRetry,
            AccountStage.Unknown,
            ExecutionWriteRefusal.Error,
            ResultCode: null,
            OrderId: null);
}
