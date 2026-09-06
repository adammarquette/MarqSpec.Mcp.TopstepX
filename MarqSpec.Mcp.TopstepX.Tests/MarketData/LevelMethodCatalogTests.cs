using System.ComponentModel;
using System.Reflection;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Tools;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// The closed vocabulary of level-detection methods.
/// </summary>
/// <remarks>
/// Same shape as the indicator catalogue's vocabulary tests, and for the same reason: a name that resolves to
/// nothing must be an <i>error listing the known ones</i>, never an empty answer. An empty level set reads as
/// "this market has no structure", which is a conclusion; a typo is a fault.
/// </remarks>
public sealed class LevelMethodCatalogTests
{
    /// <summary>The catalogue, built with the session calendar <c>session</c> is anchored to.</summary>
    /// <remarks>
    /// The shipped 16:00 Central close with no declared holidays. It is a parsed value rather than a live
    /// source, which is what lets a method hold one and stay a pure function of what it is handed (gh#257).
    /// </remarks>
    private static LevelMethodCatalog Catalog() => new(BarSessionCalendar.Parse("16:00", []));

    [Fact]
    public void AnUnknownMethod_IsAnError_AndListsTheKnownOnes()
    {
        // `murrey` rather than `fibonacci`: since gh#258 the vocabulary contains `pivot-fibonacci`, and an
        // unknown-name case whose name is a substring of a known one cannot tell the error message apart
        // from the list it prints.
        Action resolve = () => Catalog().Resolve("murrey");

        resolve.Should().Throw<KeyNotFoundException>()
            .WithMessage("*murrey*")
            .WithMessage("*swing*")
            .WithMessage("*session*")
            .WithMessage("*pivot-camarilla*")
            .WithMessage("*volume-poc*");
    }

    [Fact]
    public void MethodNamesAreCaseInsensitiveOnInput()
    {
        Catalog().Resolve("  SWING ").Name.Should().Be("swing");
    }

    [Fact]
    public void TheVocabularyIsExactlyTheMethodsThisServerDetectsWith()
    {
        // One place declares the set. `get_key_levels` asks for these by name (gh#259); an unknown
        // name is an error listing them, never an empty level set.
        Catalog().KnownNames.Should().BeEquivalentTo(
        [
            "swing",
            "session",
            "pivot-classic",
            "pivot-fibonacci",
            "pivot-camarilla",
            "pivot-woodie",
            "pivot-demark",
            "volume-poc",
            "volume-vah",
            "volume-val",
            "volume-traded",
        ]);
    }

    [Fact]
    public void EveryPivotFormulaTheDomainCanCompute_IsInTheVocabulary()
    {
        // The registration is written out five times, so a sixth formula added to `PivotFormula` and given a
        // name would compute perfectly well and be unaskable -- which from outside is indistinguishable from
        // a formula that does not exist. Read off the enum rather than listed, for the reason gh#259 gives
        // about the sixth variant.
        IEnumerable<string> servable = Enum.GetValues<PivotFormula>()
            .Where(formula => formula != PivotFormula.Unknown)
            .Select(PivotLevels.NameOf);

        servable.Should().BeSubsetOf(
            Catalog().KnownNames,
            "a formula the domain can compute but the catalogue cannot name is one no caller can ever ask for");
    }

    [Fact]
    public void EveryNameInTheVocabularyResolvesToTheMethodThatCarriesIt()
    {
        // The dictionary is keyed by each method's own Name, so a method whose Name changed without the
        // registration changing would resolve to something else entirely.
        LevelMethodCatalog catalog = Catalog();

        catalog.All.Should().NotBeEmpty("the vocabulary must actually contain something");

        foreach (ILevelMethod method in catalog.All)
        {
            catalog.Resolve(method.Name).Should().BeSameAs(method);
        }
    }

    [Fact]
    public void EveryMethodNameIsLowercase()
    {
        // Names are matched after lowercasing the caller's input, so an uppercase registration would be a
        // name nothing can ever resolve -- and the failure would be an "unknown method" error naming it.
        foreach (ILevelMethod method in Catalog().All)
        {
            method.Name.Should().Be(method.Name.ToLowerInvariant());
        }
    }

    [Fact]
    public void GetKeyLevelsDescriptions_NameEveryRegisteredMethod()
    {
        // A name the catalogue serves but the tool description does not list is a method an agent
        // cannot discover. Both the tool [Description] and the methods-parameter one must name them.
        MethodInfo method = typeof(KeyLevelTools).GetMethod(nameof(KeyLevelTools.GetKeyLevels))!;
        string tool = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty;
        string parameter = method.GetParameters().Single(p => p.Name == "methods")
            .GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty;

        foreach (string name in Catalog().KnownNames)
        {
            tool.Should().Contain(name, "get_key_levels [Description] must list " + name);
            parameter.Should().Contain(name, "the methods parameter [Description] must list " + name);
        }
    }
}
