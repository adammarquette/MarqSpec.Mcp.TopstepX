namespace MarqSpec.Mcp.TopstepX.Domain.MarketData;

/// <summary>Which tape-derived level a volume method reports.</summary>
/// <remarks>
/// Four names rather than one parameterised entry, following the pivot family's MACD precedent: a
/// caller asks for <c>volume-poc</c>, not for <c>volume</c> with a variant argument, and a score
/// naming its constituents names the one that contributed. <see cref="Unknown"/> is never valid.
/// </remarks>
public enum VolumeLevelKind
{
    /// <summary>Unset. Never a valid value — a zero default would pick a level by accident.</summary>
    Unknown = 0,

    /// <summary>The point of control — the price with the most volume.</summary>
    PointOfControl = 1,

    /// <summary>The highest price in the 70% value area.</summary>
    ValueAreaHigh = 2,

    /// <summary>The lowest price in the 70% value area.</summary>
    ValueAreaLow = 3,

    /// <summary>Every other price the tape actually traded.</summary>
    Traded = 4,
}

/// <summary>
/// Volume-derived price levels — point of control, value-area high/low, and the other prices the
/// tape printed.
/// </summary>
/// <remarks>
/// <para>
/// <b>The honest input is the profile, never OHLCV.</b> A POC computed from bars moves when the
/// spreading rule changes. A POC computed from prints is a price the front contract traded
/// (gh#213, gh#319). <see cref="Bar.Volume"/> is not read.
/// </para>
/// <para>
/// <b>The line-to-zone tolerance is <see cref="KeyLevelOptions.ZoneAtrMultiple"/></b>, the value
/// gh#257 settled for <c>session</c> and the pivot family consumed rather than re-deciding. The
/// width that turns a swing pivot into a zone is the width that turns a volume line into one.
/// </para>
/// <para>
/// <b>Significance is volume prominence, not price prominence.</b> A computed volume line has no
/// swing-style dominance. The number is the price's volume as a multiple of the mean volume at
/// prices that traded — <c>R-3.2</c> allows a method that does not find levels by dominance to
/// state what its score means. The scale that sizes the zone is still the ATR at the newest bar
/// that has one, so a missing ATR yields no level rather than a band scaled by a substitute.
/// </para>
/// <para>
/// <b>All four declare <see cref="FamilyName"/></b>, so a confluence score treats them as one
/// budget: POC, VAH and VAL landing on a price is one tape read three ways, not three
/// confirmations (gh#259, gh#319).
/// </para>
/// </remarks>
public static class VolumeLevels
{
    /// <summary>The correlation family every volume method declares.</summary>
    public const string FamilyName = "volume";

    /// <summary>
    /// Why <c>get_key_levels</c> names a volume method that had no tape for the window.
    /// </summary>
    public const string NoTapeReason = "no tape";

    /// <summary>The name a kind is registered and asked for under.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The lowercase, stable method name.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The kind is unset or outside the vocabulary.</exception>
    public static string NameOf(VolumeLevelKind kind)
    {
        RequireServableKind(kind);

        return kind switch
        {
            VolumeLevelKind.PointOfControl => "volume-poc",
            VolumeLevelKind.ValueAreaHigh => "volume-vah",
            VolumeLevelKind.ValueAreaLow => "volume-val",
            VolumeLevelKind.Traded => "volume-traded",
            _ => throw UnnamedKind(kind),
        };
    }

    /// <summary>
    /// Detects the volume levels a bound profile carries, using the bars only for the roll
    /// guard, ATR scale, and the close that labels a zone.
    /// </summary>
    /// <param name="bars">The series, in strictly ascending time order and from one contract.</param>
    /// <param name="atr">ATR aligned one-to-one with <paramref name="bars"/>.</param>
    /// <param name="options">Detection options. The line-to-zone width is <see cref="KeyLevelOptions.ZoneAtrMultiple"/>.</param>
    /// <param name="profile">The tape-derived profile. Never invented from <see cref="Bar.Volume"/>.</param>
    /// <param name="kind">Which named level to report.</param>
    /// <returns>The zones, ordered by price.</returns>
    public static IReadOnlyList<KeyLevelZone> Compute(
        IReadOnlyList<Bar> bars,
        IReadOnlyList<decimal?> atr,
        KeyLevelOptions options,
        VolumeProfile profile,
        VolumeLevelKind kind)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(atr);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(profile);
        RequireUsableOptions(options);
        KeyLevels.RequireUsableOptions(options);
        RequireServableKind(kind);

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

        if (!TryScale(bars, atr, out decimal scale, out DateTimeOffset formedAt))
        {
            return [];
        }

        List<KeyLevelZone> lines = [];
        int priceCount = profile.ByPrice.Count;
        long total = profile.TotalVolume;

        foreach (VolumeAtPrice level in PricesFor(profile, kind))
        {
            decimal significance = total == 0 ? 0m : (decimal)level.Volume * priceCount / total;
            if (significance < options.MinSignificance)
            {
                continue;
            }

            decimal halfBand = scale * options.ZoneAtrMultiple / 2m;
            KeyLevelKind seed = kind switch
            {
                VolumeLevelKind.ValueAreaHigh => KeyLevelKind.Resistance,
                VolumeLevelKind.ValueAreaLow => KeyLevelKind.Support,
                _ => KeyLevelKind.Unknown,
            };

            lines.Add(new KeyLevelZone(
                Bottom: level.Price - halfBand,
                Top: level.Price + halfBand,
                Kind: seed,
                FormedAtBucket: formedAt,
                TouchCount: 1,
                Significance: significance,
                Period: PeriodOf(kind)));
        }

        IReadOnlyList<KeyLevelZone> withinWidth =
            KeyLevels.ApplyWidthCap(KeyLevels.MergeOverlapping(lines), options);
        return KeyLevels.ApplyLevelCap(KeyLevels.ApplyClose(withinWidth, bars[^1].Close), options);
    }

    private static IEnumerable<VolumeAtPrice> PricesFor(VolumeProfile profile, VolumeLevelKind kind)
    {
        return kind switch
        {
            VolumeLevelKind.PointOfControl => [At(profile, profile.PointOfControl)],
            VolumeLevelKind.ValueAreaHigh => [At(profile, profile.ValueAreaHigh)],
            VolumeLevelKind.ValueAreaLow => [At(profile, profile.ValueAreaLow)],
            VolumeLevelKind.Traded => profile.ByPrice.Where(level =>
                level.Price != profile.PointOfControl
                && level.Price != profile.ValueAreaHigh
                && level.Price != profile.ValueAreaLow),
            _ => throw UnnamedKind(kind),
        };
    }

    private static VolumeAtPrice At(VolumeProfile profile, decimal price)
    {
        foreach (VolumeAtPrice level in profile.ByPrice)
        {
            if (level.Price == price)
            {
                return level;
            }
        }

        throw new ArgumentException(
            "The profile does not contain volume at "
            + price.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".",
            nameof(profile));
    }

    private static string? PeriodOf(VolumeLevelKind kind) => kind switch
    {
        VolumeLevelKind.PointOfControl => "poc",
        VolumeLevelKind.ValueAreaHigh => "vah",
        VolumeLevelKind.ValueAreaLow => "val",
        _ => null,
    };

    private static bool TryScale(
        IReadOnlyList<Bar> bars,
        IReadOnlyList<decimal?> atr,
        out decimal scale,
        out DateTimeOffset formedAt)
    {
        for (int i = bars.Count - 1; i >= 0; i--)
        {
            if (atr[i] is { } value && value > 0m)
            {
                scale = value;
                formedAt = bars[i].OpenTime;
                return true;
            }
        }

        scale = 0m;
        formedAt = default;
        return false;
    }

    private static void RequireServableKind(VolumeLevelKind kind)
    {
        if (kind == VolumeLevelKind.Unknown || !Enum.IsDefined(kind))
        {
            throw UnnamedKind(kind);
        }
    }

    private static ArgumentOutOfRangeException UnnamedKind(VolumeLevelKind kind) =>
        new(nameof(kind),
            kind,
            "The volume level must be one of PointOfControl, ValueAreaHigh, ValueAreaLow, Traded. "
            + "Unknown is what an unset value binds to, and a value outside the vocabulary is not "
            + "resolved to a default either.");

    private static void RequireUsableOptions(KeyLevelOptions options)
    {
        if (options.ZoneAtrMultiple <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.ZoneAtrMultiple,
                "The zone width must be positive; a volume line needs a width before it is a zone.");
        }

        if (options.MinSignificance < 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), options.MinSignificance, "The significance floor cannot be negative.");
        }
    }
}

/// <summary>The <see cref="ILevelMethod"/> face of <see cref="VolumeLevels"/> — one named tape level.</summary>
/// <param name="kind">Which named level this method reports.</param>
/// <remarks>
/// <para>
/// <b>One class, four registrations.</b> Constructed without a profile, so every method stays inside
/// <c>LevelMethodCatalog.All</c>. <see cref="Detect"/> reads the request-scoped
/// <see cref="VolumeProfileScope"/> after the roll and ordering guards.
/// </para>
/// </remarks>
public sealed class VolumeLevelMethod(VolumeLevelKind kind) : ILevelMethod
{
    private readonly string _name = VolumeLevels.NameOf(kind);

    /// <summary>The method name — <c>volume-poc</c>, <c>volume-vah</c>, and so on.</summary>
    public string Name => _name;

    /// <summary>The correlation family, <c>volume</c> — shared by all four.</summary>
    public string Family => VolumeLevels.FamilyName;

    /// <inheritdoc />
    public IReadOnlyList<KeyLevelZone> Detect(
        IReadOnlyList<Bar> bars,
        IReadOnlyList<decimal?> atr,
        KeyLevelOptions options)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(atr);
        ArgumentNullException.ThrowIfNull(options);

        // Roll and order before the bind: a spliced series must refuse for the stated reason
        // even when nothing is bound, which is what the catalogue sweeps count.
        IndicatorGuard.RequireStrictlyAscending(bars, nameof(bars));
        IndicatorGuard.RequireSingleContract(bars, nameof(bars));

        VolumeProfile profile = VolumeProfileScope.Require();
        return VolumeLevels.Compute(bars, atr, options, profile, kind);
    }
}
