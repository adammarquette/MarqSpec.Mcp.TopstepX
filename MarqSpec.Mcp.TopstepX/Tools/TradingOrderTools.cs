using System.ComponentModel;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Venue;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MarqSpec.Mcp.TopstepX.Tools;

/// <summary>
/// Venue write tools — every call goes through <see cref="TradingRiskGate"/> only (ADR-0024, gh#711).
/// </summary>
/// <remarks>
/// When <c>Trading__Enabled</c> is false (the default), the gate refuses before audit, caps, or the execution
/// gateway run. MCP tools do not bypass that check.
/// </remarks>
[McpServerToolType]
public sealed class TradingOrderTools(TradingRiskGate gate)
{
    private readonly TradingRiskGate _gate = gate;

    /// <summary>Places an order when the trading gate permits.</summary>
    [McpServerTool(ReadOnly = false, Idempotent = false, Title = "Place order")]
    [Description(
        "Places an order on a practice account when Trading__Enabled=true and every gate layer permits. "
        + "Refused when trading is disabled, the kill switch is engaged, or any risk rule blocks the write.")]
    public Task<ToolPayloads.TradingWriteResult> SubmitPlace(
        [Description("The venue account id, from list_accounts.")] int accountId,
        [Description("The venue contract id, e.g. CON.F.US.MES.Z26.")] string contractId,
        [Description("Order type: Limit, Market, Stop, StopLimit, TrailingStop, JoinBid, JoinAsk.")]
        ExecutionOrderType orderType,
        [Description("Buy or Sell.")] VenueSide side,
        [Description("Contract size.")] int size,
        [Description("Limit price when the type requires it.")] decimal? limitPrice = null,
        [Description("Stop price when the type requires it.")] decimal? stopPrice = null,
        [Description("Trail price when the type requires it.")] decimal? trailPrice = null,
        [Description(
            "When true, run risk checks and return a preview without transmitting. Omit for false.")]
        bool dryRun = false,
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
                new TradingRiskAttemptContext(dryRun),
                cancellationToken));

    /// <summary>Modifies an open order when the trading gate permits.</summary>
    [McpServerTool(ReadOnly = false, Idempotent = false, Title = "Modify order")]
    [Description("Modifies an open order when trading is enabled and every gate layer permits.")]
    public Task<ToolPayloads.TradingWriteResult> SubmitModify(
        [Description("The venue account id.")] int accountId,
        [Description("The order id to modify.")] long orderId,
        [Description("New size, when changing size.")] int? size = null,
        [Description("New limit price.")] decimal? limitPrice = null,
        [Description("New stop price.")] decimal? stopPrice = null,
        [Description("Preview only — no venue write. Omit for false.")] bool dryRun = false,
        CancellationToken cancellationToken = default) =>
        RunAsync(
            _gate.SubmitModify(
                new ExecutionModifyOrderCommand(accountId, orderId, size, limitPrice, stopPrice),
                new TradingRiskAttemptContext(dryRun),
                cancellationToken));

    /// <summary>Cancels an order when the trading gate permits.</summary>
    [McpServerTool(ReadOnly = false, Idempotent = false, Title = "Cancel order")]
    [Description("Cancels an order when trading is enabled and every gate layer permits.")]
    public Task<ToolPayloads.TradingWriteResult> SubmitCancel(
        [Description("The venue account id.")] int accountId,
        [Description("The order id to cancel.")] long orderId,
        [Description("Preview only — no venue write. Omit for false.")] bool dryRun = false,
        CancellationToken cancellationToken = default) =>
        RunAsync(
            _gate.SubmitCancel(
                new ExecutionCancelOrderCommand(accountId, orderId),
                new TradingRiskAttemptContext(dryRun),
                cancellationToken));

    /// <summary>Flattens a position when the trading gate permits.</summary>
    [McpServerTool(ReadOnly = false, Idempotent = false, Title = "Close position")]
    [Description("Flattens a position when trading is enabled and every gate layer permits.")]
    public Task<ToolPayloads.TradingWriteResult> SubmitClose(
        [Description("The venue account id.")] int accountId,
        [Description("The contract id to flatten.")] string contractId,
        [Description("Preview only — no venue write. Omit for false.")] bool dryRun = false,
        CancellationToken cancellationToken = default) =>
        RunAsync(
            _gate.SubmitClose(
                new ExecutionClosePositionCommand(accountId, contractId),
                new TradingRiskAttemptContext(dryRun),
                cancellationToken));

    /// <summary>Partially closes a position when the trading gate permits.</summary>
    [McpServerTool(ReadOnly = false, Idempotent = false, Title = "Partial close position")]
    [Description("Reduces a position when trading is enabled and every gate layer permits.")]
    public Task<ToolPayloads.TradingWriteResult> SubmitPartialClose(
        [Description("The venue account id.")] int accountId,
        [Description("The contract id.")] string contractId,
        [Description("Contracts to close.")] int size,
        [Description("Preview only — no venue write. Omit for false.")] bool dryRun = false,
        CancellationToken cancellationToken = default) =>
        RunAsync(
            _gate.SubmitPartialClose(
                new ExecutionPartialClosePositionCommand(accountId, contractId, size),
                new TradingRiskAttemptContext(dryRun),
                cancellationToken));

    private static async Task<ToolPayloads.TradingWriteResult> RunAsync(Task<TradingRiskGateResult> attempt)
    {
        try
        {
            TradingRiskGateResult result = await attempt.ConfigureAwait(false);
            return new ToolPayloads.TradingWriteResult(
                result.Outcome.ToString(),
                result.ParsedStage,
                result.Refusal?.Code,
                result.WriteResult?.OrderId,
                result.Preview?.WouldTransmit == true);
        }
        catch (VenueException ex)
        {
            throw new McpException("The venue could not answer: " + ex.Message);
        }
    }
}
