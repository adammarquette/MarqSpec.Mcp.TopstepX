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
    [Fact]
    public void AnUnknownMethod_IsAnError_AndListsTheKnownOnes()
    {
        Action resolve = () => new LevelMethodCatalog().Resolve("fibonacci");

        resolve.Should().Throw<KeyNotFoundException>()
            .WithMessage("*fibonacci*")
            .WithMessage("*swing*");
    }

    [Fact]
    public void MethodNamesAreCaseInsensitiveOnInput()
    {
        new LevelMethodCatalog().Resolve("  SWING ").Name.Should().Be("swing");
    }

    [Fact]
    public void TheVocabularyIsExactlyTheMethodsThisServerDetectsWith()
    {
        // One place declares the set. The tool surface does not yet carry a method argument -- selecting one
        // per call is a later card on gh#232 -- so this is the whole vocabulary, not a subset of it.
        new LevelMethodCatalog().KnownNames.Should().BeEquivalentTo(["swing"]);
    }

    [Fact]
    public void EveryNameInTheVocabularyResolvesToTheMethodThatCarriesIt()
    {
        // The dictionary is keyed by each method's own Name, so a method whose Name changed without the
        // registration changing would resolve to something else entirely.
        LevelMethodCatalog catalog = new();

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
        foreach (ILevelMethod method in new LevelMethodCatalog().All)
        {
            method.Name.Should().Be(method.Name.ToLowerInvariant());
        }
    }
}
