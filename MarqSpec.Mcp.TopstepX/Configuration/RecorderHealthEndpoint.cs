using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Tools;
using ModelContextProtocol;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// <c>GET /health/recorder</c> — the same report as <c>get_recorder_health</c>, for a monitor that is not an
/// MCP client (R-5.14, gh#756).
/// </summary>
/// <remarks>
/// A mapped endpoint, installed <b>after</b> the bearer gate, so it stays authenticated. It is not the
/// liveness probe: a down feed is still HTTP 200, and the path opens a database connection, which is why it
/// is not carved out of the pipeline the way <see cref="HealthEndpoint"/> is. The body is the tool payload,
/// serialised with the same options, and carries no connection target.
/// </remarks>
public static class RecorderHealthEndpoint
{
    /// <summary>The path this serves.</summary>
    public const string Path = "/health/recorder";

    /// <summary>Maps the recorder report. Call this after the bearer gate.</summary>
    /// <param name="app">The application.</param>
    public static void MapRecorderHealth(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet(Path, WriteAsync);
    }

    private static async Task WriteAsync(
        HttpContext context,
        RecorderHealthService health,
        CancellationToken cancellationToken)
    {
        RecorderHealthReport.Report report = await health.ReadAsync(cancellationToken).ConfigureAwait(false);
        await context.Response.WriteAsJsonAsync(report, McpJsonUtilities.DefaultOptions, cancellationToken)
            .ConfigureAwait(false);
    }
}
