using System.Security.Claims;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// The claims check for an access token from an issuer that puts an audience in it — Keycloak, Auth0, Entra
/// ID, or any other OAuth 2.1 / OpenID Connect server (gh#TBD).
/// </summary>
/// <remarks>
/// <para>
/// <b>The audience itself is checked by the JWT bearer handler, not here.</b> Under
/// <see cref="OAuthOptions.IsAudienceBound"/> the handler requires <c>aud</c> to carry
/// <see cref="OAuthOptions.Audience"/> before a principal exists; this runs after it, in
/// <c>OnTokenValidated</c>, and checks what the audience does not: a subject, the client when a list is
/// configured, and the scope.
/// </para>
/// <para>
/// <b>Where each claim is read, because issuers disagree.</b> The client is <c>client_id</c> (RFC 9068,
/// Cognito) or, when that is absent, <c>azp</c> (Keycloak, Auth0, Entra v2). The scope is the
/// space-separated <c>scope</c> claim (RFC 9068, Keycloak, Auth0) or Entra's <c>scp</c>; an entry from either
/// counts. A <c>token_use</c> claim, when present, must say <c>access</c>, as it must under Cognito.
/// </para>
/// <para>
/// <b>Every comparison is ordinal and whole, and the reason never quotes a value.</b> The reason reaches a log
/// line, and the values in a refused token are untrusted input.
/// </para>
/// </remarks>
public static class AudienceBoundAccessTokenPolicy
{
    /// <summary>The authorized party: the client, where an issuer does not write <c>client_id</c>.</summary>
    public const string AuthorizedPartyClaim = "azp";

    /// <summary>Entra ID's scope claim.</summary>
    public const string EntraScopeClaim = "scp";

    /// <summary>
    /// Says why a validated principal is still not acceptable, or <c>null</c> when it is.
    /// </summary>
    /// <param name="principal">The principal a signature-valid, issuer-valid, audience-valid, in-date token produced.</param>
    /// <param name="acceptedClientIds">The configured client ids; empty accepts any client. Ordinal.</param>
    /// <param name="requiredScope">The one scope the token must carry; empty requires none.</param>
    /// <returns>A reason naming the offending claim, or <c>null</c>.</returns>
    public static string? Reject(ClaimsPrincipal principal, IReadOnlySet<string> acceptedClientIds, string requiredScope)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(acceptedClientIds);
        ArgumentNullException.ThrowIfNull(requiredScope);

        if (principal.FindAll(CognitoAccessTokenPolicy.TokenUseClaim).Any()
            && !string.Equals(
                Single(principal, CognitoAccessTokenPolicy.TokenUseClaim),
                CognitoAccessTokenPolicy.AccessTokenUse,
                StringComparison.Ordinal))
        {
            return $"the {CognitoAccessTokenPolicy.TokenUseClaim} claim is present and not "
                + $"'{CognitoAccessTokenPolicy.AccessTokenUse}' — an ID token is not a credential here";
        }

        if (string.IsNullOrEmpty(Single(principal, CognitoAccessTokenPolicy.SubjectClaim)))
        {
            return $"the {CognitoAccessTokenPolicy.SubjectClaim} claim is missing";
        }

        if (acceptedClientIds.Count > 0)
        {
            string? clientId = ClientId(principal);
            if (clientId is null || !acceptedClientIds.Contains(clientId))
            {
                return $"the {CognitoAccessTokenPolicy.ClientIdClaim} (or {AuthorizedPartyClaim}) claim is missing, "
                    + "duplicated, or not one of the configured client ids";
            }
        }

        if (requiredScope.Length == 0)
        {
            return null;
        }

        bool hasScope = principal.FindAll(claim =>
                string.Equals(claim.Type, CognitoAccessTokenPolicy.ScopeClaim, StringComparison.Ordinal)
                || string.Equals(claim.Type, EntraScopeClaim, StringComparison.Ordinal))
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Any(scope => string.Equals(scope, requiredScope, StringComparison.Ordinal));

        return hasScope
            ? null
            : $"the {CognitoAccessTokenPolicy.ScopeClaim} (or {EntraScopeClaim}) claim does not carry the required scope";
    }

    /// <summary>
    /// The client the token was issued to: a single <c>client_id</c>, else a single <c>azp</c>, else <c>null</c>.
    /// </summary>
    /// <param name="principal">The principal.</param>
    /// <returns>The client id, or <c>null</c> when neither claim is present exactly once.</returns>
    public static string? ClientId(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        return principal.FindAll(CognitoAccessTokenPolicy.ClientIdClaim).Any()
            ? Single(principal, CognitoAccessTokenPolicy.ClientIdClaim)
            : Single(principal, AuthorizedPartyClaim);
    }

    /// <summary>The claim's value when there is exactly one, else <c>null</c>.</summary>
    private static string? Single(ClaimsPrincipal principal, string type)
    {
        string? value = null;
        foreach (Claim claim in principal.FindAll(type))
        {
            if (value is not null)
            {
                return null;
            }

            value = claim.Value;
        }

        return value;
    }
}
