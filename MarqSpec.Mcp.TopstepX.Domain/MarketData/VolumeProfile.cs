namespace MarqSpec.Mcp.TopstepX.Domain.MarketData;

/// <summary>Volume at one price across the cells that were aggregated.</summary>
/// <param name="Price">The price level.</param>
/// <param name="Volume">Buy plus sell volume at that price.</param>
public sealed record VolumeAtPrice(decimal Price, long Volume);

/// <summary>
/// Volume by price, the point of control, and the 70% value area — an aggregate over
/// <see cref="FootprintCell"/>s, never a stored table (gh#221).
/// </summary>
/// <param name="ByPrice">Every price that traded, in price order, with its volume.</param>
/// <param name="PointOfControl">The price with the most volume.</param>
/// <param name="ValueAreaLow">The lowest price in the 70% value area.</param>
/// <param name="ValueAreaHigh">The highest price in the 70% value area.</param>
/// <param name="ValueAreaVolume">How much volume sits inside the value area.</param>
/// <param name="TotalVolume">How much volume the cells carried.</param>
public sealed record VolumeProfile(
    IReadOnlyList<VolumeAtPrice> ByPrice,
    decimal PointOfControl,
    decimal ValueAreaLow,
    decimal ValueAreaHigh,
    long ValueAreaVolume,
    long TotalVolume);

/// <summary>
/// One half-open range during which a subscription was listening — the Domain face of a
/// <c>TapeCoverage</c> row. Values are passed in; nothing here reads a store (ADR-0006).
/// </summary>
/// <param name="ContractId">The venue contract that was subscribed.</param>
/// <param name="RangeStart">The start of the listening range, inclusive. Always UTC.</param>
/// <param name="RangeEnd">The end of the listening range, exclusive. Always UTC.</param>
public sealed record ListeningRange(
    string ContractId,
    DateTimeOffset RangeStart,
    DateTimeOffset RangeEnd);

/// <summary>
/// The window a profile was actually computed over, after confinement to one contract.
/// </summary>
/// <param name="ContractId">The contract in front — the one <c>get_key_levels</c> would keep.</param>
/// <param name="Start">The start of the covered window, inclusive. From the ledger, not the ask.</param>
/// <param name="End">The end of the covered window, exclusive.</param>
/// <param name="Narrowed">
/// Whether the ask was cut back — a roll, a late start or early end, or a listening hole
/// that left only the newest contiguous run.
/// </param>
public sealed record CoveredTapeWindow(
    string ContractId,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool Narrowed);

/// <summary>A profile together with the window it was computed over.</summary>
/// <param name="Profile">The aggregate.</param>
/// <param name="Window">The covered window, from <c>TapeCoverage</c>.</param>
public sealed record VolumeProfileRead(VolumeProfile Profile, CoveredTapeWindow Window);
