using MarqSpec.Mcp.TopstepX.Infra;

namespace MarqSpec.Mcp.TopstepX.Infra.Tests;

/// <summary>
/// The two environments, synthesised once per test class rather than once per test: each synthesis is a
/// round trip through the jsii kernel, and the assertions below are about what the template says, not about
/// synthesising it again.
/// </summary>
/// <remarks>
/// The outbound shape used here is <b>arbitrary for the tests and is not a choice</b> — every assertion in
/// the classes that share this fixture holds in all four shapes, and the shapes themselves are asserted one
/// by one in <see cref="OutboundPathTests"/>.
/// </remarks>
public sealed class EnvironmentTemplates
{
    public const OutboundPath FixtureShape = OutboundPath.NatGateway;

    public Synthesised Production { get; } = Synthesised.Production(FixtureShape);

    public Synthesised Staging { get; } = Synthesised.Staging(FixtureShape);

    public static IEnumerable<object[]> Both =>
    [
        ["production", "marqspec.com"],
        ["staging", "staging.marqspec.com"],
    ];

    public Synthesised For(string envName) => envName == "production" ? Production : Staging;
}
