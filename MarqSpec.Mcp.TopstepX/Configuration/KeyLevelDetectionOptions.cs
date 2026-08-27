using System.ComponentModel.DataAnnotations;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// How this server detects key levels when a call does not say otherwise.
/// </summary>
/// <remarks>
/// <para>
/// <b>These are defaults, not the whole story: three of the seven are also per-call arguments on
/// <c>get_key_levels</c>.</b> The tool's <c>pivotSource</c>, <c>pivotLookback</c> and
/// <c>pivotRightLookback</c> override what is set here for one request; <see cref="ZoneAtrMultiple"/>,
/// <see cref="MinSignificance"/>, <see cref="MaxZoneWidthPercent"/> and <see cref="MaxLevels"/> are settable
/// only here, so every level this server reports is sized, filtered and capped the same way and two of them
/// can be compared.
/// </para>
/// <para>
/// <b>The two lookbacks move together because they describe one window.</b> Exposing the left edge per call
/// and not the right would let a caller narrow the history a pivot must clear while still, invisibly,
/// requiring the shipped fifteen bars of confirmation — control the argument appears to give and does not.
/// </para>
/// <para>
/// <b>Per-call detection parameters are safe here, and the reason is a recorded decision rather than a
/// property of this class.</b> Levels are computed on read and nothing stores one (ADR-0013), so there is no
/// storage key for a parameter to fall out of — and since gh#276 there is no level table either, the empty
/// one having been dropped. The reason <see cref="IndicatorOptions"/> refuses the same freedom — ADR-0006,
/// where the storage key is <c>(Indicator, Period)</c> and a parameter the key cannot see makes two
/// parameterisations indistinguishable once written — does not reach this class, and ADR-0013 says so
/// explicitly. It also names the condition that would bring it back: the moment anything stores a level,
/// every field below becomes part of that level's identity.
/// </para>
/// </remarks>
public sealed class KeyLevelDetectionOptions : IValidatableObject
{
    /// <summary>The configuration section this binds to.</summary>
    public const string SectionName = "KeyLevels";

    /// <summary>
    /// How many bars <b>to its left</b> a pivot must dominate. Larger means fewer, more structural levels.
    /// </summary>
    /// <remarks>
    /// Twenty by default, and it was five until gh#245 adopted Bjorgum's <i>Key Levels</i> calibration whole.
    /// A window needs <c>this + <see cref="PivotRightLookback"/> + 1</c> bars before it can hold a single
    /// pivot.
    /// </remarks>
    [Range(1, 1_000)]
    public int PivotLookback { get; init; } = 20;

    /// <summary>
    /// How many bars <b>to its right</b> a pivot must dominate — the confirmation window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Fifteen by default: shorter than the left window, because the two sides are asked different
    /// questions. The left window asks how much history a level has stood clear of. The right window only
    /// has to establish that the extreme held, and every bar it waits for is a bar the level is reported
    /// late by — the last <see cref="PivotRightLookback"/> bars of any series can never produce a pivot.
    /// </para>
    /// <para>
    /// <b>Zero is refused rather than allowed as "no confirmation".</b> <c>R-3.4</c> is that detection never
    /// reports a pivot the later bars have not confirmed: a candidate judged only against what came before
    /// it repaints the moment the next bar arrives, and it repaints into a level an agent has already been
    /// shown. The floor is one, enforced by the range below and again in <c>Domain</c>.
    /// </para>
    /// </remarks>
    [Range(1, 1_000)]
    public int PivotRightLookback { get; init; } = 15;

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
    /// The widest a reported zone may be, as a percentage of its own midpoint price. 2.5 by default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A backstop on the merge, not a calibration knob — and it exists because gh#245 made merging
    /// cross-kind.</b> Before that, a support could only ever absorb another support: the two kinds were
    /// merged in separate groups, so an overlapping support and resistance ended the chain by construction.
    /// They no longer do, and a chain that used to stop at a polarity change now continues through it. Every
    /// other stage bounds a zone's width — a pre-merge zone is exactly <see cref="ZoneAtrMultiple"/> × ATR
    /// wide — so the merge is the only stage that can widen one without limit, and this is the only cap
    /// downstream of it.
    /// </para>
    /// <para>
    /// <b>Deliberately loose.</b> 2.5% of ES at 5,000 is a 125-point band, which is wider than most whole
    /// sessions: a zone that wide is a merge that has swallowed the range rather than a level anyone can
    /// trade against. It is set where it catches that and nothing else, because a cap that fires on ordinary
    /// structure deletes levels silently — the same failure <see cref="MinSignificance"/> has, and an empty
    /// level set reads as a market with no structure. An operator who wants it to bite tightens it.
    /// </para>
    /// </remarks>
    [Range(typeof(decimal), "0.01", "100", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public decimal MaxZoneWidthPercent { get; init; } = 2.5m;

    /// <summary>
    /// The most levels one detection may report. Twelve by default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The cap is on the answer, not on the work</b> — detection runs over every bar either way, which
    /// ADR-0013 priced at <b>0.51 ms</b> over the tool's 500-bar window at a lookback of 20. (That record's
    /// headline 0.21 ms is the figure for a lookback of <b>5</b>, which was the shipped value when it was
    /// written and is not the shipped value now.) What the cap bounds is how much an agent is handed: the
    /// same measurement found <b>44 zones</b> over 10,000 bars at a lookback of 20, and a wall of
    /// forty-four levels is a list nobody ranks before acting on it.
    /// </para>
    /// <para>
    /// <b>The levels kept are the most significant ones, and the rest are absent rather than summarised.</b>
    /// Significance is prominence in ATR multiples, which is the one score this server already treats as
    /// comparable across instruments and regimes (<c>R-3.2</c>), so it is the ranking that means the same thing
    /// on ES and on NQ. <c>maxLevels</c> is reported beside the answer, so a caller holding exactly this
    /// many levels can tell a capped list from a complete one.
    /// </para>
    /// <para>
    /// <b>Zero is refused.</b> A cap of zero empties every level set the server can produce, and an empty
    /// level set is indistinguishable from a market that has produced no structure — a conclusion an agent
    /// acts on. The floor is one, here and again in <c>Domain</c>.
    /// </para>
    /// </remarks>
    [Range(1, 1_000)]
    public int MaxLevels { get; init; } = 12;

    /// <summary>
    /// These options as the detection record <c>Domain</c> takes.
    /// </summary>
    /// <returns>The configured defaults.</returns>
    public KeyLevelOptions Defaults() =>
        new(
            PivotLookback,
            Source,
            ZoneAtrMultiple,
            MinSignificance,
            PivotRightLookback,
            MaxZoneWidthPercent,
            MaxLevels);

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
