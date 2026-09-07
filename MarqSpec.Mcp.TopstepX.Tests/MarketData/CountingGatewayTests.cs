using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// The double's own invariants — the ones a test built on it would otherwise assert about silently.
/// </summary>
/// <remarks>
/// <see cref="CountingGateway"/> lists everything it holds <b>front first</b>, and a caller that reads
/// <c>contracts[0]</c> is reading the front. That ordering is a <see cref="Enumerable.OrderByDescending"/> on
/// "is this the front", so a <c>frontContractId</c> the dictionary does not hold does not fail — every key
/// ties, the sort is stable, and <c>contracts[0]</c> quietly becomes whichever contract happened to be
/// inserted first. A test written to pin "the venue front is the thin one" would then be pinning nothing,
/// and would pass or fail for a reason unrelated to what it names. The constructor refuses instead.
/// </remarks>
public sealed class CountingGatewayTests
{
    [Fact]
    public void CountingGateway_RefusesAFrontContractItDoesNotHold()
    {
        Dictionary<string, IEnumerable<Bar>> byContract = new(StringComparer.Ordinal)
        {
            ["CON.F.US.TEST.Z26"] = [],
            ["CON.F.US.TEST.M27"] = [],
        };

        Action building = () => _ = new CountingGateway(byContract, "CON.F.US.TEST.H27");

        building.Should().Throw<ArgumentException>(
            "a front the double does not hold cannot be listed first, so contracts[0] would silently be "
            + "some other contract the test never named as the front")
            .WithMessage("*CON.F.US.TEST.H27*");
    }

    [Fact]
    public void CountingGateway_ListsTheFrontFirst_WhenItHoldsIt()
    {
        // The converse, and the reason the refusal above is a guard rather than a restriction: a front the
        // double DOES hold still comes back at contracts[0] whatever order the keys went in.
        Dictionary<string, IEnumerable<Bar>> byContract = new(StringComparer.Ordinal)
        {
            ["CON.F.US.TEST.Z26"] = [],
            ["CON.F.US.TEST.M27"] = [],
        };

        CountingGateway gateway = new(byContract, "CON.F.US.TEST.M27");

        gateway.KnownContracts.Should().Contain("CON.F.US.TEST.M27");
    }
}
