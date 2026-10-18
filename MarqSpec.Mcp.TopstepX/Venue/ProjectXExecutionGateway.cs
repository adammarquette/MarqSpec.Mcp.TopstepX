using MarqSpec.Client.ProjectX;
using MarqSpec.Client.ProjectX.Api.Models;
using MarqSpec.Mcp.TopstepX.Telemetry;

namespace MarqSpec.Mcp.TopstepX.Venue;

/// <summary>
/// ProjectX order adapter — reachable only from <see cref="GatedExecutionGateway"/> (gh#710).
/// </summary>
/// <remarks>
/// This type is the only product code that calls the venue order surface. Until gh#718 evolves CI, that
/// remains a deliberate violation of the legacy absolute ban (ADR-0024).
/// </remarks>
public sealed class ProjectXExecutionGateway
{
    private readonly IProjectXApiClient _client;
    private readonly VenueCallGuard _calls;

    /// <summary>Creates the adapter.</summary>
    /// <param name="client">The vendor client.</param>
    /// <param name="telemetry">App-owned telemetry (ADR-0019).</param>
    public ProjectXExecutionGateway(IProjectXApiClient client, HostTelemetry telemetry)
    {
        _client = client;
        _calls = new VenueCallGuard(telemetry);
    }

    /// <summary>Places an order after the combined gate has permitted the account.</summary>
    public async Task<ExecutionWriteResult> PlaceOrderAsync(
        ExecutionPlaceOrderCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        PlaceOrderRequest request = new()
        {
            AccountId = command.AccountId,
            ContractId = command.ContractId,
            Type = ToOrderType(command.OrderType),
            Side = ToOrderSide(command.Side),
            Size = command.Size,
            LimitPrice = command.LimitPrice,
            StopPrice = command.StopPrice,
            TrailPrice = command.TrailPrice,
        };

        PlaceOrderResponse response = await _calls.RunAsync(
            ExecutionOperation.PlaceOrder,
            () => _client.PlaceOrderAsync(request, cancellationToken),
            "placing an order").ConfigureAwait(false);

        return MapPlaceResponse(response);
    }

    /// <summary>Modifies an order after the combined gate has permitted the account.</summary>
    public async Task<ExecutionWriteResult> ModifyOrderAsync(
        ExecutionModifyOrderCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        ModifyOrderRequest request = new()
        {
            AccountId = command.AccountId,
            OrderId = command.OrderId,
            Size = command.Size,
            LimitPrice = command.LimitPrice,
            StopPrice = command.StopPrice,
        };

        ModifyOrderResponse response = await _calls.RunAsync(
            ExecutionOperation.ModifyOrder,
            () => _client.ModifyOrderAsync(request, cancellationToken),
            "modifying an order").ConfigureAwait(false);

        return MapSimpleResponse(response.Success, response.ErrorCode, response.ErrorMessage, orderId: null);
    }

    /// <summary>Cancels an order after the combined gate has permitted the account.</summary>
    public async Task<ExecutionWriteResult> CancelOrderAsync(
        ExecutionCancelOrderCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        CancelOrderResponse response = await _calls.RunAsync(
            ExecutionOperation.CancelOrder,
            () => _client.CancelOrderAsync(command.AccountId, command.OrderId, cancellationToken),
            "canceling an order").ConfigureAwait(false);

        return MapSimpleResponse(response.Success, response.ErrorCode, response.ErrorMessage, orderId: null);
    }

    /// <summary>Flattens a position after the combined gate has permitted the account.</summary>
    public async Task<ExecutionWriteResult> ClosePositionAsync(
        ExecutionClosePositionCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        ClosePositionResponse response = await _calls.RunAsync(
            ExecutionOperation.ClosePosition,
            () => _client.ClosePositionAsync(command.AccountId, command.ContractId, cancellationToken),
            "closing a position").ConfigureAwait(false);

        return MapSimpleResponse(response.Success, response.ErrorCode, response.ErrorMessage, orderId: null);
    }

    /// <summary>Partially closes a position after the combined gate has permitted the account.</summary>
    public async Task<ExecutionWriteResult> PartialClosePositionAsync(
        ExecutionPartialClosePositionCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        PartialClosePositionResponse response = await _calls.RunAsync(
            ExecutionOperation.PartialClosePosition,
            () => _client.PartialClosePositionAsync(
                command.AccountId,
                command.ContractId,
                command.Size,
                cancellationToken),
            "partially closing a position").ConfigureAwait(false);

        return MapSimpleResponse(response.Success, response.ErrorCode, response.ErrorMessage, orderId: null);
    }

    private static ExecutionWriteResult MapPlaceResponse(PlaceOrderResponse response)
    {
        if (response.Success && response.OrderId is long orderId)
        {
            return new ExecutionWriteResult(
                ExecutionWriteLifecycle.Acknowledged,
                GateRefusal: null,
                VenueErrorCode: response.ErrorCode,
                VenueErrorMessage: response.ErrorMessage,
                OrderId: orderId);
        }

        return new ExecutionWriteResult(
            ExecutionWriteLifecycle.PendingOrRejected,
            GateRefusal: null,
            VenueErrorCode: response.ErrorCode,
            VenueErrorMessage: response.ErrorMessage,
            OrderId: null);
    }

    private static ExecutionWriteResult MapSimpleResponse(
        bool success,
        int errorCode,
        string? errorMessage,
        long? orderId)
    {
        ExecutionWriteLifecycle lifecycle = success
            ? ExecutionWriteLifecycle.Acknowledged
            : ExecutionWriteLifecycle.PendingOrRejected;

        return new ExecutionWriteResult(
            lifecycle,
            GateRefusal: null,
            VenueErrorCode: errorCode,
            VenueErrorMessage: errorMessage,
            OrderId: orderId);
    }

    private static OrderType ToOrderType(ExecutionOrderType orderType) => orderType switch
    {
        ExecutionOrderType.Limit => OrderType.Limit,
        ExecutionOrderType.Market => OrderType.Market,
        ExecutionOrderType.StopLimit => OrderType.StopLimit,
        ExecutionOrderType.Stop => OrderType.Stop,
        ExecutionOrderType.TrailingStop => OrderType.TrailingStop,
        ExecutionOrderType.JoinBid => OrderType.JoinBid,
        ExecutionOrderType.JoinAsk => OrderType.JoinAsk,
        _ => throw new ArgumentOutOfRangeException(nameof(orderType), orderType, "Unmapped execution order type."),
    };

    private static OrderSide ToOrderSide(VenueSide side) => side switch
    {
        VenueSide.Buy => OrderSide.Bid,
        VenueSide.Sell => OrderSide.Ask,
        _ => throw new ArgumentOutOfRangeException(nameof(side), side, "Cannot place an order without a side."),
    };
}
