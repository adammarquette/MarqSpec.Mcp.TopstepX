using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;

namespace MarqSpec.Mcp.TopstepX.Tests.Domain;

/// <summary>
/// The calculations, against hand-checked numbers.
/// </summary>
/// <remarks>
/// Every expected value here is worked out by hand from the definition, not captured from a run. A test that
/// asserts the code does what the code does passes forever and proves nothing — and an indicator that is
/// quietly four points off looks exactly like one that is right.
/// <para>
/// The series are chosen so every expectation is <b>exact in decimal</b>. A period whose smoothing factor is
/// non-terminating (EMA at period 2 gives 2/3) would force approximate comparisons and hide real drift.
/// </para>
/// </remarks>
public sealed class IndicatorTests
{
    private static Bar Bar(int index, decimal high, decimal low, decimal close, long volume = 1) =>
        new(new DateTimeOffset(2026, 8, 18, 9, 0, 0, TimeSpan.Zero).AddMinutes(5 * index),
            close, high, low, close, volume);

    private static IReadOnlyList<Bar> Closes(params decimal[] closes) =>
        [.. closes.Select((c, i) => Bar(i, c, c, c))];

    /// <summary>The same series with two bars exchanged — same bars, same prices, one pair out of order.</summary>
    /// <remarks>
    /// Each refusal below transposes the fixture from the hand-checked case beside it rather than inventing a
    /// new one. That is what makes the green half of the two-run rule free: the very same bars, in order, are
    /// pinned against worked-out numbers a few lines up, so the guard is shown to refuse the disorder and
    /// nothing else.
    /// </remarks>
    private static IReadOnlyList<Bar> Transposed(IReadOnlyList<Bar> bars, int first, int second) =>
        [.. bars.Select((bar, i) => i == first ? bars[second] : i == second ? bars[first] : bar)];

    // ── ATR ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Atr_IsNull_UntilThePeriodIsSatisfied()
    {
        // True range needs a previous close, so the first bar can never carry a value: the seed needs
        // period + 1 bars, and the first value lands at index `period`.
        IReadOnlyList<Bar> bars =
        [
            Bar(0, 10, 8, 9),
            Bar(1, 11, 9, 10),
            Bar(2, 12, 10, 11),
            Bar(3, 13, 11, 12),
        ];

        IReadOnlyList<decimal?> atr = AverageTrueRange.Compute(bars, 3);

        atr.Should().HaveCount(4);
        atr[0].Should().BeNull();
        atr[1].Should().BeNull();
        atr[2].Should().BeNull();
        atr[3].Should().Be(2m); // (2 + 2 + 2) / 3
    }

    [Fact]
    public void Atr_AppliesWilderSmoothingAfterTheSeed()
    {
        IReadOnlyList<Bar> bars =
        [
            Bar(0, 10, 8, 9),
            Bar(1, 11, 9, 10),
            Bar(2, 12, 10, 11),
            Bar(3, 13, 11, 12),
            Bar(4, 20, 12, 19), // true range 8
        ];

        IReadOnlyList<decimal?> atr = AverageTrueRange.Compute(bars, 3);

        // Wilder: (previous * (period - 1) + newTrueRange) / period = (2 * 2 + 8) / 3 = 4.
        atr[4].Should().Be(4m);
    }

    [Fact]
    public void TrueRange_TakesTheGapIntoAccount()
    {
        // The two gap terms are what make this "true" range rather than the bar's own high-low. A measure
        // that ignored the previous close would read a violent gap open as a quiet bar.
        Bar previous = Bar(0, 10, 9, 10);
        Bar gapped = Bar(1, 20, 19, 20);

        AverageTrueRange.TrueRange(gapped, previous).Should().Be(10m); // 20 - 10, not 20 - 19
    }

    [Fact]
    public void Atr_RefusesAShuffledSeries()
    {
        // A shuffled series does not fail on its own -- it quietly computes a different, wrong number.
        IReadOnlyList<Bar> shuffled = [Bar(2, 12, 10, 11), Bar(0, 10, 8, 9), Bar(1, 11, 9, 10)];

        Action compute = () => AverageTrueRange.Compute(shuffled, 2);

        compute.Should().Throw<ArgumentException>().WithMessage("*ascending*");
    }

    // ── RSI ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Rsi_IsOneHundred_WhenEveryChangeIsAGain()
    {
        RelativeStrengthIndex.Compute(Closes(10m, 11m, 12m), 2)[2].Should().Be(100m);
    }

    [Fact]
    public void Rsi_IsFifty_WhenTheWindowIsFlat()
    {
        // A flat window is NEUTRAL. The general formula would return 0 or 100 here by accident, reading a
        // motionless market as a maximal signal.
        RelativeStrengthIndex.Compute(Closes(10m, 10m, 10m), 2)[2].Should().Be(50m);
    }

    [Fact]
    public void Rsi_IsZero_WhenEveryChangeIsALoss()
    {
        RelativeStrengthIndex.Compute(Closes(12m, 11m, 10m), 2)[2].Should().Be(0m);
    }

    [Fact]
    public void Rsi_SmoothsGainsAndLossesAfterTheSeed()
    {
        // Closes 10, 11, 12, 11 at period 2.
        // Seed: gains 1 + 1 = 2, losses 0 -> RSI 100. avgGain 1, avgLoss 0.
        // Next: change -1 -> avgGain (1*1 + 0)/2 = 0.5, avgLoss (0*1 + 1)/2 = 0.5 -> 100 * 0.5 / 1 = 50.
        IReadOnlyList<decimal?> rsi = RelativeStrengthIndex.Compute(Closes(10m, 11m, 12m, 11m), 2);

        rsi[2].Should().Be(100m);
        rsi[3].Should().Be(50m);
    }

    [Fact]
    public void Rsi_IsNull_BeforeThePeriodIsSatisfied()
    {
        IReadOnlyList<decimal?> rsi = RelativeStrengthIndex.Compute(Closes(10m, 11m, 12m), 2);

        rsi[0].Should().BeNull();
        rsi[1].Should().BeNull();
    }

    [Fact]
    public void Rsi_RefusesATransposedSeries()
    {
        // Rsi_SmoothsGainsAndLossesAfterTheSeed's four closes, with two bars exchanged. RSI reads changes
        // pairwise and then smooths them, so a transposition flips the sign of one change and Wilder carries
        // the consequence to the end of the series.
        Action compute = () => RelativeStrengthIndex.Compute(Transposed(Closes(10m, 11m, 12m, 11m), 1, 2), 2);

        compute.Should().Throw<ArgumentException>().WithMessage("*ascending*");
    }

    // ── Moving averages ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Sma_AveragesTheWindow()
    {
        IReadOnlyList<decimal?> sma = MovingAverages.Simple(Closes(1m, 2m, 3m, 4m), 2);

        sma[0].Should().BeNull();
        sma[1].Should().Be(1.5m);
        sma[2].Should().Be(2.5m);
        sma[3].Should().Be(3.5m);
    }

    [Fact]
    public void Ema_SeedsFromTheSimpleAverageOfTheFirstWindow()
    {
        // Period 3 gives an exact smoothing factor of 0.5, so every expectation below is exact.
        // Seed at index 2 = (1 + 2 + 3) / 3 = 2. Then 2 + 0.5*(4-2) = 3, and 3 + 0.5*(5-3) = 4.
        IReadOnlyList<decimal?> ema = MovingAverages.Exponential(Closes(1m, 2m, 3m, 4m, 5m), 3);

        ema[1].Should().BeNull();
        ema[2].Should().Be(2m);
        ema[3].Should().Be(3m);
        ema[4].Should().Be(4m);
    }

    [Fact]
    public void MovingAverages_AreNull_WhenTheSeriesIsShorterThanThePeriod()
    {
        MovingAverages.Simple(Closes(1m, 2m), 5).Should().AllSatisfy(v => v.Should().BeNull());
        MovingAverages.Exponential(Closes(1m, 2m), 5).Should().AllSatisfy(v => v.Should().BeNull());
    }

    [Fact]
    public void Sma_RefusesATransposedSeries()
    {
        // Sma_AveragesTheWindow's series, two bars exchanged. The simple average is where an out-of-order
        // series hides best: a window holding BOTH exchanged bars sums the same multiset and returns the
        // identical number, so the disorder is invisible at that index and shows only in the windows
        // straddling the pair. A spot-check of the wrong bar agrees, which is why the guard is not optional.
        Action compute = () => MovingAverages.Simple(Transposed(Closes(1m, 2m, 3m, 4m), 1, 2), 2);

        compute.Should().Throw<ArgumentException>().WithMessage("*ascending*");
    }

    [Fact]
    public void Ema_RefusesATransposedSeries()
    {
        // Ema_SeedsFromTheSimpleAverageOfTheFirstWindow's series, two bars exchanged across the seed window's
        // edge. Unlike the simple average this never re-converges: the seed itself differs, and every value
        // the series carries is wrong from the first one onward.
        Action compute = () => MovingAverages.Exponential(Transposed(Closes(1m, 2m, 3m, 4m, 5m), 2, 3), 3);

        compute.Should().Throw<ArgumentException>().WithMessage("*ascending*");
    }

    // ── MACD ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MacdLine_StartsWithTheSlowerOfItsTwoInputs()
    {
        // The fast EMA warms first. Subtracting a warm fast from a not-yet-warm slow would have to invent
        // the slow value, so the line starts when the SLOWER of the two does.
        IReadOnlyList<Bar> bars = [.. Enumerable.Range(1, 40).Select(i => Bar(i, i, i, i))];

        IReadOnlyList<decimal?> line = Macd.Line(bars, 26);

        line.Take(25).Should().AllSatisfy(v => v.Should().BeNull());
        line[25].Should().NotBeNull();
    }

    [Fact]
    public void MacdSignal_WarmsUpAfterTheLine()
    {
        // The warm-ups stack: the signal is a 9-period EMA OF the line, so it cannot start until the line
        // has produced nine values.
        IReadOnlyList<Bar> bars = [.. Enumerable.Range(1, 60).Select(i => Bar(i, i, i, i))];

        IReadOnlyList<decimal?> signal = Macd.Signal(bars, 26);

        signal[32].Should().BeNull();
        signal[33].Should().NotBeNull(); // 25 (line seed) + 9 - 1
    }

    [Fact]
    public void MacdLine_RefusesASlowPeriodThatIsNotSlower()
    {
        Action compute = () => Macd.Line(Closes(1m, 2m, 3m), Macd.FastPeriod);
        compute.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ── Bollinger ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BollingerBands_SitTwoPopulationDeviationsEitherSide()
    {
        // Closes 1 and 2 at period 2: mean 1.5, population variance 0.25, deviation 0.5, band 2 * 0.5 = 1.
        IReadOnlyList<Bar> bars = Closes(1m, 2m);

        BollingerBands.Middle(bars, 2)[1].Should().Be(1.5m);
        BollingerBands.StandardDeviation(bars, 2)[1].Should().Be(0.5m);
        BollingerBands.Upper(bars, 2)[1].Should().Be(2.5m);
        BollingerBands.Lower(bars, 2)[1].Should().Be(0.5m);
    }

    [Fact]
    public void BollingerStandardDeviation_RefusesATransposedSeries()
    {
        // BollingerBands_SitTwoPopulationDeviationsEitherSide's two closes, exchanged.
        //
        // Called on StandardDeviation DIRECTLY, and that is the point rather than a shortcut: Upper and Lower
        // go through Band, which computes Middle first — and Middle is MovingAverages.Simple. A case entered
        // through a band would therefore be reddened by the moving average's guard and would leave the
        // deviation's own call site exactly as unpinned as it is today.
        Action compute = () => BollingerBands.StandardDeviation(Transposed(Closes(1m, 2m), 0, 1), 2);

        compute.Should().Throw<ArgumentException>().WithMessage("*ascending*");
    }

    // ── VWAP ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Vwap_AccumulatesAcrossTheSession()
    {
        BarSessionCalendar calendar = BarSessionCalendar.Parse("16:00", []);
        DateTimeOffset open = MarketClock.FromMarket(new DateOnly(2026, 8, 18), new TimeOnly(9, 0));

        // Typical price is (H + L + C) / 3: 11 then 14.
        IReadOnlyList<Bar> bars =
        [
            new(open, 10m, 12m, 9m, 12m, 10),
            new(open.AddMinutes(5), 12m, 15m, 12m, 15m, 10),
        ];

        IReadOnlyList<decimal?> vwap = VolumeWeightedAveragePrice.Compute(bars, calendar);

        vwap[0].Should().Be(11m);            // 110 / 10
        vwap[1].Should().Be(12.5m);          // (110 + 140) / 20
    }

    [Fact]
    public void Vwap_ResetsAtTheStartOfEachSession()
    {
        // The anchor is the point. A VWAP that carried across the overnight break would be a different
        // statistic that traders do not read the same way.
        BarSessionCalendar calendar = BarSessionCalendar.Parse("16:00", []);
        DateTimeOffset tuesday = MarketClock.FromMarket(new DateOnly(2026, 8, 18), new TimeOnly(15, 0));
        DateTimeOffset wednesday = MarketClock.FromMarket(new DateOnly(2026, 8, 19), new TimeOnly(9, 0));

        IReadOnlyList<Bar> bars =
        [
            new(tuesday, 100m, 100m, 100m, 100m, 10),
            new(wednesday, 200m, 200m, 200m, 200m, 10),
        ];

        IReadOnlyList<decimal?> vwap = VolumeWeightedAveragePrice.Compute(bars, calendar);

        vwap[0].Should().Be(100m);
        vwap[1].Should().Be(200m); // not 150 -- a new session starts a new accumulator
    }

    [Fact]
    public void Vwap_IsNull_OutsideASession()
    {
        BarSessionCalendar calendar = BarSessionCalendar.Parse("16:00", []);
        DateTimeOffset saturday = MarketClock.FromMarket(new DateOnly(2026, 8, 22), new TimeOnly(10, 0));

        IReadOnlyList<decimal?> vwap =
            VolumeWeightedAveragePrice.Compute([new(saturday, 100m, 100m, 100m, 100m, 10)], calendar);

        vwap[0].Should().BeNull();
    }

    [Fact]
    public void Vwap_RefusesATransposedSeries()
    {
        // Vwap_AccumulatesAcrossTheSession's two bars, exchanged. VWAP is a running accumulation and the
        // session anchor is read in series order, so disorder does not merely reorder the output: it restarts
        // the day's average early, and every value after the disorder is computed from a partial session.
        // It cannot carry volume across sessions — TradeDateFor is a pure per-bar function and the accumulator
        // resets on every change of session — so no permutation adds a bar to a total labelled with another
        // trade date. Measured, not reasoned from the shape of the loop.
        BarSessionCalendar calendar = BarSessionCalendar.Parse("16:00", []);
        DateTimeOffset open = MarketClock.FromMarket(new DateOnly(2026, 8, 18), new TimeOnly(9, 0));

        IReadOnlyList<Bar> bars =
        [
            new(open, 10m, 12m, 9m, 12m, 10),
            new(open.AddMinutes(5), 12m, 15m, 12m, 15m, 10),
        ];

        Action compute = () => VolumeWeightedAveragePrice.Compute(Transposed(bars, 0, 1), calendar);

        compute.Should().Throw<ArgumentException>().WithMessage("*ascending*");
    }

    // ── Decimal maths ────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(0.25)]
    [InlineData(1000000)]
    public void Sqrt_IsExactForPerfectSquares(double value)
    {
        decimal input = (decimal)value;
        decimal root = DecimalMath.Sqrt(input);

        (root * root).Should().BeApproximately(input, 0.0000000001m);
    }

    [Fact]
    public void Sqrt_RefusesANegativeValue()
    {
        Action root = () => DecimalMath.Sqrt(-1m);
        root.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ── The IIndicator wrappers ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void IndicatorNames_AreLowercaseAndStable()
    {
        // The name is a STORAGE KEY. Renaming one orphans every row already written under the old name,
        // where they read back as an absence rather than an error.
        new AtrIndicator(14).Name.Should().Be("atr");
        new RsiIndicator(14).Name.Should().Be("rsi");
        new SmaIndicator(20).Name.Should().Be("sma");
        new EmaIndicator(20).Name.Should().Be("ema");
        new MacdLineIndicator(26).Name.Should().Be("macd");
        new MacdSignalIndicator(26).Name.Should().Be("macd-signal");
        new MacdHistogramIndicator(26).Name.Should().Be("macd-histogram");
        new BollingerUpperIndicator(20).Name.Should().Be("bb-upper");
        new BollingerMiddleIndicator(20).Name.Should().Be("bb-middle");
        new BollingerLowerIndicator(20).Name.Should().Be("bb-lower");
    }

    [Fact]
    public void VwapIndicator_HasPeriodZero_BecauseItIsAnchoredRatherThanWindowed()
    {
        // Period 0 keeps VWAP's rows from colliding with a windowed indicator of the same name, and says
        // plainly that it takes no period rather than inventing one.
        new VwapIndicator(BarSessionCalendar.Parse("16:00", [])).Period.Should().Be(0);
    }
}
