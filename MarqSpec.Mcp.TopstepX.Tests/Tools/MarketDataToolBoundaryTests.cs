using System.Reflection;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Tools;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace MarqSpec.Mcp.TopstepX.Tests.Tools;

/// <summary>
/// The boundary gh#414 exists to create: each market-data tool type holds only what its own concern reads.
/// </summary>
/// <remarks>
/// <para>
/// <b>The number is the point of this card, so the number is pinned here rather than only stated in a PR
/// body.</b> gh#391 split <c>MarketDataTools</c> into five files and its reviewer ruled that sufficient for
/// that card and explicitly insufficient for the coupling — "one type, 15 dependencies, no compiler-enforced
/// boundary". A partial class splits the file, not the type: every concern could still reach every field,
/// and nothing would have gone red when a future edit in the bars file took a footprint cache.
/// </para>
/// <para>
/// <b>Why the exact SET and not the count.</b> A count is satisfied by a swap — trading
/// <c>BarCacheService</c> for <c>FootprintCacheService</c> keeps <see cref="BarTools"/> at four and undoes
/// the whole point. So each case names the types it expects, and the count follows from the set rather than
/// being asserted beside it, so the two cannot drift apart.
/// </para>
/// <para>
/// <b>These are the constructor's parameters, which is what the compiler and the container both read.</b>
/// A field assigned from elsewhere is not reachable without one, and <see cref="InstrumentResolver"/> and
/// <see cref="VolumeFrontReader"/> are the seams the shared members went behind — so the negative cases
/// below check that no tool type can reach <see cref="InstrumentRegistry"/>,
/// <see cref="StoreAvailabilityHolder"/> or <see cref="TapeVolumeFrontService"/> at all.
/// </para>
/// </remarks>
public sealed class MarketDataToolBoundaryTests
{
    /// <summary>The five types <c>MarketDataTools</c> became, and what each one is allowed to hold.</summary>
    /// <remarks>
    /// Read this table as the card's deliverable. Fifteen dependencies on one type became 4, 6, 8, 8 and 6 —
    /// and, more than the arithmetic, none of the five can now name what another one holds.
    /// </remarks>
    public static TheoryData<Type, Type[]> Expected =>
        new()
        {
            {
                typeof(BarTools),
                [
                    typeof(InstrumentResolver),
                    typeof(BarCacheService),
                    typeof(ToolGuards),
                    typeof(TimeProvider),
                ]
            },
            {
                typeof(IndicatorTools),
                [
                    typeof(InstrumentResolver),
                    typeof(TopstepXDbContext),
                    typeof(IndicatorCatalog),
                    typeof(IndicatorCacheService),
                    typeof(IMarketDataGateway),
                    typeof(ToolGuards),
                ]
            },
            {
                typeof(KeyLevelTools),
                [
                    typeof(InstrumentResolver),
                    typeof(TopstepXDbContext),
                    typeof(IndicatorCatalog),
                    typeof(LevelMethodCatalog),
                    typeof(IMarketDataGateway),
                    typeof(ToolGuards),
                    typeof(VolumeProfileService),
                    typeof(IOptions<KeyLevelDetectionOptions>),
                ]
            },
            {
                typeof(TapeTools),
                [
                    typeof(InstrumentResolver),
                    typeof(TopstepXDbContext),
                    typeof(IMarketDataGateway),
                    typeof(ToolGuards),
                    typeof(TapeAvailabilityHolder),
                    typeof(VolumeProfileService),
                    typeof(VolumeFrontReader),
                    typeof(FootprintCacheService),
                ]
            },
            {
                typeof(ContractRollTools),
                [
                    typeof(InstrumentResolver),
                    typeof(TopstepXDbContext),
                    typeof(IMarketDataGateway),
                    typeof(LevelMethodCatalog),
                    typeof(VolumeFrontReader),
                    typeof(TimeProvider),
                ]
            },
        };

    [Theory]
    [MemberData(nameof(Expected))]
    public void AMarketDataToolType_HoldsExactlyTheDependenciesItsOwnConcernReads(Type tool, Type[] expected)
    {
        // Equivalence in BOTH directions, deliberately. A subset assertion would pass while a type quietly
        // took a sixth collaborator it never uses, which is the drift this exists to stop; a superset one
        // would pass while a dependency the concern genuinely needs was replaced by a wider stand-in.
        Constructor(tool).GetParameters().Select(p => p.ParameterType)
            .Should().BeEquivalentTo(
                expected,
                tool.Name + " no longer holds exactly its own concern's dependencies. If the concern really "
                + "changed, change this table in the same commit and say so -- the whole of gh#414 is that "
                + "this list is short and specific.");
    }

    [Fact]
    public void NoMarketDataToolType_CanReachTheRegistryOrTheStoreHolder()
    {
        // Resolve() is the one member every concern calls, and gh#414's Scope asked where it lives. It is an
        // INJECTED COLLABORATOR rather than a base class or an extension, and this is the property that
        // choice buys: a base class would have put both of these back into all five constructors as base
        // parameters, and an extension method cannot hold state so it would have taken them as arguments at
        // every call site. Behind InstrumentResolver, neither is nameable from a tool type at all -- so the
        // store-availability check cannot be forgotten by a new tool, because there is no way to resolve a
        // symbol without going through the object that performs it.
        foreach (Type tool in Expected.Select(row => (Type)row[0]))
        {
            Constructor(tool).GetParameters().Select(p => p.ParameterType)
                .Should().NotContain(
                    [typeof(InstrumentRegistry), typeof(StoreAvailabilityHolder)],
                    tool.Name + " takes the registry or the store-availability holder directly, which lets "
                    + "it resolve a symbol without the store check InstrumentResolver performs.");
        }
    }

    [Fact]
    public void NoToolType_ReachesTheTapeVolumeFrontServiceDirectly()
    {
        // The second shared member, on the same terms. get_footprint, get_volume_profile and
        // get_contract_roll all publish `front`, and the VenueException translation that gives it its
        // "The venue could not answer: " prefix lives once, inside VolumeFrontReader. A type that took the
        // service directly would be one hand-written catch away from dropping that prefix on one tool and
        // keeping it on the others.
        //
        // Swept over EVERY [McpServerToolType] in the assembly rather than the five in the table, because
        // the failure this describes is a tool added tomorrow, and one named in a list is one the list's
        // author already thought about.
        List<Type> reaching =
        [
            .. ToolTypes().Where(t => Constructor(t).GetParameters()
                .Any(p => p.ParameterType == typeof(TapeVolumeFrontService))),
        ];

        reaching.Should().BeEmpty(
            "the front-month read is VolumeFrontReader's, and a tool holding the service can publish a "
            + "differently-translated `front` than its siblings");
    }

    [Fact]
    public void EveryMarketDataToolType_IsRegisteredAsAToolTypeAndHasOneConstructor()
    {
        // The table above is only meaningful if the thing it measures is the constructor the SDK and the
        // container actually use. Two public constructors would mean ActivatorUtilities picks one by
        // greediest-resolvable, and the narrow one this card is about could be bypassed entirely.
        foreach (Type tool in Expected.Select(row => (Type)row[0]))
        {
            tool.GetCustomAttribute<McpServerToolTypeAttribute>().Should().NotBeNull(
                tool.Name + " is not an [McpServerToolType], so WithToolsFromAssembly never finds its tools");

            tool.GetConstructors().Should().ContainSingle(
                tool.Name + " must have exactly one public constructor, or the container's choice of which "
                + "one to call stops being the narrow one this test measures");
        }
    }

    [Fact]
    public void NoMarketDataToolType_TakesAnOptionalConstructorParameter()
    {
        // gh#391's regression, kept closed across the split. ActivatorUtilities honours a parameter's
        // DEFAULT VALUE instead of throwing when the type behind it is unregistered, so an optional
        // parameter turns "this fails at container build" into "this quietly builds a throwaway on every
        // call". CompositionRootTests drops a registration per type and proves the build throws; this is the
        // same guarantee stated on the constructors, where the mistake is actually made.
        foreach (Type tool in Expected.Select(row => (Type)row[0]))
        {
            Constructor(tool).GetParameters().Where(p => p.IsOptional).Select(p => p.Name)
                .Should().BeEmpty(
                    tool.Name + " has an optional constructor parameter, so dropping the registration behind "
                    + "it fails at CALL time rather than at container build");
        }
    }

    private static IEnumerable<Type> ToolTypes() =>
        typeof(BarTools).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null);

    private static ConstructorInfo Constructor(Type tool) => tool.GetConstructors().Single();
}
