using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;

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
            .WithMessage("*pivot-camarilla*");
    }

    [Fact]
    public void MethodNamesAreCaseInsensitiveOnInput()
    {
        Catalog().Resolve("  SWING ").Name.Should().Be("swing");
    }

    [Fact]
    public void TheVocabularyIsExactlyTheMethodsThisServerDetectsWith()
    {
        // One place declares the set. The tool surface does not yet carry a method argument -- selecting one
        // per call is a later card on gh#232 -- so this is the whole vocabulary, and `session` and the five
        // `pivot-*` names are in it without `get_key_levels` being able to ask for them yet.
        Catalog().KnownNames.Should().BeEquivalentTo(
        [
            "swing",
            "session",
            "pivot-classic",
            "pivot-fibonacci",
            "pivot-camarilla",
            "pivot-woodie",
            "pivot-demark",
        ]);
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
}
