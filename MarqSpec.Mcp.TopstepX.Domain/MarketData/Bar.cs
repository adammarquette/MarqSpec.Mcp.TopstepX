namespace MarqSpec.Mcp.TopstepX.Domain.MarketData;

/// <summary>
/// One OHLCV bar, in the venue-neutral shape everything downstream computes over.
/// </summary>
/// <remarks>
/// <para>
/// Prices are <see cref="decimal"/> throughout, never <see cref="double"/>: a price is money, and binary
/// floating point cannot represent a tick size of <c>0.25</c> or <c>0.01</c> exactly. An indicator that
/// accumulates over thousands of bars would drift.
/// </para>
/// <para>
/// <b><see cref="ContractId"/> is the bar's provenance, and it is what makes a roll visible.</b> A series is
/// keyed by the venue-neutral symbol, so when the front month rolls the next fetch stores a
/// <i>different contract's</i> bars under the same key. Adjacent quarters do not trade at the same price, and
/// without this field the seam is unrecoverable — the two contracts read back as one continuous series and
/// everything derived from it inherits a gap that was a bookkeeping event
/// (<see cref="ContractRollDetector"/>, ADR-0011).
/// </para>
/// </remarks>
/// <param name="OpenTime">When the bar opened. Always UTC, and the bucket's identity.</param>
/// <param name="Open">The opening price.</param>
/// <param name="High">The highest traded price in the bar.</param>
/// <param name="Low">The lowest traded price in the bar.</param>
/// <param name="Close">The closing price.</param>
/// <param name="Volume">The traded volume in the bar.</param>
/// <param name="ContractId">
/// The venue contract this bar was fetched from, e.g. <c>CON.F.US.EP.U26</c>, or <see langword="null"/> when
/// the provenance was <b>not recorded</b>. Null is "unknown", never "the same as its neighbour": bars stored
/// before this field existed carry no contract and the information was never captured, so it cannot be
/// recovered. It defaults so that a caller assembling a bar by hand — a test, a fixture — is not forced to
/// invent one.
/// </param>
public sealed record Bar(
    DateTimeOffset OpenTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long Volume,
    string? ContractId = null);
