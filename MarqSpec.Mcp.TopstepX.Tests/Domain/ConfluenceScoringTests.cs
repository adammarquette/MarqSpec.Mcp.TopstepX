using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;

namespace MarqSpec.Mcp.TopstepX.Tests.Domain;

/// <summary>
/// Weighted, family-aware confluence — hand-derived scores, not round-trips through the scorer.
/// </summary>
/// <remarks>
/// <para>
/// gh#259 is the card that makes several methods worth having: the question is what they agree on, and a
/// score that cannot be explained is a score two people cannot compare. Every number below is worked from
/// the weighting rule written beside it. Nothing here captures an output and pins it.
/// </para>
/// <para>
/// The rule, stated once: each method carries a weight (1 when none is given). Methods that share a
/// <see cref="ILevelMethod.Family"/> share one budget equal to the largest weight among the members that
/// actually hit the cluster, so five pivot variants landing on one price count as one confirmation. Zones
/// agree when they overlap. The overall score is the strongest cluster's family-aware weight. The
/// tolerance is an input and is written on the result; it is not read from a clock, a store or a
/// configuration singleton.
/// </para>
/// </remarks>
public sealed class ConfluenceScoringTests
{
    private static readonly DateTimeOffset _formed = new(2026, 8, 18, 14, 0, 0, TimeSpan.Zero);

    /// <summary>A one-point-wide zone centred on <paramref name="mid"/> — overlap is then obvious by eye.</summary>
    private static KeyLevelZone At(decimal mid, string? period = null) =>
        new(mid - 0.5m, mid + 0.5m, KeyLevelKind.Support, _formed, TouchCount: 1, Significance: 1m, Period: period);

    private static ConfluenceMethodInput Hit(string name, string family, params decimal[] mids) =>
        new(name, family, [.. mids.Select(m => At(m))]);

    private static ConfluenceMethodInput Miss(string name, string family, string reason) =>
        new(name, family, [], reason);

    private static IReadOnlyDictionary<string, decimal> UnitWeights =>
        new Dictionary<string, decimal>(StringComparer.Ordinal);

    [Fact]
    public void ThreeIndependentMethodsOnTheSamePrice_ScoreThree()
    {
        // Each method is its own family, weight 1 (the default). All three zones are [99.5, 100.5],
        // so they form one cluster. Independent families do not share a budget:
        //
        //   swing    1
        //   session  1
        //   volume   1   -- a third family, not a pivot; the name is only a label
        //   --------------
        //   score    3
        ConfluenceResult result = ConfluenceScoring.Score(
            [
                Hit("swing", "swing", 100m),
                Hit("session", "session", 100m),
                Hit("volume-profile", "volume-profile", 100m),
            ],
            UnitWeights,
            tolerance: 0.5m);

        result.Score.Should().Be(3m);
        result.Tolerance.Should().Be(0.5m);
        result.Absent.Should().BeEmpty();
        result.Constituents.Select(c => (c.Method, c.Family, c.Weight, c.ZoneCount)).Should().Equal(
            ("swing", "swing", 1m, 1),
            ("session", "session", 1m, 1),
            ("volume-profile", "volume-profile", 1m, 1));
    }

    [Fact]
    public void ThreePivotVariantsOnTheSamePrice_ScoreOne_BecauseTheyShareABudget()
    {
        // The same three zones as the independent case, but all three declare family `pivot`. The
        // budget is the largest weight among the members that hit — 1 — not the sum:
        //
        //   pivot-classic    1 ┐
        //   pivot-fibonacci  1 ├─ family `pivot` contributes max(1, 1, 1) = 1
        //   pivot-woodie     1 ┘
        //   ----------------------
        //   score            1
        //
        // That is 2 less than the independent case above, and it is the whole reason the family
        // identifier exists. A hardcoded list of five names would have missed a sixth variant;
        // grouping is by <see cref="ConfluenceMethodInput.Family"/>, so `pivot-murrey` below is
        // enough to prove the grouping is not a list.
        ConfluenceResult result = ConfluenceScoring.Score(
            [
                Hit("pivot-classic", PivotLevels.FamilyName, 100m),
                Hit("pivot-fibonacci", PivotLevels.FamilyName, 100m),
                Hit("pivot-murrey", PivotLevels.FamilyName, 100m),
            ],
            UnitWeights,
            tolerance: 0.5m);

        result.Score.Should().Be(1m);
    }

    [Fact]
    public void ACorrelatedFamily_ScoresLowerThanTheSameZonesFromIndependentMethods()
    {
        IReadOnlyList<ConfluenceMethodInput> independent =
        [
            Hit("swing", "swing", 100m),
            Hit("session", "session", 100m),
            Hit("volume-profile", "volume-profile", 100m),
        ];

        IReadOnlyList<ConfluenceMethodInput> family =
        [
            Hit("pivot-classic", PivotLevels.FamilyName, 100m),
            Hit("pivot-fibonacci", PivotLevels.FamilyName, 100m),
            Hit("pivot-murrey", PivotLevels.FamilyName, 100m),
        ];

        decimal independentScore = ConfluenceScoring.Score(independent, UnitWeights, 0.5m).Score;
        decimal familyScore = ConfluenceScoring.Score(family, UnitWeights, 0.5m).Score;

        // 3 against 1, derived above. Asserted as a comparison so deleting the family discount
        // reddens this case rather than leaving a comment that says it should.
        familyScore.Should().BeLessThan(independentScore);
        independentScore.Should().Be(3m);
        familyScore.Should().Be(1m);
    }

    [Fact]
    public void RemovingTheFamilyDiscount_ReddensTheCaseThatExistsForIt()
    {
        IReadOnlyList<ConfluenceMethodInput> family =
        [
            Hit("pivot-classic", PivotLevels.FamilyName, 100m),
            Hit("pivot-fibonacci", PivotLevels.FamilyName, 100m),
            Hit("pivot-camarilla", PivotLevels.FamilyName, 100m),
        ];

        decimal discounted = ConfluenceScoring.Score(family, UnitWeights, 0.5m, applyFamilyDiscount: true).Score;
        decimal undiscounted = ConfluenceScoring.Score(family, UnitWeights, 0.5m, applyFamilyDiscount: false).Score;

        // Without the discount the three weights sum: 1 + 1 + 1 = 3. With it they share 1.
        // `discounted < undiscounted` is the pin: a scorer that always summed would make both 3
        // and this assertion go red.
        discounted.Should().Be(1m);
        undiscounted.Should().Be(3m);
        discounted.Should().BeLessThan(undiscounted);
    }

    [Fact]
    public void ARequestedMethodThatContributedNothing_IsNamedWithTheReason()
    {
        // swing produced a zone; session was asked and returned nothing because the prior day is
        // not in the window. A 1/1 that does not name the missing method is a 1/1 from one-of-two
        // dressed as a complete answer.
        ConfluenceResult result = ConfluenceScoring.Score(
            [
                Hit("swing", "swing", 100m),
                Miss("session", "session", "no data: prior trading day is not in the window"),
            ],
            UnitWeights,
            tolerance: 0.5m);

        result.Score.Should().Be(1m);
        result.Absent.Should().ContainSingle()
            .Which.Should().Be(new ConfluenceAbsence("session", "no data: prior trading day is not in the window"));
        result.Constituents.Should().Contain(c => c.Method == "session" && c.ZoneCount == 0);
        result.Constituents.Should().Contain(c => c.Method == "swing" && c.ZoneCount == 1);
    }

    [Fact]
    public void TheSameInputs_AlwaysProduceTheSameScore()
    {
        IReadOnlyList<ConfluenceMethodInput> methods =
        [
            Hit("swing", "swing", 100m, 200m),
            Hit("session", "session", 100m),
            Miss("pivot-classic", PivotLevels.FamilyName, "no levels"),
        ];
        Dictionary<string, decimal> weights = new(StringComparer.Ordinal)
        {
            ["swing"] = 2m,
            ["session"] = 1m,
            ["pivot-classic"] = 1m,
        };

        ConfluenceResult first = ConfluenceScoring.Score(methods, weights, tolerance: 0.5m);
        ConfluenceResult second = ConfluenceScoring.Score(methods, weights, tolerance: 0.5m);

        second.Should().BeEquivalentTo(first);
        first.Score.Should().Be(3m, "the 100-cluster is swing 2 + session 1; the 200-cluster is swing 2 alone");
    }

    [Fact]
    public void DifferentTolerances_CannotShareAScore()
    {
        // Two callers, same methods, different tolerances. The tolerance is on the result so they
        // cannot be shown each other's number and told it is the same score. The zones themselves
        // also change with the width: at 0.5 they are [99.75, 100.25] and [100.40, 100.90] and do
        // not overlap (gap of 0.15); at 2.0 they are [99.0, 101.0] and [99.4, 101.4] and do.
        //
        //   narrow: two singleton clusters, strongest weight = 1  → score 1
        //   wide:   one cluster, swing 1 + session 1             → score 2
        KeyLevelZone narrowSwing = new(99.75m, 100.25m, KeyLevelKind.Support, _formed, 1, 1m);
        KeyLevelZone narrowSession = new(100.40m, 100.90m, KeyLevelKind.Support, _formed, 1, 1m);
        KeyLevelZone wideSwing = new(99.00m, 101.00m, KeyLevelKind.Support, _formed, 1, 1m);
        KeyLevelZone wideSession = new(99.40m, 101.40m, KeyLevelKind.Support, _formed, 1, 1m);

        ConfluenceResult narrow = ConfluenceScoring.Score(
            [new("swing", "swing", [narrowSwing]), new("session", "session", [narrowSession])],
            UnitWeights,
            tolerance: 0.5m);
        ConfluenceResult wide = ConfluenceScoring.Score(
            [new("swing", "swing", [wideSwing]), new("session", "session", [wideSession])],
            UnitWeights,
            tolerance: 2.0m);

        narrow.Tolerance.Should().NotBe(wide.Tolerance);
        narrow.Score.Should().Be(1m);
        wide.Score.Should().Be(2m);
        narrow.Score.Should().NotBe(wide.Score);
    }

    [Fact]
    public void AConfiguredWeight_IsTheNumberTheScoreUses_AndIsReportedOnTheConstituent()
    {
        // swing weighs 2, session weighs 1, both hit 100. Score = 3, and the result says so.
        Dictionary<string, decimal> weights = new(StringComparer.Ordinal)
        {
            ["swing"] = 2m,
            ["session"] = 1m,
        };

        ConfluenceResult result = ConfluenceScoring.Score(
            [Hit("swing", "swing", 100m), Hit("session", "session", 100m)],
            weights,
            tolerance: 0.5m);

        result.Score.Should().Be(3m);
        result.Constituents.Single(c => c.Method == "swing").Weight.Should().Be(2m);
        result.Constituents.Single(c => c.Method == "session").Weight.Should().Be(1m);
    }

    [Fact]
    public void TwoNonOverlappingClusters_TakeTheStronger()
    {
        // swing hits 100 (weight 2) and 200 (weight 2, same method, same family).
        // session hits only 100 (weight 1).
        //
        //   cluster 100: swing 2 + session 1 = 3
        //   cluster 200: swing 2             = 2
        //   score = 3
        Dictionary<string, decimal> weights = new(StringComparer.Ordinal) { ["swing"] = 2m };

        ConfluenceResult result = ConfluenceScoring.Score(
            [Hit("swing", "swing", 100m, 200m), Hit("session", "session", 100m)],
            weights,
            tolerance: 0.5m);

        result.Score.Should().Be(3m);
    }
}
