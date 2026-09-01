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
        // THE RULE FOLLOWS THE FIELDS, and gh#414 is why it had to. It used to read "any tool type whose
        // CONSTRUCTOR TAKES an IMarketDataGateway must catch VenueException", which was exact while the
        // market-data surface was one type holding the gateway, the bar cache and the front-month service
        // all at once. Splitting that type into five broke the proxy in both directions:
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
        // So: a type can reach the venue when it HOLDS a gateway, and a tool reaches it when anything in
        // its field graph does. The translation may live anywhere in that same graph, because that is where
        // it legitimately lives: BarTools catches for its own cache, VolumeFrontReader catches for the two
        // tools that publish a front.
        Assembly assembly = typeof(ReferenceTools).Assembly;

        List<Type> toolTypes =
        [
            .. assembly.GetTypes().Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null),
        ];

        toolTypes.Should().NotBeEmpty("the reflection filter must actually match something");

        List<Type> venueFacing = [.. toolTypes.Where(t => Graph(t).Any(HoldsAGateway))];

        venueFacing.Should().NotBeEmpty(
            "some tool must still reach the venue, or this check is measuring an empty set");

        foreach (Type type in venueFacing)
        {
            Graph(type).Any(CatchesVenueException).Should().BeTrue(
                type.Name + " reaches the venue through "
                + string.Join(", ", Graph(type).Where(HoldsAGateway).Select(t => t.Name))
                + " but nothing on that path catches VenueException, so a venue failure would reach the "
                + "caller as a bare 'an error occurred' with no reason.");
        }
    }

    /// <summary>Every type this one can call, itself included — its instance fields, transitively.</summary>
    /// <param name="root">The type to walk from.</param>
    /// <returns>The reachable set, this assembly only.</returns>
    /// <remarks>
    /// Fields rather than constructor parameters, because a parameter that is read and dropped cannot be
    /// called afterwards. That distinction is the whole of the too-wide half above, and it is the reason
    /// four market-data tool types can take an <see cref="IMarketDataGateway"/> for its id and still, truly,
    /// not reach the venue.
    /// </remarks>
    private static IReadOnlyList<Type> Graph(Type root)
    {
        HashSet<Type> seen = [];
        Queue<Type> pending = new([root]);

        while (pending.TryDequeue(out Type? next))
        {
            if (!seen.Add(next))
            {
                continue;
            }

            foreach (Type field in next.GetFields(Members).Select(f => f.FieldType))
            {
                // This assembly only. Walking into EF Core or the BCL would be unbounded and would report
                // types nothing here can be responsible for translating.
                if (field.Assembly == typeof(ReferenceTools).Assembly)
                {
                    pending.Enqueue(field);
                }
            }
        }

        return [.. seen];
    }

    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic
        | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static bool HoldsAGateway(Type type) =>
        type.GetFields(Members).Any(f => f.FieldType == typeof(IMarketDataGateway));

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
