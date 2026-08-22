namespace MarqSpec.Mcp.TopstepX.Domain.MarketData;

/// <summary>
/// One OHLCV bar, in the venue-neutral shape everything downstream computes over.
/// </summary>
/// <remarks>
/// Prices are <see cref="decimal"/> throughout, never <see cref="double"/>: a price is money, and binary
/// floating point cannot represent a tick size of <c>0.25</c> or <c>0.01</c> exactly. An indicator that
/// accumulates over thousands of bars would drift.
/// </remarks>
/// <param name="OpenTime">When the bar opened. Always UTC, and the bucket's identity.</param>
/// <param name="Open">The opening price.</param>
/// <param name="High">The highest traded price in the bar.</param>
/// <param name="Low">The lowest traded price in the bar.</param>
/// <param name="Close">The closing price.</param>
/// <param name="Volume">The traded volume in the bar.</param>
public sealed record Bar(
    DateTimeOffset OpenTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long Volume);
