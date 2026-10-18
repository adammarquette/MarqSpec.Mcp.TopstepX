namespace MarqSpec.Mcp.TopstepX.Venue;

/// <summary>
/// Gated venue write seam for practice v1 (ADR-0024, gh#710). MCP tools register in later issues.
/// </summary>
/// <remarks>
/// <para>
/// Method names mirror the venue verbs but deliberately omit the <c>Async</c> suffix so this file stays
/// outside gh#718's closed order-method vocabulary; only <see cref="ProjectXExecutionGateway"/> spells the
/// vendor method names when calling the client.
/// </para>
/// <para>
/// Future MCP tools reach writes through <c>TradingRiskGate</c> (gh#711), not this interface directly.
/// </para>
/// <para>
/// <b>Never auto-retry.</b> When <see cref="ExecutionWriteResult.Outcome"/> is
/// <see cref="ExecutionWriteOutcome.TransmittedOutcomeUnknown"/> or
/// <see cref="ExecutionWriteOutcome.BrokerSuccessWithoutOrderId"/>, the caller must reconcile open orders and
/// positions before attempting the same command again. Only retry when
/// <see cref="ExecutionWriteResult.IsRetrySafe"/> is true.
/// </para>
/// <para>
/// The host wraps <c>AddProjectXApiClient</c> with
/// <see cref="ProjectXHttpResilienceConfiguration.SuppressTransportRetriesOnVenueWrites"/> so place, modify,
/// cancel, close and partial-close never transport-retry (the pinned client exempts only
/// <c>/api/Order/place</c> without this host wrap).
/// </para>
/// </remarks>
public interface IExecutionGateway
{
    /// <summary>Places an order when the combined gate permits.</summary>
    Task<ExecutionWriteResult> PlaceOrder(
        ExecutionPlaceOrderCommand command,
        CancellationToken cancellationToken);

    /// <summary>Modifies an open order when the combined gate permits.</summary>
    Task<ExecutionWriteResult> ModifyOrder(
        ExecutionModifyOrderCommand command,
        CancellationToken cancellationToken);

    /// <summary>Cancels an order when the combined gate permits.</summary>
    Task<ExecutionWriteResult> CancelOrder(
        ExecutionCancelOrderCommand command,
        CancellationToken cancellationToken);

    /// <summary>Flattens a position when the combined gate permits.</summary>
    Task<ExecutionWriteResult> ClosePosition(
        ExecutionClosePositionCommand command,
        CancellationToken cancellationToken);

    /// <summary>Partially closes a position when the combined gate permits.</summary>
    Task<ExecutionWriteResult> PartialClosePosition(
        ExecutionPartialClosePositionCommand command,
        CancellationToken cancellationToken);
}
