using MarqSpec.Mcp.TopstepX.Configuration;

namespace MarqSpec.Mcp.TopstepX.Venue;

/// <summary>
/// The only public entry to venue writes — every call runs the combined gate first (ADR-0024, gh#718).
/// </summary>
public sealed class GatedExecutionGateway : IExecutionGateway
{
    private readonly TradingExecutionGate _gate;
    private readonly IMarketDataGateway _marketData;
    private readonly ProjectXExecutionGateway _adapter;
    private readonly ITradingUnknownStageRefusalSignal _unknownStageSignal;

    /// <summary>Creates the gated gateway.</summary>
    public GatedExecutionGateway(
        TradingExecutionGate gate,
        IMarketDataGateway marketData,
        ProjectXExecutionGateway adapter,
        ITradingUnknownStageRefusalSignal unknownStageSignal)
    {
        _gate = gate;
        _marketData = marketData;
        _adapter = adapter;
        _unknownStageSignal = unknownStageSignal;
    }

    /// <inheritdoc />
    public Task<ExecutionWriteResult> PlaceOrderAsync(
        ExecutionPlaceOrderCommand command,
        CancellationToken cancellationToken) =>
        ExecuteWhenPermittedAsync(
            command.AccountId,
            cancellationToken,
            ct => _adapter.PlaceOrderAsync(command, ct));

    /// <inheritdoc />
    public Task<ExecutionWriteResult> ModifyOrderAsync(
        ExecutionModifyOrderCommand command,
        CancellationToken cancellationToken) =>
        ExecuteWhenPermittedAsync(
            command.AccountId,
            cancellationToken,
            ct => _adapter.ModifyOrderAsync(command, ct));

    /// <inheritdoc />
    public Task<ExecutionWriteResult> CancelOrderAsync(
        ExecutionCancelOrderCommand command,
        CancellationToken cancellationToken) =>
        ExecuteWhenPermittedAsync(
            command.AccountId,
            cancellationToken,
            ct => _adapter.CancelOrderAsync(command, ct));

    /// <inheritdoc />
    public Task<ExecutionWriteResult> ClosePositionAsync(
        ExecutionClosePositionCommand command,
        CancellationToken cancellationToken) =>
        ExecuteWhenPermittedAsync(
            command.AccountId,
            cancellationToken,
            ct => _adapter.ClosePositionAsync(command, ct));

    /// <inheritdoc />
    public Task<ExecutionWriteResult> PartialClosePositionAsync(
        ExecutionPartialClosePositionCommand command,
        CancellationToken cancellationToken) =>
        ExecuteWhenPermittedAsync(
            command.AccountId,
            cancellationToken,
            ct => _adapter.PartialClosePositionAsync(command, ct));

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

        if (refusal is not null)
        {
            return new ExecutionWriteResult(
                ExecutionWriteLifecycle.NotTransmitted,
                refusal,
                VenueErrorCode: null,
                VenueErrorMessage: null,
                OrderId: null);
        }

        return await transmit(cancellationToken).ConfigureAwait(false);
    }
}
