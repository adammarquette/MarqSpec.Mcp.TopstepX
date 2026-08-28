namespace MarqSpec.Mcp.TopstepX.Domain.MarketData;

/// <summary>Which published pivot formula a set of levels is computed with.</summary>
/// <remarks>
/// Five names rather than one parameterised entry, following the precedent MACD set: a caller asks for
/// <c>pivot-camarilla</c>, not for <c>pivot</c> with a variant argument, and a score naming its constituents
/// names the one that contributed. <see cref="Unknown"/> is never valid, on the same terms as
/// <see cref="PivotSource.Unknown"/> — a zero default here would answer with somebody's pivot set and never
/// say whose.
/// </remarks>
public enum PivotFormula
{
    /// <summary>Unset. Never a valid value — a zero default would pick a formula by accident.</summary>
    Unknown = 0,

    /// <summary>Floor-trader pivots: <c>P</c> and three levels either side of it.</summary>
    Classic = 1,

    /// <summary>The classic pivot with the legs at 0.382, 0.618 and 1.000 of the period's range.</summary>
    Fibonacci = 2,

    /// <summary>Four levels either side of the period's <b>close</b>, at <c>1.1 / n</c> of its range.</summary>
    Camarilla = 3,

    /// <summary>The pivot with the close weighted twice, and two levels either side of it.</summary>
    Woodie = 4,

    /// <summary>Tom DeMark's, which branches on where the period closed against where it opened.</summary>
    DeMark = 5,
}

/// <summary>
/// One finished period's four prices — everything the pivot family is computed from.
/// </summary>
/// <remarks>
/// <b>The open is here because one of the five reads it.</b> DeMark branches on the close against the open,
/// so a period carrying only the high, low and close would silently take the same branch forever. The other
/// four ignore it.
/// </remarks>
/// <param name="Open">The first price the period traded at.</param>
/// <param name="High">The highest price in the period.</param>
/// <param name="Low">The lowest price in the period.</param>
/// <param name="Close">The last price the period traded at.</param>
public sealed record PivotPeriod(decimal Open, decimal High, decimal Low, decimal Close)
{
    /// <summary>The period's range — <see cref="High"/> less <see cref="Low"/>.</summary>
    public decimal Range => High - Low;
}

/// <summary>
/// One computed pivot line, before it is given a width.
/// </summary>
/// <param name="Price">The computed price.</param>
/// <param name="Kind">
/// The side the formula itself names it on. Every published set labels its own legs <c>R</c> or <c>S</c>, and
/// that naming is the seed <see cref="KeyLevels.ApplyClose"/> keeps when the current price sits inside the
/// zone — which is the one place a formation's own reading is the honest one (<c>R-3.3</c>).
/// </param>
public sealed record PivotLine(decimal Price, KeyLevelKind Kind);

/// <summary>
/// The pivot family — five published formulas over one finished session's open, high, low and close.
/// </summary>
/// <remarks>
/// <para>
/// <b>All five are arithmetic on one prior period, and that is the whole family and both of its traps.</b>
/// The first is that they are <i>lines</i>, not ranges: a camarilla R3 is a single computed price with no
/// neighbours and no dominance, so it needs a width before it can be a zone and it needs something other
/// than prominence before it can carry a significance. The second is that five methods agreeing on a price
/// is one input transformed five ways rather than five confirmations, which is why every one of them
/// declares <see cref="PivotLevels.FamilyName"/> as its family (gh#232, gh#259).
/// </para>
/// <para>
/// <b>Significance is the period's own range in ATR multiples, exactly as <see cref="SessionLevels"/>
/// defines it</b> — and the definition is stated rather than inherited by resemblance, because
/// <c>R-3.2</c>'s prominence does not survive the trip. A swing pivot's significance is how far it stands
/// clear of its neighbours; a computed line has none. What the whole family <i>does</i> have in common is
/// the period it came from, and how far that period moved relative to how far the instrument moves is a
/// number that means the same thing across instruments. So it is one score shared by every line in a set,
/// and <see cref="KeyLevelOptions.MinSignificance"/> therefore keeps or drops the family whole. Choosing the
/// same definition <c>session</c> uses is not a convenience: gh#232's confluence weighs zones from several
/// methods against each other, and two significances that meant different things would make the comparison
/// meaningless without either being wrong.
/// </para>
/// <para>
/// <b>The line-to-zone tolerance is <see cref="KeyLevelOptions.ZoneAtrMultiple"/></b>, which is the value
/// gh#257 settled for <c>session</c> and this consumes rather than re-decides. The width that turns a swing
/// pivot into a zone is the width that turns a computed line into one, the tool already reports it as
/// <c>detection.zoneAtrMultiple</c>, and three cards choosing independently would have been three answers to
/// one question.
/// </para>
/// <para>
/// <b>Both caps are applied, and that is a decision rather than an omission.</b> gh#259 records that
/// <c>detection</c> reports <see cref="KeyLevelOptions.MaxZoneWidthPercent"/> and
/// <see cref="KeyLevelOptions.MaxLevels"/> as applied while only <c>swing</c> applies them. This family
/// hands its zones to <see cref="KeyLevels.ApplyWidthCap"/> and <see cref="KeyLevels.ApplyLevelCap"/> so it
/// is not the third instance. One consequence is visible from outside and is stated rather than glossed:
/// those carriers refuse the <i>whole</i> option set, so a <see cref="KeyLevelOptions.Lookback"/> or
/// <see cref="KeyLevelOptions.Source"/> this family never reads is nevertheless refused. It is refused
/// <i>up front</i> — the alternative is a refusal that fires only on the calls whose window happens to hold
/// a period, which is a refusal an operator meets late.
/// </para>
/// <para>
/// <b>A period this series cannot supply is absent, and there are three ways it cannot.</b> The window may
/// not reach the period's opening — <c>session</c>'s rule, unchanged, and the one that stops a prior high
/// being taken from half a prior session. The calendar may say the day did not trade. Or the series may
/// cover the period with a <b>single bar</b>: <see cref="Bar"/> records no width, so one bar carrying a
/// trade date is indistinguishable from a bar spanning that date and everything around it, and at a daily
/// resolution or above "the prior day's high" would be the high of whatever that bar covered. Two bars are
/// the least that shows the series samples the session more finely than the session itself. That last rule
/// is the deliberately conservative side of gh#259's first routed finding, where <c>session</c> refuses a
/// <i>partial</i> period but accepts an <i>over-wide</i> one silently: a genuine one-bar session is refused
/// here, and an absence is the failure this repository prefers to a well-formed number no period produced.
/// </para>
/// <para>
/// <b>Known bound, inherited from <see cref="SessionLevels"/> and stated on the same terms:</b> the coverage
/// rule checks where the window starts and how many bars carry the trade date, not that the period is
/// densely sampled throughout. A store with a hole in the middle of a prior session yields that session's
/// extremes over the bars it holds. Holes are <c>BarGapDetector</c>'s subject.
/// </para>
/// </remarks>
public static class PivotLevels
{
    /// <summary>The correlation family every one of the five declares.</summary>
    /// <remarks>
    /// A constant rather than five string literals, because the thing that must not drift is that they are
    /// the <b>same</b> string. A confluence score groups by it (gh#259), and a variant that spelled it
    /// differently would be counted as an independent confirmation of the period it is computed from.
    /// </remarks>
    public const string FamilyName = "pivot";

    /// <summary>The name a formula is registered and asked for under.</summary>
    /// <param name="formula">The formula.</param>
    /// <returns>The lowercase, stable method name.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The formula is unset or outside the vocabulary.</exception>
    /// <remarks>
    /// Written out rather than derived from the enum member's name. A name is a request vocabulary — what a
    /// caller asks for, and what a recorded score says it was a score of — so it must not move when a C#
    /// identifier does.
    /// </remarks>
    public static string NameOf(PivotFormula formula) => formula switch
    {
        PivotFormula.Classic => "pivot-classic",
        PivotFormula.Fibonacci => "pivot-fibonacci",
        PivotFormula.Camarilla => "pivot-camarilla",
        PivotFormula.Woodie => "pivot-woodie",
        PivotFormula.DeMark => "pivot-demark",
        _ => throw new ArgumentOutOfRangeException(
            nameof(formula),
            formula,
            "The pivot formula must be one of Classic, Fibonacci, Camarilla, Woodie, DeMark. Unknown is what "
            + "an unset value binds to, so it is refused rather than resolved to a default."),
    };

    /// <summary>
    /// The lines one formula computes from one finished period.
    /// </summary>
    /// <param name="formula">The formula.</param>
    /// <param name="period">The finished period.</param>
    /// <returns>The lines, in the order the published set states them.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="period"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The formula is unset or outside the vocabulary.</exception>
    /// <remarks>
    /// <para>
    /// Separated from <see cref="Compute"/> so the arithmetic can be checked against the published formula
    /// on its own, without a fixture, a calendar or a zone in the way. gh#232's trap 5 is that
    /// <c>KeyLevels</c> went two years with no hand-checked number in it.
    /// </para>
    /// <para>
    /// <b>Camarilla carries eight lines and no central pivot</b>, which is the published set: its legs are
    /// measured from the period's close rather than from a pivot, and adding <c>(H + L + C) / 3</c> to it
    /// would report another method's line under this one's name. <b>DeMark carries three</b>, which is also
    /// the published set — the formula produces a pivot and one level either side, and inventing an R2 to
    /// make the family look uniform would be arithmetic nobody published.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<PivotLine> Lines(PivotFormula formula, PivotPeriod period)
    {
        ArgumentNullException.ThrowIfNull(period);

        return formula switch
        {
            PivotFormula.Classic => Classic(period),
            PivotFormula.Fibonacci => Fibonacci(period),
            PivotFormula.Camarilla => Camarilla(period),
            PivotFormula.Woodie => Woodie(period),
            PivotFormula.DeMark => DeMark(period),
            _ => throw new ArgumentOutOfRangeException(
                nameof(formula), formula, "The pivot formula must be set explicitly."),
        };
    }

    /// <summary>
    /// Detects the pivot levels a series carries, for one formula.
    /// </summary>
    /// <param name="bars">The series, in strictly ascending time order and from one contract.</param>
    /// <param name="atr">ATR aligned one-to-one with <paramref name="bars"/>.</param>
    /// <param name="options">
    /// Detection options. <see cref="KeyLevelOptions.ZoneAtrMultiple"/> gives every line its width,
    /// <see cref="KeyLevelOptions.MinSignificance"/> keeps or drops the family whole, and both caps are
    /// applied through <see cref="KeyLevels"/>. <see cref="KeyLevelOptions.Lookback"/>,
    /// <see cref="KeyLevelOptions.RightLookback"/> and <see cref="KeyLevelOptions.Source"/> describe how a
    /// <i>pivot</i> is found by dominance and are not read here — but they are validated, because the caps
    /// this hands its zones to own the whole set.
    /// </param>
    /// <param name="calendar">The session calendar that decides where each period begins and ends.</param>
    /// <param name="formula">Which published formula to compute.</param>
    /// <returns>The zones, ordered by price.</returns>
    /// <exception cref="ArgumentException">
    /// The bars are not strictly ascending, they span a contract roll, or the ATR series is not aligned.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The formula is unset, or the options are ones no stage could honour.
    /// </exception>
    public static IReadOnlyList<KeyLevelZone> Compute(
        IReadOnlyList<Bar> bars,
        IReadOnlyList<decimal?> atr,
        KeyLevelOptions options,
        BarSessionCalendar calendar,
        PivotFormula formula)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(atr);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(calendar);

        // Before the series is even looked at, and before the empty-window exit rather than after it: a
        // refusal that waits for data is one the operator meets long after the mistake was made, which is
        // the reason KeyLevels.Detect checks in this order too.
        RequireServableFormula(formula);
        RequireUsableOptions(options);
        KeyLevels.RequireUsableOptions(options);

        // In the order the rest of the catalogue refuses in. A disordered series computes a different, wrong
        // answer rather than failing, and a spliced one puts a level at a price neither contract has traded
        // (`R-3.5`). Called here rather than inherited: a level method has no shared compute path.
        IndicatorGuard.RequireStrictlyAscending(bars, nameof(bars));
        IndicatorGuard.RequireSingleContract(bars, nameof(bars));

        if (atr.Count != bars.Count)
        {
            throw new ArgumentException(
                "The ATR series must align one-to-one with the bars; got "
                + atr.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " values for "
                + bars.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " bars.",
                nameof(atr));
        }

        if (PriorPeriodIndices(bars, calendar) is not { } indices)
        {
            return [];
        }

        // The scale is the ATR at the bar the period CLOSED on — the last scale the period itself supplied,
        // and the bar whose close every one of the five formulas reads. A line scaled by a later bar's ATR
        // would move as the current session traded, which is a level that repaints; one scaled by a
        // neighbour's is a width no bar in the period supports.
        int closeIndex = indices[^1];
        if (atr[closeIndex] is not { } scale || scale <= 0m)
        {
            return [];
        }

        PivotPeriod period = PeriodFrom(bars, indices);
        decimal significance = period.Range / scale;
        if (significance < options.MinSignificance)
        {
            return [];
        }

        decimal halfBand = scale * options.ZoneAtrMultiple / 2m;
        decimal lastClose = bars[^1].Close;
        DateTimeOffset formedAt = bars[closeIndex].OpenTime;

        List<KeyLevelZone> lines = [];
        foreach (PivotLine line in Lines(formula, period))
        {
            // The pivot itself is neither a floor nor a ceiling by name, so it seeds from where it sits
            // against the current price -- the same reading `SessionLevels` gives a session's close, and the
            // same one `ApplyClose` is about to apply to everything the price is not inside.
            KeyLevelKind kind = line.Kind is KeyLevelKind.Unknown
                ? line.Price > lastClose ? KeyLevelKind.Resistance : KeyLevelKind.Support
                : line.Kind;

            lines.Add(new KeyLevelZone(
                Bottom: line.Price - halfBand,
                Top: line.Price + halfBand,
                Kind: kind,
                FormedAtBucket: formedAt,
                TouchCount: 1,
                Significance: significance));
        }

        // The shared invariant carriers, in the order `KeyLevels.Detect` reaches them: merge (`R-3.1`), then
        // the width cap on what the merge produced, then the relabelling against the current price
        // (`R-3.3`), then the level cap (`R-3.9`).
        IReadOnlyList<KeyLevelZone> withinWidth =
            KeyLevels.ApplyWidthCap(KeyLevels.MergeOverlapping(lines), options);
        return KeyLevels.ApplyLevelCap(KeyLevels.ApplyClose(withinWidth, lastClose), options);
    }

    /// <summary>
    /// The bars of the most recent finished session the series can supply a period from, or
    /// <see langword="null"/> when it can supply none.
    /// </summary>
    /// <param name="bars">The series.</param>
    /// <param name="calendar">The calendar.</param>
    /// <returns>The indices, ascending; or <see langword="null"/>.</returns>
    private static IReadOnlyList<int>? PriorPeriodIndices(IReadOnlyList<Bar> bars, BarSessionCalendar calendar)
    {
        if (bars.Count == 0)
        {
            return null;
        }

        // One pass, so the calendar is consulted once per bar. A bar the calendar places outside every
        // session — the maintenance window, a weekend, a declared holiday — carries no trade date and
        // belongs to no period.
        DateOnly?[] tradeDates = new DateOnly?[bars.Count];
        for (int i = 0; i < bars.Count; i++)
        {
            tradeDates[i] = calendar.TradeDateFor(bars[i].OpenTime);
        }

        // The session in progress is the one the LATEST in-session bar sits in, not the one the last bar
        // sits in: a series that happens to end inside the maintenance window is an ordinary series.
        DateOnly? current = null;
        for (int i = bars.Count - 1; i >= 0 && current is null; i--)
        {
            current = tradeDates[i];
        }

        if (current is not { } currentTradeDate)
        {
            return null;
        }

        // The most recent trade date before this one that the series carries bars for. A weekend or a
        // declared holiday never becomes a trade date, so nothing here has to exclude one.
        DateOnly? prior = null;
        for (int i = 0; i < tradeDates.Length; i++)
        {
            if (tradeDates[i] is { } date && date < currentTradeDate && (prior is null || date > prior))
            {
                prior = date;
            }
        }

        // A period is reported only when the series reaches the OPENING of the session it began in. The
        // boundary is the calendar's own — handed back to it and kept only if it agrees — rather than one
        // reconstructed from the close and the window.
        if (prior is not { } priorTradeDate
            || SessionLevels.SessionOpenFor(calendar, priorTradeDate) is not { } open
            || bars[0].OpenTime > open)
        {
            return null;
        }

        List<int> indices = [];
        for (int i = 0; i < tradeDates.Length; i++)
        {
            if (tradeDates[i] == priorTradeDate)
            {
                indices.Add(i);
            }
        }

        // A session the series covers with one bar is a session the series cannot resolve: `Bar` carries no
        // width, so that bar is indistinguishable from one spanning the trade date and everything around it.
        return indices.Count >= 2 ? indices : null;
    }

    private static PivotPeriod PeriodFrom(IReadOnlyList<Bar> bars, IReadOnlyList<int> indices)
    {
        decimal high = bars[indices[0]].High;
        decimal low = bars[indices[0]].Low;

        foreach (int i in indices)
        {
            high = Math.Max(high, bars[i].High);
            low = Math.Min(low, bars[i].Low);
        }

        return new PivotPeriod(
            Open: bars[indices[0]].Open,
            High: high,
            Low: low,
            Close: bars[indices[^1]].Close);
    }

    /// <summary>Floor-trader pivots — <c>P = (H + L + C) / 3</c>, three levels either side.</summary>
    private static IReadOnlyList<PivotLine> Classic(PivotPeriod p)
    {
        decimal pivot = (p.High + p.Low + p.Close) / 3m;

        return
        [
            new PivotLine(p.Low - (2m * (p.High - pivot)), KeyLevelKind.Support),     // S3
            new PivotLine(pivot - p.Range, KeyLevelKind.Support),                     // S2
            new PivotLine((2m * pivot) - p.High, KeyLevelKind.Support),               // S1
            new PivotLine(pivot, KeyLevelKind.Unknown),                               // P
            new PivotLine((2m * pivot) - p.Low, KeyLevelKind.Resistance),             // R1
            new PivotLine(pivot + p.Range, KeyLevelKind.Resistance),                  // R2
            new PivotLine(p.High + (2m * (pivot - p.Low)), KeyLevelKind.Resistance),  // R3
        ];
    }

    /// <summary>The classic pivot with the legs at 0.382, 0.618 and 1.000 of the period's range.</summary>
    private static IReadOnlyList<PivotLine> Fibonacci(PivotPeriod p)
    {
        decimal pivot = (p.High + p.Low + p.Close) / 3m;
        decimal near = 0.382m * p.Range;
        decimal far = 0.618m * p.Range;

        return
        [
            new PivotLine(pivot - p.Range, KeyLevelKind.Support),     // S3
            new PivotLine(pivot - far, KeyLevelKind.Support),         // S2
            new PivotLine(pivot - near, KeyLevelKind.Support),        // S1
            new PivotLine(pivot, KeyLevelKind.Unknown),               // P
            new PivotLine(pivot + near, KeyLevelKind.Resistance),     // R1
            new PivotLine(pivot + far, KeyLevelKind.Resistance),      // R2
            new PivotLine(pivot + p.Range, KeyLevelKind.Resistance),  // R3
        ];
    }

    /// <summary>Four levels either side of the period's close, at <c>1.1 / n</c> of its range.</summary>
    private static IReadOnlyList<PivotLine> Camarilla(PivotPeriod p)
    {
        decimal scaled = 1.1m * p.Range;
        decimal one = scaled / 12m;
        decimal two = scaled / 6m;
        decimal three = scaled / 4m;
        decimal four = scaled / 2m;

        return
        [
            new PivotLine(p.Close - four, KeyLevelKind.Support),      // S4
            new PivotLine(p.Close - three, KeyLevelKind.Support),     // S3
            new PivotLine(p.Close - two, KeyLevelKind.Support),       // S2
            new PivotLine(p.Close - one, KeyLevelKind.Support),       // S1
            new PivotLine(p.Close + one, KeyLevelKind.Resistance),    // R1
            new PivotLine(p.Close + two, KeyLevelKind.Resistance),    // R2
            new PivotLine(p.Close + three, KeyLevelKind.Resistance),  // R3
            new PivotLine(p.Close + four, KeyLevelKind.Resistance),   // R4
        ];
    }

    /// <summary>The pivot with the close weighted twice, and two levels either side.</summary>
    private static IReadOnlyList<PivotLine> Woodie(PivotPeriod p)
    {
        decimal pivot = (p.High + p.Low + (2m * p.Close)) / 4m;

        return
        [
            new PivotLine(pivot - p.Range, KeyLevelKind.Support),        // S2
            new PivotLine((2m * pivot) - p.High, KeyLevelKind.Support),  // S1
            new PivotLine(pivot, KeyLevelKind.Unknown),                  // P
            new PivotLine((2m * pivot) - p.Low, KeyLevelKind.Resistance), // R1
            new PivotLine(pivot + p.Range, KeyLevelKind.Resistance),     // R2
        ];
    }

    /// <summary>
    /// DeMark's, which branches on where the period closed against where it opened.
    /// </summary>
    /// <param name="p">The period.</param>
    /// <returns>The three lines.</returns>
    /// <remarks>
    /// <b>The equality case is a branch, not a rounding of one of the others.</b> A session that closes
    /// exactly where it opened is rare and neither inequality answers it wrongly enough to notice: both
    /// produce a well-formed pivot set, and the two differ from this one and from each other. A formula with
    /// an unpinned branch is a formula with an untested third of itself, which is why gh#258 asks for all
    /// three.
    /// </remarks>
    private static IReadOnlyList<PivotLine> DeMark(PivotPeriod p)
    {
        decimal x = p.Close.CompareTo(p.Open) switch
        {
            < 0 => p.High + (2m * p.Low) + p.Close,
            > 0 => (2m * p.High) + p.Low + p.Close,
            _ => p.High + p.Low + (2m * p.Close),
        };

        return
        [
            new PivotLine((x / 2m) - p.High, KeyLevelKind.Support),      // S1
            new PivotLine(x / 4m, KeyLevelKind.Unknown),                 // P
            new PivotLine((x / 2m) - p.Low, KeyLevelKind.Resistance),    // R1
        ];
    }

    /// <summary>
    /// Refuses a formula outside the vocabulary.
    /// </summary>
    /// <param name="formula">The formula.</param>
    /// <exception cref="ArgumentOutOfRangeException">The formula is unset or outside the vocabulary.</exception>
    /// <remarks>
    /// <b>Checked against the whole vocabulary, not against <see cref="PivotFormula.Unknown"/> alone</b> —
    /// the same lesson <see cref="KeyLevels"/> learned about <see cref="PivotSource"/>. A cast integer
    /// outside the enum would otherwise fall through some switch's default arm, and the difference between
    /// "refused" and "answered by whichever arm happens to be last" is a level set nobody named.
    /// </remarks>
    private static void RequireServableFormula(PivotFormula formula)
    {
        if (formula == PivotFormula.Unknown || !Enum.IsDefined(formula))
        {
            throw new ArgumentOutOfRangeException(
                nameof(formula),
                formula,
                "The pivot formula must be one of Classic, Fibonacci, Camarilla, Woodie, DeMark. Unknown is "
                + "what an unset value binds to, so it is refused rather than resolved to a default.");
        }
    }

    /// <summary>
    /// Refuses the two options this family reads directly.
    /// </summary>
    /// <param name="options">The options.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The zone width is not positive, or the significance floor is negative.
    /// </exception>
    /// <remarks>
    /// The same two <see cref="SessionLevels"/> checks, and for the same reason: a computed line is a price
    /// until something gives it a width, and a negative floor is a filter that cannot bite.
    /// <see cref="KeyLevels.RequireUsableOptions"/> covers the rest.
    /// </remarks>
    private static void RequireUsableOptions(KeyLevelOptions options)
    {
        if (options.ZoneAtrMultiple <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.ZoneAtrMultiple,
                "The zone width must be positive; a pivot line needs a width before it is a zone.");
        }

        if (options.MinSignificance < 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), options.MinSignificance, "The significance floor cannot be negative.");
        }
    }
}

/// <summary>The <see cref="ILevelMethod"/> face of <see cref="PivotLevels"/> — one published formula.</summary>
/// <param name="formula">Which formula this method computes.</param>
/// <param name="calendar">The session calendar that decides where the prior period begins and ends.</param>
/// <remarks>
/// <para>
/// <b>One class, five registrations.</b> The five differ by a formula and by nothing else, and what gh#232's
/// MACD precedent asks for is five <i>names</i> in the vocabulary rather than one parameterised entry a
/// caller has to configure — which is what <c>LevelMethodCatalog</c> registers. Five near-identical
/// classes would put the same three lines in the repository five times and give a sixth variant five places
/// to be added inconsistently.
/// </para>
/// <para>
/// <b>The calendar is held, not fetched</b>, on the terms <see cref="ILevelMethod"/>'s remarks set out for
/// <c>session</c> and gh#257 settled: it is a parsed value, one instance for the process, so holding one is
/// not the configuration singleton those remarks forbid. <see cref="ILevelMethod.Detect"/> is not widened,
/// deliberately and for the third time.
/// </para>
/// <para>
/// Both refusals happen inside <see cref="PivotLevels.Compute"/>, before it reads a price. Like
/// <c>session</c> and unlike <see cref="SwingLevelMethod"/> there is nothing underneath to delegate them to:
/// this method does not go through <see cref="KeyLevels.FindPivots"/>, which is exactly the case
/// <c>LevelMethodCatalogOrderingTests</c> and <c>LevelMethodCatalogRollTests</c> sweep for.
/// </para>
/// </remarks>
public sealed class PivotLevelMethod(PivotFormula formula, BarSessionCalendar calendar) : ILevelMethod
{
    private readonly BarSessionCalendar _calendar =
        calendar ?? throw new ArgumentNullException(nameof(calendar));

    private readonly string _name = PivotLevels.NameOf(formula);

    /// <summary>The method name — <c>pivot-classic</c>, <c>pivot-camarilla</c>, and so on.</summary>
    public string Name => _name;

    /// <summary>
    /// The correlation family, <c>pivot</c> — shared by all five so a score can discount them together.
    /// </summary>
    public string Family => PivotLevels.FamilyName;

    /// <inheritdoc />
    public IReadOnlyList<KeyLevelZone> Detect(
        IReadOnlyList<Bar> bars,
        IReadOnlyList<decimal?> atr,
        KeyLevelOptions options) => PivotLevels.Compute(bars, atr, options, _calendar, formula);
}
