using System.Text.Json.Serialization;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// The RFC 9728 protected-resource metadata: the one document a connector reads before it has a token.
/// </summary>
/// <remarks>
/// <para>
/// <b>The 401 names it, so it cannot sit behind the gate.</b> Anthropic's connector flow is: call the MCP
/// URL, receive <c>401</c> with <c>WWW-Authenticate: Bearer resource_metadata="…"</c>, fetch that document,
/// read <c>authorization_servers</c>, discover the issuer, run authorization-code + PKCE, and come back with
/// a bearer. Every step before the last is credential-less by construction.
/// </para>
/// <para>
/// <b>A terminal branch, for the reason <see cref="HealthEndpoint"/> gives.</b> <c>WebApplication</c> runs
/// every mapped endpoint after every middleware, so a <c>MapGet</c> here would be answered 401 by the gate
/// registered after it. <c>MapWhen</c> short-circuits the pipeline for the two paths it claims — the bare
/// well-known path and the path-suffixed one RFC 9728 derives for a resource at <c>/mcp</c> — matched
/// ordinally, <c>GET</c> only. Neighbouring paths, other casings, a trailing slash and any other method stay
/// behind the gate, and the tests say so.
/// </para>
/// <para>
/// <b><c>resource</c> is the configured string, byte for byte.</b> The client compares it with what the
/// user typed; a <c>Uri</c> round trip would lowercase the host and drop a default port.
/// </para>
/// </remarks>
public static class ProtectedResourceMetadataEndpoint
{
    /// <summary>The well-known path for the resource itself.</summary>
    public const string Path = "/.well-known/oauth-protected-resource";

    /// <summary>The RFC 9728 path-suffixed form for a resource at <c>/mcp</c>, and what the challenge names.</summary>
    public const string McpPath = Path + McpOptions.McpEndpointPath;

    /// <summary>The one way a token is presented: the <c>Authorization</c> header.</summary>
    private const string HeaderMethod = "header";

    /// <summary>
    /// Serves the metadata document on both paths, short-circuiting everything registered after it.
    /// </summary>
    /// <param name="app">The application. Install this <b>before</b> the gate.</param>
    /// <param name="oauth">The OAuth section.</param>
    public static void UseProtectedResourceMetadata(this WebApplication app, OAuthOptions oauth)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(oauth);

        // Built once: nothing in it changes per request, and the shape must not move when a serializer
        // option somewhere else does.
        ProtectedResourceMetadata document = new(
            oauth.ResourceUrl,
            [oauth.Issuer],
            [oauth.RequiredScope],
            [HeaderMethod]);

        app.MapWhen(IsTheDocument, branch => branch.Run(context => context.Response.WriteAsJsonAsync(document)));
    }

    private static bool IsTheDocument(HttpContext context)
        => HttpMethods.IsGet(context.Request.Method)
            && (context.Request.Path.Equals(Path, StringComparison.Ordinal)
                || context.Request.Path.Equals(McpPath, StringComparison.Ordinal));

    /// <summary>The document, with every name written out (RFC 9728 §2).</summary>
    private sealed record ProtectedResourceMetadata(
        [property: JsonPropertyName("resource")] string Resource,
        [property: JsonPropertyName("authorization_servers")] string[] AuthorizationServers,
        [property: JsonPropertyName("scopes_supported")] string[] ScopesSupported,
        [property: JsonPropertyName("bearer_methods_supported")] string[] BearerMethodsSupported);
}
