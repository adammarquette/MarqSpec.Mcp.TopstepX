using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// Every level method this server detects with refuses a series whose bars are out of time order — swept
/// through the <see cref="ILevelMethod"/> seam, and watched failing on a method that skips the guard.
/// </summary>
/// <remarks>
/// <para>
/// <b>The sweep next door pins a different guard, and neither covers the other.</b>
/// <see cref="LevelMethodCatalogRollTests"/> counts a refusal only when the message says
/// <c>contract roll</c>, which is <see cref="IndicatorGuard.RequireSingleContract"/>'s wording; this one
/// counts one only when the message says <c>ascending</c>, which is
/// <see cref="IndicatorGuard.RequireStrictlyAscending"/>'s. That is not a distinction on paper: deleting the
/// ordering guard from <see cref="KeyLevels.FindPivots"/> left all four tests in that file green and turned
/// the sweep below red, naming <c>swing</c> — measured on this branch, and tabled in the PR that added this
/// file.
/// </para>
/// <para>
/// It exists because <b>a level method does not inherit its guards</b>, which is the sentence that file
/// opens with. An indicator is a projection over one shared compute path, so a catalogue sweep covers the
/// twelfth indicator by construction. Each method here detects its own way, so ordering is a rule each
/// implementation satisfies rather than a step each inherits. <c>swing</c> reaches the guard through
/// <see cref="KeyLevels.FindPivots"/>; the next one need not, and a method that detects by another path
/// simply never calls it (gh#283).
/// </para>
/// <para>
/// <b>The next one arrived, and it is why this file was sequenced first.</b> <c>session</c> does not go
/// through <see cref="KeyLevels.FindPivots"/> at all — it reads a session's extremes directly — so it calls
/// <see cref="IndicatorGuard.RequireStrictlyAscending"/> itself, and nothing but this sweep says it must.
/// Measured on gh#257's branch: with that call removed from <c>SessionLevels.Compute</c>, the sweep below
/// went red naming <c>session</c>, and nothing else did.
/// </para>
/// <para>
/// <b>And then five more arrived at once.</b> The <c>pivot-*</c> family reads a finished session's four
/// prices and never goes near <see cref="KeyLevels.FindPivots"/> either, so <c>PivotLevels.Compute</c> calls
/// the guard itself. Measured on gh#258's branch: with that call removed, the sweep below went red naming
/// <c>pivot-classic</c> — the first of the five the catalogue reaches — while all four tests in
/// <see cref="LevelMethodCatalogRollTests"/> stayed green, which is the separation this file's first
/// paragraph claims.
/// </para>
/// <para>
/// <b>Entered through <see cref="ILevelMethod.Detect"/> and nothing else</b>, because the seam is the thing
/// that must not be bypassable: a case calling <c>KeyLevels.FindPivots</c> directly pins the path today's
/// one method happens to take and stays green for a method that takes another. And like the roll sweep, it
/// sweeps for <b>the refusal, not the call</b> — a method that reaches the guard through whatever it
/// delegates detection to satisfies it, which is how <c>swing</c> satisfies it.
/// </para>
/// <para>
/// The green half of the two-run rule is <see cref="LevelMethodCatalogRollTests"/>'s
/// <c>EveryRegisteredMethod_StillDetectsOverASingleContractSeries</c> — the same catalogue over an ordinary
/// ascending single-contract series, asserting that a zone comes back. That is the right assertion for what
/// this half exists to catch: a guard that refused <i>everything</i> would pass the sweep below and break
/// the tool, and the two failures look nothing alike from outside. It is not duplicated here, because a
/// second copy would prove the same thing twice and drift separately.
/// </para>
/// <para>
/// <b>What it does not pin is where in a method the refusal happens.</b> The predicate asks only whether the
/// refusal arrives, so a method that computed a full level set and then noticed the disorder would pass it.
/// Nor does the fixture rest on the call order inside <c>FindPivots</c>: it is a series a method can
/// actually work on rather than one too short to detect in, so a refusal is the guard's and not the length
/// check's.
/// </para>
/// </remarks>
public sealed class LevelMethodCatalogOrderingTests
{
    /// <summary>Bars in the run.</summary>
    /// <remarks>
    /// Forty-one, the same as <see cref="LevelMethodCatalogRollTests"/>'s, and it moved with that one when
    /// the shipped lookback became asymmetric (gh#245): 20 bars of left dominance and 15 of right
    /// confirmation need <c>20 + 15 + 1 = 36</c> before a series can hold a pivot at all. The count did not
    /// move again for gh#258 — what moved there was the <i>spacing</i> and the origin, so forty-one bars
    /// now span a whole session and part of the next instead of three and a half hours. The refusal this
    /// file sweeps for fires either way — <see cref="IndicatorGuard.RequireStrictlyAscending"/> runs before
    /// the length check, and a series too short returns empty rather than throwing — but a fixture nothing
    /// could detect in is what made the sweep next door vacuous once already, so the two runs stay the same
    /// series.
    /// </remarks>
    private const int RunLength = 41;

    /// <summary>The index, in the ordered run, of the one bar that stands clear of everything around it.</summary>
    private const int PeakIndex = 20;

    private const string ContractId = "CON.F.US.EP.U26";

    /// <summary>
    /// 17:00 Central on Sunday 16 August 2026 — Monday's reopen, and the same origin
    /// <see cref="LevelMethodCatalogRollTests"/> uses.
    /// </summary>
    /// <remarks>
    /// The two must agree, because the run below claims to be that file's clean series bar for bar. It moved
    /// there so <c>session</c> had an initial balance to find (gh#257) and again, together with the hourly
    /// spacing, so the <c>pivot-*</c> family had a <b>finished</b> prior session to compute from (gh#258);
    /// it moved here both times so the claim stays true.
    /// </remarks>
    private static DateTimeOffset SessionStart =>
        MarketClock.FromMarket(new DateOnly(2026, 8, 16), new TimeOnly(17, 0)).ToUniversalTime();

    private static DateTimeOffset At(int index) => SessionStart.AddHours(index);

    /// <summary>
    /// One contiguous run of one contract, with the peak bar and the one after it exchanged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The run <b>is</b> <c>LevelMethodCatalogRollTests</c>'s clean single-contract series, bar for bar:
    /// flat, with a single unmistakable high in the middle, far enough above its neighbours to dominate the
    /// window under any of the three <see cref="PivotSource"/> readings. That series is proven productive
    /// there — its detection sweep asserts a zone comes back from it — so a refusal here cannot be an
    /// artefact of a fixture nothing could detect in.
    /// </para>
    /// <para>
    /// <b>One contract throughout, deliberately.</b> Spliced, it would be refused by the roll guard instead,
    /// whose message says <c>contract roll</c> rather than <c>ascending</c>: the predicate below would not
    /// count that as a refusal, the exception would propagate, and the sweep would report a fixture fault as
    /// a coverage gap.
    /// </para>
    /// <para>
    /// Exchanging two <i>adjacent</i> bars is the smallest disorder there is, and it is the one the guard
    /// exists for — after the swap the bar at <see cref="PeakIndex"/> + 1 opens an hour before the one
    /// preceding it.
    /// </para>
    /// <para>
    /// <b>The disorder does not show in the answer, and that is the whole point.</b> With
    /// <see cref="IndicatorGuard.RequireStrictlyAscending"/> deleted from <see cref="KeyLevels.FindPivots"/>,
    /// <c>swing</c> answered this series with one zone — <c>128.75</c>–<c>129.75</c>, resistance, formed at
    /// <c>2026-08-17T18:00Z</c>, significance <c>7.3125</c> — the same zone, to the tick, that it returns
    /// for these same bars sorted into order, and the sweep below went red naming <c>swing</c> and nothing
    /// else. <b>Measured four times, and the time is the only thing that has ever moved.</b> First on
    /// gh#283's branch, where the run began at 09:00 Central and the answer was <c>14:50Z</c>. Then on
    /// gh#257's, which moved <see cref="SessionStart"/> to the reopen so <c>session</c> had an initial
    /// balance to find, giving <c>22:50Z</c>. Then on gh#245's, which extended the run to forty-one bars for
    /// the asymmetric window and moved <see cref="PeakIndex"/> from the tenth bucket to the twentieth. Then
    /// on gh#258's, which made the bars hourly and moved the origin back one session so the pivot family had
    /// a finished prior session. The zone and the score are identical in all four, because a price does not
    /// depend on when the bar carrying it opened, on which index carries it, or on how far apart the buckets
    /// are. A <see cref="KeyLevelZone"/> records no provenance, so it cannot say which series it was computed
    /// from: a method that answers a disordered one hands back something that reads exactly like a level.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<Bar> Transposed()
    {
        const decimal baseline = 100m;

        List<Bar> bars =
        [
            .. Enumerable.Range(0, RunLength).Select(i => i == PeakIndex
                ? new Bar(At(i), baseline, baseline + 100m, baseline - 1m, baseline + 18m, 1_000, ContractId)
                : new Bar(At(i), baseline, baseline + 1m, baseline - 1m, baseline, 1_000, ContractId)),
        ];

        (bars[PeakIndex], bars[PeakIndex + 1]) = (bars[PeakIndex + 1], bars[PeakIndex]);
        return bars;
    }

    /// <summary>An ATR of 2 at every bar, aligned one-to-one with the series.</summary>
    /// <remarks>
    /// Supplied so that a refusal is the ordering guard's and nothing else's: an ATR series of the wrong
    /// length is refused too, with a different message, and a sweep satisfied by that would be satisfied by
    /// the wrong failure.
    /// </remarks>
    /// <param name="count">How many bars the series carries.</param>
    /// <returns>The ATR series.</returns>
    private static IReadOnlyList<decimal?> FlatAtr(int count) => [.. Enumerable.Repeat((decimal?)2m, count)];

    /// <summary>
    /// Whether a method refuses a series whose bars are out of time order, for the stated reason.
    /// </summary>
    /// <param name="method">The method under test.</param>
    /// <returns><see langword="true"/> when it refused the disorder.</returns>
    /// <remarks>
    /// Written as a predicate rather than as an assertion so that both halves of the two-run rule call
    /// <b>the same code</b>. Any exception other than the ordering refusal propagates and fails the caller:
    /// a method that threw for some unrelated reason has not been shown to carry the guard.
    /// </remarks>
    private static bool RefusesATransposedSeries(ILevelMethod method)
    {
        IReadOnlyList<Bar> transposed = Transposed();

        try
        {
            VolumeProfile profile = VolumeProfileAggregator.From(
            [
                new FootprintCell("ES", 60, SessionStart, 100m, 10, 0),
                new FootprintCell("ES", 60, SessionStart, 150m, 40, 0),
                new FootprintCell("ES", 60, SessionStart, 200m, 10, 0),
            ]);

            using VolumeProfileScope scope = new(profile);
            method.Detect(transposed, FlatAtr(transposed.Count), new KeyLevelOptions());
        }
        catch (ArgumentException e) when (e.Message.Contains("ascending", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    [Fact]
    public void EveryRegisteredMethod_RefusesATransposedSeries()
    {
        LevelMethodCatalog catalog = new(BarSessionCalendar.Parse("16:00", []));

        catalog.All.Should().NotBeEmpty("the sweep must actually cover something");

        foreach (ILevelMethod method in catalog.All)
        {
            RefusesATransposedSeries(method).Should().BeTrue(
                method.Name + " accepted bars that are not in strictly ascending time order. The lookback "
                + "window then slid over neighbours that are not neighbours, and the levels that came back "
                + "carry nothing that says so.");
        }
    }

    [Fact]
    public void TheOrderingSweepGoesRed_WhenAMethodBypassesTheGuard()
    {
        OrderBlindLevelMethod bypassing = new();
        IReadOnlyList<Bar> transposed = Transposed();

        // It does not fail, which is the whole problem: it answers with an ordinary-looking zone built over
        // bars that arrived out of order. Reproduced here so the catch is checkable rather than merely
        // present.
        bypassing.Detect(transposed, FlatAtr(transposed.Count), new KeyLevelOptions())
            .Should().ContainSingle("a method that skips the guard answers a disordered series instead of refusing it");

        RefusesATransposedSeries(bypassing).Should().BeFalse(
            "the sweep must go RED on a method that bypasses IndicatorGuard.RequireStrictlyAscending — a "
            + "gate proven only against code that already passes it is a gate nobody has watched fail");
    }

    /// <summary>
    /// A deliberately defective method: it detects without asking whether the bars are in time order.
    /// </summary>
    /// <remarks>
    /// It is never registered anywhere. It exists so the sweep above can be watched failing, which is the
    /// difference between a gate that is proven and a gate that is merely present. Its zone is built from
    /// the single highest bar, so it comes back whatever order the series arrived in — which is precisely
    /// the property a level method must not have.
    /// </remarks>
    private sealed class OrderBlindLevelMethod : ILevelMethod
    {
        public string Name => "order-blind";

        public string Family => "order-blind";

        public IReadOnlyList<KeyLevelZone> Detect(
            IReadOnlyList<Bar> bars,
            IReadOnlyList<decimal?> atr,
            KeyLevelOptions options)
        {
            // No RequireStrictlyAscending, on purpose. The zone reports the highest bar's own open time as
            // the moment it formed, which over a transposed series is a formation time that does not sit
            // where the bar does -- and nothing about the zone says so.
            Bar highest = bars.MaxBy(bar => bar.High)!;
            return [new KeyLevelZone(highest.Low, highest.High, KeyLevelKind.Resistance, highest.OpenTime, 1, 1m)];
        }
    }
}
