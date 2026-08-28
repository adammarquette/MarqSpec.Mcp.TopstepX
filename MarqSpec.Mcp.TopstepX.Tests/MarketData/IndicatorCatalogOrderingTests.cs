using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// Every indicator this server actually computes refuses a series whose bars are out of time order — swept,
/// not listed, and watched failing on an indicator that skips the guard.
/// </summary>
/// <remarks>
/// <para>
/// The sibling of <see cref="IndicatorCatalogRollTests"/>, one guard over, and it exists for the same reason:
/// <see cref="IndicatorGuard"/>'s remarks claim both preconditions sit on the shared path so that <b>a new
/// indicator inherits them rather than remembering them</b>. That is a claim about indicators nobody has
/// written yet, so listing today's eleven cannot pin it — a test naming them stays green on the day someone
/// adds a twelfth that computes straight through a shuffle.
/// </para>
/// <para>
/// <b>The sweep is necessary and not sufficient, and that is measured rather than assumed.</b>
/// <see cref="BollingerBands.StandardDeviation"/> carries a call to
/// <see cref="IndicatorGuard.RequireStrictlyAscending"/> that nothing reachable from this catalogue can
/// exercise: all three registered Bollinger indicators enter through <c>Middle</c> or <c>Band</c>, and
/// <c>Band</c> computes <c>Middle</c> — which <i>is</i> <see cref="MovingAverages.Simple"/> — first, so the
/// moving average's guard throws before the deviation's is reached. Delete the deviation's line and this
/// sweep stays green. <c>IndicatorTests.BollingerStandardDeviation_RefusesATransposedSeries</c> calls that
/// entry point directly for exactly this reason; a sweep on its own would have left the call site as
/// unpinned as it was found.
/// </para>
/// <para>
/// The green half of the two-run rule is <see cref="IndicatorCatalogRollTests"/>'s
/// <c>EveryConfiguredIndicator_StillComputesValuesOverASingleContractSeries</c> — the same catalogue over an
/// ordinary ascending series. Part of it is still <c>NotThrow</c>, the absence of an exception, and that is
/// the right assertion for one of the two failures it exists to catch: a guard that refused <i>everything</i>
/// would pass the sweep above and still break the server. It is not duplicated here, because a second copy
/// would prove the same thing twice and drift separately.
/// </para>
/// <para>
/// <b>Its other half asserts that values come back, and until gh#285 nothing in this tier did.</b> That
/// sweep's fixture was <c>Spliced().Take(30)</c> — thirty bars, against two of the catalogue's eleven
/// members that declare a warm-up of 35: <c>macd-signal</c> and <c>macd-histogram</c>, both
/// <c>MacdSlowPeriod + Macd.SignalPeriod</c> = 26 + 9. Both answered it with 0 non-null values out of 30, so
/// for those two it passed because nothing was computed rather than because something was. The hole that
/// left is the twelfth indicator whose warm-up arithmetic is wrong: it returns all-nulls forever, it does
/// refuse a splice and it does refuse a shuffle, so every sweep here stays green while
/// <c>get_indicators</c> answers with an empty series on every instrument. That fixture is now
/// <c>IndicatorCatalog.All.Max(i =&gt; i.WarmupBars)</c> plus headroom rather than a chosen number, the sweep
/// counts values, and a fake returning all-nulls is watched reddening it.
/// </para>
/// </remarks>
public sealed class IndicatorCatalogOrderingTests
{
    private static DateTimeOffset SessionStart =>
        MarketClock.FromMarket(new DateOnly(2026, 8, 18), new TimeOnly(9, 0)).ToUniversalTime();

    private static IndicatorCatalog Catalog() =>
        new(Options.Create(new IndicatorOptions()), BarSessionCalendar.Parse("16:00", []));

    /// <summary>Sixty bars under one contract, with two adjacent bars exchanged in the middle.</summary>
    /// <remarks>
    /// Long enough that the <b>longest</b> warm-up in the catalogue is satisfied — 35, <c>macd-signal</c> and
    /// <c>macd-histogram</c> — so a refusal cannot be an artefact of a series too short to compute over. The
    /// enumeration this replaced named Bollinger at 20 and MACD's slow leg at 26 and stopped there; that same
    /// short reading is what left the roll suite's single-contract fixture five bars under its own slowest
    /// member (gh#285). Sixty is still chosen rather than derived the way
    /// <c>IndicatorCatalogRollTests.RunLength</c> now is — this fixture also fixes the transposed pair at
    /// indices 30 and 31, and deriving one without the other is half a change — so an indicator warming up in
    /// more than sixty would put this sentence back where it started. <b>One contract throughout</b>,
    /// deliberately: were it spliced, the roll guard would refuse it and the sweep would pass without the
    /// ordering guard being reached at all, which is the vacuous-fixture failure PR #252 found next door.
    /// </remarks>
    private static IReadOnlyList<Bar> Transposed()
    {
        List<Bar> bars =
        [
            .. Enumerable.Range(0, 60).Select(i =>
            {
                decimal close = 100m + (i % 5);
                return new Bar(
                    SessionStart.AddMinutes(5 * i), close, close + 1m, close - 1m, close, 1_000,
                    "CON.F.US.EP.U26");
            }),
        ];

        (bars[30], bars[31]) = (bars[31], bars[30]);
        return bars;
    }

    /// <summary>
    /// Whether an indicator refuses a series whose bars are out of order, for the stated reason.
    /// </summary>
    /// <param name="indicator">The indicator under test.</param>
    /// <returns><see langword="true"/> when it refused the disorder.</returns>
    /// <remarks>
    /// Written as a predicate rather than as an assertion so that both halves of the two-run rule call
    /// <b>the same code</b>. Any exception other than the ordering refusal propagates and fails the caller:
    /// an indicator that threw for some unrelated reason has not been shown to carry the guard.
    /// </remarks>
    private static bool RefusesATransposedSeries(IIndicator indicator)
    {
        try
        {
            indicator.Compute(Transposed());
        }
        catch (ArgumentException e) when (e.Message.Contains("ascending", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    [Fact]
    public void EveryConfiguredIndicator_RefusesATransposedSeries()
    {
        IndicatorCatalog catalog = Catalog();

        catalog.All.Should().NotBeEmpty("the sweep must actually cover something");

        foreach (IIndicator indicator in catalog.All)
        {
            RefusesATransposedSeries(indicator).Should().BeTrue(
                indicator.Name + " computed a value over bars that are not in time order. Nothing about the "
                + "answer says so: the window slid over neighbours that are not neighbours, and what came "
                + "back is an ordinary-looking number that is simply wrong.");
        }
    }

    [Fact]
    public void TheOrderingSweepGoesRed_WhenAnIndicatorBypassesTheGuard()
    {
        GuardlessIndicator bypassing = new();

        // It does not fail, which is the whole problem: it answers with ordinary-looking numbers computed in
        // whatever order the bars arrived in. Reproduced here so that the catch is checkable rather than
        // merely present.
        bypassing.Compute(Transposed()).Should().Contain(
            value => value.HasValue,
            "an indicator that skips the guard answers a disordered series instead of refusing it");

        RefusesATransposedSeries(bypassing).Should().BeFalse(
            "the sweep must go RED on an indicator that bypasses IndicatorGuard.RequireStrictlyAscending — a "
            + "gate proven only against code that already passes it is a gate nobody has watched fail");
    }

    /// <summary>
    /// A deliberately defective indicator: it computes without asking whether the bars are in order.
    /// </summary>
    /// <remarks>
    /// It is never registered anywhere. It exists so the sweep above can be watched failing, which is the
    /// difference between a gate that is proven and a gate that is merely present.
    /// </remarks>
    private sealed class GuardlessIndicator : IIndicator
    {
        public string Name => "guardless";

        public int Period => 1;

        public int WarmupBars => 1;

        public IReadOnlyList<decimal?> Compute(IReadOnlyList<Bar> bars)
        {
            ArgumentNullException.ThrowIfNull(bars);

            return [.. bars.Select(bar => (decimal?)bar.Close)];
        }
    }
}
