using MarqSpec.Mcp.TopstepX.Configuration;

namespace MarqSpec.Mcp.TopstepX.Venue;

/// <summary>Order type on the venue wire, exposed without raw integers (ADR-0008).</summary>
public enum ExecutionOrderType
{
    /// <summary>Limit order.</summary>
    Limit = 1,

    /// <summary>Market order.</summary>
    Market = 2,

    /// <summary>Stop-limit order.</summary>
    StopLimit = 3,

    /// <summary>Stop order.</summary>
    Stop = 4,

    /// <summary>Trailing stop.</summary>
    TrailingStop = 5,

    /// <summary>Join bid.</summary>
    JoinBid = 6,

    /// <summary>Join ask.</summary>
    JoinAsk = 7,
}

/// <summary>How a gated venue write ended (ADR-0024 §5).</summary>
public enum ExecutionWriteOutcome
{
    /// <summary>The combined gate refused before any venue call.</summary>
    NotTransmitted = 0,

    /// <summary>The request may have reached the venue; the response was not received.</summary>
    TransmittedOutcomeUnknown = 1,

    /// <summary>The venue responded with <c>success: false</c>.</summary>
    VenueRejected = 2,

    /// <summary>The venue responded with <c>success: true</c> but no order id (place only).</summary>
    BrokerSuccessWithoutOrderId = 3,

    /// <summary>Broker acknowledged the write.</summary>
    BrokerAcknowledged = 4,
}

/// <summary>
/// Whether a venue write was transmitted and whether the broker acknowledged it (ADR-0024 §5).
/// </summary>
/// <param name="Transmitted">True after the gateway order call may have reached the venue.</param>
/// <param name="BrokerAcknowledged">True only when the outcome is <see cref="ExecutionWriteOutcome.BrokerAcknowledged"/>.</param>
public readonly record struct ExecutionWriteLifecycle(bool Transmitted, bool BrokerAcknowledged)
{
    /// <summary>Gate refused before any venue write.</summary>
    public static ExecutionWriteLifecycle NotTransmitted { get; } = new(false, false);

    /// <summary>Sent, outcome unknown (timeout or transport failure).</summary>
    public static ExecutionWriteLifecycle TransmittedOutcomeUnknown { get; } = new(true, false);

    /// <summary>Venue responded without broker success — not live.</summary>
    public static ExecutionWriteLifecycle VenueRejected { get; } = new(true, false);

    /// <summary>Success without an assigned order id — not live.</summary>
    public static ExecutionWriteLifecycle BrokerSuccessWithoutOrderId { get; } = new(true, false);

    /// <summary>Broker acknowledged the write.</summary>
    public static ExecutionWriteLifecycle Acknowledged { get; } = new(true, true);
}

/// <summary>Outcome of one gated execution write.</summary>
/// <param name="Outcome">The terminal outcome.</param>
/// <param name="GateRefusal">Set when the combined gate refused before transmit.</param>
/// <param name="VenueErrorCode">Venue error code on rejection only.</param>
/// <param name="VenueErrorMessage">Venue message on rejection or uncertain transmit.</param>
/// <param name="OrderId">Assigned order id after a broker-acked place.</param>
public sealed record ExecutionWriteResult(
    ExecutionWriteOutcome Outcome,
    TradingExecutionRefusal? GateRefusal,
    int? VenueErrorCode,
    string? VenueErrorMessage,
    long? OrderId)
{
    /// <summary>Projection for callers that reason about transmission and ack separately.</summary>
    public ExecutionWriteLifecycle Lifecycle => Outcome switch
    {
        ExecutionWriteOutcome.NotTransmitted => ExecutionWriteLifecycle.NotTransmitted,
        ExecutionWriteOutcome.TransmittedOutcomeUnknown => ExecutionWriteLifecycle.TransmittedOutcomeUnknown,
        ExecutionWriteOutcome.VenueRejected => ExecutionWriteLifecycle.VenueRejected,
        ExecutionWriteOutcome.BrokerSuccessWithoutOrderId => ExecutionWriteLifecycle.BrokerSuccessWithoutOrderId,
        ExecutionWriteOutcome.BrokerAcknowledged => ExecutionWriteLifecycle.Acknowledged,
        _ => ExecutionWriteLifecycle.NotTransmitted,
    };
}


/// <summary>Inputs for placing an order through the execution gateway.</summary>
public sealed record ExecutionPlaceOrderCommand(
    int AccountId,
    string ContractId,
    ExecutionOrderType OrderType,
    VenueSide Side,
    int Size,
    decimal? LimitPrice,
    decimal? StopPrice,
    decimal? TrailPrice);

/// <summary>Inputs for modifying an open order.</summary>
public sealed record ExecutionModifyOrderCommand(
    int AccountId,
    long OrderId,
    int? Size,
    decimal? LimitPrice,
    decimal? StopPrice);

/// <summary>Inputs for canceling an order.</summary>
public sealed record ExecutionCancelOrderCommand(int AccountId, long OrderId);

/// <summary>Inputs for flattening a position.</summary>
public sealed record ExecutionClosePositionCommand(int AccountId, string ContractId);

/// <summary>Inputs for reducing a position.</summary>
public sealed record ExecutionPartialClosePositionCommand(int AccountId, string ContractId, int Size);
