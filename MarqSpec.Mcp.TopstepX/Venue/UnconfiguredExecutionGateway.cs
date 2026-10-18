using MarqSpec.Mcp.TopstepX.Configuration;

namespace MarqSpec.Mcp.TopstepX.Venue;

/// <summary>Inert execution seam when the venue is not configured.</summary>
public sealed class UnconfiguredExecutionGateway : IExecutionGateway
{
    private static ExecutionWriteResult Refused { get; } = new(
        ExecutionWriteLifecycle.NotTransmitted,
        new TradingExecutionRefusal(
            "venue_not_configured",
            "Venue credentials are not configured, so trading writes cannot reach the gateway."),
        VenueErrorCode: null,
        VenueErrorMessage: null,
        OrderId: null);

    /// <inheritdoc />
    public Task<ExecutionWriteResult> PlaceOrder(
        ExecutionPlaceOrderCommand command,
        CancellationToken cancellationToken) =>
        Task.FromResult(Refused);

    /// <inheritdoc />
    public Task<ExecutionWriteResult> ModifyOrder(
        ExecutionModifyOrderCommand command,
        CancellationToken cancellationToken) =>
        Task.FromResult(Refused);

    /// <inheritdoc />
    public Task<ExecutionWriteResult> CancelOrder(
        ExecutionCancelOrderCommand command,
        CancellationToken cancellationToken) =>
        Task.FromResult(Refused);

    /// <inheritdoc />
    public Task<ExecutionWriteResult> ClosePosition(
        ExecutionClosePositionCommand command,
        CancellationToken cancellationToken) =>
        Task.FromResult(Refused);

    /// <inheritdoc />
    public Task<ExecutionWriteResult> PartialClosePosition(
        ExecutionPartialClosePositionCommand command,
        CancellationToken cancellationToken) =>
        Task.FromResult(Refused);
}
