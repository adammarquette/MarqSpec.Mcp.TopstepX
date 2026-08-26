using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;

namespace MarqSpec.Mcp.TopstepX.Tests.Domain;

/// <summary>
/// The <c>swing</c> method — the existing detector behind the <see cref="ILevelMethod"/> seam.
/// </summary>
/// <remarks>
/// <para>
/// Two claims, and they are different claims. The first is that <c>swing</c> produces <b>hand-derived</b>
/// numbers, worked out below from the definition rather than captured from a run. The second is that it
/// produces <b>exactly</b> what the static pipeline produced before the seam existed — which is what "moved
/// behind it with behaviour unchanged" means, and the only claim a wrapper can falsify on its own.
/// </para>
/// <para>
/// The four stages themselves are pinned by <see cref="KeyLevelsTests"/>, at length and against hand-checked
/// numbers. Nothing here re-pins them: this file is about the seam, not about the arithmetic underneath it.
/// </para>
/// </remarks>
public sealed class SwingLevelMethodTests
{
    /// <summary>The open time of bar <paramref name="index"/> — five-minute buckets from a fixed origin.</summary>
    private static DateTimeOffset At(int index) =>
        new DateTimeOffset(2026, 8, 18, 9, 0, 0, TimeSpan.Zero).AddMinutes(5 * index);

    /// <summary>A bar written as just its high and its low, opening at the low and closing at the high.</summary>
    private static Bar HighLowBar(int index, decimal high, decimal low) =>
        new(At(index), Open: low, High: high, Low: low, Close: high, Volume: 100);

    /// <summary>The options the fixture below is derived under.</summary>
    /// <remarks>
    /// Lookback 2 keeps the dominance window five bars wide. The zone multiple and the significance floor are
    /// the production defaults, so the numbers below are the numbers the tool serves.
    /// </remarks>
    private static KeyLevelOptions Options => new(
        Lookback: 2,
        Source: PivotSource.HighLow,
        ZoneAtrMultiple: 0.5m,
        MinSignificance: 0.5m);

    // ──────────────────────────────────────────────────────────────────────────────────────────────────
    //  THE WORKED FIXTURE — five bars, read High/Low, lookback 2, ATR 2 at every bar.
    //
    //    i:      0     1     2     3     4
    //    high: 104   106   110   106   104
    //    low:  100   102   106   102   100
    //
    //  A pivot needs `i` in [2, 5 - 2), so index 2 is the only eligible one:
    //
    //    i=2  high 110 vs {104, 106, 106, 104} -> dominates. Prominence 110 - 106 = 4. Resistance.
    //         (low 106 does not: bar 0's 100 is under it, so this bar is a high, not a low.)
    //
    //  One pivot. Then, with ATR 2 at bar 2:
    //
    //    significance = prominence / atr      = 4 / 2           = 2
    //    half-band    = atr * multiple / 2    = 2 * 0.5 / 2     = 0.5
    //    zone         = [110 - 0.5, 110 + 0.5]                  = [109.5, 110.5]
    //
    //  Nothing merges — there is one zone. The last close is bar 4's, which is 104, and 109.5 > 104, so the
    //  zone stays resistance. Touch count 1; formed at bar 2's open time.
    // ──────────────────────────────────────────────────────────────────────────────────────────────────
    private static IReadOnlyList<Bar> Fixture =>
    [
        HighLowBar(0, high: 104, low: 100),
        HighLowBar(1, high: 106, low: 102),
        HighLowBar(2, high: 110, low: 106),
        HighLowBar(3, high: 106, low: 102),
        HighLowBar(4, high: 104, low: 100),
    ];

    /// <summary>ATR of 2 at every bar, aligned one-to-one with <see cref="Fixture"/>.</summary>
    private static IReadOnlyList<decimal?> FlatAtr => [2m, 2m, 2m, 2m, 2m];

    [Fact]
    public void Swing_IsTheNameTheExistingDetectorIsRegisteredUnder()
    {
        // The name is identity: a caller asks for a method by it, and a confluence score names its
        // constituents by it. Renaming one silently changes what an existing request means.
        new SwingLevelMethod().Name.Should().Be("swing");
    }

    [Fact]
    public void Swing_ProducesTheHandDerivedZoneForTheWorkedFixture()
    {
        IReadOnlyList<KeyLevelZone> zones = new SwingLevelMethod().Detect(Fixture, FlatAtr, Options);

        zones.Should().ContainSingle();
        KeyLevelZone zone = zones[0];
        zone.Bottom.Should().Be(109.5m);
        zone.Top.Should().Be(110.5m);
        zone.Midpoint.Should().Be(110m);
        zone.Kind.Should().Be(KeyLevelKind.Resistance);
        zone.Significance.Should().Be(2m);
        zone.TouchCount.Should().Be(1);
        zone.FormedAtBucket.Should().Be(At(2));
    }

    [Fact]
    public void Swing_ReturnsExactlyWhatTheStaticPipelineReturns()
    {
        // "Behaviour unchanged" stated as an assertion. The seam is a face on KeyLevels, not a second
        // implementation of it, and a wrapper that reordered, filtered or re-scored would go red here while
        // every hand-derived case above still passed.
        IReadOnlyList<KeyLevelZone> throughTheSeam = new SwingLevelMethod().Detect(Fixture, FlatAtr, Options);
        IReadOnlyList<KeyLevelZone> direct = KeyLevels.Detect(Fixture, FlatAtr, Options);

        throughTheSeam.Should().BeEquivalentTo(direct, o => o.WithStrictOrdering());
    }

    [Fact]
    public void Swing_RefusesAnAtrSeriesThatIsNotAlignedWithTheBars()
    {
        // The seam passes its arguments through rather than repairing them. A shorter ATR series is a caller
        // bug, and padding it would size some zone off the wrong bar's volatility.
        Action detect = () => new SwingLevelMethod().Detect(Fixture, [2m, 2m], Options);

        detect.Should().Throw<ArgumentException>().WithMessage("*align*");
    }
}
