using System.Text.Json.Serialization;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// The one path that answers without a credential.
/// </summary>
/// <remarks>
/// <para>
/// <b>An Application Load Balancer cannot send a bearer token.</b> A target-group probe is a bare
/// <c>GET</c> from the load balancer, and <see cref="BearerTokenGate"/> is global middleware, so before this
/// existed every path answered 401 and a task in the deployment ADR-0021 describes would be marked unhealthy
/// and killed on arrival (gh#513, gh#509). The gate stays global and deny-by-default; exactly one path is
/// allowed past it.
/// </para>
/// <para>
/// <b>A terminal branch, not a mapped endpoint, and that is the whole mechanism.</b>
/// <c>WebApplication</c> inserts <c>UseRouting</c> ahead of every middleware the composition root adds and
/// <c>UseEndpoints</c> after all of them, so a <c>MapGet("/health", …)</c> registered before the gate would
/// still execute after it — the gate would answer 401 and the endpoint would never run. Registering earlier
/// does not put an endpoint earlier. <c>MapWhen</c> short-circuits the pipeline for the requests it claims,
/// so nothing added after it is reached, and <i>that</i> is what makes the ordering in
/// <c>Program.MapHttpTransport</c> mean what it reads like. Changing this to an endpoint is a one-word edit
/// that silently closes the probe; <c>HealthEndpointTests</c> is what catches it.
/// </para>
/// <para>
/// <b>One exact path, matched ordinally, and one method.</b> A prefix match would hand
/// <c>/health/anything</c> out unauthenticated and a case-insensitive one would turn a path into a family.
/// Both read as tidying in a diff, which is why both are pinned by tests rather than by care.
/// </para>
/// <para>
/// <b>It reaches nothing.</b> The store answer is the one
/// <see cref="StoreAvailabilityHolder"/> already carries from startup — not <c>MapHealthChecks</c>, and no
/// round trip. The ALB probes every 30 s per task, and a health check that opened a database connection
/// would be load rather than a measurement of it. An unavailable store is still <c>200</c>: this is
/// liveness, and the tools that need no store answer normally, so killing the task would replace a degraded
/// server with no server. Readiness is deliberately not modelled here.
/// </para>
/// <para>
/// <b>Nothing about the venue, the token or the connection string appears in the body.</b> This is the only
/// thing on the server reachable with no credential, and THIS REPOSITORY IS PUBLIC.
/// </para>
/// </remarks>
public static class HealthEndpoint
{
    /// <summary>The one path this claims.</summary>
    public const string Path = "/health";

    private const string Available = "available";
    private const string Unavailable = "unavailable";

    /// <summary>
    /// Serves the liveness probe, short-circuiting everything registered after it.
    /// </summary>
    /// <param name="app">The application. Install this <b>before</b> the bearer gate.</param>
    public static void UseHealthEndpoint(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapWhen(IsTheProbe, branch => branch.Run(WriteAsync));
    }

    private static bool IsTheProbe(HttpContext context)
        => HttpMethods.IsGet(context.Request.Method)
            && context.Request.Path.Equals(Path, StringComparison.Ordinal);

    private static async Task WriteAsync(HttpContext context)
    {
        StoreAvailability store = context.RequestServices
            .GetRequiredService<StoreAvailabilityHolder>().Value;
        DeploymentOptions deployment = context.RequestServices
            .GetRequiredService<IOptions<DeploymentOptions>>().Value;

        // The explanation StoreAvailability carries when it is unavailable is NOT written here: it names the
        // connection string and the fix, which is the right thing to tell an operator holding a token and
        // the wrong thing to hand an unauthenticated caller.
        await context.Response.WriteAsJsonAsync(
            new HealthReport(
                "ok",
                store.IsAvailable ? Available : Unavailable,
                OrUnknown(deployment.Version),
                OrUnknown(deployment.ImageDigest)))
            .ConfigureAwait(false);
    }

    /// <summary>An unset value and a blank one are the same absence.</summary>
    private static string OrUnknown(string? value)
        => string.IsNullOrWhiteSpace(value) ? DeploymentOptions.Unknown : value;

    /// <summary>
    /// The probe's body.
    /// </summary>
    /// <remarks>
    /// The names are written out rather than left to a serializer policy: this shape is read by a target
    /// group's health check and by whoever is asking which release is running, so it must not move when a
    /// JSON option somewhere else changes.
    /// </remarks>
    private sealed record HealthReport(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("store")] string Store,
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("digest")] string Digest);
}
