using System.Reflection;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Tools;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MarqSpec.Mcp.TopstepX.Tests.Tools;

/// <summary>
/// Every tool that touches the venue must translate a venue failure into something a caller can act on.
/// </summary>
/// <remarks>
/// <para>
/// This exists because one tool did not. <c>search_contracts</c> reached the venue without catching
/// <see cref="VenueException"/>, so the SDK reported a bare <i>"An error occurred invoking
/// 'search_contracts'"</i> and the reason — which is usually "no credentials yet" — never reached the caller
/// at all. Every sibling tool had the translation; that one was missed, and nothing noticed.
/// </para>
/// <para>
/// The last test here is the guard against it happening again: it walks the tool surface by reflection rather
/// than naming tools, so a tool added tomorrow is covered without anyone remembering to add it.
/// </para>
/// </remarks>
public sealed class VenueFailureReportingTests
{
    private static readonly DateTimeOffset _tuesday =
        MarketClock.FromMarket(new DateOnly(2026, 8, 18), new TimeOnly(9, 30)).ToUniversalTime();

    private static MarketDataOptions Options() =>
        new() { Instruments = "ES,NQ", MaxRows = 5_000, SessionCloseCentral = "16:00" };

    private static ReferenceTools Reference()
    {
        IOptions<MarketDataOptions> wrapped = Microsoft.Extensions.Options.Options.Create(Options());
        return new ReferenceTools(
            new InstrumentRegistry(wrapped),
            BarSessionCalendar.Parse("16:00", []),
            new UnconfiguredMarketDataGateway(),
            wrapped,
            new FakeTimeProvider(_tuesday));
    }

    private static AccountTools Accounts() =>
        new(new UnconfiguredMarketDataGateway(),
            new ToolGuards(Microsoft.Extensions.Options.Options.Create(Options())));

    [Fact]
    public async Task SearchContracts_ReportsWhyTheVenueCouldNotAnswer()
    {
        // The regression. Without the translation this surfaced as "An error occurred invoking
        // 'search_contracts'" and nothing else -- the operator was told something failed, not what to do.
        Func<Task> call = () => Reference().SearchContracts("ES", CancellationToken.None);

        (await call.Should().ThrowAsync<McpException>())
            .WithMessage("*credentials*")
            .WithMessage("*ProjectX__ApiKey*");
    }

    [Fact]
    public async Task ListAccounts_ReportsWhyTheVenueCouldNotAnswer()
    {
        Func<Task> call = () => Accounts().ListAccounts(true, CancellationToken.None);

        (await call.Should().ThrowAsync<McpException>()).WithMessage("*credentials*");
    }

    [Fact]
    public async Task GetPositions_ReportsWhyTheVenueCouldNotAnswer()
    {
        Func<Task> call = () => Accounts().GetPositions(9001, CancellationToken.None);

        (await call.Should().ThrowAsync<McpException>()).WithMessage("*credentials*");
    }

    [Fact]
    public void TheUnconfiguredExplanation_NamesTheSettingsAndTheirInversion()
    {
        // The message previously described a blocker that had since been resolved -- it still cited a NuGet
        // release that had already happened. A stale explanation is worse than a terse one, because it sends
        // the reader somewhere that is no longer true.
        Action call = () => new UnconfiguredMarketDataGateway()
            .GetAccountsAsync(true, CancellationToken.None);

        call.Should().Throw<VenueException>()
            .WithMessage("*ProjectX__ApiKey*")
            .WithMessage("*ProjectX__ApiSecret*")
            .WithMessage("*ProjectX__DataTier*")
            .WithMessage("*USERNAME*");   // the inversion, which is not guessable from the names
    }

    [Fact]
    public void NoVenueFacingToolIsMissingTheTranslation()
    {
        // Walks the surface by reflection rather than naming tools, so a tool added tomorrow is covered
        // without anyone remembering to add it here.
        //
        // THE RULE FOLLOWS A PATH, NOT A SET, and gh#414 is why it had to move at all. It used to read "any
        // tool type whose CONSTRUCTOR TAKES an IMarketDataGateway must catch VenueException", which was
        // exact while the market-data surface was one type holding the gateway, the bar cache and the
        // front-month service all at once. Splitting that type into five broke the proxy in both directions:
        //
        //   * TOO WIDE. IndicatorTools and KeyLevelTools read the gateway for its VenueId -- the key on
        //     every stored row -- and call nothing on it. Demanding a catch there would have meant adding
        //     a catch for an exception that cannot be raised, which is dead code that goes stale silently.
        //     They now take the gateway, read the id in the constructor and do NOT keep it, so there is no
        //     field to call and the venue is genuinely out of reach.
        //   * TOO NARROW, which is the half that mattered. BarTools reaches the venue through
        //     BarCacheService and takes no gateway at all; TapeTools and ContractRollTools reach it through
        //     VolumeFrontReader. Under the old parameter rule all three would have dropped out of the
        //     filter entirely and the market-data surface -- where the translation actually lives -- would
        //     have been covered by nothing.
        //
        // The first replacement asked "does ANY type in the tool's field graph catch?", and that was WEAKER
        // than what it replaced in two ways PR #423's review reproduced on a real tree:
        //
        //   * A CATCH SOMEWHERE ELSE COUNTED. Removing ReferenceTools' own catch -- reopening the historical
        //     search_contracts defect -- and giving it any field whose graph contains a catch made the check
        //     GREEN, where the old constructor rule was red. That is not a hypothetical reach: only four
        //     types in this assembly catch VenueException and SnapshotTools already holds two of them, so
        //     the first direct gateway call ever added there would have been invisible from the moment it
        //     was written.
        //   * A GATEWAY BEHIND ONE LAYER OF STRUCTURE VANISHED. HoldsAGateway matched an exactly
        //     IMarketDataGateway-typed field, so an IMarketDataGateway[] -- or a List<>, or a property's
        //     backing field of either -- dropped the type out of the walk entirely and the check went green
        //     with no translation anywhere.
        //
        // So the rule is now: WALK DOWN FROM THE TOOL TOWARDS THE GATEWAY, and a catch counts only where it
        // is actually ON that route. A type that catches shields what it holds -- BarTools catches for its
        // own cache, VolumeFrontReader for the two tools that publish a front -- and a type that holds a
        // gateway ITSELF and does not catch is a violation whatever its siblings do. The field walk looks
        // THROUGH arrays and generic arguments, so a gateway held in a collection is still held.
        Assembly assembly = typeof(ReferenceTools).Assembly;

        List<Type> toolTypes =
        [
            .. assembly.GetTypes().Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null),
        ];

        toolTypes.Should().NotBeEmpty("the reflection filter must actually match something");

        List<Type> venueFacing = [.. toolTypes.Where(Reaches)];

        venueFacing.Should().NotBeEmpty(
            "some tool must still reach the venue, or this check is measuring an empty set");

        foreach (Type type in venueFacing)
        {
            IReadOnlyList<Type>? unshielded = UnshieldedPath(type, []);

            unshielded.Should().BeNull(
                type.Name + " reaches the venue along "
                + string.Join(" -> ", (unshielded ?? []).Select(t => t.Name))
                + " and NOTHING ON THAT ROUTE catches VenueException, so a venue failure would reach the "
                + "caller as a bare 'an error occurred' with no reason. A catch elsewhere in this type's "
                + "field graph does not cover this route.");
        }
    }

    /// <summary>
    /// A route from this type down to one that holds a gateway, with no translation anywhere along it.
    /// </summary>
    /// <param name="type">The type to walk from.</param>
    /// <param name="onPath">The types already on this route, so a field cycle terminates.</param>
    /// <returns>The offending route, or <see langword="null"/> when every route is covered.</returns>
    /// <remarks>
    /// <para>
    /// <b>A catch shields what is BELOW it, and nothing beside it.</b> A <c>VenueException</c> raised inside
    /// a gateway call propagates through every frame between that call and the tool boundary, so the
    /// translation covers iff it sits on that route — which is what walking the route expresses and what
    /// gathering the graph into a set and asking whether it contains a catch does not.
    /// </para>
    /// <para>
    /// <b>Holding a gateway and not catching is a violation on its own terms.</b> That is the case the
    /// set-shaped version let through, and it is the shape of the original <c>search_contracts</c> defect.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<Type>? UnshieldedPath(Type type, HashSet<Type> onPath)
    {
        if (CatchesVenueException(type))
        {
            return null;
        }

        if (!onPath.Add(type))
        {
            // A field cycle. It adds no route this walk has not already taken.
            return null;
        }

        try
        {
            if (HoldsAGateway(type))
            {
                return [type];
            }

            foreach (Type collaborator in Collaborators(type))
            {
                if (UnshieldedPath(collaborator, onPath) is { } rest)
                {
                    return [type, .. rest];
                }
            }

            return null;
        }
        finally
        {
            onPath.Remove(type);
        }
    }

    /// <summary>Whether the venue is reachable from this type at all.</summary>
    /// <param name="type">The type to walk from.</param>
    /// <returns>Whether it, or anything it holds transitively, holds a gateway.</returns>
    private static bool Reaches(Type type) => Reaches(type, []);

    private static bool Reaches(Type type, HashSet<Type> seen) =>
        seen.Add(type)
        && (HoldsAGateway(type) || Collaborators(type).Any(c => Reaches(c, seen)));

    /// <summary>The product types this one holds, looked at through arrays and generic arguments.</summary>
    /// <param name="type">The holder.</param>
    /// <returns>Its collaborators, this assembly only.</returns>
    /// <remarks>
    /// Fields rather than constructor parameters, because a parameter that is read and dropped cannot be
    /// called afterwards. That distinction is the too-wide half above, and it is why four market-data tool
    /// types can take an <see cref="IMarketDataGateway"/> for its id and still, truly, not reach the venue.
    /// This assembly only: walking into EF Core or the BCL is unbounded and reports types nothing here can
    /// be responsible for translating.
    /// </remarks>
    private static IEnumerable<Type> Collaborators(Type type) =>
        type.GetFields(Members)
            .SelectMany(f => Carried(f.FieldType))
            .Where(t => t.Assembly == typeof(ReferenceTools).Assembly)
            .Distinct();

    /// <summary>A field's type and everything it structurally carries.</summary>
    /// <param name="type">The field's declared type.</param>
    /// <returns>That type, its element type, and its generic arguments, recursively.</returns>
    /// <remarks>
    /// <b>An <c>IMarketDataGateway[]</c> is a held gateway, and so is a <c>List&lt;IMarketDataGateway&gt;</c>
    /// or a property whose backing field is either.</b> Matching the declared field type alone let both out
    /// of the walk — green, with no translation anywhere — which PR #423's review reproduced.
    /// </remarks>
    private static IEnumerable<Type> Carried(Type type)
    {
        yield return type;

        if (type.HasElementType && type.GetElementType() is { } element)
        {
            foreach (Type carried in Carried(element))
            {
                yield return carried;
            }
        }

        if (type.IsGenericType)
        {
            foreach (Type argument in type.GetGenericArguments())
            {
                foreach (Type carried in Carried(argument))
                {
                    yield return carried;
                }
            }
        }
    }

    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic
        | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    /// <summary>Whether this type keeps a gateway it could call.</summary>
    /// <param name="type">The type.</param>
    /// <returns>Whether any field carries something assignable to <see cref="IMarketDataGateway"/>.</returns>
    /// <remarks>
    /// <b>Assignable, not equal</b>, so a field declared as a concrete gateway counts too — and through
    /// <see cref="Carried"/>, so does one held inside an array or a generic.
    /// </remarks>
    private static bool HoldsAGateway(Type type) =>
        type.GetFields(Members)
            .SelectMany(f => Carried(f.FieldType))
            .Any(typeof(IMarketDataGateway).IsAssignableFrom);

    private static bool CatchesVenueException(Type type)
    {
        // Nested types are searched too, and both static and instance members. An async method's body lives
        // in a compiler-generated state-machine type rather than on the method itself, and the translation
        // is often in a shared static helper -- an instance-only scan of the outer type misses both, which
        // is how this check first reported a false positive on AccountTools.
        IEnumerable<Type> family = [type, .. type.GetNestedTypes(Members)];

        return family
            .SelectMany(t => t.GetMethods(Members).Cast<MethodBase>().Concat(t.GetConstructors(Members)))
            .Select(m => m.GetMethodBody())
            .Where(b => b is not null)
            .SelectMany(b => b!.ExceptionHandlingClauses)
            .Any(c => c.Flags == ExceptionHandlingClauseOptions.Clause
                && c.CatchType == typeof(VenueException));
    }
}
