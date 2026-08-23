namespace MarqSpec.Mcp.TopstepX.Domain.MarketData;

/// <summary>
/// A bar-derived indicator — a named, period-parameterised projection over an OHLCV series.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="Compute"/> is a <b>pure function of the bars handed in</b> — no clock, no storage, no state.
/// That is what keeps "rebuild = replay" true (ADR-0006): recomputing over the same stored series reproduces
/// the same values exactly, so a stored indicator is a cache of a derivation rather than a fact of its own.
/// An implementation that read the clock would make a value depend on when the projection happened to run.
/// </para>
/// <para>
/// <b>Period is part of the identity, so it lives on the instance.</b> An ATR(14) and an ATR(3) are different
/// numbers stored under different keys, and one configured instance maps to exactly one
/// <c>(Indicator, Period)</c> family of rows. A multi-output indicator — MACD's line, signal and histogram —
/// is expressed as several single-value indicators with distinct <see cref="Name"/>s, so this single-value
/// contract admits them without a reshape.
/// </para>
/// </remarks>
public interface IIndicator
{
    /// <summary>
    /// The stored indicator name — row identity, lowercase and stable, e.g. <c>atr</c>, <c>rsi</c>.
    /// </summary>
    /// <remarks>
    /// Stable because it is a storage key: renaming one orphans every row already written under the old name,
    /// and the orphans read back as an absence rather than an error.
    /// </remarks>
    string Name { get; }

    /// <summary>The period parameter that is part of a value's identity. Wilder's default is 14.</summary>
    int Period { get; }

    /// <summary>
    /// The minimum number of bars needed before this indicator produces its first value.
    /// </summary>
    /// <remarks>
    /// A caller loading a window for projection must reach back at least this far <i>before</i> the window, or
    /// the leading values come back null and the series looks like it has a hole.
    /// </remarks>
    int WarmupBars { get; }

    /// <summary>
    /// Computes the indicator for each bar, aligned one-to-one with <paramref name="bars"/>.
    /// </summary>
    /// <param name="bars">The series, in <b>strictly ascending</b> time order.</param>
    /// <returns>
    /// One entry per bar; <see langword="null"/> until the period is satisfied. No value is deliberate and
    /// better than a partial one — a half-warmed indicator looks ordinary and would mislead whatever reads it.
    /// </returns>
    /// <exception cref="ArgumentException">The bars are not in strictly ascending time order.</exception>
    IReadOnlyList<decimal?> Compute(IReadOnlyList<Bar> bars);
}
