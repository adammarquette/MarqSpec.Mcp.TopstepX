namespace MarqSpec.Mcp.TopstepX.Domain.MarketData;

/// <summary>Which price on a bar a pivot is measured from.</summary>
public enum PivotSource
{
    /// <summary>Unset. Never a valid configured value — a zero default here would pick a source by accident.</summary>
    Unknown = 0,

    /// <summary>The Heikin-Ashi body. The default: it smooths single-bar noise into structure.</summary>
    HeikinAshiBody = 1,

    /// <summary>The raw candle body — open and close.</summary>
    Body = 2,

    /// <summary>The raw high and low, wicks included.</summary>
    HighLow = 3,
}

/// <summary>Which side of price a level sits on.</summary>
public enum KeyLevelKind
{
    /// <summary>Unset.</summary>
    Unknown = 0,

    /// <summary>Below the current price — a floor.</summary>
    Support = 1,

    /// <summary>Above the current price — a ceiling.</summary>
    Resistance = 2,
}

/// <summary>
/// One swing pivot — a local extreme in the series.
/// </summary>
/// <param name="BarIndex">The index of the pivot bar in the series it was found in.</param>
/// <param name="OpenTime">When the pivot bar opened.</param>
/// <param name="Price">The pivot price.</param>
/// <param name="Kind">Whether the pivot is a high (resistance) or a low (support).</param>
/// <param name="Prominence">
/// How far, in price, the pivot stands clear of the most extreme opposing price in its window. This is the raw
/// measure; dividing it by ATR is what makes it comparable across instruments.
/// </param>
public sealed record SwingPivot(
    int BarIndex,
    DateTimeOffset OpenTime,
    decimal Price,
    KeyLevelKind Kind,
    decimal Prominence);

/// <summary>
/// A price zone rather than a price line.
/// </summary>
/// <remarks>
/// Levels are zones because price does not respect a single tick. The band is scaled by ATR so a zone is the
/// same <i>size relative to how much the instrument moves</i> on ES and on NQ — a fixed point width would be a
/// wide zone on one and invisible on the other.
/// </remarks>
/// <param name="Bottom">The lower edge.</param>
/// <param name="Top">The upper edge.</param>
/// <param name="Kind">Which side of price the zone sits on.</param>
/// <param name="FormedAtBucket">When the earliest pivot in this zone formed.</param>
/// <param name="TouchCount">How many pivots fell inside this zone. More touches, more agreement.</param>
/// <param name="Significance">Prominence in ATR multiples — comparable across instruments and regimes.</param>
public sealed record KeyLevelZone(
    decimal Bottom,
    decimal Top,
    KeyLevelKind Kind,
    DateTimeOffset FormedAtBucket,
    int TouchCount,
    decimal Significance)
{
    /// <summary>The middle of the zone.</summary>
    public decimal Midpoint => (Top + Bottom) / 2m;

    /// <summary>Whether a price falls inside the zone, edges included.</summary>
    /// <param name="price">The price.</param>
    /// <returns><see langword="true"/> when the price is within the zone.</returns>
    public bool Contains(decimal price) => price >= Bottom && price <= Top;

    /// <summary>Whether this zone overlaps another.</summary>
    /// <param name="other">The other zone.</param>
    /// <returns><see langword="true"/> when the two intersect.</returns>
    public bool Overlaps(KeyLevelZone other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Bottom <= other.Top && other.Bottom <= Top;
    }
}

/// <summary>How key levels are detected and sized.</summary>
/// <param name="Lookback">
/// How many bars <b>to its left</b> a pivot must dominate. Larger means fewer, more structural levels.
/// </param>
/// <param name="Source">Which price on a bar the pivot is measured from.</param>
/// <param name="ZoneAtrMultiple">The zone's full width, in ATR multiples.</param>
/// <param name="MinSignificance">
/// The smallest prominence, in ATR multiples, worth reporting. Filtering here rather than at the caller keeps
/// the noise out of the merge step, where two insignificant pivots could otherwise combine into something that
/// looks meaningful.
/// </param>
/// <param name="RightLookback">
/// How many bars <b>to its right</b> a pivot must dominate — the confirmation window, and the reason a pivot
/// is not a guess about bars that have not arrived (<c>R-3.4</c>).
/// </param>
/// <param name="MaxZoneWidthPercent">
/// The widest a reported zone may be, as a percentage of its own midpoint price. A zone over it is
/// <b>dropped</b>, not narrowed.
/// </param>
/// <param name="MaxLevels">The most levels a detection may report. The rest are absent, not summarised.</param>
/// <remarks>
/// <para>
/// <b>The two lookbacks are separate because a pivot's two sides do different jobs.</b> The left window asks
/// how much history a level has stood clear of; the right window is confirmation, and it is what stops a
/// pivot repainting. Bjorgum's <i>Key Levels</i> — the method this pipeline follows, and which gh#232 adopted
/// whole — uses 20 and 15, and those are the shipped defaults.
/// </para>
/// <para>
/// <b>Both caps drop rather than adjust, and that is the same rule the significance floor already follows.</b>
/// A capped-away level is absent. It is not a narrowed zone, and it is not folded into the nearest survivor:
/// either of those reports a band the detection never produced, at a price nothing was measured at.
/// </para>
/// </remarks>
public sealed record KeyLevelOptions(
    int Lookback = 20,
    PivotSource Source = PivotSource.HeikinAshiBody,
    decimal ZoneAtrMultiple = 0.5m,
    decimal MinSignificance = 0.5m,
    int RightLookback = 15,
    decimal MaxZoneWidthPercent = 2.5m,
    int MaxLevels = 12);

/// <summary>
/// Swing-pivot detection and the support/resistance zones built from it.
/// </summary>
/// <remarks>
/// Pure functions throughout: given the same bars and options, the same levels. Nothing here reads a clock or
/// a store, so a level an agent was shown can be reproduced exactly from the bars that were on hand.
/// </remarks>
public static class KeyLevels
{
    /// <summary>
    /// Finds swing highs and lows.
    /// </summary>
    /// <param name="bars">The series, in strictly ascending time order.</param>
    /// <param name="options">Detection options.</param>
    /// <returns>The pivots, in bar order.</returns>
    /// <exception cref="ArgumentException">The bars are not in strictly ascending time order.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The lookback is less than one, or the source is unset.</exception>
    /// <remarks>
    /// <para>
    /// A pivot must dominate <see cref="KeyLevelOptions.Lookback"/> bars to its <b>left</b> and
    /// <see cref="KeyLevelOptions.RightLookback"/> to its <b>right</b>, so a series shorter than
    /// <c>Lookback + RightLookback + 1</c> can hold none and the last <c>RightLookback</c> bars can never
    /// produce one. That trailing blindness is deliberate: a "pivot" confirmed only by the bars before it is
    /// a guess about the bars after it, and it repaints as soon as they arrive.
    /// </para>
    /// <para>
    /// <b>The two windows are separate because they are asked different questions</b> — the left is how much
    /// history the extreme stood clear of, the right is only whether it held — and the right one is also the
    /// lag, since every bar it waits for is a bar the level is reported late by. Symmetry was never a
    /// property of the method; it was what a single knob could express.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<SwingPivot> FindPivots(IReadOnlyList<Bar> bars, KeyLevelOptions options)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(options);
        RequireUsableOptions(options);

        IndicatorGuard.RequireStrictlyAscending(bars, nameof(bars));
        IndicatorGuard.RequireSingleContract(bars, nameof(bars));

        int left = options.Lookback;
        int right = options.RightLookback;

        if (bars.Count < left + right + 1)
        {
            return [];
        }

        (decimal[] highs, decimal[] lows) = PivotPrices(bars, options.Source);

        List<SwingPivot> pivots = [];

        for (int i = left; i < bars.Count - right; i++)
        {
            decimal high = highs[i];
            decimal low = lows[i];

            bool isHigh = true;
            bool isLow = true;
            decimal highestOther = decimal.MinValue;
            decimal lowestOther = decimal.MaxValue;

            for (int j = i - left; j <= i + right; j++)
            {
                if (j == i)
                {
                    continue;
                }

                if (highs[j] >= high)
                {
                    isHigh = false;
                }

                if (lows[j] <= low)
                {
                    isLow = false;
                }

                if (highs[j] > highestOther)
                {
                    highestOther = highs[j];
                }

                if (lows[j] < lowestOther)
                {
                    lowestOther = lows[j];
                }
            }

            if (isHigh)
            {
                pivots.Add(new SwingPivot(i, bars[i].OpenTime, high, KeyLevelKind.Resistance, high - highestOther));
            }
            else if (isLow)
            {
                pivots.Add(new SwingPivot(i, bars[i].OpenTime, low, KeyLevelKind.Support, lowestOther - low));
            }
        }

        return pivots;
    }

    /// <summary>
    /// Builds a zone around a pivot, sized in ATR multiples.
    /// </summary>
    /// <param name="pivot">The pivot.</param>
    /// <param name="atr">The ATR at the pivot bar. Must be positive.</param>
    /// <param name="options">Detection options.</param>
    /// <returns>The zone, or <see langword="null"/> when the pivot is below the significance floor.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="atr"/> is not positive.</exception>
    /// <remarks>
    /// Returning null rather than a zone with a zero significance keeps the filter in one place. A caller that
    /// received an insignificant zone would have to remember to drop it, and the one that forgot would merge it.
    /// </remarks>
    public static KeyLevelZone? ZoneFor(SwingPivot pivot, decimal atr, KeyLevelOptions options)
    {
        ArgumentNullException.ThrowIfNull(pivot);
        ArgumentNullException.ThrowIfNull(options);

        if (atr <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(atr), atr, "ATR must be positive to scale a zone.");
        }

        decimal significance = pivot.Prominence / atr;
        if (significance < options.MinSignificance)
        {
            return null;
        }

        decimal halfBand = atr * options.ZoneAtrMultiple / 2m;
        return new KeyLevelZone(
            Bottom: pivot.Price - halfBand,
            Top: pivot.Price + halfBand,
            Kind: pivot.Kind,
            FormedAtBucket: pivot.OpenTime,
            TouchCount: 1,
            Significance: significance);
    }

    /// <summary>
    /// Merges overlapping zones, whichever side of price each of them formed on.
    /// </summary>
    /// <param name="zones">The zones.</param>
    /// <returns>The merged zones, ordered by <see cref="KeyLevelZone.Bottom"/>.</returns>
    /// <remarks>
    /// <para>
    /// A merge keeps the <b>earliest</b> formation time — the level dates from when it was first respected, not
    /// from the most recent retest — the <b>strongest</b> significance, and the <b>sum</b> of touches. Taking
    /// the latest time instead would make every old level look new every time price came back to it.
    /// </para>
    /// <para>
    /// <b>It merges ACROSS kinds, and until gh#245 it did not.</b> A support and a resistance occupying the
    /// same prices are one piece of structure that has been traded from both sides, and reporting them as two
    /// zones stacked on each other said the opposite: two ordinary levels with one touch each, where the
    /// prices had in fact been respected twice.
    /// </para>
    /// <para>
    /// <b>The merged zone takes its kind from its strongest constituent</b>, ties going to the earlier
    /// formation and then to price, so the answer does not depend on the order the zones arrived in. That
    /// matters in exactly one place and it is not a corner: <see cref="ApplyClose"/> relabels every zone
    /// against the close <i>except</i> one the close sits inside, where it keeps the formation's own reading
    /// — and a merged cross-kind zone is precisely the shape price tends to be inside.
    /// </para>
    /// <para>
    /// The result is in <see cref="KeyLevelZone.Bottom"/> order because the sweep runs in that order and each
    /// merged zone reports the lowest bottom in its chain; there is no second sort. That is pinned rather
    /// than argued — the cases in <c>KeyLevelsTests</c> hand the zones in deliberately out of order.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<KeyLevelZone> MergeOverlapping(IReadOnlyList<KeyLevelZone> zones)
    {
        ArgumentNullException.ThrowIfNull(zones);

        List<KeyLevelZone> merged = [];
        KeyLevelZone? open = null;

        // The kind carrier is one of the ORIGINAL zones, never the running merge: the running merge already
        // carries a decided kind, so comparing against it would let an early pair fix the answer for a chain
        // whose strongest member has not arrived yet.
        KeyLevelZone? strongest = null;

        foreach (KeyLevelZone zone in zones.OrderBy(z => z.Bottom).ThenBy(z => z.Top))
        {
            if (open is null)
            {
                (open, strongest) = (zone, zone);
                continue;
            }

            if (open.Overlaps(zone))
            {
                strongest = Stronger(strongest!, zone);
                open = new KeyLevelZone(
                    Bottom: Math.Min(open.Bottom, zone.Bottom),
                    Top: Math.Max(open.Top, zone.Top),
                    Kind: strongest.Kind,
                    FormedAtBucket: open.FormedAtBucket <= zone.FormedAtBucket ? open.FormedAtBucket : zone.FormedAtBucket,
                    TouchCount: open.TouchCount + zone.TouchCount,
                    Significance: Math.Max(open.Significance, zone.Significance));
            }
            else
            {
                merged.Add(open);
                (open, strongest) = (zone, zone);
            }
        }

        if (open is not null)
        {
            merged.Add(open);
        }

        return merged;
    }

    /// <summary>
    /// Which of two zones a merge takes its kind from.
    /// </summary>
    /// <param name="left">One zone.</param>
    /// <param name="right">The other.</param>
    /// <returns>The zone whose kind the merge carries.</returns>
    /// <remarks>
    /// Significance first, because it is the one score comparable across instruments (<c>R-3.2</c>) and it is
    /// already what a merge keeps. Every later step exists only to make the answer total: without them two
    /// equally significant zones would be separated by enumeration order, and two identical requests could
    /// disagree about a level's polarity with nothing to say which was right.
    /// </remarks>
    private static KeyLevelZone Stronger(KeyLevelZone left, KeyLevelZone right)
    {
        if (left.Significance != right.Significance)
        {
            return left.Significance > right.Significance ? left : right;
        }

        if (left.FormedAtBucket != right.FormedAtBucket)
        {
            return left.FormedAtBucket < right.FormedAtBucket ? left : right;
        }

        if (left.Bottom != right.Bottom)
        {
            return left.Bottom < right.Bottom ? left : right;
        }

        return left.Top != right.Top
            ? left.Top < right.Top ? left : right
            : left.Kind <= right.Kind ? left : right;
    }

    /// <summary>
    /// Re-labels zones against the current price.
    /// </summary>
    /// <param name="zones">The zones.</param>
    /// <param name="close">The current price.</param>
    /// <returns>The zones, each labelled support or resistance relative to <paramref name="close"/>.</returns>
    /// <remarks>
    /// A level's <i>kind</i> is not a property of how it formed — it is a property of where price is now. An
    /// old resistance that price has broken above is today's support, and reporting it as resistance would put
    /// a ceiling underneath the market.
    /// </remarks>
    public static IReadOnlyList<KeyLevelZone> ApplyClose(IReadOnlyList<KeyLevelZone> zones, decimal close)
    {
        ArgumentNullException.ThrowIfNull(zones);

        List<KeyLevelZone> relabelled = new(zones.Count);
        foreach (KeyLevelZone zone in zones)
        {
            KeyLevelKind kind = zone.Top < close ? KeyLevelKind.Support
                : zone.Bottom > close ? KeyLevelKind.Resistance
                : zone.Kind; // Price is inside the zone; leave the formation's own reading alone.

            relabelled.Add(zone with { Kind = kind });
        }

        return relabelled;
    }

    /// <summary>
    /// Drops zones wider than <see cref="KeyLevelOptions.MaxZoneWidthPercent"/> of their own midpoint.
    /// </summary>
    /// <param name="zones">The zones.</param>
    /// <param name="options">Detection options.</param>
    /// <returns>The zones narrow enough to be levels, in the order they arrived.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The width cap is not positive.</exception>
    /// <remarks>
    /// <para>
    /// <b>Belongs after <see cref="MergeOverlapping"/>, and the reason is measurable rather than stylistic.</b>
    /// Every zone reaching the merge is exactly <c>ZoneAtrMultiple × ATR</c> wide, so the merge is the only
    /// stage that can widen one without limit. <c>ApplyWidthCap_FiresOnWhatTheMergeProduced_NotOnWhatWentIntoIt</c>
    /// is that claim as a run: on its fixture the three pre-merge zones all clear the shipped 2.5% cap and
    /// the single zone they chain into does not.
    /// </para>
    /// <para>
    /// <b>Measured as a percentage of the zone's own price</b>, so "too wide" means the same thing on ES and
    /// on NQ, on the same reasoning that sizes the zone in ATR multiples in the first place. The comparison
    /// is written as a multiplication rather than a division so that it is exact in
    /// <see langword="decimal"/> — a percentage of most prices is not.
    /// </para>
    /// <para>
    /// <b>An over-wide zone is dropped, not narrowed to the cap.</b> A narrowed one would report edges no
    /// pivot produced, centred on a midpoint chosen by arithmetic rather than by the market — the same
    /// substitution a filled-in indicator makes, and just as unreadable from outside.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<KeyLevelZone> ApplyWidthCap(
        IReadOnlyList<KeyLevelZone> zones,
        KeyLevelOptions options)
    {
        ArgumentNullException.ThrowIfNull(zones);
        ArgumentNullException.ThrowIfNull(options);
        RequireUsableOptions(options);

        return [.. zones.Where(zone =>
            (zone.Top - zone.Bottom) * 100m <= options.MaxZoneWidthPercent * zone.Midpoint)];
    }

    /// <summary>
    /// Keeps at most <see cref="KeyLevelOptions.MaxLevels"/> zones — the most significant ones.
    /// </summary>
    /// <param name="zones">The zones.</param>
    /// <param name="options">Detection options.</param>
    /// <returns>The survivors, ordered by <see cref="KeyLevelZone.Bottom"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The level cap is less than one.</exception>
    /// <remarks>
    /// <para>
    /// <b>Ranked by significance, which is prominence in ATR multiples (<c>R-3.2</c>)</b> — the one score
    /// this server already treats as comparable across instruments and volatility regimes. Ranking by
    /// distance from the close was the alternative and it was rejected: it drops the strongest structure in
    /// the series whenever price happens to be far from it, which is exactly when a reader wants to see it.
    /// </para>
    /// <para>
    /// <b>The survivors come back in price order</b>, because that is the order the tool has always reported
    /// and the cap is a filter rather than a re-ordering. Selecting is a total order to the last step, so the
    /// same zones handed in a different order give the same answer; a cap that fell back on enumeration order
    /// would let two identical requests differ, which is the reproducibility ADR-0013 rests on.
    /// </para>
    /// <para>
    /// <b>What it removes is gone, not summarised.</b> No survivor grows to cover a dropped neighbour's
    /// prices and none inherits its touches — <c>maxLevels</c> is reported beside the answer instead, so a
    /// caller holding exactly that many levels can tell a capped list from a complete one.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<KeyLevelZone> ApplyLevelCap(
        IReadOnlyList<KeyLevelZone> zones,
        KeyLevelOptions options)
    {
        ArgumentNullException.ThrowIfNull(zones);
        ArgumentNullException.ThrowIfNull(options);
        RequireUsableOptions(options);

        return zones.Count <= options.MaxLevels
            ? zones
            : [.. zones
                .OrderByDescending(zone => zone.Significance)
                .ThenByDescending(zone => zone.TouchCount)
                .ThenBy(zone => zone.Bottom)
                .ThenBy(zone => zone.Top)
                .Take(options.MaxLevels)
                .OrderBy(zone => zone.Bottom)
                .ThenBy(zone => zone.Top)];
    }

    /// <summary>
    /// The whole pipeline: pivots, ATR-scaled zones, merge, then label against the last close.
    /// </summary>
    /// <param name="bars">The series, in strictly ascending time order.</param>
    /// <param name="atr">ATR aligned one-to-one with <paramref name="bars"/>; nulls are skipped.</param>
    /// <param name="options">Detection options.</param>
    /// <returns>The levels, ordered by price.</returns>
    /// <exception cref="ArgumentException">The ATR series is not aligned with the bars.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The lookback is less than one, or the source is unset.</exception>
    /// <remarks>
    /// <b>The options are checked here as well as in <see cref="FindPivots"/>, and before the empty-series
    /// exit rather than after it.</b> An empty store is exactly when a bad source is invisible: the pipeline
    /// returns no levels, which is what an empty store looks like anyway, and the configuration that would
    /// have measured pivots from a price nobody chose is never mentioned. A refusal that only fires once
    /// somebody has bars is a refusal that fires after the mistake has been in place for a while.
    /// </remarks>
    public static IReadOnlyList<KeyLevelZone> Detect(
        IReadOnlyList<Bar> bars,
        IReadOnlyList<decimal?> atr,
        KeyLevelOptions options)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(atr);
        ArgumentNullException.ThrowIfNull(options);
        RequireUsableOptions(options);

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

        List<KeyLevelZone> zones = [];
        foreach (SwingPivot pivot in FindPivots(bars, options))
        {
            // No ATR at the pivot means no scale to size or score the zone with. Skipping is the honest
            // outcome; substituting a default would produce a level whose significance is fiction.
            if (atr[pivot.BarIndex] is not { } scale || scale <= 0m)
            {
                continue;
            }

            if (ZoneFor(pivot, scale, options) is { } zone)
            {
                zones.Add(zone);
            }
        }

        IReadOnlyList<KeyLevelZone> withinWidth = ApplyWidthCap(MergeOverlapping(zones), options);
        return ApplyLevelCap(ApplyClose(withinWidth, bars[^1].Close), options);
    }

    /// <summary>
    /// Refuses a set of options no pipeline stage could honour.
    /// </summary>
    /// <param name="options">The options.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Either lookback is less than one, the source is unset or outside the vocabulary, the width cap is not
    /// positive, or the level cap is less than one.
    /// </exception>
    /// <remarks>
    /// <b>The source check is on the vocabulary, not on <see cref="PivotSource.Unknown"/> alone.</b>
    /// <see cref="PivotPrices"/> selects High/Low and Body explicitly and treats <i>everything else</i> as
    /// Heikin-Ashi, so a cast integer outside the enum reads a real price series and returns a level set that
    /// looks like any other — measured from a source nobody named. <c>Unknown</c> was refused from the start
    /// because a zero default picks a source by accident; the same sentence is true of
    /// <c>(PivotSource)99</c>, which had been arriving as a silent Heikin-Ashi.
    /// </remarks>
    private static void RequireUsableOptions(KeyLevelOptions options)
    {
        if (options.Lookback < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Lookback, "The lookback must be at least 1.");
        }

        if (options.RightLookback < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.RightLookback,
                "The right lookback must be at least 1. It is the confirmation window, so a pivot with none "
                + "is a guess about the bars that have not arrived (R-3.4).");
        }

        if (options.MaxZoneWidthPercent <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MaxZoneWidthPercent,
                "The zone width cap must be greater than 0 percent. At or below zero no zone can be reported "
                + "at all, and an empty level set reads as a market with no structure.");
        }

        if (options.MaxLevels < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MaxLevels,
                "The level cap must be at least 1. A cap of zero empties every level set this server can "
                + "produce, and an empty one is indistinguishable from a market that has produced none.");
        }

        if (options.Source == PivotSource.Unknown)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Source, "The pivot source must be set explicitly.");
        }

        if (!PivotSources.IsServable(options.Source))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.Source,
                "The pivot source must be one of " + PivotSources.KnownNames + ".");
        }
    }

    private static (decimal[] Highs, decimal[] Lows) PivotPrices(IReadOnlyList<Bar> bars, PivotSource source)
    {
        decimal[] highs = new decimal[bars.Count];
        decimal[] lows = new decimal[bars.Count];

        if (source == PivotSource.HighLow)
        {
            for (int i = 0; i < bars.Count; i++)
            {
                highs[i] = bars[i].High;
                lows[i] = bars[i].Low;
            }

            return (highs, lows);
        }

        if (source == PivotSource.Body)
        {
            for (int i = 0; i < bars.Count; i++)
            {
                highs[i] = Math.Max(bars[i].Open, bars[i].Close);
                lows[i] = Math.Min(bars[i].Open, bars[i].Close);
            }

            return (highs, lows);
        }

        // Heikin-Ashi. The close is the bar's own average; the open is the previous HA candle's midpoint, which
        // is what carries the smoothing forward. The first candle has no predecessor, so it seeds from itself.
        decimal haOpen = (bars[0].Open + bars[0].Close) / 2m;
        for (int i = 0; i < bars.Count; i++)
        {
            Bar bar = bars[i];
            decimal haClose = (bar.Open + bar.High + bar.Low + bar.Close) / 4m;
            if (i > 0)
            {
                haOpen = (haOpen + PreviousHaClose(bars, i)) / 2m;
            }

            highs[i] = Math.Max(haOpen, haClose);
            lows[i] = Math.Min(haOpen, haClose);
        }

        return (highs, lows);

        static decimal PreviousHaClose(IReadOnlyList<Bar> series, int index)
        {
            Bar previous = series[index - 1];
            return (previous.Open + previous.High + previous.Low + previous.Close) / 4m;
        }
    }
}

/// <summary>The <see cref="ILevelMethod"/> face of <see cref="KeyLevels"/> — swing pivots.</summary>
/// <remarks>
/// <para>
/// A face, not a second implementation. <see cref="Detect"/> hands its arguments straight to
/// <see cref="KeyLevels.Detect(IReadOnlyList{Bar}, IReadOnlyList{decimal?}, KeyLevelOptions)"/>, so the
/// numbers this returns are the numbers the pipeline has always returned and the hand-checked fixtures that
/// pin the four stages keep pinning them unchanged.
/// </para>
/// <para>
/// The roll refusal <c>ILevelMethod</c> requires is reached through
/// <see cref="KeyLevels.FindPivots"/>, which calls <see cref="IndicatorGuard.RequireSingleContract"/> before
/// it looks at a single price. Deliberately not repeated here: a second call would change which of two
/// refusals a caller sees when a series is both spliced and handed a misaligned ATR, and the property that
/// matters — that a spliced series is refused — is swept over the whole catalogue rather than assumed of any
/// one method.
/// </para>
/// </remarks>
public sealed class SwingLevelMethod : ILevelMethod
{
    /// <summary>The method name, <c>swing</c>.</summary>
    public string Name => "swing";

    /// <inheritdoc />
    public IReadOnlyList<KeyLevelZone> Detect(
        IReadOnlyList<Bar> bars,
        IReadOnlyList<decimal?> atr,
        KeyLevelOptions options) => KeyLevels.Detect(bars, atr, options);
}
