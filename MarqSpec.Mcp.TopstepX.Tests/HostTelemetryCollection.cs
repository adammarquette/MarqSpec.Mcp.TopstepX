using System.Diagnostics;

namespace MarqSpec.Mcp.TopstepX.Tests;

/// <summary>
/// Binds every suite that subscribes an <see cref="ActivityListener"/> to <c>MarqSpec.Mcp.TopstepX</c> into
/// one collection, so xUnit runs them serially.
/// </summary>
/// <remarks>
/// <b><see cref="ActivitySource.AddActivityListener"/> is process-global.</b> Two suites listening at once
/// would each see the other's spans, and — worse — the "nothing is listening, so nothing is allocated" claim
/// would fail whenever another class happened to be holding a listener. That is a flake whose cause sits in a
/// different file, so the coupling is declared here rather than discovered later.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class HostTelemetryCollection
{
    /// <summary>The collection name.</summary>
    public const string Name = "host-telemetry";
}
