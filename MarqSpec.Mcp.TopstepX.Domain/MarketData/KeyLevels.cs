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
/// How many bars either side a pivot must dominate. Larger means fewer, more structural levels.
/// </param>
/// <param name="Source">Which price on a bar the pivot is measured from.</param>
/// <param name="ZoneAtrMultiple">The zone's full width, in ATR multiples.</param>
/// <param name="MinSignificance">
/// The smallest prominence, in ATR multiples, worth reporting. Filtering here rather than at the caller keeps
/// the noise out of the merge step, where two insignificant pivots could otherwise combine into something that
/// looks meaningful.
/// </param>
public sealed record KeyLevelOptions(
    int Lookback = 5,
    PivotSource Source = PivotSource.HeikinAshiBody,
    decimal ZoneAtrMultiple = 0.5m,
    decimal MinSignificance = 0.5m);

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
    /// A pivot must dominate <paramref name="options"/>'s lookback on <b>both</b> sides, which means the last
    /// <c>Lookback</c> bars can never produce one. That is deliberate: a "pivot" confirmed only by the bars
    /// before it is a guess about the bars after it, and it repaints as soon as they arrive.
    /// </remarks>
    public static IReadOnlyList<SwingPivot> FindPivots(IReadOnlyList<Bar> bars, KeyLevelOptions options)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(options);

        if (options.Lookback < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Lookback, "The lookback must be at least 1.");
        }

        if (options.Source == PivotSource.Unknown)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Source, "The pivot source must be set explicitly.");
        }

        IndicatorGuard.RequireStrictlyAscending(bars, nameof(bars));
        IndicatorGuard.RequireSingleContract(bars, nameof(bars));

        if (bars.Count < (2 * options.Lookback) + 1)
        {
            return [];
        }

        (decimal[] highs, decimal[] lows) = PivotPrices(bars, options.Source);

        List<SwingPivot> pivots = [];
        int lookback = options.Lookback;

        for (int i = lookback; i < bars.Count - lookback; i++)
        {
            decimal high = highs[i];
            decimal low = lows[i];

            bool isHigh = true;
            bool isLow = true;
            decimal highestOther = decimal.MinValue;
            decimal lowestOther = decimal.MaxValue;

            for (int j = i - lookback; j <= i + lookback; j++)
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
    /// Merges overlapping zones of the same kind.
    /// </summary>
    /// <param name="zones">The zones.</param>
    /// <returns>The merged zones, ordered by <see cref="KeyLevelZone.Bottom"/>.</returns>
    /// <remarks>
    /// A merge keeps the <b>earliest</b> formation time — the level dates from when it was first respected, not
    /// from the most recent retest — the <b>strongest</b> significance, and the <b>sum</b> of touches. Taking
    /// the latest time instead would make every old level look new every time price came back to it.
    /// </remarks>
    public static IReadOnlyList<KeyLevelZone> MergeOverlapping(IReadOnlyList<KeyLevelZone> zones)
    {
        ArgumentNullException.ThrowIfNull(zones);

        List<KeyLevelZone> merged = [];

        foreach (IGrouping<KeyLevelKind, KeyLevelZone> group in zones.GroupBy(z => z.Kind))
        {
            List<KeyLevelZone> ordered = [.. group.OrderBy(z => z.Bottom)];
            KeyLevelZone? open = null;

            foreach (KeyLevelZone zone in ordered)
            {
                if (open is null)
                {
                    open = zone;
                    continue;
                }

                if (open.Overlaps(zone))
                {
                    open = new KeyLevelZone(
                        Bottom: Math.Min(open.Bottom, zone.Bottom),
                        Top: Math.Max(open.Top, zone.Top),
                        Kind: open.Kind,
                        FormedAtBucket: open.FormedAtBucket <= zone.FormedAtBucket ? open.FormedAtBucket : zone.FormedAtBucket,
                        TouchCount: open.TouchCount + zone.TouchCount,
                        Significance: Math.Max(open.Significance, zone.Significance));
                }
                else
                {
                    merged.Add(open);
                    open = zone;
                }
            }

            if (open is not null)
            {
                merged.Add(open);
            }
        }

        return [.. merged.OrderBy(z => z.Bottom)];
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
    /// The whole pipeline: pivots, ATR-scaled zones, merge, then label against the last close.
    /// </summary>
    /// <param name="bars">The series, in strictly ascending time order.</param>
    /// <param name="atr">ATR aligned one-to-one with <paramref name="bars"/>; nulls are skipped.</param>
    /// <param name="options">Detection options.</param>
    /// <returns>The levels, ordered by price.</returns>
    /// <exception cref="ArgumentException">The ATR series is not aligned with the bars.</exception>
    public static IReadOnlyList<KeyLevelZone> Detect(
        IReadOnlyList<Bar> bars,
        IReadOnlyList<decimal?> atr,
        KeyLevelOptions options)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(atr);

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

        return ApplyClose(MergeOverlapping(zones), bars[^1].Close);
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
