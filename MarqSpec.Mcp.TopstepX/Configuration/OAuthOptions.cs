namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// The <c>Mcp:OAuth</c> section: what the OAuth mode of the HTTP gate validates a token against, and what it
/// tells a client that has none (ADR-0021, ADR-0023 §9, gh#512).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two token shapes, chosen by whether <see cref="Audience"/> is set.</b> Unset, the issuer is treated as
/// Amazon Cognito, whose access tokens carry no <c>aud</c>: <see cref="ClientIds"/> is required and stands in
/// for the audience (<see cref="CognitoAccessTokenPolicy"/>). Set, the issuer is any OAuth 2.1 / OpenID
/// Connect server that puts an audience in its access tokens — Keycloak, Auth0, Entra ID, a self-hosted
/// issuer — and the <c>aud</c> claim must carry it (<see cref="AudienceBoundAccessTokenPolicy"/>).
/// <see cref="ClientIds"/> is then optional, because a client registered dynamically has an id no
/// configuration can know in advance.
/// </para>
/// <para>
/// The issuer and the resource URL are required and validated at startup naming their key; the scope has a
/// default. Every value is public information — an issuer URL, an audience, app client ids, a scope name and
/// this server's own address — so none of them is a secret and none of them is redacted anywhere. <b>The
/// issuer's client
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
    /// The issuer, exactly as it appears in a token's <c>iss</c> claim — Keycloak's
    /// <c>https://&lt;host&gt;/realms/&lt;realm&gt;</c>, Auth0's <c>https://&lt;tenant&gt;.auth0.com/</c>,
    /// Cognito's
    /// <c>https://cognito-idp.&lt;region&gt;.amazonaws.com/&lt;poolId&gt;</c>. Discovery is read from
    /// <c>{Issuer}/.well-known/openid-configuration</c>.
    /// </summary>
    public string Issuer { get; init; } = string.Empty;

    /// <summary>
    /// The app clients whose tokens are accepted, comma-separated — the connector client and the deploy-check
    /// client. A token from any other client on the same pool is refused.
    /// </summary>
    /// <remarks>
    /// Required when <see cref="Audience"/> is unset (Cognito). Optional when it is set: blank accepts any
    /// client the issuer minted a token for this audience, which is what dynamic client registration needs;
    /// a list still narrows it to those clients, read from <c>client_id</c> or, failing that, <c>azp</c>.
    /// </remarks>
    public string ClientIds { get; init; } = string.Empty;

    /// <summary>
    /// The value a token's <c>aud</c> claim must carry, compared ordinally against each entry. Unset selects
    /// the Cognito shape, which has no audience. Set it to <see cref="ResourceUrl"/> unless the issuer names
    /// its APIs some other way (Auth0's API identifier, Entra's <c>api://…</c> application id URI).
    /// </summary>
    /// <remarks>
    /// <b>The audience is what binds a token to this server.</b> Without it every access token the issuer
    /// signs — for any client, for any API — would pass the signature and issuer checks. RFC 8707 has a
    /// connector ask for a token whose audience is the resource URL; an issuer that ignores the
    /// <c>resource</c> parameter needs an audience mapper that adds it.
    /// </remarks>
    public string Audience { get; init; } = string.Empty;

    /// <summary>Whether tokens are checked against <see cref="Audience"/> rather than the Cognito
    /// shape.</summary>
    public bool IsAudienceBound => !string.IsNullOrWhiteSpace(Audience);

    private readonly string? _requiredScope;

    /// <summary>
    /// The one scope a token must carry, compared as a whole entry of the space-separated claim; empty when
    /// no scope is required.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Blank is the same as unset</b>, and unset depends on the token shape. For Cognito it binds to
    /// <see cref="DefaultRequiredScope"/>: the pool defines that scope on its resource server. For an
    /// audience-bound issuer it is <b>empty — no scope is required</b>, because issuers such as WorkOS
    /// AuthKit
    /// grant only their own scopes (<c>openid</c>, <c>profile</c>, <c>email</c>, <c>offline_access</c>) and
    /// would refuse a connector that asked for one they do not define. The audience, and
    /// <see cref="Subjects"/> when set, are then what admit a token. Set it explicitly to require a scope
    /// your
    /// issuer does define.
    /// </para>
    /// <para>
    /// <c>.env.example</c> lists the key with no value, as it lists every key, and
    /// <c>Mcp__OAuth__RequiredScope=</c> exported from that file used to refuse startup while the comment
    /// beside it said it defaulted (gh#512 review). A value with whitespace inside is still refused — the
    /// claim is space-separated and this is one entry.
    /// </para>
    /// </remarks>
    public string RequiredScope
    {
        get => !string.IsNullOrWhiteSpace(_requiredScope)
            ? _requiredScope
            : IsAudienceBound ? string.Empty : DefaultRequiredScope;
        init => _requiredScope = value;
    }

    /// <summary>Whether a token must carry <see cref="RequiredScope"/>.</summary>
    public bool RequiresScope => RequiredScope.Length > 0;

    /// <summary>
    /// The subjects whose tokens are accepted, comma-separated — the <c>sub</c> claim, ordinally. Blank
    /// accepts
    /// any subject the other checks admit.
    /// </summary>
    /// <remarks>
    /// <b>Set it for a single-operator server.</b> An issuer whose sign-up is open mints a token with this
    /// server's audience for anyone who signs in; this list is what limits the endpoint to the people who
    /// should read the account. Each entry is the issuer's user id (WorkOS: <c>user_…</c>). Not a secret.
    /// </remarks>
    public string Subjects { get; init; } = string.Empty;

    /// <summary>
    /// Where the issuer's metadata, and through it the signing keys, are read. Blank reads
    /// <c>{Issuer}/.well-known/openid-configuration</c>; set it for an issuer that publishes only RFC 8414's
    /// <c>{Issuer}/.well-known/oauth-authorization-server</c>.
    /// </summary>
    public string MetadataUrl { get; init; } = string.Empty;

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

    /// <summary>The configured subjects as the set the gate consults; empty accepts any. Ordinal.</summary>
    public IReadOnlySet<string> AcceptedSubjects()
        => string.IsNullOrWhiteSpace(Subjects)
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(
                Subjects.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.Ordinal);

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
    /// <remarks>
    /// The <c>scope</c> parameter is written only when a scope is required: a connector requests what it is
    /// told, and an issuer refuses a scope it does not define.
    /// </remarks>
    public string Challenge()
        => RequiresScope
            ? $"Bearer resource_metadata=\"{ResourceMetadataUrl()}\", scope=\"{RequiredScope}\""
            : $"Bearer resource_metadata=\"{ResourceMetadataUrl()}\"";

    /// <summary>The keys that carry a value, named as environment variables. Empty when nothing is
    /// set.</summary>
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

        if (!string.IsNullOrWhiteSpace(Audience))
        {
            yield return "Mcp__OAuth__Audience";
        }

        if (!string.IsNullOrWhiteSpace(Subjects))
        {
            yield return "Mcp__OAuth__Subjects";
        }

        if (!string.IsNullOrWhiteSpace(MetadataUrl))
        {
            yield return "Mcp__OAuth__MetadataUrl";
        }
    }

    /// <summary>Everything wrong with this section for the OAuth mode, each naming its key. Empty when it is
    /// usable.</summary>
    public IEnumerable<string> Problems()
    {
        foreach (string problem in IssuerProblems())
        {
            yield return problem;
        }

        if (ClientIdList().Count == 0 && !IsAudienceBound)
        {
            yield return "Mcp__OAuth__ClientIds is required when Mcp__Auth__Mode=OAuth "
                + "and Mcp__OAuth__Audience is "
                + "unset: the app client ids whose tokens are accepted, comma-separated. "
                + "A Cognito access token "
                + "carries no aud, so this list is the audience check. "
                + "An issuer that puts an audience in its "
                + "tokens can set Mcp__OAuth__Audience instead.";
        }

        foreach (string problem in AudienceProblems())
        {
            yield return problem;
        }

        // An audience with no subject list, no client list and no required scope admits every token the
        // issuer will sign for that audience. Open sign-up then reaches balances, positions and history.
        if (IsAudienceBound
            && ClientIdList().Count == 0
            && AcceptedSubjects().Count == 0
            && string.IsNullOrWhiteSpace(_requiredScope))
        {
            yield return "Mcp__OAuth__Audience is set but Mcp__OAuth__Subjects, Mcp__OAuth__ClientIds and "
                + "Mcp__OAuth__RequiredScope are all empty. Set at least one: an audience-bound issuer with "
                + "open sign-up would otherwise admit anyone who can obtain a token for this audience.";
        }

        foreach (string problem in MetadataUrlProblems())
        {
            yield return problem;
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
                + "iss claim, e.g. https://auth.example.com/realms/<realm>.";
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

        // A trailing slash after a path is refused: Cognito and Keycloak write none, and the comparison is
        // byte
        // for byte. A host-root issuer that ends in one — Auth0 writes `https://<tenant>.auth0.com/` — is
        // that
        // issuer's own spelling and is kept as entered.
        bool strayTrailingSlash = Issuer.EndsWith('/')
            && !string.Equals(issuer.AbsolutePath, "/", StringComparison.Ordinal);
        if (strayTrailingSlash
            || !string.IsNullOrEmpty(issuer.Query)
            || !string.IsNullOrEmpty(issuer.Fragment))
        {
            yield return $"{key} must be the issuer exactly as a token's iss claim carries it "
                + "— no trailing slash after "
                + "a path, no query, no fragment. The comparison is byte for byte, "
                + "and a trailing slash would refuse "
                + "every token with no hint why.";
        }
    }

    private IEnumerable<string> MetadataUrlProblems()
    {
        const string key = "Mcp__OAuth__MetadataUrl";

        if (string.IsNullOrWhiteSpace(MetadataUrl))
        {
            yield break;
        }

        if (!Uri.TryCreate(MetadataUrl, UriKind.Absolute, out Uri? metadata)
            || (metadata.Scheme != Uri.UriSchemeHttps && metadata.Scheme != Uri.UriSchemeHttp))
        {
            yield return $"{key} must be an absolute https URL; got something that is not one.";
            yield break;
        }

        if (metadata.Scheme == Uri.UriSchemeHttp && !metadata.IsLoopback)
        {
            yield return $"{key} is plain http on a non-loopback host. "
                + "The signing keys would be found through a "
                + "document fetched in clear, and whoever answered would choose the keys this server trusts.";
        }
    }

    private IEnumerable<string> AudienceProblems()
    {
        const string key = "Mcp__OAuth__Audience";

        if (!IsAudienceBound)
        {
            yield break;
        }

        if (Audience.Any(char.IsWhiteSpace))
        {
            yield return $"{key} must be exactly one audience, with no whitespace: "
                + "it is compared whole against each "
                + "entry of the aud claim.";
        }

        if (ClientIdList().Contains(Audience, StringComparer.Ordinal))
        {
            // An OpenID Connect ID token's aud is the client it was issued to. An audience equal to a client
            // id
            // would let that client's ID token through as though it were an access token for this server.
            yield return $"{key} must not equal one of Mcp__OAuth__ClientIds: "
                + "an ID token's aud is its client id, "
                + "so that audience would accept ID tokens. Use the API's own identifier, "
                + "normally Mcp__OAuth__ResourceUrl.";
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
