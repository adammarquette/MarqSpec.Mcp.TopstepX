namespace MarqSpec.Mcp.TopstepX.Domain.MarketData;

/// <summary>
/// The levels a session leaves behind — prior day, prior week, the overnight range and the initial balance.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every one of these is a fact about a period that has finished</b>, which is what separates them from a
/// swing pivot. A pivot is a candidate found by dominance; a prior-day high is simply the highest price that
/// traded in a session, and the only questions worth asking about it are <i>which</i> session, and whether
/// the series on hand actually covers it.
/// </para>
/// <para>
/// <b>Both questions are answered by the calendar, and neither is answered from the bars.</b> Inferring a
/// session boundary from gaps in a series produces a plausible boundary from a thin overnight, which is this
/// repository's oldest failure class (gh#213): a well-formed, believable number that no trade produced. So
/// the boundaries come from <see cref="BarSessionCalendar"/> — <see cref="BarSessionCalendar.TradeDateFor"/>
/// for which session a bar belongs to, and <see cref="BarSessionCalendar.IsTradingDay"/> for whether a date
/// has a session at all. Counting back 1,440 minutes would report a holiday's high as yesterday's.
/// </para>
/// <para>
/// <b>The calendar arrives as a parameter, so this stays a pure function of what it is handed</b> — the same
/// terms <see cref="VolumeWeightedAveragePrice.Compute"/> is on, and for the same reason. A calendar is
/// itself a deterministic function of its configuration, so passing one in does not put a clock, a store or
/// a configuration singleton inside the computation, and "rebuild = replay" still holds.
/// </para>
/// <para>
/// <b>A period the window does not cover is absent, never approximated.</b> The rule is one line — a period
/// is reported only when the series reaches the <i>opening</i> of the session that period began in — and it
/// is what stops a prior-day high being taken from half a prior day. At the tool's default 500-bar window a
/// five-minute series spans roughly forty hours, which is one session and part of another, so prior-week
/// levels will normally be absent and prior-day levels often will be. That is the honest answer to a window
/// that does not contain the week.
/// </para>
/// <para>
/// <b>Known bound:</b> the coverage rule checks where the window <i>starts</i>, not that a session is
/// densely sampled inside it. A store with a hole in the middle of a prior day yields that day's extremes
/// over the bars it holds. Holes are <c>BarGapDetector</c>'s subject, and it is the cache path rather than
/// this one that closes them.
/// </para>
/// </remarks>
public static class SessionLevels
{
    /// <summary>
    /// How much of a session's opening the initial balance covers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One hour, fixed at the conventional value rather than exposed as a knob. <b>The reason is the one
    /// <see cref="KeyLevelOptions.ZoneAtrMultiple"/> is not per-call, not ADR-0006's</b> — nothing stores a
    /// level, so no key can lose a parameter (ADR-0013); what a movable length would cost is
    /// <i>comparability</i>. gh#232's confluence weighs levels from several methods against each other, and
    /// an initial balance measured over a length the caller chose is not the same level two readers think
    /// they are both looking at.
    /// </para>
    /// <para>
    /// It is measured from the session's own reopen, which for this server's calendar is
    /// <see cref="BarSessionCalendar.SessionOpen"/> on the previous evening. <b>That is not the cash
    /// open</b>, and the difference is stated rather than glossed: a pit-session initial balance runs from
    /// the equity open, and nothing in this repository's configuration names one — there is a
    /// <c>SessionCloseCentral</c> and a maintenance window and nothing else. Inventing a cash open here
    /// would be a boundary no configuration set and no trade produced.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan InitialBalanceLength = TimeSpan.FromHours(1);

    /// <summary>The period label an initial-balance zone carries.</summary>
    public const string InitialBalancePeriod = "initial-balance";

    /// <summary>The period label an overnight-range zone carries.</summary>
    public const string OvernightPeriod = "overnight";

    /// <summary>
    /// Detects the session levels a series carries.
    /// </summary>
    /// <param name="bars">The series, in strictly ascending time order and from one contract.</param>
    /// <param name="atr">ATR aligned one-to-one with <paramref name="bars"/>.</param>
    /// <param name="options">
    /// Detection options. Only <see cref="KeyLevelOptions.ZoneAtrMultiple"/> and
    /// <see cref="KeyLevelOptions.MinSignificance"/> are read. <see cref="KeyLevelOptions.Lookback"/> and
    /// <see cref="KeyLevelOptions.Source"/> describe how a <i>pivot</i> is found and are ignored here rather
    /// than validated — a session's high is its high, not a dominance measured from a chosen price series,
    /// and refusing a parameter this method never consults would be refusing a caller for something that
    /// could not have changed the answer.
    /// </param>
    /// <param name="calendar">The session calendar that decides where each session begins and ends.</param>
    /// <returns>The zones, ordered by price.</returns>
    /// <exception cref="ArgumentException">
    /// The bars are not strictly ascending, they span a contract roll, or the ATR series is not aligned.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The zone width is not positive, or the significance floor is negative.
    /// </exception>
    public static IReadOnlyList<KeyLevelZone> Compute(
        IReadOnlyList<Bar> bars,
        IReadOnlyList<decimal?> atr,
        KeyLevelOptions options,
        BarSessionCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(atr);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(calendar);
        RequireUsableOptions(options);
        KeyLevels.RequireUsableOptions(options);

        // Before anything reads a price, and in the order the rest of the catalogue refuses in: a disordered
        // series computes a different, wrong answer rather than failing, and a spliced one puts a level at a
        // price neither contract has traded (`R-3.5`). Called here rather than inherited, because a level
        // method has no shared compute path to inherit them from.
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

        if (bars.Count == 0)
        {
            return [];
        }

        // One pass, so the calendar is consulted once per bar rather than once per level. A bar the calendar
        // places outside every session — the maintenance window, a weekend, a declared holiday — carries no
        // trade date and belongs to no period below.
        DateOnly?[] tradeDates = new DateOnly?[bars.Count];
        for (int i = 0; i < bars.Count; i++)
        {
            tradeDates[i] = calendar.TradeDateFor(bars[i].OpenTime);
        }

        // The session in progress is the one the LATEST in-session bar sits in. Taking the last bar's own
        // trade date would answer null for a series that happens to end in the maintenance window, and a
        // series ending at 16:30 is an ordinary series, not one with no current session.
        DateOnly? current = null;
        for (int i = bars.Count - 1; i >= 0 && current is null; i--)
        {
            current = tradeDates[i];
        }

        if (current is not { } currentTradeDate)
        {
            return [];
        }

        DateTimeOffset windowStart = bars[0].OpenTime;
        List<KeyLevelZone> lines = [];

        AddPriorDay(bars, atr, options, calendar, tradeDates, currentTradeDate, windowStart, lines);
        AddPriorWeek(bars, atr, options, calendar, tradeDates, currentTradeDate, windowStart, lines);
        AddCurrentSession(bars, atr, options, calendar, tradeDates, currentTradeDate, windowStart, lines);

        // The shared invariant carriers, exactly as `swing` and the pivot family reach them: merge
        // (`R-3.1`), the width cap, relabel against the close (`R-3.3`), then the level cap. Both
        // caps are applied so `detection` reporting them is a fact about this method too (gh#259).
        IReadOnlyList<KeyLevelZone> withinWidth =
            KeyLevels.ApplyWidthCap(KeyLevels.MergeOverlapping(lines), options);
        return KeyLevels.ApplyLevelCap(KeyLevels.ApplyClose(withinWidth, bars[^1].Close), options);
    }

    /// <summary>
    /// The immediately previous trading day, or <see langword="null"/> when none falls inside two weeks.
    /// </summary>
    /// <param name="calendar">The calendar that decides which dates trade.</param>
    /// <param name="tradeDate">The day whose predecessor is wanted.</param>
    /// <returns>The previous trading day, never an older day the series happens to hold.</returns>
    /// <remarks>
    /// The most recent date <i>in a series</i> is a different question, and answering it here is how
    /// Friday's levels get served when Monday traded but was not loaded (gh#259 finding 2). The
    /// calendar is the source of "which day"; the series is only asked whether it covers that day.
    /// </remarks>
    public static DateOnly? PreviousTradingDay(BarSessionCalendar calendar, DateOnly tradeDate)
    {
        ArgumentNullException.ThrowIfNull(calendar);

        DateOnly floor = tradeDate.AddDays(-14);
        for (DateOnly day = tradeDate.AddDays(-1); day >= floor; day = day.AddDays(-1))
        {
            if (calendar.IsTradingDay(day))
            {
                return day;
            }
        }

        return null;
    }

    /// <summary>
    /// When the session for a trade date opens, or <see langword="null"/> when the calendar does not agree
    /// that it does.
    /// </summary>
    /// <param name="calendar">The calendar.</param>
    /// <param name="tradeDate">The trade date.</param>
    /// <returns>The session's opening instant, or <see langword="null"/>.</returns>
    /// <remarks>
    /// A trade date's session opens at <see cref="BarSessionCalendar.SessionOpen"/> on the previous calendar
    /// evening — Sunday evening for a Monday, and the evening before a holiday for the day after it. Rather
    /// than restate that rule, the candidate instant is handed back to
    /// <see cref="BarSessionCalendar.TradeDateFor"/> and kept only if the calendar agrees it belongs to this
    /// trade date. A close late enough that the reopen lands after midnight makes the previous evening the
    /// wrong place to look, and this returns null there instead of a boundary the calendar disowns —
    /// measured at a <c>23:30</c> close, where the candidate for Tuesday the 18th is Monday the 17th at
    /// <c>00:30</c> and the calendar reads it as the 17th, so every period goes absent
    /// (<c>SessionLevelMethodTests</c> pins it).
    /// <para>
    /// <b>Public since gh#258, so the pivot family reads this boundary rather than defining a second
    /// one.</b> "The prior period" a pivot is computed from is the same session boundary, and
    /// <see cref="ILevelMethod"/>'s own remarks reject putting a second definition of a session next to
    /// <see cref="BarSessionCalendar"/>.
    /// </para>
    /// </remarks>
    public static DateTimeOffset? SessionOpenFor(BarSessionCalendar calendar, DateOnly tradeDate)
    {
        ArgumentNullException.ThrowIfNull(calendar);

        DateTimeOffset candidate = MarketClock.FromMarket(tradeDate.AddDays(-1), calendar.SessionOpen);
        return calendar.TradeDateFor(candidate) == tradeDate ? candidate : null;
    }

    private static void AddPriorDay(
        IReadOnlyList<Bar> bars,
        IReadOnlyList<decimal?> atr,
        KeyLevelOptions options,
        BarSessionCalendar calendar,
        IReadOnlyList<DateOnly?> tradeDates,
        DateOnly currentTradeDate,
        DateTimeOffset windowStart,
        List<KeyLevelZone> lines)
    {
        // The immediately previous trading day the calendar names — not the most recent date the
        // series happens to hold. A weekend or a declared holiday is not a trading day, so this
        // walks back past them; a trading day the series does not carry is absent, not replaced
        // by Friday (gh#259 finding 2).
        if (PreviousTradingDay(calendar, currentTradeDate) is not { } day
            || SessionOpenFor(calendar, day) is not { } open
            || windowStart > open)
        {
            return;
        }

        List<int> indices = [];
        for (int i = 0; i < tradeDates.Count; i++)
        {
            if (tradeDates[i] == day)
            {
                indices.Add(i);
            }
        }

        AddPeriod(bars, atr, options, indices, withClose: true, PeriodLabel(day), lines);
    }

    private static void AddPriorWeek(
        IReadOnlyList<Bar> bars,
        IReadOnlyList<decimal?> atr,
        KeyLevelOptions options,
        BarSessionCalendar calendar,
        IReadOnlyList<DateOnly?> tradeDates,
        DateOnly currentTradeDate,
        DateTimeOffset windowStart,
        List<KeyLevelZone> lines)
    {
        // Monday of the current trade date's week, then the seven days before it. Trade dates are weekdays
        // by construction, so a Monday-to-Sunday block and an ISO week agree on every date that can appear.
        DateOnly monday = currentTradeDate.AddDays(-(((int)currentTradeDate.DayOfWeek + 6) % 7));
        DateOnly priorMonday = monday.AddDays(-7);
        DateOnly priorSunday = priorMonday.AddDays(6);

        // A week is covered when the series reaches the opening of that week's FIRST trading session, which
        // is not necessarily Monday's — a shut Monday moves the week's open to the first day that trades.
        DateOnly? firstTradingDate = null;
        for (DateOnly d = priorMonday; d <= priorSunday && firstTradingDate is null; d = d.AddDays(1))
        {
            if (calendar.IsTradingDay(d))
            {
                firstTradingDate = d;
            }
        }

        if (firstTradingDate is not { } weekStartDate
            || SessionOpenFor(calendar, weekStartDate) is not { } open
            || windowStart > open)
        {
            return;
        }

        List<int> indices = [];
        for (int i = 0; i < tradeDates.Count; i++)
        {
            if (tradeDates[i] is { } date && date >= priorMonday && date <= priorSunday)
            {
                indices.Add(i);
            }
        }

        AddPeriod(bars, atr, options, indices, withClose: true, "week:" + PeriodLabel(priorMonday), lines);
    }

    private static void AddCurrentSession(
        IReadOnlyList<Bar> bars,
        IReadOnlyList<decimal?> atr,
        KeyLevelOptions options,
        BarSessionCalendar calendar,
        IReadOnlyList<DateOnly?> tradeDates,
        DateOnly currentTradeDate,
        DateTimeOffset windowStart,
        List<KeyLevelZone> lines)
    {
        if (SessionOpenFor(calendar, currentTradeDate) is not { } open || windowStart > open)
        {
            return;
        }

        DateTimeOffset last = bars[^1].OpenTime;

        // The overnight leg is the part of this session that traded before its own trade date arrived — from
        // the reopen to midnight, Central. It is a level only once it has finished: a range that is still
        // being made repaints on the next bar, which `R-3.4` refuses.
        if (MarketClock.MarketDate(last) >= currentTradeDate)
        {
            List<int> overnight = [];
            for (int i = 0; i < tradeDates.Count; i++)
            {
                if (tradeDates[i] == currentTradeDate
                    && MarketClock.MarketDate(bars[i].OpenTime) < currentTradeDate)
                {
                    overnight.Add(i);
                }
            }

            AddPeriod(bars, atr, options, overnight, withClose: false, OvernightPeriod, lines);
        }

        // The initial balance, on the same terms: reported once the hour it covers is behind the series,
        // and absent while it is still forming. A resolution coarser than that hour would report a
        // four-hour bar as the one-hour balance — refused, symmetrically with a partial period
        // (gh#259 finding 1). Resolution 0 means the caller did not say, and the existing fixtures
        // that do not pass one still measure the hour.
        DateTimeOffset initialBalanceEnd = open + InitialBalanceLength;
        int ibMinutes = (int)InitialBalanceLength.TotalMinutes;
        if (last >= initialBalanceEnd
            && (options.ResolutionMinutes == 0 || options.ResolutionMinutes <= ibMinutes))
        {
            List<int> initialBalance = [];
            for (int i = 0; i < tradeDates.Count; i++)
            {
                if (tradeDates[i] == currentTradeDate
                    && bars[i].OpenTime >= open
                    && bars[i].OpenTime < initialBalanceEnd)
                {
                    initialBalance.Add(i);
                }
            }

            AddPeriod(bars, atr, options, initialBalance, withClose: false, InitialBalancePeriod, lines);
        }
    }

    /// <summary>
    /// Turns one finished period into its high, its low and — for a whole session — its close.
    /// </summary>
    /// <param name="bars">The series.</param>
    /// <param name="atr">The ATR series.</param>
    /// <param name="options">Detection options.</param>
    /// <param name="indices">The period's bars. An empty period contributes nothing.</param>
    /// <param name="withClose">
    /// Whether the period has a close worth reporting. A day and a week do; the overnight leg and the
    /// initial balance are <i>ranges</i>, and the price a range happened to stop at is not a level anybody
    /// watches.
    /// </param>
    /// <param name="period">The period label every line of this period carries.</param>
    /// <param name="lines">The accumulator.</param>
    /// <remarks>
    /// <para>
    /// <b>Significance is the period's own range in ATR multiples</b>, and for the high and the low that is
    /// literally <see cref="KeyLevels.FindPivots"/>'s prominence with the session as the window: how far the
    /// level stands clear of the most extreme opposing price in it (<c>R-3.2</c>). A close is not an
    /// extreme, so the same number reads there as how much movement the session resolved.
    /// </para>
    /// <para>
    /// <b>The scale is the ATR at the bar that made the level</b>, matching <see cref="KeyLevels.ZoneFor"/>,
    /// and a level whose own bar has no ATR is dropped rather than scaled by a neighbour's. A borrowed ATR
    /// would produce a zone width and a significance that no bar in the series supports.
    /// </para>
    /// <para>
    /// <b>Ties resolve to the earliest bar</b>, so a level dates from when it was first respected — the same
    /// rule <see cref="KeyLevels.MergeOverlapping"/> applies when it merges two.
    /// </para>
    /// </remarks>
    private static void AddPeriod(
        IReadOnlyList<Bar> bars,
        IReadOnlyList<decimal?> atr,
        KeyLevelOptions options,
        IReadOnlyList<int> indices,
        bool withClose,
        string period,
        List<KeyLevelZone> lines)
    {
        if (indices.Count == 0)
        {
            return;
        }

        int highIndex = indices[0];
        int lowIndex = indices[0];
        foreach (int i in indices)
        {
            if (bars[i].High > bars[highIndex].High)
            {
                highIndex = i;
            }

            if (bars[i].Low < bars[lowIndex].Low)
            {
                lowIndex = i;
            }
        }

        decimal range = bars[highIndex].High - bars[lowIndex].Low;

        AddLine(bars, atr, options, highIndex, bars[highIndex].High, range, KeyLevelKind.Resistance, period, lines);
        AddLine(bars, atr, options, lowIndex, bars[lowIndex].Low, range, KeyLevelKind.Support, period, lines);

        if (!withClose)
        {
            return;
        }

        int closeIndex = indices[^1];
        decimal close = bars[closeIndex].Close;

        // A close is neither a floor nor a ceiling when it forms; it becomes one relative to where price is
        // now. Seeding it from the last close is the same reading `ApplyClose` applies, and it is what
        // decides which side of the merge it groups with -- a seed of `Unknown` would group with nothing and
        // reach the payload as an unset kind.
        KeyLevelKind seed = close > bars[^1].Close ? KeyLevelKind.Resistance : KeyLevelKind.Support;
        AddLine(bars, atr, options, closeIndex, close, range, seed, period, lines);
    }

    private static string PeriodLabel(DateOnly tradeDate) =>
        tradeDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static void AddLine(
        IReadOnlyList<Bar> bars,
        IReadOnlyList<decimal?> atr,
        KeyLevelOptions options,
        int sourceIndex,
        decimal price,
        decimal range,
        KeyLevelKind kind,
        string period,
        List<KeyLevelZone> lines)
    {
        if (atr[sourceIndex] is not { } scale || scale <= 0m)
        {
            return;
        }

        decimal significance = range / scale;
        if (significance < options.MinSignificance)
        {
            return;
        }

        // The line-to-zone tolerance, and it is `ZoneAtrMultiple` rather than a constant or a new field: the
        // width that turns a pivot into a zone is the width that turns a session line into one, so a
        // confluence score comparing the two is comparing like with like (gh#232). The tool already reports
        // it as `detection.zoneAtrMultiple`, which is what makes a score reproducible.
        decimal halfBand = scale * options.ZoneAtrMultiple / 2m;

        lines.Add(new KeyLevelZone(
            Bottom: price - halfBand,
            Top: price + halfBand,
            Kind: kind,
            FormedAtBucket: bars[sourceIndex].OpenTime,
            TouchCount: 1,
            Significance: significance,
            Period: period));
    }

    private static void RequireUsableOptions(KeyLevelOptions options)
    {
        if (options.ZoneAtrMultiple <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.ZoneAtrMultiple,
                "The zone width must be positive; a session line needs a width before it is a zone.");
        }

        if (options.MinSignificance < 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), options.MinSignificance, "The significance floor cannot be negative.");
        }
    }
}

/// <summary>The <see cref="ILevelMethod"/> face of <see cref="SessionLevels"/> — session extremes.</summary>
/// <param name="calendar">The session calendar that decides where each session begins and ends.</param>
/// <remarks>
/// <para>
/// <b>The calendar is held, not fetched.</b> It is a parsed value rather than a live source — one instance,
/// built once at startup from <c>MarketData__SessionCloseCentral</c> and <c>MarketData__Holidays</c> — so
/// holding it satisfies <see cref="ILevelMethod"/>'s "no clock, no store, no configuration singleton" on the
/// same terms <see cref="VwapIndicator"/> already satisfies <see cref="IIndicator"/>'s. Detection over
/// identical bars and an identical calendar returns an identical zone list.
/// </para>
/// <para>
/// The roll and ordering refusals both happen inside <see cref="SessionLevels.Compute"/>, before it reads a
/// price. Unlike <see cref="SwingLevelMethod"/> there is nothing underneath to delegate them to: this method
/// does not go through <see cref="KeyLevels.FindPivots"/>, which is precisely the case
/// <c>LevelMethodCatalogOrderingTests</c> was built ahead of this method to catch.
/// </para>
/// </remarks>
public sealed class SessionLevelMethod(BarSessionCalendar calendar) : ILevelMethod
{
    private readonly BarSessionCalendar _calendar =
        calendar ?? throw new ArgumentNullException(nameof(calendar));

    /// <summary>The method name, <c>session</c>.</summary>
    public string Name => "session";

    /// <summary>
    /// The correlation family, <c>session</c> — a family of one. Nothing else reports a finished period's
    /// own extremes, and the pivot family shares its <i>input</i> rather than its answer.
    /// </summary>
    public string Family => "session";

    /// <inheritdoc />
    public IReadOnlyList<KeyLevelZone> Detect(
        IReadOnlyList<Bar> bars,
        IReadOnlyList<decimal?> atr,
        KeyLevelOptions options) => SessionLevels.Compute(bars, atr, options, _calendar);
}
