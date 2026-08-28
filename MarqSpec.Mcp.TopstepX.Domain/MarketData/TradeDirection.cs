namespace MarqSpec.Mcp.TopstepX.Domain.MarketData;

/// <summary>Which side of the tape produced a print.</summary>
/// <remarks>
/// <para>
/// Zero is <see cref="Unknown"/>, and unlike a store enum that refuses zero it <b>is</b> a real value.
/// An unstated or unparseable venue direction must remain missing rather than silently become a buy
/// (<c>TradeLogType.Buy = 0</c> is the trap this exists to survive — gh#213, gh#220).
/// </para>
/// <para>
/// Lives here, not on the store entity, so the aggregator and the row share one closed vocabulary.
/// Domain cannot reference Data; two enums with the same names would be two vocabularies that can drift.
/// </para>
/// </remarks>
public enum TradeDirection
{
    /// <summary>The direction could not be determined. Stored, never defaulted to a side.</summary>
    Unknown = 0,

    /// <summary>The aggressor was lifting (buying).</summary>
    Buy = 1,

    /// <summary>The aggressor was hitting (selling).</summary>
    Sell = 2,
}
