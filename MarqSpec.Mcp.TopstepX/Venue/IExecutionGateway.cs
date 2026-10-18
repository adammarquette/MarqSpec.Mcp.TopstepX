namespace MarqSpec.Mcp.TopstepX.Venue;

/// <summary>
/// Gated venue write seam for practice v1 (ADR-0024, gh#710). MCP tools register in later issues.
/// </summary>
/// <remarks>
/// Every implementation must refuse non-practice accounts and honor the master enable switch before any
/// venue order call. The ProjectX adapter is reachable only through <see cref="GatedExecutionGateway"/>.
/// </remarks>
public interface IExecutionGateway
{
    /// <summary>Places an order when the combined gate permits.</summary>
    Task<ExecutionWriteResult> PlaceOrderAsync(
        ExecutionPlaceOrderCommand command,
        CancellationToken cancellationToken);

    /// <summary>Modifies an open order when the combined gate permits.</summary>
    Task<ExecutionWriteResult> ModifyOrderAsync(
        ExecutionModifyOrderCommand command,
        CancellationToken cancellationToken);

    /// <summary>Cancels an order when the combined gate permits.</summary>
    Task<ExecutionWriteResult> CancelOrderAsync(
        ExecutionCancelOrderCommand command,
        CancellationToken cancellationToken);

    /// <summary>Flattens a position when the combined gate permits.</summary>
    Task<ExecutionWriteResult> ClosePositionAsync(
        ExecutionClosePositionCommand command,
        CancellationToken cancellationToken);

    /// <summary>Partially closes a position when the combined gate permits.</summary>
    Task<ExecutionWriteResult> PartialClosePositionAsync(
        ExecutionPartialClosePositionCommand command,
        CancellationToken cancellationToken);
}
