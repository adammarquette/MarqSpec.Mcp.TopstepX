using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// The closed vocabulary of sessions this server serves, on the same terms
/// <see cref="IndicatorCatalog"/> holds indicator names.
/// </summary>
public sealed class SessionCatalogTests
{
    /// <summary>
    /// A caller's session name is matched trimmed and case-insensitively, and a name outside the vocabulary is
    /// an error that LISTS the vocabulary — a typo answered with an empty series is indistinguishable from a
    /// market that produced none.
    /// </summary>
    [Fact]
    public void Resolve_IsCaseInsensitive_AndListsKnownNamesOnAMiss()
    {
        SessionCatalog catalog = Build();

        catalog.Resolve(" RTH ").Should()
            .Be(new SessionDefinition("rth", new TimeOnly(8, 30), new TimeOnly(15, 0), 30));

        Action miss = () => catalog.Resolve("regular");

        miss.Should().Throw<KeyNotFoundException>()
            .WithMessage("*regular*")
            .WithMessage("*asia*")
            .WithMessage("*europe*")
            .WithMessage("*full*")
            .WithMessage("*rth*");
    }

    /// <summary>
    /// <see cref="SessionCatalog.All"/> is the shipped order, and <see cref="SessionCatalog.KnownNames"/> is
    /// the same set sorted — the order an error message lists them in.
    /// </summary>
    [Fact]
    public void All_IsTheConfiguredOrder_AndKnownNamesIsSorted()
    {
        SessionCatalog catalog = Build();

        catalog.All.Select(session => session.Name).Should()
            .Equal(SessionDefinition.Defaults.Select(session => session.Name));
        catalog.KnownNames.Should().Equal("asia", "europe", "full", "rth");
    }

    /// <summary>
    /// The defensive re-check: options that never went through <c>ValidateOnStart</c> — built by hand, as here
    /// — cannot smuggle an off-grid session into the catalogue.
    /// </summary>
    [Fact]
    public void TheCatalogue_Refuses_WhenAHandBuiltOptionCarriesAnOffGridWindow()
    {
        MarketDataOptions options = new()
        {
            Sessions = new Dictionary<string, SessionOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["rth"] = new SessionOptions { Window = "08:45-15:00", BaseResolutionMinutes = 30 },
            },
        };

        Action build = () => new SessionCatalog(
            Options.Create(options), BarSessionCalendar.Parse("16:00", []));

        build.Should().Throw<ArgumentException>().WithMessage("*stored UTC bucket grid*");
    }

    private static SessionCatalog Build() => new(
        Options.Create(new MarketDataOptions()), BarSessionCalendar.Parse("16:00", []));
}
