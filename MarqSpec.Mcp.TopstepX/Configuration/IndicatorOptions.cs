using System.ComponentModel.DataAnnotations;
using System.Globalization;

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
/// <b>The period is not one of those parameters</b> — the key names it in a column — which is why the lists
/// below are read as a SELECTION set rather than as ad-hoc computation inputs (ADR-0018).
/// </para>
/// <para>
/// <b>Additional periods are additive, and the singular key stays primary.</b> Each indicator above keeps its
/// existing singular <c>Indicators__*Period</c> key as the PRIMARY period a caller gets by leaving
/// <c>period</c> unset. An operator may also list ADDITIONAL periods per indicator as a comma-separated
/// string — the same string-not-array shape as <see cref="MarketDataOptions.Instruments"/> and
/// <see cref="MarketDataOptions.Holidays"/>, for the same reason: compose's <c>${VAR:-}</c> passthrough cannot
/// express an array. A caller then SELECTS among the configured periods with <c>period</c> on
/// <c>get_indicators</c> / <c>get_indicator_at</c>; the primary is always one of the choices, and the *Periods()
/// accessors below return it first so a caller enumerating "what is configured" sees it named as such.
/// </para>
/// </remarks>
public sealed class IndicatorOptions : IValidatableObject
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

    /// <summary>
    /// The rolling-VWAP window — the PRIMARY period of the <c>vwap-rolling</c> indicator, a rolling VWAP over
    /// the trailing <c>period</c> bars. Different from the session-anchored <c>vwap</c>, which refuses a
    /// period altogether.
    /// </summary>
    [Range(1, 1_000)]
    public int RollingVwapPeriod { get; init; } = 20;

    /// <summary>Additional ATR periods, comma-separated. Empty when unset.</summary>
    public string? AdditionalAtrPeriods { get; init; }

    /// <summary>Additional RSI periods, comma-separated. Empty when unset.</summary>
    public string? AdditionalRsiPeriods { get; init; }

    /// <summary>Additional SMA periods, comma-separated. Empty when unset.</summary>
    public string? AdditionalSmaPeriods { get; init; }

    /// <summary>Additional EMA periods, comma-separated. Empty when unset.</summary>
    public string? AdditionalEmaPeriods { get; init; }

    /// <summary>
    /// Additional MACD slow lengths, comma-separated. Empty when unset. Each must exceed the fixed fast
    /// length of 12, the same floor <see cref="MacdSlowPeriod"/> carries.
    /// </summary>
    public string? AdditionalMacdSlowPeriods { get; init; }

    /// <summary>Additional Bollinger windows, comma-separated. Empty when unset.</summary>
    public string? AdditionalBollingerPeriods { get; init; }

    /// <summary>Additional rolling-VWAP windows, comma-separated. Empty when unset.</summary>
    public string? AdditionalRollingVwapPeriods { get; init; }

    /// <summary>The configured ATR periods: <see cref="AtrPeriod"/> first, then the additional ones, in order.</summary>
    /// <returns>The configured periods.</returns>
    /// <exception cref="InvalidOperationException">
    /// The list fails a rule <see cref="Validate"/> would already have refused at startup. Callers only reach
    /// this after validation passes, so this is a defensive backstop rather than an expected path.
    /// </exception>
    public IReadOnlyList<int> AtrPeriods() =>
        BuildPeriods(nameof(AdditionalAtrPeriods), AdditionalAtrPeriods, AtrPeriod, 1, 1_000);

    /// <summary>The configured RSI periods: <see cref="RsiPeriod"/> first, then the additional ones, in order.</summary>
    /// <returns>The configured periods.</returns>
    /// <exception cref="InvalidOperationException">The list fails a rule <see cref="Validate"/> would refuse.</exception>
    public IReadOnlyList<int> RsiPeriods() =>
        BuildPeriods(nameof(AdditionalRsiPeriods), AdditionalRsiPeriods, RsiPeriod, 1, 1_000);

    /// <summary>The configured SMA periods: <see cref="SmaPeriod"/> first, then the additional ones, in order.</summary>
    /// <returns>The configured periods.</returns>
    /// <exception cref="InvalidOperationException">The list fails a rule <see cref="Validate"/> would refuse.</exception>
    public IReadOnlyList<int> SmaPeriods() =>
        BuildPeriods(nameof(AdditionalSmaPeriods), AdditionalSmaPeriods, SmaPeriod, 1, 1_000);

    /// <summary>The configured EMA periods: <see cref="EmaPeriod"/> first, then the additional ones, in order.</summary>
    /// <returns>The configured periods.</returns>
    /// <exception cref="InvalidOperationException">The list fails a rule <see cref="Validate"/> would refuse.</exception>
    public IReadOnlyList<int> EmaPeriods() =>
        BuildPeriods(nameof(AdditionalEmaPeriods), AdditionalEmaPeriods, EmaPeriod, 1, 1_000);

    /// <summary>
    /// The configured MACD slow lengths: <see cref="MacdSlowPeriod"/> first, then the additional ones, in
    /// order.
    /// </summary>
    /// <returns>The configured periods.</returns>
    /// <exception cref="InvalidOperationException">The list fails a rule <see cref="Validate"/> would refuse.</exception>
    public IReadOnlyList<int> MacdSlowPeriods() =>
        BuildPeriods(nameof(AdditionalMacdSlowPeriods), AdditionalMacdSlowPeriods, MacdSlowPeriod, 13, 1_000);

    /// <summary>
    /// The configured Bollinger windows: <see cref="BollingerPeriod"/> first, then the additional ones, in
    /// order.
    /// </summary>
    /// <returns>The configured periods.</returns>
    /// <exception cref="InvalidOperationException">The list fails a rule <see cref="Validate"/> would refuse.</exception>
    public IReadOnlyList<int> BollingerPeriods() =>
        BuildPeriods(nameof(AdditionalBollingerPeriods), AdditionalBollingerPeriods, BollingerPeriod, 2, 1_000);

    /// <summary>
    /// The configured rolling-VWAP windows: <see cref="RollingVwapPeriod"/> first, then the additional ones,
    /// in order.
    /// </summary>
    /// <returns>The configured periods.</returns>
    /// <exception cref="InvalidOperationException">The list fails a rule <see cref="Validate"/> would refuse.</exception>
    public IReadOnlyList<int> RollingVwapPeriods() =>
        BuildPeriods(nameof(AdditionalRollingVwapPeriods), AdditionalRollingVwapPeriods, RollingVwapPeriod, 1, 1_000);

    /// <summary>
    /// Refuses an additional-period list that does not parse, falls outside range, repeats a value, or
    /// repeats the indicator's primary.
    /// </summary>
    /// <param name="validationContext">The validation context.</param>
    /// <returns>One result per offending list.</returns>
    /// <remarks>
    /// <c>ValidateDataAnnotations</c> runs this and <c>ValidateOnStart</c> makes it a boot failure — the same
    /// arrangement <see cref="KeyLevelDetectionOptions.Validate"/> uses, and for the same reason: a bad list
    /// here would otherwise surface only the first time something reads it, mid-request.
    /// </remarks>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach ((string propertyName, string? raw, int primary, int min, int max) in PeriodListSpecs())
        {
            if (!TryParseList(raw, min, max, primary, propertyName, out _, out ValidationResult? error))
            {
                yield return error!;
            }
        }
    }

    /// <summary>Every additional-period list this class validates, paired with its rule inputs.</summary>
    /// <returns>The list specs, in the order they are declared above.</returns>
    private IEnumerable<(string PropertyName, string? Raw, int Primary, int Min, int Max)> PeriodListSpecs()
    {
        yield return (nameof(AdditionalAtrPeriods), AdditionalAtrPeriods, AtrPeriod, 1, 1_000);
        yield return (nameof(AdditionalRsiPeriods), AdditionalRsiPeriods, RsiPeriod, 1, 1_000);
        yield return (nameof(AdditionalSmaPeriods), AdditionalSmaPeriods, SmaPeriod, 1, 1_000);
        yield return (nameof(AdditionalEmaPeriods), AdditionalEmaPeriods, EmaPeriod, 1, 1_000);
        yield return (nameof(AdditionalMacdSlowPeriods), AdditionalMacdSlowPeriods, MacdSlowPeriod, 13, 1_000);
        yield return (nameof(AdditionalBollingerPeriods), AdditionalBollingerPeriods, BollingerPeriod, 2, 1_000);
        yield return (
            nameof(AdditionalRollingVwapPeriods), AdditionalRollingVwapPeriods, RollingVwapPeriod, 1, 1_000);
    }

    /// <summary>The primary period followed by a validated additional list, or a thrown explanation.</summary>
    /// <param name="propertyName">The additional-list property's own name, for the exception message.</param>
    /// <param name="raw">The configured additional list.</param>
    /// <param name="primary">The indicator's primary period.</param>
    /// <param name="min">The smallest period this indicator accepts.</param>
    /// <param name="max">The largest period this indicator accepts.</param>
    /// <returns>The primary period, then the additional ones in configured order.</returns>
    private static IReadOnlyList<int> BuildPeriods(string propertyName, string? raw, int primary, int min, int max)
    {
        if (!TryParseList(raw, min, max, primary, propertyName, out IReadOnlyList<int> additional, out ValidationResult? error))
        {
            throw new InvalidOperationException(error!.ErrorMessage);
        }

        return [primary, .. additional];
    }

    /// <summary>Parses and validates one comma-separated additional-period list.</summary>
    /// <param name="raw">The configured list, or <see langword="null"/>/empty/whitespace for none.</param>
    /// <param name="min">The smallest period this indicator accepts.</param>
    /// <param name="max">The largest period this indicator accepts.</param>
    /// <param name="primary">The indicator's primary period — repeating it in the list is refused.</param>
    /// <param name="propertyName">The property's own name, used both as the <see cref="ValidationResult"/>
    /// member name and, prefixed with <see cref="SectionName"/>, in the message an operator reads.</param>
    /// <param name="parsed">The parsed periods, in configured order, when this returns <see langword="true"/>.</param>
    /// <param name="error">The refusal, when this returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="raw"/> is empty or every token is valid.</returns>
    private static bool TryParseList(
        string? raw,
        int min,
        int max,
        int primary,
        string propertyName,
        out IReadOnlyList<int> parsed,
        out ValidationResult? error)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            parsed = [];
            error = null;
            return true;
        }

        string key = SectionName + "__" + propertyName;
        List<int> values = [];
        HashSet<int> seen = [];

        foreach (string token in raw.Split(',', StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            {
                parsed = [];
                error = new ValidationResult(
                    key + " has '" + token + "', which is not a whole number. Use a comma-separated list of "
                    + "integers, e.g. \"10,13,200\", or remove the key to configure no additional periods.",
                    [propertyName]);
                return false;
            }

            if (value < min || value > max)
            {
                parsed = [];
                error = new ValidationResult(
                    key + " has " + value.ToString(CultureInfo.InvariantCulture) + ", which is outside the "
                    + "allowed range " + min.ToString(CultureInfo.InvariantCulture) + ".."
                    + max.ToString(CultureInfo.InvariantCulture) + ". Remove it or bring it into range.",
                    [propertyName]);
                return false;
            }

            if (!seen.Add(value))
            {
                parsed = [];
                error = new ValidationResult(
                    key + " has " + value.ToString(CultureInfo.InvariantCulture) + " more than once. Each "
                    + "period may appear only once in the list.",
                    [propertyName]);
                return false;
            }

            if (value == primary)
            {
                parsed = [];
                error = new ValidationResult(
                    key + " has " + value.ToString(CultureInfo.InvariantCulture) + ", which is already the "
                    + "primary period. Remove it from the additional list — the primary is always included.",
                    [propertyName]);
                return false;
            }

            values.Add(value);
        }

        parsed = values;
        error = null;
        return true;
    }
}
