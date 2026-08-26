using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// Every level method this server detects with refuses a spliced series — swept, not listed, and the sweep
/// itself is shown to fail on a method that bypasses the guard.
/// </summary>
/// <remarks>
/// <para>
/// An indicator <b>inherits</b> the roll guard: every implementation goes through the same shared compute
/// path, so <see cref="IndicatorCatalogRollTests"/> can sweep the catalogue and trust that a twelfth
/// indicator will be covered by construction. <b>A level method does not.</b> Each one detects its own way —
/// swing pivots, session extremes, arithmetic on a prior bar — so
/// <see cref="IndicatorGuard.RequireSingleContract"/> is a rule each must satisfy rather than a step each
/// inherits. That is trap 4 of gh#232: a method reached by a different path loses <c>R-3.5</c>
/// <i>without failing</i>, and a level computed across a roll looks exactly like an ordinary level.
/// </para>
/// <para>
/// So the sweep below is the only thing enforcing it, which means the sweep needs two runs rather than one
/// (Coding contract, Tests). <see cref="EveryRegisteredMethod_RefusesASplicedSeries"/> is the green half,
/// run against the real registered method rather than a stand-in.
/// <see cref="TheSweepGoesRed_WhenAMethodBypassesTheGuard"/> is the red half: the same check, applied to a
/// method that deliberately skips the guard, answers <see langword="false"/>. Without that second test this
/// file would prove only that today's one method happens to call the guard.
/// </para>
/// </remarks>
public sealed class LevelMethodCatalogRollTests
{
    private static DateTimeOffset SessionStart =>
        MarketClock.FromMarket(new DateOnly(2026, 8, 18), new TimeOnly(9, 0)).ToUniversalTime();

    /// <summary>
    /// Sixty bars under one symbol, the back half from a different contract forty points higher.
    /// </summary>
    /// <remarks>
    /// The same fixture shape as the indicator sweep, and long enough that the default lookback of 5 is
    /// satisfied on both sides of the seam. A series too short to produce a pivot would return nothing and
    /// pass this test without the guard ever being reached, which is a green test that proves nothing.
    /// </remarks>
    private static IReadOnlyList<Bar> Spliced() =>
        [.. Enumerable.Range(0, 60).Select(i =>
        {
            bool rolled = i >= 30;
            decimal close = (rolled ? 140m : 100m) + (i % 5);
            return new Bar(
                SessionStart.AddMinutes(5 * i),
                close,
                close + 1m,
                close - 1m,
                close,
                1_000,
                rolled ? "CON.F.US.EP.Z26" : "CON.F.US.EP.U26");
        })];

    /// <summary>An ATR of 2 at every bar, aligned one-to-one with the series.</summary>
    /// <remarks>
    /// Supplied so that a refusal is the roll guard's and nothing else's: an ATR series of the wrong length
    /// is refused too, with a different message, and a sweep that accepted either would be satisfied by the
    /// wrong failure.
    /// </remarks>
    private static IReadOnlyList<decimal?> FlatAtr(int count) => [.. Enumerable.Repeat((decimal?)2m, count)];

    /// <summary>
    /// Whether a method refuses a series that spans a contract roll, for the stated reason.
    /// </summary>
    /// <param name="method">The method under test.</param>
    /// <returns><see langword="true"/> when it refused the splice.</returns>
    /// <remarks>
    /// Written as a predicate rather than as an assertion so that both halves of the two-run rule can call
    /// <b>the same code</b>. Any exception other than the roll refusal propagates and fails the caller: a
    /// method that threw for some unrelated reason has not been shown to carry the guard.
    /// </remarks>
    private static bool RefusesASplicedSeries(ILevelMethod method)
    {
        IReadOnlyList<Bar> spliced = Spliced();

        try
        {
            method.Detect(spliced, FlatAtr(spliced.Count), new KeyLevelOptions());
        }
        catch (ArgumentException e) when (e.Message.Contains("contract roll", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    [Fact]
    public void EveryRegisteredMethod_RefusesASplicedSeries()
    {
        LevelMethodCatalog catalog = new();

        catalog.All.Should().NotBeEmpty("the sweep must actually cover something");

        foreach (ILevelMethod method in catalog.All)
        {
            RefusesASplicedSeries(method).Should().BeTrue(
                method.Name + " detected levels across a contract roll. Adjacent quarters do not trade at "
                + "the same price, so whatever it returned sits where neither contract has ever been.");
        }
    }

    [Fact]
    public void TheSweepGoesRed_WhenAMethodBypassesTheGuard()
    {
        GuardlessLevelMethod bypassing = new();
        IReadOnlyList<Bar> spliced = Spliced();

        // It does not fail, which is the whole problem: it answers with an ordinary-looking zone built across
        // the seam. This is the silent loss the sweep exists to catch, reproduced so the catch is checkable.
        bypassing.Detect(spliced, FlatAtr(spliced.Count), new KeyLevelOptions())
            .Should().ContainSingle("a method that skips the guard answers a spliced series instead of refusing it");

        RefusesASplicedSeries(bypassing).Should().BeFalse(
            "the sweep must go RED on a method that bypasses IndicatorGuard.RequireSingleContract — a gate "
            + "proven only against code that already passes it is a gate nobody has watched fail");
    }

    [Fact]
    public void EveryRegisteredMethod_StillDetectsOverASingleContractSeries()
    {
        // The other half of the guard. A method that refused everything would pass the sweep above and break
        // the tool, and the two failures look nothing alike from outside.
        IReadOnlyList<Bar> singleContract = [.. Spliced().Take(30)];

        foreach (ILevelMethod method in new LevelMethodCatalog().All)
        {
            Action detect = () => method.Detect(
                singleContract, FlatAtr(singleContract.Count), new KeyLevelOptions());

            detect.Should().NotThrow(method.Name + " refuses an ordinary single-contract series");
        }
    }

    /// <summary>
    /// A deliberately defective method: it detects without asking whether the series spans a roll.
    /// </summary>
    /// <remarks>
    /// It is never registered anywhere. It exists so the sweep above can be watched failing, which is the
    /// difference between a gate that is proven and a gate that is merely present.
    /// </remarks>
    private sealed class GuardlessLevelMethod : ILevelMethod
    {
        public string Name => "guardless";

        public IReadOnlyList<KeyLevelZone> Detect(
            IReadOnlyList<Bar> bars,
            IReadOnlyList<decimal?> atr,
            KeyLevelOptions options)
        {
            // No RequireSingleContract, on purpose. One zone spanning the whole series: across a roll that is
            // the gap between two quarters reported as a level, and nothing about it looks wrong.
            decimal top = bars.Max(b => b.High);
            decimal bottom = bars.Min(b => b.Low);
            return [new KeyLevelZone(bottom, top, KeyLevelKind.Resistance, bars[0].OpenTime, bars.Count, 1m)];
        }
    }
}
