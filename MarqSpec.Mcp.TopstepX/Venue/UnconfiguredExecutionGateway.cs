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
    public Task<ExecutionWriteResult> PlaceOrderAsync(
        ExecutionPlaceOrderCommand command,
        CancellationToken cancellationToken) =>
        Task.FromResult(Refused);

    /// <inheritdoc />
    public Task<ExecutionWriteResult> ModifyOrderAsync(
        ExecutionModifyOrderCommand command,
        CancellationToken cancellationToken) =>
        Task.FromResult(Refused);

    /// <inheritdoc />
    public Task<ExecutionWriteResult> CancelOrderAsync(
        ExecutionCancelOrderCommand command,
        CancellationToken cancellationToken) =>
        Task.FromResult(Refused);

    /// <inheritdoc />
    public Task<ExecutionWriteResult> ClosePositionAsync(
        ExecutionClosePositionCommand command,
        CancellationToken cancellationToken) =>
        Task.FromResult(Refused);

    /// <inheritdoc />
    public Task<ExecutionWriteResult> PartialClosePositionAsync(
        ExecutionPartialClosePositionCommand command,
        CancellationToken cancellationToken) =>
        Task.FromResult(Refused);
}
