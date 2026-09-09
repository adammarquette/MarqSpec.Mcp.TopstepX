using System.Reflection;

namespace MarqSpec.Mcp.TopstepX.Infra;

/// <summary>
/// The checked-in OTLP collector configuration, <c>Collector/otel-collector-config.yaml</c>, embedded in this
/// assembly and read at synth time (gh#537).
/// </summary>
/// <remarks>
/// Embedded rather than read from a path so a <c>cdk synth</c> works from wherever the CLI runs the app, and
/// so the file cannot be edited into a template without a rebuild. The file itself explains why the
/// configuration travels as an environment variable rather than as a mounted file, and why no endpoint or
/// token may appear in it.
/// </remarks>
public static class CollectorConfiguration
{
    private const string ResourceName = "otel-collector-config.yaml";

    /// <summary>The file's text, exactly as it is checked in.</summary>
    public static string Yaml { get; } = Read();

    private static string Read()
    {
        using var stream = typeof(CollectorConfiguration).GetTypeInfo().Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The embedded resource '{ResourceName}' is missing from the Infra assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
