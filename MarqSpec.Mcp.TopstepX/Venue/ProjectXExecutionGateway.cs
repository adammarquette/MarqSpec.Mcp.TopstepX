using MarqSpec.Client.ProjectX;
using MarqSpec.Client.ProjectX.Api.Models;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Telemetry;
using Microsoft.Extensions.Logging;

namespace MarqSpec.Mcp.TopstepX.Venue;

/// <summary>
/// ProjectX order adapter that calls <see cref="TradingExecutionGate"/> before every transmit (ADR-0024, gh#710).
/// </summary>
/// <remarks>
/// <para>
/// <b>The only product file that may name the venue order surface</b> (gh#718). Every transmit runs
/// <see cref="TradingExecutionGate.RefuseWriteUnlessPermittedAsync"/> first.
/// </para>
/// <para>
/// Order POSTs rely on the ProjectX HTTP client having no transport-level retry handler; a retry would
/// duplicate an order before this adapter observes an outcome.
/// </para>
/// </remarks>
public sealed class ProjectXExecutionGateway : IExecutionGateway
{
    private readonly TradingExecutionGate _gate;
    private readonly IMarketDataGateway _marketData;
    private readonly ITradingUnknownStageRefusalSignal _unknownStageSignal;
    private readonly IProjectXApiClient _client;
    private readonly VenueCallGuard _calls;
    private readonly ILogger<ProjectXExecutionGateway> _logger;

    /// <summary>Creates the gated adapter.</summary>
    public ProjectXExecutionGateway(
        TradingExecutionGate gate,
        IMarketDataGateway marketData,
        ITradingUnknownStageRefusalSignal unknownStageSignal,
        IProjectXApiClient client,
        HostTelemetry telemetry,
        ILogger<ProjectXExecutionGateway> logger)
    {
        _gate = gate;
        _marketData = marketData;
        _unknownStageSignal = unknownStageSignal;
        _client = client;
        _calls = new VenueCallGuard(telemetry);
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<ExecutionWriteResult> PlaceOrder(
        ExecutionPlaceOrderCommand command,
        CancellationToken cancellationToken) =>
        ExecuteWhenPermittedAsync(
            command.AccountId,
            cancellationToken,
            ct => TransmitPlaceOrderAsync(command, ct));

    /// <inheritdoc />
    public Task<ExecutionWriteResult> ModifyOrder(
        ExecutionModifyOrderCommand command,
        CancellationToken cancellationToken) =>
        ExecuteWhenPermittedAsync(
            command.AccountId,
            cancellationToken,
            ct => TransmitModifyOrderAsync(command, ct));

    /// <inheritdoc />
    public Task<ExecutionWriteResult> CancelOrder(
        ExecutionCancelOrderCommand command,
        CancellationToken cancellationToken) =>
        ExecuteWhenPermittedAsync(
            command.AccountId,
            cancellationToken,
            ct => TransmitCancelOrderAsync(command, ct));

    /// <inheritdoc />
    public Task<ExecutionWriteResult> ClosePosition(
        ExecutionClosePositionCommand command,
        CancellationToken cancellationToken) =>
        ExecuteWhenPermittedAsync(
            command.AccountId,
            cancellationToken,
            ct => TransmitClosePositionAsync(command, ct));

    /// <inheritdoc />
    public Task<ExecutionWriteResult> PartialClosePosition(
        ExecutionPartialClosePositionCommand command,
        CancellationToken cancellationToken) =>
        ExecuteWhenPermittedAsync(
            command.AccountId,
            cancellationToken,
            ct => TransmitPartialClosePositionAsync(command, ct));

    private async Task<ExecutionWriteResult> ExecuteWhenPermittedAsync(
        int accountId,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<ExecutionWriteResult>> transmit)
    {
        TradingExecutionRefusal? refusal = await _gate
            .RefuseWriteUnlessPermittedAsync(
                accountId,
                _marketData,
                _unknownStageSignal,
                cancellationToken)
            .ConfigureAwait(false);

        if (refusal is TradingExecutionRefusal gateRefusal)
        {
            return GateRefused(gateRefusal);
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await transmit(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ExecutionTransmitFault.MapOrRethrow(ex, transmitted: false, cancellationToken, _logger);
        }
    }

    private static ExecutionWriteResult GateRefused(TradingExecutionRefusal refusal) =>
        new(
            ExecutionWriteOutcome.NotTransmitted,
            refusal,
            VenueErrorCode: null,
            VenueErrorMessage: null,
            OrderId: null);

    private async Task<ExecutionWriteResult> InvokeVenueCallAsync<TResponse>(
        CancellationToken cancellationToken,
        string operation,
        string what,
        Func<CancellationToken, Task<TResponse>> call,
        Func<TResponse, ExecutionWriteResult> map)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool transmitted = false;
        try
        {
            TResponse response = await _calls.RunAsync(
                operation,
                async () =>
                {
                    transmitted = true;
                    return await call(cancellationToken).ConfigureAwait(false);
                },
                what).ConfigureAwait(false);

            return map(response);
        }
        catch (Exception ex)
        {
            return ExecutionTransmitFault.MapOrRethrow(ex, transmitted, cancellationToken, _logger);
        }
    }

    private Task<ExecutionWriteResult> TransmitPlaceOrderAsync(
        ExecutionPlaceOrderCommand command,
        CancellationToken cancellationToken)
    {
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

        return InvokeVenueCallAsync(
            cancellationToken,
            ExecutionOperation.PlaceOrderOperation,
            "placing an order",
            ct => _client.PlaceOrderAsync(request, ct),
            MapPlaceResponse);
    }

    private Task<ExecutionWriteResult> TransmitModifyOrderAsync(
        ExecutionModifyOrderCommand command,
        CancellationToken cancellationToken)
    {
        ModifyOrderRequest request = new()
        {
            AccountId = command.AccountId,
            OrderId = command.OrderId,
            Size = command.Size,
            LimitPrice = command.LimitPrice,
            StopPrice = command.StopPrice,
        };

        return InvokeVenueCallAsync(
            cancellationToken,
            ExecutionOperation.ModifyOrderOperation,
            "modifying an order",
            ct => _client.ModifyOrderAsync(request, ct),
            response => MapSimpleResponse(response.Success, response.ErrorCode, response.ErrorMessage));
    }

    private Task<ExecutionWriteResult> TransmitCancelOrderAsync(
        ExecutionCancelOrderCommand command,
        CancellationToken cancellationToken) =>
        InvokeVenueCallAsync(
            cancellationToken,
            ExecutionOperation.CancelOrderOperation,
            "canceling an order",
            ct => _client.CancelOrderAsync(command.AccountId, command.OrderId, ct),
            response => MapSimpleResponse(response.Success, response.ErrorCode, response.ErrorMessage));

    private Task<ExecutionWriteResult> TransmitClosePositionAsync(
        ExecutionClosePositionCommand command,
        CancellationToken cancellationToken) =>
        InvokeVenueCallAsync(
            cancellationToken,
            ExecutionOperation.ClosePositionOperation,
            "closing a position",
            ct => _client.ClosePositionAsync(command.AccountId, command.ContractId, ct),
            response => MapSimpleResponse(response.Success, response.ErrorCode, response.ErrorMessage));

    private Task<ExecutionWriteResult> TransmitPartialClosePositionAsync(
        ExecutionPartialClosePositionCommand command,
        CancellationToken cancellationToken) =>
        InvokeVenueCallAsync(
            cancellationToken,
            ExecutionOperation.PartialClosePositionOperation,
            "partially closing a position",
            ct => _client.PartialClosePositionAsync(
                command.AccountId,
                command.ContractId,
                command.Size,
                ct),
            response => MapSimpleResponse(response.Success, response.ErrorCode, response.ErrorMessage));

    private static ExecutionWriteResult MapPlaceResponse(PlaceOrderResponse response)
    {
        if (response.Success && response.OrderId is long orderId)
        {
            return new ExecutionWriteResult(
                ExecutionWriteOutcome.BrokerAcknowledged,
                GateRefusal: null,
                VenueErrorCode: null,
                VenueErrorMessage: null,
                OrderId: orderId);
        }

        if (response.Success)
        {
            return new ExecutionWriteResult(
                ExecutionWriteOutcome.BrokerSuccessWithoutOrderId,
                GateRefusal: null,
                VenueErrorCode: null,
                VenueErrorMessage: ExecutionVenueText.Sanitize(response.ErrorMessage),
                OrderId: null);
        }

        return new ExecutionWriteResult(
            ExecutionWriteOutcome.VenueRejected,
            GateRefusal: null,
            VenueErrorCode: response.ErrorCode,
            VenueErrorMessage: ExecutionVenueText.Sanitize(response.ErrorMessage),
            OrderId: null);
    }

    private static ExecutionWriteResult MapSimpleResponse(
        bool success,
        int errorCode,
        string? errorMessage)
    {
        if (success)
        {
            return new ExecutionWriteResult(
                ExecutionWriteOutcome.BrokerAcknowledged,
                GateRefusal: null,
                VenueErrorCode: null,
                VenueErrorMessage: null,
                OrderId: null);
        }

        return new ExecutionWriteResult(
            ExecutionWriteOutcome.VenueRejected,
            GateRefusal: null,
            VenueErrorCode: errorCode,
            VenueErrorMessage: ExecutionVenueText.Sanitize(errorMessage),
            OrderId: null);
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
