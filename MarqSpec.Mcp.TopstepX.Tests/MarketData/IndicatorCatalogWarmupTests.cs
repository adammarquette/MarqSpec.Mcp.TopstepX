using System.Globalization;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.Extensions.Options;
using Xunit.Sdk;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// Every catalogue member's <see cref="IIndicator.WarmupBars"/> is the bar count at which
/// <see cref="IIndicator.Compute"/> first yields a value — swept, not listed, and watched failing
/// on an indicator that declares one more bar than it uses.
/// </summary>
/// <remarks>
/// <para>
/// The interface says the minimum, not an upper bound. Over a clean series of N bars that therefore
/// means <c>nonNull = N - WarmupBars + 1</c>. A declared minimum one too high is conservative for a
/// fetch window and silent for a fixture derived from <c>Max(WarmupBars)</c>, which is how the two
/// MACD legs hid: they returned a value one bar earlier than they claimed.
/// </para>
/// <para>
/// Measured on <c>fc55f3b</c> at N = <see cref="ProbeLength"/>, one <c>Compute</c> per member over
/// the same single-contract session series the roll suite uses. Warm-up / non-null:
/// </para>
/// <para>
/// atr 15/26, rsi 15/26, sma 20/21, ema 20/21, macd 26/15, vwap 1/40, bb-upper 20/21,
/// bb-middle 20/21, bb-lower 20/21 — each <c>N - WarmupBars + 1</c>. macd-signal 35/7 and
/// macd-histogram 35/7 — each <c>N - WarmupBars + 2</c>. First value on both legs after 34 bars;
/// <c>Period + Macd.SignalPeriod</c> double-counts the bar the two windows share. The same
/// off-by-one was first tabled on <c>cd98d24</c> (gh#299). Corrected to
/// <c>Period + SignalPeriod - 1</c>, those two become 34/7 and the formula holds for every
/// member.
/// </para>
/// </remarks>
public sealed class IndicatorCatalogWarmupTests
{
    /// <summary>A stated N long enough that every shipped warm-up has produced more than one value.</summary>
    private const int ProbeLength = 40;

    private static DateTimeOffset SessionStart =>
        MarketClock.FromMarket(new DateOnly(2026, 8, 18), new TimeOnly(9, 0)).ToUniversalTime();

    private static IndicatorCatalog Catalog() =>
        new(Options.Create(new IndicatorOptions()), BarSessionCalendar.Parse("16:00", []));

    /// <summary>One contiguous run of one contract, sawtoothing over five prices.</summary>
    private static IReadOnlyList<Bar> SingleContract(int count) =>
        [.. Enumerable.Range(0, count).Select(i =>
        {
            decimal close = 100m + (i % 5);
            return new Bar(
                SessionStart.AddMinutes(5 * i), close, close + 1m, close - 1m, close, 1_000,
                "CON.F.US.EP.U26");
        })];

    /// <summary>
    /// Asserts that every indicator handed in answers <paramref name="bars"/> with exactly
    /// <c>N - WarmupBars + 1</c> values.
    /// </summary>
    /// <param name="indicators">The indicators to sweep.</param>
    /// <param name="bars">The series they are measured over.</param>
    /// <remarks>
    /// One routine so both halves of the two-run rule call the same code. Every mismatch is
    /// collected before anything is asserted, so a reader sees both MACD legs rather than the
    /// first name and takes that for the whole list.
    /// </remarks>
    private static void AssertNonNullCountMatchesWarmup(
        IReadOnlyList<IIndicator> indicators, IReadOnlyList<Bar> bars)
    {
        indicators.Should().NotBeEmpty("the sweep must actually cover something");

        int n = bars.Count;
        List<string> mismatches =
        [
            .. indicators
                .Select(indicator =>
                {
                    int nonNull = indicator.Compute(bars).Count(value => value.HasValue);
                    int expected = n - indicator.WarmupBars + 1;
                    return (indicator, nonNull, expected);
                })
                .Where(row => row.nonNull != row.expected)
                .Select(row =>
                    row.indicator.Name + " (warm-up "
                    + row.indicator.WarmupBars.ToString(CultureInfo.InvariantCulture)
                    + "): expected "
                    + row.expected.ToString(CultureInfo.InvariantCulture)
                    + " non-null, got "
                    + row.nonNull.ToString(CultureInfo.InvariantCulture)),
        ];

        mismatches.Should().BeEmpty(
            "every configured indicator must satisfy nonNull = N - WarmupBars + 1 over "
            + n.ToString(CultureInfo.InvariantCulture)
            + " ordinary single-contract bars (measured shape; see the class remarks for the "
            + "table on fc55f3b), and these did not: "
            + string.Join(", ", mismatches)
            + ". The declared minimum is then not the bar at which Compute first yields a value.");
    }

    [Fact]
    public void EveryConfiguredIndicator_NonNullCountEqualsBarsMinusWarmupBarsPlusOne()
    {
        AssertNonNullCountMatchesWarmup(Catalog().All, SingleContract(ProbeLength));
    }

    [Fact]
    public void TheWarmupProbeGoesRed_WhenDeclaredWarmupIsOneTooHigh()
    {
        OffByOneWarmupIndicator inflated = new();
        IReadOnlyList<Bar> bars = SingleContract(ProbeLength);

        int nonNull = inflated.Compute(bars).Count(value => value.HasValue);
        int formula = ProbeLength - inflated.WarmupBars + 1;
        nonNull.Should().Be(formula + 1,
            "the fake must reproduce the MACD-leg shape: a value one bar earlier than WarmupBars, "
            + "so the formula is short by one and the probe has something to catch");

        Action sweep = () => AssertNonNullCountMatchesWarmup([inflated], bars);

        sweep.Should().Throw<XunitException>(
            "the sweep must go RED on an indicator whose WarmupBars is one more than the bar at "
            + "which Compute first yields a value — a gate proven only against code that already "
            + "passes it is a gate nobody has watched fail")
            .WithMessage(
                "*" + inflated.Name + " (warm-up*",
                "the failure has to name the member whose declared minimum was wrong");
    }

    /// <summary>
    /// A deliberately defective indicator: it produces a first value one bar earlier than
    /// <see cref="WarmupBars"/> claims.
    /// </summary>
    /// <remarks>
    /// Registered nowhere. It is the failure the probe exists to catch — the same off-by-one
    /// <c>macd-signal</c> and <c>macd-histogram</c> had when their warm-up was the sum of the
    /// two periods rather than the sum minus the shared bar.
    /// </remarks>
    private sealed class OffByOneWarmupIndicator : IIndicator
    {
        public string Name => "off-by-one-warmup";

        public int Period => 4;

        public int WarmupBars => Period + 1;

        public IReadOnlyList<decimal?> Compute(IReadOnlyList<Bar> bars)
        {
            ArgumentNullException.ThrowIfNull(bars);

            return [.. bars.Select((bar, i) => i >= Period - 1 ? (decimal?)bar.Close : null)];
        }
    }
}
