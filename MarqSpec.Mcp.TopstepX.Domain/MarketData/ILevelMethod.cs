namespace MarqSpec.Mcp.TopstepX.Domain.MarketData;

/// <summary>
/// A named way of detecting price levels — swing pivots today, session extremes and pivot-family arithmetic
/// later.
/// </summary>
/// <remarks>
/// <para>
/// <b>The name is identity, exactly as <see cref="IIndicator.Name"/> is</b> — a caller asks for a method by
/// it, and a score built from several methods names its constituents by it. The mechanism differs and it is
/// worth being exact about: an indicator's name is a <i>storage</i> key, so renaming one orphans rows.
/// Levels are computed on read and nothing has ever written the <c>PriceLevels</c> table, so renaming one of
/// these orphans no rows — it silently changes what an existing request means, and what a recorded score was
/// a score of. Stable, lowercase, for the same reason arrived at down a different road.
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
/// </remarks>
public interface ILevelMethod
{
    /// <summary>The method name — lowercase and stable, e.g. <c>swing</c>.</summary>
    string Name { get; }

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
