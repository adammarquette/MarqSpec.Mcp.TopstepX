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
    /// <summary>No venue write was transmitted (gate refusal, mapping failure, or guard refusal before ack).</summary>
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

/// <summary>Outcome of one gated execution write.</summary>
/// <param name="Outcome">The terminal outcome.</param>
/// <param name="GateRefusal">Set when the combined gate refused before transmit.</param>
/// <param name="HttpStatusCode">HTTP status when the guard translated a gateway refusal.</param>
/// <param name="VendorErrorCode">Vendor business error code from a parsed response body.</param>
/// <param name="OrderId">Assigned order id after a broker-acked place.</param>
public sealed record ExecutionWriteResult(
    ExecutionWriteOutcome Outcome,
    TradingExecutionRefusal? GateRefusal,
    int? HttpStatusCode,
    int? VendorErrorCode,
    long? OrderId)
{
    /// <summary>
    /// True when repeating the same command without reconciliation is unlikely to duplicate a live order.
    /// </summary>
    /// <remarks>
    /// <see cref="ExecutionWriteOutcome.TransmittedOutcomeUnknown"/> and
    /// <see cref="ExecutionWriteOutcome.BrokerSuccessWithoutOrderId"/> are never retry-safe.
    /// </remarks>
    public bool IsRetrySafe => Outcome is ExecutionWriteOutcome.NotTransmitted
        or ExecutionWriteOutcome.VenueRejected;
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
