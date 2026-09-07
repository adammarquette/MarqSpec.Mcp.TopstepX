using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// The catalogue seen through a <see cref="SeriesKey"/>: the same vocabulary on a resolution series, and the
/// vocabulary minus session-anchored VWAP on a session series.
/// </summary>
/// <remarks>
/// <para>
/// <b>The resolution case has to be the identical list, not merely an equal one.</b> Three consumers walk it
/// — what the projection computes, what the reconcile is allowed to delete, and the read-time probe — and
/// they must agree by construction. Anything that reordered or re-derived the list for a resolution key
/// would change the stored series that already exists, so it is pinned with <c>BeSameAs</c>.
/// </para>
/// <para>
/// <b>A session series has one bar a day, so <c>vwap</c> is meaningless on it</b> — it anchors on the
/// session, and a session that is one bar has no intra-session volume distribution to weight (ADR-0022).
/// <c>vwap-rolling</c> is a window over N bars and stays: on a session series that is a twenty-day rolling
/// VWAP, which is a real number.
/// </para>
/// <para>
/// <b>The refusal is by name, at the surface, rather than an empty series.</b> ADR-0022 settles it: the tool
/// surface says so rather than omitting it silently. An empty series is indistinguishable from a market that
/// produced none, which is the exact failure the closed vocabulary exists to prevent.
/// </para>
/// </remarks>
public sealed class IndicatorCatalogSessionTests
{
    private static readonly SeriesKey _resolution = new SeriesKey.Resolution("test", "ES", 5);
    private static readonly SeriesKey _session = new SeriesKey.Session("test", "ES", "rth");

    private static IndicatorCatalog Catalog() =>
        new(
            Options.Create(new IndicatorOptions
            {
                // An additional period on two families, so nothing below can pass by accident on a catalogue
                // that happens to hold one instance per name.
                AdditionalEmaPeriods = "50",
                AdditionalRollingVwapPeriods = "60",
            }),
            BarSessionCalendar.Parse("16:00", []));

    [Fact]
    public void ForASessionSeries_ExcludesVwap()
    {
        IndicatorCatalog catalog = Catalog();

        IReadOnlyList<IIndicator> forSession = catalog.ForSeries(_session);

        forSession.Select(i => i.Name).Should().NotContain("vwap");
        forSession.Select(i => i.Name).Should().Contain("vwap-rolling");

        // All's order, minus the excluded instances and nothing else.
        forSession.Should().Equal(catalog.All.Where(i => i.Name != "vwap"));

        // Computed once per catalogue: the projection asks for it per series, and rebuilding the list on
        // every call would allocate a new one per bar segment.
        catalog.ForSeries(new SeriesKey.Session("test", "NQ", "globex")).Should().BeSameAs(forSession);
    }

    [Fact]
    public void ForAResolutionSeries_IsTheSameListAsAll()
    {
        IndicatorCatalog catalog = Catalog();

        catalog.ForSeries(_resolution).Should().BeSameAs(catalog.All);
    }

    [Fact]
    public void ResolveFor_RefusesVwapOnASession_NamingTheAlternatives()
    {
        IndicatorCatalog catalog = Catalog();

        Action resolve = () => catalog.ResolveFor(_session, "vwap", null);

        string names = string.Join(
            ", ",
            catalog.ForSeries(_session).Select(i => i.Name).Distinct(StringComparer.Ordinal));

        resolve.Should().Throw<ArgumentException>()
            .Which.Message.Should().StartWith(
                "'vwap' anchors on the session and a one-bar session has no VWAP; on a session series ask "
                + "for 'vwap-rolling' or one of: " + names);

        // The rest of the vocabulary is untouched on a session series.
        catalog.ResolveFor(_session, "vwap-rolling", null).Name.Should().Be("vwap-rolling");
        catalog.ResolveFor(_session, "atr", null).Name.Should().Be("atr");
    }

    [Fact]
    public void ResolveFor_OnAResolution_IsResolve()
    {
        IndicatorCatalog catalog = Catalog();

        catalog.ResolveFor(_resolution, "vwap", null).Should().BeSameAs(catalog.Resolve("vwap", null));
        catalog.ResolveFor(_resolution, "ema", 50).Should().BeSameAs(catalog.Resolve("ema", 50));
        catalog.ResolveFor(_resolution, "ATR", null).Should().BeSameAs(catalog.Resolve("atr", null));
    }
}
