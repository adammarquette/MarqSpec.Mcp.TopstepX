using System.ComponentModel.DataAnnotations;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// How this server detects key levels when a call does not say otherwise.
/// </summary>
/// <remarks>
/// <para>
/// <b>These are defaults, not the whole story: two of the four are also per-call arguments on
/// <c>get_key_levels</c>.</b> The tool's <c>pivotSource</c> and <c>pivotLookback</c> override what is set
/// here for one request; <see cref="ZoneAtrMultiple"/> and <see cref="MinSignificance"/> are settable only
/// here, so every level this server reports is sized and filtered the same way and two of them can be
/// compared.
/// </para>
/// <para>
/// <b>Per-call detection parameters are safe here, and the reason is a recorded decision rather than a
/// property of this class.</b> Levels are computed on read and nothing writes <c>PriceLevels</c>
/// (ADR-0013), so there is no storage key for a parameter to fall out of. The reason
/// <see cref="IndicatorOptions"/> refuses the same freedom — ADR-0006, where the storage key is
/// <c>(Indicator, Period)</c> and a parameter the key cannot see makes two parameterisations
/// indistinguishable once written — does not reach this class, and ADR-0013 says so explicitly. It also
/// names the condition that would bring it back: the moment anything writes <c>PriceLevels</c>, every field
/// below becomes part of a level's identity.
/// </para>
/// </remarks>
public sealed class KeyLevelDetectionOptions : IValidatableObject
{
    /// <summary>The configuration section this binds to.</summary>
    public const string SectionName = "KeyLevels";

    /// <summary>
    /// How many bars either side a pivot must dominate. Larger means fewer, more structural levels.
    /// </summary>
    /// <remarks>
    /// Five by default. A pivot must dominate this many bars on <b>both</b> sides, so a window needs
    /// <c>2 × this + 1</c> bars before it can hold a single one, and the last few bars of any series can
    /// never produce one — a pivot confirmed only by the bars before it repaints as soon as the next arrives.
    /// </remarks>
    [Range(1, 1_000)]
    public int PivotLookback { get; init; } = 5;

    /// <summary>
    /// Which price on a bar a pivot is measured from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The Heikin-Ashi body, and the reason is the reason it has always been:</b> it smooths single-bar
    /// noise into structure. The raw body and the raw high/low are both reachable —
    /// <see cref="PivotSource.Body"/> and <see cref="PivotSource.HighLow"/> — and neither is a better default
    /// on intraday futures bars, where a single spike would otherwise anchor a level nobody traded twice.
    /// </para>
    /// <para>
    /// <see cref="PivotSource.Unknown"/> is refused at startup by <see cref="Validate"/> rather than
    /// tolerated as "unset". It is what a missing or mistyped value binds to, so honouring it would pick a
    /// price series by accident — which is the whole reason the enum reserves zero for it.
    /// </para>
    /// </remarks>
    public PivotSource Source { get; init; } = PivotSource.HeikinAshiBody;

    /// <summary>
    /// A zone's full width, in ATR multiples. Half an ATR by default.
    /// </summary>
    /// <remarks>
    /// <b>Not per-call, deliberately.</b> The width is what makes "the same level" mean the same thing to two
    /// readers, and gh#232's confluence score compares zones across methods — a width that moved per request
    /// would make two scores incomparable without either being wrong, and the score would have to report the
    /// width to be reproducible at all. One server-wide value keeps that question closed.
    /// </remarks>
    [Range(typeof(decimal), "0.01", "100", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public decimal ZoneAtrMultiple { get; init; } = 0.5m;

    /// <summary>
    /// The smallest prominence, in ATR multiples, worth reporting. Half an ATR by default.
    /// </summary>
    /// <remarks>
    /// <b>Not per-call, and this is the one that most needed saying no to.</b> It is the noise floor: turn it
    /// down and a wall of insignificant pivots arrives looking like structure, turn it up and an empty level
    /// set arrives looking like a market that has none. Both are answers a caller acts on, and neither says
    /// anywhere that the floor moved. ADR-0013 measured the shape of it — on a 10,000-bar series a floor of
    /// 0.25 produced 154 zones and 1.5 produced none, against 56 at this default.
    /// </remarks>
    [Range(typeof(decimal), "0", "100", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public decimal MinSignificance { get; init; } = 0.5m;

    /// <summary>
    /// These options as the detection record <c>Domain</c> takes.
    /// </summary>
    /// <returns>The configured defaults.</returns>
    public KeyLevelOptions Defaults() =>
        new(PivotLookback, Source, ZoneAtrMultiple, MinSignificance);

    /// <summary>
    /// Refuses a source outside the vocabulary — including the <c>Unknown</c> an unset value binds to.
    /// </summary>
    /// <param name="validationContext">The validation context.</param>
    /// <returns>One result when the configured source is not servable, otherwise none.</returns>
    /// <remarks>
    /// <b>On the type rather than in a <c>.Validate(...)</c> lambda at the composition root</b>, so the rule
    /// travels with the value and a second place that binds this section cannot bind it unchecked.
    /// <c>ValidateDataAnnotations</c> runs this, and <c>ValidateOnStart</c> makes it a boot failure — which
    /// is the right place for it to be loud, because the alternative is a server that answers every
    /// <c>get_key_levels</c> call from a source nobody chose.
    /// </remarks>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!PivotSources.IsServable(Source))
        {
            yield return new ValidationResult(
                SectionName + "__Source is '" + Source + "', which is not a pivot source. Known sources: "
                + PivotSources.KnownNames + ". Unknown is what an unset or mistyped value binds to, so it is "
                + "refused rather than honoured — a zero default would pick a price series by accident.",
                [nameof(Source)]);
        }
    }
}
