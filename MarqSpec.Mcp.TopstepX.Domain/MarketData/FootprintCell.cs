namespace MarqSpec.Mcp.TopstepX.Domain.MarketData;

/// <summary>
/// Buy and sell volume at one price inside one bar — the pure projection of a run of prints.
/// </summary>
/// <remarks>
/// No <c>RecordedAt</c>: that is when a host pass last wrote the row, and the aggregator does
/// not read a clock. No <c>Venue</c>: the host stamps the series it asked for. No
/// <c>ContractId</c>: a cell is always computed inside a single-contract run (ADR-0011), so the
/// contract is a property of the prints, not a second copy that can disagree with them.
/// </remarks>
/// <param name="Instrument">The normalised venue-neutral symbol.</param>
/// <param name="ResolutionMinutes">The bar size this cell was aggregated into.</param>
/// <param name="BucketStart">When the bar opened. Always UTC. Aligns with <c>Bar.OpenTime</c>.</param>
/// <param name="Price">The price level inside the bar.</param>
/// <param name="BuyVolume">Volume whose aggressor was lifting.</param>
/// <param name="SellVolume">Volume whose aggressor was hitting.</param>
public sealed record FootprintCell(
    string Instrument,
    int ResolutionMinutes,
    DateTimeOffset BucketStart,
    decimal Price,
    long BuyVolume,
    long SellVolume);
