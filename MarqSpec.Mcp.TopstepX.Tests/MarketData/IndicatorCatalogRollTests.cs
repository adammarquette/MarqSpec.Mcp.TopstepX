using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// Every indicator this server actually computes refuses a spliced series — swept, not listed.
/// </summary>
/// <remarks>
/// <para>
/// ADR-0011 claims the roll guard sits on the shared path so that <b>a new indicator inherits the rule rather
/// than remembering it</b>. That is a claim about indicators nobody has written yet, so it cannot be pinned by
/// naming the ones that exist: a test listing today's eleven would stay green on the day someone adds a
/// twelfth that computes straight through a roll.
/// </para>
/// <para>
/// So this walks <see cref="IndicatorCatalog.All"/> — the closed vocabulary the projection and the tool
/// surface both read — and asserts the refusal for whatever is in it. Same shape as
/// <c>NoVenueFacingToolIsMissingTheTranslation</c>, and for the same reason: the interesting failure is the
/// one nobody remembered to add a test for.
/// </para>
/// </remarks>
public sealed class IndicatorCatalogRollTests
{
    private static DateTimeOffset SessionStart =>
        MarketClock.FromMarket(new DateOnly(2026, 8, 18), new TimeOnly(9, 0)).ToUniversalTime();

    private static IndicatorCatalog Catalog() =>
        new(Options.Create(new IndicatorOptions()), BarSessionCalendar.Parse("16:00", []));

    /// <summary>
    /// Sixty bars under one symbol, the back half from a different contract forty points higher.
    /// </summary>
    /// <remarks>
    /// Long enough that every default period — Bollinger at 20, MACD's slow leg at 26 — is satisfied on both
    /// sides of the seam. A series too short to warm up would return all-nulls and pass this test without the
    /// guard ever being reached, which is a green test that proves nothing.
    /// </remarks>
    private static IReadOnlyList<Bar> Spliced() =>
        [.. Enumerable.Range(0, 60).Select(i =>
        {
            bool rolled = i >= 30;
            decimal close = (rolled ? 140m : 100m) + (i % 5);
            return new Bar(
                SessionStart.AddMinutes(5 * i),
                close,
                close + 1m,
                close - 1m,
                close,
                1_000,
                rolled ? "CON.F.US.EP.Z26" : "CON.F.US.EP.U26");
        })];

    [Fact]
    public void EveryConfiguredIndicator_RefusesASplicedSeries()
    {
        IndicatorCatalog catalog = Catalog();
        IReadOnlyList<Bar> spliced = Spliced();

        catalog.All.Should().NotBeEmpty("the sweep must actually cover something");

        foreach (IIndicator indicator in catalog.All)
        {
            Action compute = () => indicator.Compute(spliced);

            compute.Should().Throw<ArgumentException>(
                indicator.Name + " computed a value across a contract roll. Adjacent quarters do not trade "
                + "at the same price, so whatever it returned is the roll gap reported as market movement.")
                .WithMessage("*contract*");
        }
    }

    [Fact]
    public void EveryConfiguredIndicator_StillComputesASingleContractSeries()
    {
        // The other half. A guard that refused everything would pass the test above and break the server, and
        // the two failures look nothing alike from the outside.
        IndicatorCatalog catalog = Catalog();
        IReadOnlyList<Bar> singleContract = [.. Spliced().Take(30)];

        foreach (IIndicator indicator in catalog.All)
        {
            Action compute = () => indicator.Compute(singleContract);

            compute.Should().NotThrow(indicator.Name + " refuses an ordinary single-contract series");
        }
    }
}
