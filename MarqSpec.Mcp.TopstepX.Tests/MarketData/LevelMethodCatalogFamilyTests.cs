using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// Every level method declares the correlation family it belongs to — swept, and watched failing on a method
/// that declares the wrong one.
/// </summary>
/// <remarks>
/// <para>
/// <b>A confluence score's worst failure is agreement it has counted twice</b> (gh#232, gh#259). The five
/// <c>pivot-*</c> methods are arithmetic on one prior session's open, high, low and close, so five of them
/// landing on a price is one input transformed five ways — and a score that read it as 5/5 would be most
/// confident exactly where it is least entitled to be. <see cref="ILevelMethod.Family"/> is what lets the
/// weighting discount them <i>as a group</i>.
/// </para>
/// <para>
/// <b>It is a property of the catalogue rather than a list of five names, and that distinction is the whole
/// point.</b> gh#259 names the alternative and rejects it: a hardcoded list of five is silently escaped by
/// the sixth variant, which is the same defect one name later. So the sweep below is over
/// <see cref="LevelMethodCatalog.All"/>, and a method's family is asked of the method.
/// </para>
/// <para>
/// <b>Two runs, not one</b> (Coding contract, Tests). Each sweep is a predicate, asserted
/// <see langword="true"/> for every registered method and asserted <see langword="false"/> for a
/// deliberately mis-declared one that is registered nowhere. A sweep proven only against the code that
/// already passes it is a sweep nobody has watched fail.
/// </para>
/// </remarks>
public sealed class LevelMethodCatalogFamilyTests
{
    /// <summary>The catalogue, built with the session calendar the anchored methods need.</summary>
    private static LevelMethodCatalog Catalog() => new(BarSessionCalendar.Parse("16:00", []));

    /// <summary>The prefix a method's name carries when it belongs to the pivot family.</summary>
    private const string PivotNamePrefix = "pivot-";

    /// <summary>
    /// Whether a method's declared family is usable as a grouping key at all.
    /// </summary>
    /// <param name="method">The method under test.</param>
    /// <returns><see langword="true"/> when the family is non-empty and lowercase.</returns>
    /// <remarks>
    /// Lowercase for the reason a name is: the family is matched, and a family nothing matches puts a
    /// correlated method into a budget of its own, which is the inflation the field exists to stop.
    /// </remarks>
    private static bool DeclaresAUsableFamily(ILevelMethod method) =>
        !string.IsNullOrWhiteSpace(method.Family) && method.Family == method.Family.ToLowerInvariant();

    /// <summary>
    /// Whether a method named for the pivot family declares it.
    /// </summary>
    /// <param name="method">The method under test.</param>
    /// <returns>
    /// <see langword="true"/> unless the method is named <c>pivot-*</c> and declares some other family.
    /// </returns>
    private static bool ItsNameAndItsFamilyAgree(ILevelMethod method) =>
        !method.Name.StartsWith(PivotNamePrefix, StringComparison.Ordinal)
        || method.Family == PivotLevels.FamilyName;

    [Fact]
    public void EveryRegisteredMethod_DeclaresAUsableFamily()
    {
        LevelMethodCatalog catalog = Catalog();

        catalog.All.Should().NotBeEmpty("the sweep must actually cover something");

        foreach (ILevelMethod method in catalog.All)
        {
            DeclaresAUsableFamily(method).Should().BeTrue(
                method.Name + " declares '" + method.Family + "' as its correlation family. A blank or "
                + "mixed-case family is one nothing groups by, so the method gets a budget of its own — "
                + "which is what a discount exists to deny it.");
        }
    }

    [Fact]
    public void TheFamilySweepGoesRed_WhenAMethodDeclaresNoFamily()
    {
        DeclaresAUsableFamily(new FamilylessLevelMethod()).Should().BeFalse(
            "the sweep must go RED on a method that declares nothing to group by");
    }

    [Fact]
    public void EveryMethodNamedForThePivotFamily_DeclaresIt()
    {
        // This is the gate against gh#259's named failure: the sixth pivot variant, registered under a
        // `pivot-` name and given a family of its own, which then escapes the discount silently and scores
        // as an independent confirmation of something it is arithmetic on.
        foreach (ILevelMethod method in Catalog().All)
        {
            ItsNameAndItsFamilyAgree(method).Should().BeTrue(
                method.Name + " is named for the pivot family and declares '" + method.Family + "'. A "
                + "variant outside the family's budget is counted as an independent confirmation of the "
                + "prior session it is computed from.");
        }
    }

    [Fact]
    public void TheNameAndFamilySweepGoesRed_WhenAPivotNamedMethodDeclaresAnother()
    {
        ItsNameAndItsFamilyAgree(new RogueVariantLevelMethod()).Should().BeFalse(
            "the sweep must go RED on a pivot variant that gives itself a budget of its own");
    }

    [Fact]
    public void TheFivePivotMethods_ShareOneFamily_AndTheOthersDoNotJoinIt()
    {
        LevelMethodCatalog catalog = Catalog();

        catalog.All.Where(m => m.Family == PivotLevels.FamilyName).Select(m => m.Name)
            .Should().BeEquivalentTo(
                ["pivot-classic", "pivot-fibonacci", "pivot-camarilla", "pivot-woodie", "pivot-demark"]);
    }

    [Fact]
    public void AMethodWithNoCorrelatedSiblings_IsItsOwnFamily()
    {
        // `swing` and `session` share nothing with anything: one measures dominance over a lookback window,
        // the other reads a finished session's extremes. Each is therefore a family of one, and its family
        // is its own name — so the weighting needs no special case for "uncorrelated".
        LevelMethodCatalog catalog = Catalog();

        catalog.Resolve("swing").Family.Should().Be("swing");
        catalog.Resolve("session").Family.Should().Be("session");
    }

    /// <summary>
    /// A deliberately defective method: it declares no family at all.
    /// </summary>
    /// <remarks>
    /// Registered nowhere. It exists so the family sweep can be watched failing, which is the difference
    /// between a gate that is proven and a gate that is merely present.
    /// </remarks>
    private sealed class FamilylessLevelMethod : ILevelMethod
    {
        public string Name => "familyless";

        public string Family => "   ";

        public IReadOnlyList<KeyLevelZone> Detect(
            IReadOnlyList<Bar> bars,
            IReadOnlyList<decimal?> atr,
            KeyLevelOptions options) => [];
    }

    /// <summary>
    /// A deliberately defective method: a sixth pivot variant that gives itself a budget of its own.
    /// </summary>
    /// <remarks>
    /// Registered nowhere. This is gh#259's named failure made checkable — the variant that a hardcoded list
    /// of five names would not have caught either.
    /// </remarks>
    private sealed class RogueVariantLevelMethod : ILevelMethod
    {
        public string Name => "pivot-murrey";

        public string Family => "pivot-murrey";

        public IReadOnlyList<KeyLevelZone> Detect(
            IReadOnlyList<Bar> bars,
            IReadOnlyList<decimal?> atr,
            KeyLevelOptions options) => [];
    }
}
