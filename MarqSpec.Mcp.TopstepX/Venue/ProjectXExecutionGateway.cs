using MarqSpec.Client.ProjectX;
using MarqSpec.Client.ProjectX.Api.Models;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Telemetry;

namespace MarqSpec.Mcp.TopstepX.Venue;

/// <summary>
/// ProjectX order adapter that calls <see cref="TradingExecutionGate"/> before every transmit (ADR-0024, gh#710).
/// </summary>
/// <remarks>
/// <b>The only product file that may name the venue order surface</b> (gh#718). Every transmit runs
/// <see cref="TradingExecutionGate.RefuseWriteUnlessPermittedAsync"/> first.
/// </remarks>
public sealed class ProjectXExecutionGateway : IExecutionGateway
{
    private readonly TradingExecutionGate _gate;
    private readonly IMarketDataGateway _marketData;
    private readonly ITradingUnknownStageRefusalSignal _unknownStageSignal;
    private readonly IProjectXApiClient _client;
    private readonly VenueCallGuard _calls;

    /// <summary>Creates the gated adapter.</summary>
    public ProjectXExecutionGateway(
        TradingExecutionGate gate,
        IMarketDataGateway marketData,
        ITradingUnknownStageRefusalSignal unknownStageSignal,
        IProjectXApiClient client,
        HostTelemetry telemetry)
    {
        _gate = gate;
        _marketData = marketData;
        _unknownStageSignal = unknownStageSignal;
        _client = client;
        _calls = new VenueCallGuard(telemetry);
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

        return await RunTransmitAsync(transmit, cancellationToken).ConfigureAwait(false);
    }

    private static ExecutionWriteResult GateRefused(TradingExecutionRefusal refusal) =>
        new(
            ExecutionWriteOutcome.NotTransmitted,
            refusal,
            VenueErrorCode: null,
            VenueErrorMessage: null,
            OrderId: null);

    private static async Task<ExecutionWriteResult> RunTransmitAsync(
        Func<CancellationToken, Task<ExecutionWriteResult>> transmit,
        CancellationToken cancellationToken)
    {
        try
        {
            return await transmit(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new ExecutionWriteResult(
                ExecutionWriteOutcome.TransmittedOutcomeUnknown,
                GateRefusal: null,
                VenueErrorCode: null,
                VenueErrorMessage: "The venue did not return a response; the write may have been placed.",
                OrderId: null);
        }
    }

    private async Task<ExecutionWriteResult> TransmitPlaceOrderAsync(
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

        PlaceOrderResponse response = await _calls.RunAsync(
            ExecutionOperation.PlaceOrderOperation,
            () => _client.PlaceOrderAsync(request, cancellationToken),
            "placing an order").ConfigureAwait(false);

        return MapPlaceResponse(response);
    }

    private async Task<ExecutionWriteResult> TransmitModifyOrderAsync(
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

        ModifyOrderResponse response = await _calls.RunAsync(
            ExecutionOperation.ModifyOrderOperation,
            () => _client.ModifyOrderAsync(request, cancellationToken),
            "modifying an order").ConfigureAwait(false);

        return MapSimpleResponse(response.Success, response.ErrorCode, response.ErrorMessage);
    }

    private async Task<ExecutionWriteResult> TransmitCancelOrderAsync(
        ExecutionCancelOrderCommand command,
        CancellationToken cancellationToken)
    {
        CancelOrderResponse response = await _calls.RunAsync(
            ExecutionOperation.CancelOrderOperation,
            () => _client.CancelOrderAsync(command.AccountId, command.OrderId, cancellationToken),
            "canceling an order").ConfigureAwait(false);

        return MapSimpleResponse(response.Success, response.ErrorCode, response.ErrorMessage);
    }

    private async Task<ExecutionWriteResult> TransmitClosePositionAsync(
        ExecutionClosePositionCommand command,
        CancellationToken cancellationToken)
    {
        ClosePositionResponse response = await _calls.RunAsync(
            ExecutionOperation.ClosePositionOperation,
            () => _client.ClosePositionAsync(command.AccountId, command.ContractId, cancellationToken),
            "closing a position").ConfigureAwait(false);

        return MapSimpleResponse(response.Success, response.ErrorCode, response.ErrorMessage);
    }

    private async Task<ExecutionWriteResult> TransmitPartialClosePositionAsync(
        ExecutionPartialClosePositionCommand command,
        CancellationToken cancellationToken)
    {
        PartialClosePositionResponse response = await _calls.RunAsync(
            ExecutionOperation.PartialClosePositionOperation,
            () => _client.PartialClosePositionAsync(
                command.AccountId,
                command.ContractId,
                command.Size,
                cancellationToken),
            "partially closing a position").ConfigureAwait(false);

        return MapSimpleResponse(response.Success, response.ErrorCode, response.ErrorMessage);
    }

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
                VenueErrorMessage: response.ErrorMessage,
                OrderId: null);
        }

        return new ExecutionWriteResult(
            ExecutionWriteOutcome.VenueRejected,
            GateRefusal: null,
            VenueErrorCode: response.ErrorCode,
            VenueErrorMessage: response.ErrorMessage,
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
            VenueErrorMessage: errorMessage,
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
