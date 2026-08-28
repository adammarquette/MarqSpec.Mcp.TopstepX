using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// Every level method this server detects with refuses a spliced series and still finds levels in a clean
/// one — swept, not listed, with both sweeps watched failing on a method that breaks them.
/// </summary>
/// <remarks>
/// <para>
/// An indicator <b>inherits</b> the roll guard: every implementation goes through the same shared compute
/// path, so <see cref="IndicatorCatalogRollTests"/> can sweep the catalogue and trust that a twelfth
/// indicator will be covered by construction. <b>A level method does not.</b> Each one detects its own way —
/// swing pivots, session extremes, arithmetic on a prior bar — so refusing a spliced series is a rule each
/// must satisfy rather than a step each inherits. That is trap 4 of gh#232: a method reached by a different
/// path loses <c>R-3.5</c> <i>without failing</i>, and a level computed across a roll looks exactly like an
/// ordinary level.
/// </para>
/// <para>
/// So these sweeps are the only thing enforcing it, which means each needs two runs rather than one (Coding
/// contract, Tests). Both are built the same way: a private predicate, asserted <see langword="true"/> for
/// every registered method, and asserted <see langword="false"/> for a deliberately broken one that is
/// registered nowhere. The red half is what makes the green half worth reading — a sweep proven only against
/// the code that already passes it is a sweep nobody has watched fail.
/// </para>
/// <para>
/// <b>The second sweep is not decoration.</b> A guard that refused everything would pass the first sweep and
/// break the tool, and so would a method whose <c>Detect</c> body is <c>return [];</c> —
/// <c>get_key_levels</c> would answer "no levels" for it on every instrument, forever, green. That is
/// exactly the failure <see cref="LevelMethodCatalog"/> names in its own XML: a name that returns nothing is
/// indistinguishable from a market that has produced no structure, and the second reads as a conclusion.
/// Each registered method is separately pinned against hand-derived numbers — <c>SwingLevelMethodTests</c>,
/// <c>SessionLevelMethodTests</c> and <c>PivotLevelMethodTests</c> — so a silent <c>return [];</c> would go
/// red there too; the hole opens the moment a sweep is the only thing covering a method, which is the whole
/// reason this card built one.
/// </para>
/// <para>
/// <b>It has already been paid for twice.</b> Registering <c>session</c> (gh#257) turned
/// <see cref="EveryRegisteredMethod_StillDetectsOverASingleContractSeries"/> red on the fixture as it stood:
/// twenty-one bars inside a single trade date carry no prior day, no prior week and no finished session leg,
/// so a session method could find nothing in them and would have shipped answering every instrument with an
/// empty level set. Registering the five <c>pivot-*</c> methods (gh#258) turned the same test red again and
/// for the same reason — a pivot is arithmetic on a <b>finished</b> prior session, and a run beginning at
/// the current session's own reopen contains none. Measured on that branch before the repair: the detection
/// sweep failed naming <c>pivot-classic</c>, and the other three tests in this file stayed green. Both times
/// the failure message's own instruction — extend the fixture — is what <see cref="SessionStart"/>
/// records, and neither time was the sweep relaxed to meet it.
/// </para>
/// </remarks>
public sealed class LevelMethodCatalogRollTests
{
    /// <summary>The catalogue, built with the session calendar <c>session</c> is anchored to.</summary>
    private static LevelMethodCatalog Catalog() => new(BarSessionCalendar.Parse("16:00", []));

    /// <summary>
    /// 17:00 Central on Sunday 16 August 2026 — the moment Monday's session reopens.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The origin has moved twice, and this file's detection sweep moved it both times.</b> It began at
    /// 09:00 on Tuesday the 18th. Registering <c>session</c> moved it to 17:00 on Monday the 17th (gh#257),
    /// because the old origin put every bar inside one trade date with nothing before them — a series a
    /// session method can find nothing in — and
    /// <see cref="EveryRegisteredMethod_StillDetectsOverASingleContractSeries"/> went red saying exactly
    /// that: <i>extend the fixture so it has something to find</i>.
    /// </para>
    /// <para>
    /// <b>Registering the pivot family moved it again, with the same message and the same repair
    /// (gh#258).</b> All five are arithmetic on one <b>finished</b> prior session, and a run beginning at
    /// the current session's own reopen holds none of one. Two things changed together, and only together
    /// do they buy a finished session: the bars are now an <b>hour</b> apart rather than five minutes, and
    /// the run starts one session earlier. Forty-one hourly bars from Monday's reopen cover the whole of
    /// Monday the 17th and the first seventeen hours of Tuesday's session — measured, the calendar puts
    /// indices 0 to 22 on trade date 2026-08-17, index 23 in the maintenance window with no trade date at
    /// all, and indices 24 to 40 on 2026-08-18.
    /// </para>
    /// <para>
    /// <b>Not one price moved</b>, which is why every price and score below is unchanged and only the
    /// timestamps are new. A five-minute run of this length could not have been made to work at any origin:
    /// forty-one five-minute bars cover three hours and twenty-five minutes, and a session on this calendar
    /// runs from 17:00 to 16:00, which is twenty-three hours.
    /// </para>
    /// </remarks>
    private static DateTimeOffset SessionStart =>
        MarketClock.FromMarket(new DateOnly(2026, 8, 16), new TimeOnly(17, 0)).ToUniversalTime();

    /// <summary>Bars per contiguous single-contract run.</summary>
    /// <remarks>
    /// <b>Forty-one, and it was twenty-one until the shipped lookback became asymmetric (gh#245).</b> The
    /// defaults are 20 bars of left dominance and 15 of right confirmation, so a series needs
    /// <c>20 + 15 + 1 = 36</c> bars before it can hold a single pivot and the eligible indices are
    /// <c>[20, 41 - 15)</c>. Twenty-one bars held none, and
    /// <see cref="EveryRegisteredMethod_StillDetectsOverASingleContractSeries"/> went red saying so — the
    /// same failure, and the same repair its own message prescribes, that registering <c>session</c> paid
    /// for before it. The run is extended rather than the sweep relaxed: a sweep that passed smaller options
    /// than the server ships would stop exercising what the server ships.
    /// </remarks>
    private const int RunLength = 41;

    /// <summary>The index, within a run, of the one bar that stands clear of everything around it.</summary>
    /// <remarks>
    /// Twenty — the first index the asymmetric window admits, and the middle of the run. It has 20 bars to
    /// its left and 20 to its right, which is at least the 15 the right window needs.
    /// </remarks>
    private const int PeakIndex = 20;

    private static DateTimeOffset At(int index) => SessionStart.AddHours(index);

    /// <summary>
    /// One contiguous run of one contract: flat, with a single unmistakable high in the middle of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Flat bars tie with each other and a tie is not a pivot, so the peak is the only thing a swing-style
    /// method can find, and it is far enough above its neighbours to dominate the window under any of the
    /// three <see cref="PivotSource"/> readings rather than only the one the defaults happen to use.
    /// <b>Deliberately not a sawtooth.</b> A short repeating price cycle never lets a bar strictly dominate
    /// its lookback, so it yields no pivots at all — which is what made the earlier version of the second
    /// sweep here vacuous (PR #252 review, finding 2).
    /// <b>Re-measured on gh#258's branch after the bars became hourly and the run moved back one session</b>,
    /// because a fixture that changed is a claim that has to be re-run: all three sources still find exactly
    /// one pivot and one zone — Heikin-Ashi <c>128.75</c>–<c>129.75</c> at significance <c>7.3125</c>, Body
    /// <c>117.5</c>–<c>118.5</c> at <c>9</c>, High/Low <c>199.5</c>–<c>200.5</c> at <c>49.5</c> — and each is
    /// 0.5% to 0.85% of its own midpoint, comfortably inside the shipped 2.5% width cap. Identical to the
    /// reading gh#245 took, and necessarily so: a price does not depend on how far apart the bars carrying
    /// it are.
    /// </para>
    /// <para>
    /// <b>The same bars carry structure for a session method and for the pivot family, and that is what
    /// <see cref="SessionStart"/> buys.</b> Beginning one session earlier puts the whole of Monday the 17th
    /// in the window — indices 0 to 22, opening at the very instant that session does — so a prior day is
    /// both present and completely covered, which is the one thing every one of the six non-<c>swing</c>
    /// methods needs. The prior week is absent throughout, correctly: it opens on Sunday the 9th and nothing
    /// that early is loaded. A method is asked here whether it can detect at all, not whether it can detect
    /// everything.
    /// </para>
    /// <para>
    /// <b>Measured on this branch, and every one of the seven answers.</b> <c>session</c> returns two zones,
    /// <c>98.5</c>–<c>101.5</c> support at significance <c>50.5</c> with six touches — the prior day's low,
    /// the overnight low, the balance low, the prior day's close, and the overnight and balance highs, all
    /// chained together by the merge — and <c>199.5</c>–<c>200.5</c> resistance at <c>50.5</c>, which is
    /// the peak. The prior period every
    /// <c>pivot-*</c> reads is that same session: open <c>100</c>, high <c>200</c>, low <c>99</c>, close
    /// <c>100</c>, so significance is <c>101 / 2 = 50.5</c> there too and the classic pivot is
    /// <c>399 / 3 = 133</c>. The five return five, six, eight, four and three zones respectively — fewer
    /// than each formula's full set, because a range that is most of its own low sends the far legs below
    /// the price scale and the shipped 2.5% width cap drops them.
    /// </para>
    /// </remarks>
    private static IEnumerable<Bar> Run(string contractId, decimal baseline, int startIndex) =>
        Enumerable.Range(0, RunLength).Select(i => i == PeakIndex
            ? new Bar(At(startIndex + i), baseline, baseline + 100m, baseline - 1m, baseline + 18m, 1_000, contractId)
            : new Bar(At(startIndex + i), baseline, baseline + 1m, baseline - 1m, baseline, 1_000, contractId));

    /// <summary>Two runs under one symbol, the second from a different contract forty points higher.</summary>
    /// <remarks>
    /// The first run <b>is</b> <see cref="SingleContract"/>, bar for bar. That matters: the refusal proven
    /// below cannot be an artefact of a series nothing could detect in, because those same bars are proven
    /// productive by the second sweep. <c>FindPivots</c> does reach the roll guard before its own length
    /// check, so the refusal would fire either way — but "would fire either way" is a claim about today's
    /// call order, and resting a fixture on it is how the second sweep here came to prove nothing.
    /// </remarks>
    private static IReadOnlyList<Bar> Spliced() =>
        [.. Run("CON.F.US.EP.U26", 100m, 0), .. Run("CON.F.US.EP.Z26", 140m, RunLength)];

    /// <summary>One run, one contract — the clean series a method must still answer.</summary>
    private static IReadOnlyList<Bar> SingleContract() => [.. Run("CON.F.US.EP.U26", 100m, 0)];

    /// <summary>An ATR of 2 at every bar, aligned one-to-one with the series.</summary>
    /// <remarks>
    /// Supplied so that a refusal is the roll guard's and nothing else's: an ATR series of the wrong length
    /// is refused too, with a different message, and a sweep that accepted either would be satisfied by the
    /// wrong failure. Two is small against the peak, so significance clears the floor comfortably.
    /// </remarks>
    private static IReadOnlyList<decimal?> FlatAtr(int count) => [.. Enumerable.Repeat((decimal?)2m, count)];

    /// <summary>
    /// Whether a method refuses a series that spans a contract roll, for the stated reason.
    /// </summary>
    /// <param name="method">The method under test.</param>
    /// <returns><see langword="true"/> when it refused the splice.</returns>
    /// <remarks>
    /// Written as a predicate rather than as an assertion so that both halves of the two-run rule call
    /// <b>the same code</b>. Any exception other than the roll refusal propagates and fails the caller: a
    /// method that threw for some unrelated reason has not been shown to carry the guard. Measured on
    /// gh#258's branch with <c>IndicatorGuard.RequireSingleContract</c> removed from
    /// <c>PivotLevels.Compute</c>: the sweep went red naming <c>pivot-classic</c>, and the ordering sweep
    /// next door stayed green.
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

    /// <summary>
    /// Whether a method finds anything at all in a clean single-contract series.
    /// </summary>
    /// <param name="method">The method under test.</param>
    /// <returns><see langword="true"/> when it returned at least one zone.</returns>
    private static bool DetectsOverASingleContractSeries(ILevelMethod method)
    {
        IReadOnlyList<Bar> clean = SingleContract();
        return method.Detect(clean, FlatAtr(clean.Count), new KeyLevelOptions()).Count > 0;
    }

    [Fact]
    public void EveryRegisteredMethod_RefusesASplicedSeries()
    {
        LevelMethodCatalog catalog = Catalog();

        catalog.All.Should().NotBeEmpty("the sweep must actually cover something");

        foreach (ILevelMethod method in catalog.All)
        {
            RefusesASplicedSeries(method).Should().BeTrue(
                method.Name + " detected levels across a contract roll. Adjacent quarters do not trade at "
                + "the same price, so whatever it returned sits where neither contract has ever been.");
        }
    }

    [Fact]
    public void TheRollSweepGoesRed_WhenAMethodBypassesTheGuard()
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
        // The other half of the guard, and it asserts a RESULT rather than the absence of an exception. A
        // method that refused everything would pass the sweep above and break the tool; so would one that
        // quietly returned nothing, and those two failures look nothing alike from outside.
        foreach (ILevelMethod method in Catalog().All)
        {
            DetectsOverASingleContractSeries(method).Should().BeTrue(
                method.Name + " found nothing in a clean series built around one unmistakable peak. Either "
                + "it is broken, or it needs structure this fixture does not carry — extend the fixture so "
                + "it has something to find, because a method that can find nothing here has not been shown "
                + "to detect at all.");
        }
    }

    [Fact]
    public void TheDetectionSweepGoesRed_WhenAMethodFindsNothing()
    {
        // The red half of the sweep above. Without it, a fixture that yields no pivots lets both sweeps pass
        // a method whose Detect body is `return [];` -- which is how `get_key_levels` comes to answer
        // "no levels" forever, green, for a name that is simply broken.
        DetectsOverASingleContractSeries(new SilentLevelMethod()).Should().BeFalse(
            "the sweep must go RED on a method that answers a perfectly ordinary series with nothing");
    }

    /// <summary>
    /// A deliberately defective method: it detects without asking whether the series spans a roll.
    /// </summary>
    /// <remarks>
    /// It is never registered anywhere. It exists so the roll sweep can be watched failing, which is the
    /// difference between a gate that is proven and a gate that is merely present.
    /// </remarks>
    private sealed class GuardlessLevelMethod : ILevelMethod
    {
        public string Name => "guardless";

        public string Family => "guardless";

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

    /// <summary>
    /// A deliberately defective method: it never finds anything, and never says why.
    /// </summary>
    /// <remarks>
    /// Registered nowhere either. It is the failure the detection sweep exists to catch, and the reason that
    /// sweep asserts a result rather than the absence of a throw.
    /// </remarks>
    private sealed class SilentLevelMethod : ILevelMethod
    {
        public string Name => "silent";

        public string Family => "silent";

        public IReadOnlyList<KeyLevelZone> Detect(
            IReadOnlyList<Bar> bars,
            IReadOnlyList<decimal?> atr,
            KeyLevelOptions options) => [];
    }
}
