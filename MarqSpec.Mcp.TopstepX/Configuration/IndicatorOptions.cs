using System.ComponentModel.DataAnnotations;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// The periods the indicator projection computes at.
/// </summary>
/// <remarks>
/// <para>
/// <b>Periods only.</b> MACD's fast and signal lengths (12 and 9) and Bollinger's width (2σ) are deliberately
/// absent from this class and fixed in the calculations.
/// </para>
/// <para>
/// The reason is the storage key, which is <c>(Indicator, Period)</c>. A parameter the key cannot see would let
/// two parameterisations be written under one key, where they become indistinguishable — a chart would show
/// them spliced together with no seam visible anywhere. If a configurable fast length is ever wanted, it goes
/// in the indicator's <i>name</i>, and ADR-0006 is superseded rather than quietly reinterpreted.
/// </para>
/// </remarks>
public sealed class IndicatorOptions
{
    /// <summary>The configuration section this binds to.</summary>
    public const string SectionName = "Indicators";

    /// <summary>The ATR period. Wilder's default is 14.</summary>
    [Range(1, 1_000)]
    public int AtrPeriod { get; init; } = 14;

    /// <summary>The RSI period. Wilder's default is 14.</summary>
    [Range(1, 1_000)]
    public int RsiPeriod { get; init; } = 14;

    /// <summary>The simple moving average window.</summary>
    [Range(1, 1_000)]
    public int SmaPeriod { get; init; } = 20;

    /// <summary>The exponential moving average window.</summary>
    [Range(1, 1_000)]
    public int EmaPeriod { get; init; } = 20;

    /// <summary>MACD's slow EMA length. Must exceed the fixed fast length of 12.</summary>
    [Range(13, 1_000)]
    public int MacdSlowPeriod { get; init; } = 26;

    /// <summary>The Bollinger window.</summary>
    [Range(2, 1_000)]
    public int BollingerPeriod { get; init; } = 20;
}
