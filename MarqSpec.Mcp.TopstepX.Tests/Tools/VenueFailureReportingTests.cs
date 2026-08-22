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
        // without anyone remembering to add it here. The check is structural: any tool type that takes an
        // IMarketDataGateway must catch VenueException somewhere in its body.
        Assembly assembly = typeof(ReferenceTools).Assembly;

        List<Type> venueFacing =
        [
            .. assembly.GetTypes()
                .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
                .Where(t => t.GetConstructors()
                    .Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(IMarketDataGateway)))),
        ];

        venueFacing.Should().NotBeEmpty("the reflection filter must actually match something");

        foreach (Type type in venueFacing)
        {
            catchesVenueException(type).Should().BeTrue(
                type.Name + " takes an IMarketDataGateway but never catches VenueException, so a venue "
                + "failure would reach the caller as a bare 'an error occurred' with no reason.");
        }

        static bool catchesVenueException(Type type)
        {
            // Nested types are searched too, and both static and instance members. An async method's body
            // lives in a compiler-generated state-machine type rather than on the method itself, and the
            // translation is often in a shared static helper -- an instance-only scan of the outer type
            // misses both, which is how this check first reported a false positive on AccountTools.
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

            IEnumerable<Type> family = [type, .. type.GetNestedTypes(all)];

            return family
                .SelectMany(t => t.GetMethods(all).Cast<MethodBase>().Concat(t.GetConstructors(all)))
                .Select(m => m.GetMethodBody())
                .Where(b => b is not null)
                .SelectMany(b => b!.ExceptionHandlingClauses)
                .Any(c => c.Flags == ExceptionHandlingClauseOptions.Clause
                    && c.CatchType == typeof(VenueException));
        }
    }
}
