namespace MarqSpec.Mcp.TopstepX.Domain.MarketData;

/// <summary>
/// A named way of detecting price levels — swing pivots, session extremes, or a pivot-family formula.
/// </summary>
/// <remarks>
/// <para>
/// <b>The name is identity, exactly as <see cref="IIndicator.Name"/> is</b> — a caller asks for a method by
/// it, and a score built from several methods names its constituents by it. The mechanism differs and it is
/// worth being exact about: an indicator's name is a <i>storage</i> key, so renaming one orphans rows.
/// Levels are computed on read and nothing stores one — measured and decided in ADR-0013, not merely the way
/// it happens to be, and since gh#276 there is no level table left to store into — so renaming one of these
/// orphans no rows: it silently changes what an existing request means, and what a recorded score was a score
/// of. Stable, lowercase, for the same reason arrived at down a different road.
/// </para>
/// <para>
/// <b>Every implementation must refuse a series that spans a contract roll</b>, by calling
/// <see cref="IndicatorGuard.RequireSingleContract"/> — directly, or through whatever it delegates its
/// detection to. This is a rule each implementation satisfies rather than one it inherits: an indicator is a
/// projection over one shared compute path, whereas each method here detects its own way, so there is no
/// single place to put the check. A method that skips it does not fail — it answers with an ordinary-looking
/// level built across the seam between two quarters, at a price neither contract has ever traded. That is
/// the silent loss of <c>R-3.5</c> the sweep in <c>LevelMethodCatalogRollTests</c> exists to catch, and that
/// sweep is the enforcement; this paragraph is only its statement.
/// </para>
/// <para>
/// <b>A method is a pure function of what it is handed</b>, on the same terms as <see cref="IIndicator"/>
/// (ADR-0006): no clock, no store, no configuration singleton. A level an agent was shown must be
/// reproducible from the bars that were on hand, or two people comparing notes are comparing different
/// numbers without either being wrong.
/// </para>
/// <para>
/// <b>If your method needs a session boundary, take it in your constructor. Do not widen
/// <see cref="Detect"/>.</b> This was settled on gh#257, when <c>session</c> became the first method whose
/// answer depends on something the bars cannot supply, and <c>pivot-*</c> inherits it whole — "the prior
/// period" is the same boundary. <see cref="BarSessionCalendar"/> is a <i>value</i>: one instance for the
/// whole server, parsed once at startup from <c>MarketData__SessionCloseCentral</c> and
/// <c>MarketData__Holidays</c>, deterministic in its configuration and fixed for the life of the process.
/// Holding one is therefore not the "configuration singleton" the paragraph above forbids — it is the same
/// arrangement <see cref="VwapIndicator"/> has lived under since VWAP existed, where
/// <see cref="IIndicator.Compute"/> also takes no calendar and <c>IndicatorCatalog</c> supplies one at
/// construction.
/// </para>
/// <para>
/// The three alternatives were weighed and are recorded here so they are not re-opened by guess.
/// <b>Widening this interface</b> costs every implementation a parameter only one of them reads, and — since
/// a parameter list and its call sites live in different files — an arity change merges clean and breaks the
/// build. <b>Widening <c>KeyLevelOptions</c></b> to carry session boundaries as values turns "detection
/// tuning" into an assembly job every caller of every method performs for one method's benefit, and puts a
/// second definition of a session next to <see cref="BarSessionCalendar"/>. <b>Deriving boundaries from the
/// bars</b> infers a session close from gaps, which produces a plausible boundary from a thin overnight —
/// a well-formed number no trade produced (gh#213). Constructor injection costs one parameter on the
/// catalogue and nothing on this interface, and <b>keeps every method inside
/// <c>LevelMethodCatalog.All</c></b>, which is what the roll and ordering sweeps iterate: a method that
/// could not be constructed without an instrument would leave those sweeps and lose <c>R-3.5</c> without
/// failing.
/// </para>
/// </remarks>
public interface ILevelMethod
{
    /// <summary>The method name — lowercase and stable, e.g. <c>swing</c>.</summary>
    string Name { get; }

    /// <summary>
    /// The correlation family this method belongs to — lowercase and stable, e.g. <c>pivot</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Methods that share a family share a budget in a confluence score.</b> The five <c>pivot-*</c>
    /// methods are arithmetic on one prior session's open, high, low and close, so all five landing on a
    /// price is one input transformed five ways rather than five independent confirmations — and a score
    /// that counted it as 5/5 would be at its most confident exactly where a reader would most want to trust
    /// it (gh#232). The weighting that discounts them is gh#259's; what this property does is make the
    /// grouping <b>a property of the catalogue</b> rather than a hardcoded list of five names, which the
    /// sixth variant would silently escape.
    /// </para>
    /// <para>
    /// <b>It has no default, and that is the decision rather than an omission.</b> A default of
    /// <see cref="Name"/> — "a method is its own family unless it says otherwise" — reads well and fails in
    /// the one direction that matters: a new variant that forgot to declare one would be given a budget of
    /// its own and counted as independent evidence for the very thing it is derived from. Declaring it is
    /// one line; forgetting it must not be silent.
    /// </para>
    /// <para>
    /// A method with no correlated siblings is a family of one and returns its own name — <c>swing</c> and
    /// <c>session</c> both do — so the weighting needs no special case for "uncorrelated".
    /// </para>
    /// </remarks>
    string Family { get; }

    /// <summary>
    /// Detects the levels this method finds in a series.
    /// </summary>
    /// <param name="bars">The series, in <b>strictly ascending</b> time order and from one contract.</param>
    /// <param name="atr">
    /// ATR aligned one-to-one with <paramref name="bars"/>; nulls mean no scale is available at that bar.
    /// Zones are sized and scored in ATR multiples so that a width means the same thing across instruments,
    /// and a bar with no ATR yields no level rather than a level scaled by a substituted number.
    /// </param>
    /// <param name="options">Detection options.</param>
    /// <returns>
    /// The zones, ordered by price. Every method returns <see cref="KeyLevelZone"/>, so
    /// <see cref="KeyLevels.MergeOverlapping"/> and <see cref="KeyLevels.ApplyClose"/> stay the shared
    /// carriers of <c>R-3.1</c> and <c>R-3.3</c> however many methods there eventually are.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// The bars are not strictly ascending, they span a contract roll, or the ATR series is not aligned with
    /// them.
    /// </exception>
    IReadOnlyList<KeyLevelZone> Detect(
        IReadOnlyList<Bar> bars,
        IReadOnlyList<decimal?> atr,
        KeyLevelOptions options);
}
