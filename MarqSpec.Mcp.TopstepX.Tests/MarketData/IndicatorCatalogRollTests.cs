using System.Globalization;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.Extensions.Options;
using Xunit.Sdk;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// Every indicator this server actually computes refuses a spliced series and still returns values over a
/// clean one — swept, not listed, with the value sweep watched failing on an indicator that returns nothing.
/// </summary>
/// <remarks>
/// <para>
/// ADR-0011 claims the roll guard sits on the shared path so that <b>a new indicator inherits the rule rather
/// than remembering it</b>. That is a claim about indicators nobody has written yet, so it cannot be pinned by
/// naming the ones that exist: a test listing today's eleven would stay green on the day someone adds a
/// twelfth that computes straight through a roll.
/// </para>
/// <para>
/// So this walks <see cref="IndicatorCatalog.All"/> — the closed vocabulary the projection and the tool
/// surface both read — and asserts the refusal for whatever is in it. Same shape as
/// <c>NoVenueFacingToolIsMissingTheTranslation</c>, and for the same reason: the interesting failure is the
/// one nobody remembered to add a test for.
/// </para>
/// <para>
/// <b>The second sweep asserts a result, and until gh#285 it asserted only <c>NotThrow</c> over thirty
/// bars.</b> Two of the eleven declare a warm-up of 35 — <c>macd-signal</c> and <c>macd-histogram</c>, both
/// <c>MacdSlowPeriod</c> 26 + <c>Macd.SignalPeriod</c> 9 — and both answered that fixture with 0 non-null
/// values out of 30, so it passed for them because nothing was computed rather than because something was.
/// A <c>Compute</c> that returned a list of nulls for every member would have passed it too. The fixture is
/// now <see cref="RunLength"/> bars, derived from the catalogue rather than chosen, and the sweep counts
/// values; <see cref="TheValueSweepGoesRed_WhenAnIndicatorComputesNothing"/> is the run that proves it can
/// fail.
/// </para>
/// </remarks>
public sealed class IndicatorCatalogRollTests
{
    /// <summary>Bars past the longest warm-up, so the slowest member returns more than a single value.</summary>
    private const int Headroom = 5;

    private static DateTimeOffset SessionStart =>
        MarketClock.FromMarket(new DateOnly(2026, 8, 18), new TimeOnly(9, 0)).ToUniversalTime();

    private static IndicatorCatalog Catalog() =>
        new(Options.Create(new IndicatorOptions()), BarSessionCalendar.Parse("16:00", []));

    /// <summary>Bars per contiguous single-contract run — <b>derived from the catalogue, never chosen</b>.</summary>
    /// <remarks>
    /// <para>
    /// The longest warm-up in <see cref="IndicatorCatalog.All"/> plus <see cref="Headroom"/>. Today that is
    /// <c>35 + 5 = 40</c>, and nothing here says forty: an indicator added with a longer warm-up than any
    /// existing one widens the fixture by arriving, which is the one thing a hand-picked number cannot do.
    /// </para>
    /// <para>
    /// <b>A hand-picked number is what gh#285 was opened about.</b> The run this replaced was thirty bars,
    /// justified by an enumeration that stopped at MACD's slow leg — 26 — and never reached the signal leg at
    /// 35, so the two members warming up in 35 were asserted over a series that could not produce a value for
    /// them. The list did not go stale; it was already short of the catalogue it was written beside.
    /// </para>
    /// </remarks>
    private static int RunLength => Catalog().All.Max(indicator => indicator.WarmupBars) + Headroom;

    private static DateTimeOffset At(int index) => SessionStart.AddMinutes(5 * index);

    /// <summary>One contiguous run of one contract, sawtoothing over five prices.</summary>
    private static IEnumerable<Bar> Run(string contractId, decimal baseline, int startIndex) =>
        Enumerable.Range(0, RunLength).Select(i =>
        {
            decimal close = baseline + (i % 5);
            return new Bar(At(startIndex + i), close, close + 1m, close - 1m, close, 1_000, contractId);
        });

    /// <summary>Two runs under one symbol, the second from a different contract forty points higher.</summary>
    /// <remarks>
    /// The first run <b>is</b> <see cref="SingleContract"/>, bar for bar — the same construction the sibling
    /// <see cref="LevelMethodCatalogRollTests"/> uses, and for the same reason: the refusal proven below
    /// cannot be an artefact of a series nothing could compute over, because those exact bars are proven
    /// productive by <see cref="EveryConfiguredIndicator_StillComputesValuesOverASingleContractSeries"/>.
    /// <b>The sixty-bar version this replaced could not say that.</b> It was split thirty/thirty, and thirty
    /// bars is under the 35 <c>macd-signal</c> and <c>macd-histogram</c> declare, so <i>both</i> sides of the
    /// old seam returned 0 non-null for those two — measured on <c>1d558eb</c>, each half separately, and
    /// tabled in gh#285's pull request. Each side clears the longest warm-up now, because
    /// <see cref="RunLength"/> is that warm-up plus <see cref="Headroom"/>.
    /// </remarks>
    private static IReadOnlyList<Bar> Spliced() =>
        [.. Run("CON.F.US.EP.U26", 100m, 0), .. Run("CON.F.US.EP.Z26", 140m, RunLength)];

    /// <summary>One run, one contract — the clean series every indicator must still answer with values.</summary>
    private static IReadOnlyList<Bar> SingleContract() => [.. Run("CON.F.US.EP.U26", 100m, 0)];

    /// <summary>
    /// Computes, failing by name rather than by stack trace if the indicator throws instead of answering.
    /// </summary>
    /// <param name="indicator">The indicator under test.</param>
    /// <param name="bars">The series.</param>
    /// <returns>What it computed.</returns>
    /// <remarks>
    /// This is the <c>NotThrow</c> half, kept rather than dropped because it catches a different failure from
    /// the count below and reports it better. A guard that refused <i>everything</i> would pass the refusal
    /// sweep and break the server; it fails here, and the failure carries the offending indicator's name,
    /// which a bare exception escaping the sweep would not.
    /// </remarks>
    private static IReadOnlyList<decimal?> ComputeNamingTheIndicator(
        IIndicator indicator, IReadOnlyList<Bar> bars)
    {
        Func<IReadOnlyList<decimal?>> compute = () => indicator.Compute(bars);

        return compute.Should().NotThrow(
            indicator.Name + " refuses an ordinary single-contract series").Subject;
    }

    /// <summary>
    /// Asserts that every indicator handed in answers <paramref name="bars"/> with at least one value.
    /// </summary>
    /// <param name="indicators">The indicators to sweep.</param>
    /// <param name="bars">The series they must produce a value over.</param>
    /// <remarks>
    /// <para>
    /// Written as one routine so that both halves of the two-run rule call <b>the same code</b> — the sweep
    /// and the run that watches it fail differ only in what is handed in.
    /// </para>
    /// <para>
    /// <b>Every silent member is collected before anything is asserted, deliberately.</b> A per-member
    /// assertion inside the loop stops at the first one, and a reader takes that single name for the whole
    /// list — which is exactly how gh#285's count was first reported as one member rather than two.
    /// <c>BeEmpty</c> prints only the first offending item too, so the full list is built into the reason.
    /// </para>
    /// </remarks>
    private static void AssertEveryIndicatorComputesAValue(
        IReadOnlyList<IIndicator> indicators, IReadOnlyList<Bar> bars)
    {
        indicators.Should().NotBeEmpty("the sweep must actually cover something");

        List<string> silent =
        [
            .. indicators
                .Where(indicator => !ComputeNamingTheIndicator(indicator, bars).Any(value => value.HasValue))
                .Select(indicator => indicator.Name + " (warm-up "
                    + indicator.WarmupBars.ToString(CultureInfo.InvariantCulture) + ")"),
        ];

        silent.Should().BeEmpty(
            "every configured indicator must return at least one value over "
            + bars.Count.ToString(CultureInfo.InvariantCulture)
            + " ordinary single-contract bars, and these answered with nothing but nulls: "
            + string.Join(", ", silent)
            + ". Either the arithmetic is wrong, or the fixture no longer covers the longest warm-up — and "
            + "an indicator that returns all-nulls forever passes every refusal sweep there is, leaving "
            + "get_indicators answering an empty series, green, on every instrument.");
    }

    [Fact]
    public void EveryConfiguredIndicator_RefusesASplicedSeries()
    {
        IndicatorCatalog catalog = Catalog();
        IReadOnlyList<Bar> spliced = Spliced();

        catalog.All.Should().NotBeEmpty("the sweep must actually cover something");

        foreach (IIndicator indicator in catalog.All)
        {
            Action compute = () => indicator.Compute(spliced);

            compute.Should().Throw<ArgumentException>(
                indicator.Name + " computed a value across a contract roll. Adjacent quarters do not trade "
                + "at the same price, so whatever it returned is the roll gap reported as market movement.")
                .WithMessage("*contract*");
        }
    }

    [Fact]
    public void EveryConfiguredIndicator_StillComputesValuesOverASingleContractSeries()
    {
        // The other half, and it asserts a RESULT. A guard that refused everything would pass the sweep above
        // and break the server; so would an indicator whose warm-up arithmetic is wrong and that quietly
        // returns nulls forever. The two failures look nothing alike from outside, so both are asserted.
        AssertEveryIndicatorComputesAValue(Catalog().All, SingleContract());
    }

    [Fact]
    public void TheValueSweepGoesRed_WhenAnIndicatorComputesNothing()
    {
        // The red half of the sweep above. Without it, the sweep is a gate nobody has watched fail — and for
        // two of eleven members it was already inert without anything going red to say so (gh#285).
        AllNullIndicator mute = new();
        IReadOnlyList<Bar> clean = SingleContract();

        // It throws nothing and its series is aligned one-to-one with the bars. The only thing wrong with it
        // is that every entry is null, so the sweep has to go red for the nulls and not for a length.
        Action compute = () => mute.Compute(clean);
        compute.Should().NotThrow("an all-null indicator satisfies NotThrow without computing anything");
        mute.Compute(clean).Should().HaveCount(clean.Count).And.OnlyContain(value => !value.HasValue);

        Action sweep = () => AssertEveryIndicatorComputesAValue([mute], clean);

        // The pattern reaches into the OFFENDER LIST — "<name> (warm-up" — rather than matching the bare
        // name anywhere in the message. Deleting `indicator.Name` from that list and re-running left a bare
        // "*all-null*" green, because the sweep's own reason carries the words "returns all-nulls": the
        // assertion was matching this test's boilerplate instead of the member it is supposed to name.
        sweep.Should().Throw<XunitException>(
            "the sweep must go RED on an indicator that answers a perfectly ordinary series with nothing — a "
            + "gate proven only against code that already passes it is a gate nobody has watched fail")
            .WithMessage(
                "*" + mute.Name + " (warm-up*",
                "the failure has to name the member that went silent, in the list of offenders");
    }

    /// <summary>
    /// A deliberately defective indicator: it answers every series with nulls, and never says why.
    /// </summary>
    /// <remarks>
    /// Registered nowhere, like <c>GuardlessIndicator</c> next door in
    /// <see cref="IndicatorCatalogOrderingTests"/>. It is the failure the value sweep exists to catch, and the
    /// reason that sweep asserts a result rather than the absence of a throw: nothing about it throws, so
    /// <c>NotThrow</c> reads it as an ordinary success.
    /// </remarks>
    private sealed class AllNullIndicator : IIndicator
    {
        public string Name => "all-null";

        public int Period => 1;

        public int WarmupBars => 1;

        public IReadOnlyList<decimal?> Compute(IReadOnlyList<Bar> bars)
        {
            ArgumentNullException.ThrowIfNull(bars);

            return [.. bars.Select(_ => (decimal?)null)];
        }
    }
}
