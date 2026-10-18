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

/// <summary>
/// Whether a venue write was transmitted and whether the broker acknowledged it (ADR-0024 §5).
/// </summary>
/// <param name="Transmitted">True after the gateway order call returns.</param>
/// <param name="BrokerAcknowledged">True only when the venue response reports success.</param>
public readonly record struct ExecutionWriteLifecycle(bool Transmitted, bool BrokerAcknowledged)
{
    /// <summary>Gate refused before any venue write.</summary>
    public static ExecutionWriteLifecycle NotTransmitted { get; } = new(false, false);

    /// <summary>Venue responded without broker success — not live.</summary>
    public static ExecutionWriteLifecycle PendingOrRejected { get; } = new(true, false);

    /// <summary>Broker acknowledged the write.</summary>
    public static ExecutionWriteLifecycle Acknowledged { get; } = new(true, true);
}

/// <summary>Outcome of one gated execution write.</summary>
/// <param name="Lifecycle">Transmission and ack state.</param>
/// <param name="GateRefusal">Set when the combined gate refused before transmit.</param>
/// <param name="VenueErrorCode">Venue error code when transmitted.</param>
/// <param name="VenueErrorMessage">Venue message when transmitted.</param>
/// <param name="OrderId">Assigned order id after a broker-acked place.</param>
public sealed record ExecutionWriteResult(
    ExecutionWriteLifecycle Lifecycle,
    TradingExecutionRefusal? GateRefusal,
    int? VenueErrorCode,
    string? VenueErrorMessage,
    long? OrderId);

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
