namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// The <c>Mcp:OAuth</c> section: what the OAuth mode of the HTTP gate validates a token against, and what it
/// tells a client that has none (ADR-0021, ADR-0023 §9, gh#512).
/// </summary>
/// <remarks>
/// <para>
/// Three values are required and validated at startup naming their key; the scope has a default. Every one
/// of them is public information — an issuer URL, app client ids, a scope name and this server's own
/// address — so none of them is a secret and none of them is redacted anywhere. <b>The issuer's client
/// secret never reaches this process</b>: a resource server verifies signatures with the issuer's published
/// public keys and holds nothing that could mint a token.
/// </para>
/// <para>
/// <b><see cref="ResourceUrl"/> is echoed exactly as entered.</b> Anthropic's connector documentation has
/// the client compare the metadata document's <c>resource</c> against the URL the user typed, path
/// included; a normalised form is a mismatch. So it is kept as a string, validated as a URL, and never
/// rebuilt from a <c>Uri</c>.
/// </para>
/// </remarks>
public sealed class OAuthOptions
{
    /// <summary>The scope a token must carry when nothing configures another.</summary>
    public const string DefaultRequiredScope = "topstepx-mcp/read";

    /// <summary>
    /// The issuer, exactly as it appears in a token's <c>iss</c> claim:
    /// <c>https://cognito-idp.&lt;region&gt;.amazonaws.com/&lt;poolId&gt;</c>. Discovery is read from
    /// <c>{Issuer}/.well-known/openid-configuration</c>.
    /// </summary>
    public string Issuer { get; init; } = string.Empty;

    /// <summary>
    /// The app clients whose tokens are accepted, comma-separated — the connector client and the deploy-check
    /// client. A token from any other client on the same pool is refused.
    /// </summary>
    public string ClientIds { get; init; } = string.Empty;

    /// <summary>The one scope a token must carry. Compared as a whole entry of the space-separated claim.</summary>
    /// <remarks>
    /// <b>Blank is the same as unset</b> and binds to <see cref="DefaultRequiredScope"/>. <c>.env.example</c>
    /// lists the key with no value, as it lists every key, and <c>Mcp__OAuth__RequiredScope=</c> exported
    /// from that file used to refuse startup while the comment beside it said it defaulted (gh#512 review).
    /// A value with whitespace inside is still refused — the claim is space-separated and this is one entry.
    /// </remarks>
    public string RequiredScope
    {
        get;
        init => field = string.IsNullOrWhiteSpace(value) ? DefaultRequiredScope : value;
    } = DefaultRequiredScope;

    /// <summary>
    /// The public URL of the MCP endpoint, exactly as a user enters it into a connector —
    /// <c>https://topstepx-mcp.staging.marqspec.com/mcp</c>. Must end in <c>/mcp</c>, because that is where
    /// the endpoint is served and nowhere else.
    /// </summary>
    public string ResourceUrl { get; init; } = string.Empty;

    /// <summary>The configured client ids, trimmed, empties dropped, in the order written.</summary>
    public IReadOnlyList<string> ClientIdList()
        => string.IsNullOrWhiteSpace(ClientIds)
            ? []
            : [.. ClientIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>The configured client ids as the set the claims check consults. Ordinal.</summary>
    public IReadOnlySet<string> AcceptedClientIds() => new HashSet<string>(ClientIdList(), StringComparer.Ordinal);

    /// <summary>
    /// Where the RFC 9728 document for the MCP endpoint lives, derived from <see cref="ResourceUrl"/> by
    /// string replacement so the origin stays exactly as entered.
    /// </summary>
    public string ResourceMetadataUrl()
        => ResourceUrl[..^McpOptions.McpEndpointPath.Length] + ProtectedResourceMetadataEndpoint.McpPath;

    /// <summary>
    /// The <c>WWW-Authenticate</c> value a refused request carries — the one thing a connector reads to find
    /// the authorization server.
    /// </summary>
    public string Challenge()
        => $"Bearer resource_metadata=\"{ResourceMetadataUrl()}\", scope=\"{RequiredScope}\"";

    /// <summary>The keys that carry a value, named as environment variables. Empty when nothing is set.</summary>
    public IEnumerable<string> ConfiguredKeys()
    {
        if (!string.IsNullOrWhiteSpace(Issuer))
        {
            yield return "Mcp__OAuth__Issuer";
        }

        if (!string.IsNullOrWhiteSpace(ClientIds))
        {
            yield return "Mcp__OAuth__ClientIds";
        }

        if (!string.IsNullOrWhiteSpace(ResourceUrl))
        {
            yield return "Mcp__OAuth__ResourceUrl";
        }
    }

    /// <summary>Everything wrong with this section for the OAuth mode, each naming its key. Empty when it is usable.</summary>
    public IEnumerable<string> Problems()
    {
        foreach (string problem in IssuerProblems())
        {
            yield return problem;
        }

        if (ClientIdList().Count == 0)
        {
            yield return "Mcp__OAuth__ClientIds is required when Mcp__Auth__Mode=OAuth: the app client ids whose "
                + "tokens are accepted, comma-separated. A token from any other client on the pool is refused.";
        }

        foreach (string problem in ResourceUrlProblems())
        {
            yield return problem;
        }

        if (RequiredScope.Any(char.IsWhiteSpace))
        {
            yield return "Mcp__OAuth__RequiredScope must be exactly one scope, with no whitespace: the scope claim is "
                + $"space-separated and the check is a whole-entry match. Unset it, or leave it blank, for {DefaultRequiredScope}.";
        }
    }

    private IEnumerable<string> IssuerProblems()
    {
        const string key = "Mcp__OAuth__Issuer";

        if (string.IsNullOrWhiteSpace(Issuer))
        {
            yield return $"{key} is required when Mcp__Auth__Mode=OAuth: the issuer exactly as it appears in a token's "
                + "iss claim, https://cognito-idp.<region>.amazonaws.com/<poolId>.";
            yield break;
        }

        if (!Uri.TryCreate(Issuer, UriKind.Absolute, out Uri? issuer)
            || (issuer.Scheme != Uri.UriSchemeHttps && issuer.Scheme != Uri.UriSchemeHttp))
        {
            yield return $"{key} must be an absolute https URL; got something that is not one. Discovery is read from "
                + "{Issuer}/.well-known/openid-configuration.";
            yield break;
        }

        if (issuer.Scheme == Uri.UriSchemeHttp && !issuer.IsLoopback)
        {
            yield return $"{key} is plain http on a non-loopback host. Discovery and the signing keys would be fetched "
                + "in clear across a network, and whoever answered would choose the keys this server trusts. http is "
                + "accepted on loopback only, for a stub issuer on this machine.";
        }

        if (Issuer.EndsWith('/') || !string.IsNullOrEmpty(issuer.Query) || !string.IsNullOrEmpty(issuer.Fragment))
        {
            yield return $"{key} must be the issuer exactly as a token's iss claim carries it — no trailing slash, no "
                + "query, no fragment. The comparison is byte for byte, and a trailing slash would refuse every "
                + "token with no hint why.";
        }
    }

    private IEnumerable<string> ResourceUrlProblems()
    {
        const string key = "Mcp__OAuth__ResourceUrl";

        if (string.IsNullOrWhiteSpace(ResourceUrl))
        {
            yield return $"{key} is required when Mcp__Auth__Mode=OAuth: the public URL of the MCP endpoint exactly as "
                + "a user enters it, e.g. https://topstepx-mcp.staging.marqspec.com/mcp.";
            yield break;
        }

        if (!Uri.TryCreate(ResourceUrl, UriKind.Absolute, out Uri? resource)
            || (resource.Scheme != Uri.UriSchemeHttps && resource.Scheme != Uri.UriSchemeHttp))
        {
            yield return $"{key} must be an absolute http(s) URL; got something that is not one.";
            yield break;
        }

        if (!ResourceUrl.EndsWith(McpOptions.McpEndpointPath, StringComparison.Ordinal)
            || !string.Equals(resource.AbsolutePath, McpOptions.McpEndpointPath, StringComparison.Ordinal)
            || !string.IsNullOrEmpty(resource.Query)
            || !string.IsNullOrEmpty(resource.Fragment))
        {
            yield return $"{key} must end in {McpOptions.McpEndpointPath} with no query or fragment: that is the only "
                + "path the MCP endpoint is served on, and the metadata document describes that URL and no other.";
        }
    }
}
