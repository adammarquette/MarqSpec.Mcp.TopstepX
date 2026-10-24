using System.ComponentModel;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Venue;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MarqSpec.Mcp.TopstepX.Tools;

/// <summary>
/// Venue write tools. Every call goes through <see cref="TradingRiskGate"/> and nowhere else (ADR-0024).
/// </summary>
/// <remarks>
/// Method names are <c>Submit*</c> on purpose. The venue order identifiers are confined to the execution
/// seam; these tools name the gate's entry points and publish <c>place_order</c> and its siblings via
/// <see cref="McpServerToolAttribute.Name"/>. <c>Trading__Enabled</c> defaults to false and refuses before
/// any vendor send. The kill switch blocks cancel and close on the same path. There is no carve-out.
/// </remarks>
[McpServerToolType]
public sealed class TradingOrderTools(TradingRiskGate gate)
{
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
        "Places an order only through the trading risk gate. Trading__Enabled defaults to false and refuses "
        + "this call before any vendor send, and the kill switch blocks it the same way. idempotencyKey is "
        + "required; a replay of that key returns the original result and does not send again. status is "
        + "Preview, Refused, Live, Failed, or Unknown. Live means the broker acknowledged the order. "
        + "retrySafety is SafeToRetry, NotSafeToRetry, or OutcomeUnknown. refusalCode, resultCode and "
        + "orderId are omitted when this call has nothing to report for them.")]
    public Task<ToolPayloads.TradingWriteResult> SubmitPlace(
        [Description("The venue account id, from list_accounts.")] int accountId,
        [Description("The venue contract id, e.g. CON.F.US.MES.Z26.")] string contractId,
        [Description("Order type: Limit, Market, Stop, StopLimit, TrailingStop, JoinBid, JoinAsk.")]
        ExecutionOrderType orderType,
        [Description("Buy or Sell. Unknown is refused.")] VenueSide side,
        [Description("Contract size, a positive number of contracts.")] int size,
        [Description("Caller-supplied idempotency key. A replay returns the original result and does not send again.")]
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
        "Modifies an open order only through the trading risk gate. Trading__Enabled defaults to false and "
        + "refuses this call before any vendor send, and the kill switch blocks it the same way. "
        + "idempotencyKey is required; a replay of that key returns the original result and does not send "
        + "again. status is Preview, Refused, Live, Failed, or Unknown. retrySafety is SafeToRetry, "
        + "NotSafeToRetry, or OutcomeUnknown. refusalCode, resultCode and orderId are omitted when this "
        + "call has nothing to report for them.")]
    public Task<ToolPayloads.TradingWriteResult> SubmitModify(
        [Description("The venue account id.")] int accountId,
        [Description("The order id to modify.")] long orderId,
        [Description("Caller-supplied idempotency key. A replay returns the original result and does not send again.")]
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
        "Cancels an order only through the trading risk gate. Trading__Enabled defaults to false and refuses "
        + "this call before any vendor send. The kill switch blocks cancel too; there is no carve-out. "
        + "idempotencyKey is required; a replay of that key returns the original result and does not send "
        + "again. status is Preview, Refused, Live, Failed, or Unknown. retrySafety is SafeToRetry, "
        + "NotSafeToRetry, or OutcomeUnknown. refusalCode, resultCode and orderId are omitted when this "
        + "call has nothing to report for them.")]
    public Task<ToolPayloads.TradingWriteResult> SubmitCancel(
        [Description("The venue account id.")] int accountId,
        [Description("The order id to cancel.")] long orderId,
        [Description("Caller-supplied idempotency key. A replay returns the original result and does not send again.")]
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
        "Flattens a position only through the trading risk gate. Trading__Enabled defaults to false and "
        + "refuses this call before any vendor send. The kill switch blocks close too; there is no carve-out. "
        + "idempotencyKey is required; a replay of that key returns the original result and does not send "
        + "again. status is Preview, Refused, Live, Failed, or Unknown. retrySafety is SafeToRetry, "
        + "NotSafeToRetry, or OutcomeUnknown. refusalCode, resultCode and orderId are omitted when this "
        + "call has nothing to report for them.")]
    public Task<ToolPayloads.TradingWriteResult> SubmitClose(
        [Description("The venue account id.")] int accountId,
        [Description("The contract id to flatten, e.g. CON.F.US.MES.Z26.")] string contractId,
        [Description("Caller-supplied idempotency key. A replay returns the original result and does not send again.")]
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
        "Reduces a position only through the trading risk gate. Trading__Enabled defaults to false and "
        + "refuses this call before any vendor send. The kill switch blocks a partial close too; there is "
        + "no carve-out. idempotencyKey is required; a replay of that key returns the original result and "
        + "does not send again. status is Preview, Refused, Live, Failed, or Unknown. retrySafety is "
        + "SafeToRetry, NotSafeToRetry, or OutcomeUnknown. refusalCode, resultCode and orderId are omitted "
        + "when this call has nothing to report for them.")]
    public Task<ToolPayloads.TradingWriteResult> SubmitPartialClose(
        [Description("The venue account id.")] int accountId,
        [Description("The contract id, e.g. CON.F.US.MES.Z26.")] string contractId,
        [Description("Contracts to close, a positive number.")] int size,
        [Description("Caller-supplied idempotency key. A replay returns the original result and does not send again.")]
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
        catch (VenueException ex)
        {
            throw new McpException("The venue could not answer: " + ex.Message);
        }
    }
}
