using System.Security.Claims;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// The claims check that stands in for audience validation on a Cognito access token.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cognito access tokens carry <c>client_id</c> and <c>scope</c>, not <c>aud</c>.</b> Standard audience
/// validation therefore has nothing to compare, and turning it off is necessary — but on its own it means
/// any token the pool ever signed, for any app client, with any scope, would be accepted. This is the check
/// that replaces it, and it is deliberately a separate, pure function so it can be pinned without a host:
/// <c>token_use</c> is exactly <c>access</c> (an ID token from the same pool is signed by the same key and
/// is not a credential for a resource server), <c>client_id</c> is exactly one claim and in the configured
/// set, and the space-separated <c>scope</c> carries the required scope as a whole entry.
/// </para>
/// <para>
/// <b>Every comparison is ordinal and whole.</b> <c>topstepx-mcp/readwrite</c> is not <c>topstepx-mcp/read</c>,
/// and neither is <c>TOPSTEPX-MCP/READ</c>.
/// </para>
/// <para>
/// <b>The reason it returns names a claim and never quotes a value.</b> The reason reaches a log line, and
/// the values in a refused token are untrusted input.
/// </para>
/// </remarks>
public static class CognitoAccessTokenPolicy
{
    /// <summary>The claim that says whether this is an access token or an ID token.</summary>
    public const string TokenUseClaim = "token_use";

    /// <summary>The value <see cref="TokenUseClaim"/> must carry.</summary>
    public const string AccessTokenUse = "access";

    /// <summary>The app client the token was issued to.</summary>
    public const string ClientIdClaim = "client_id";

    /// <summary>The space-separated scopes the token was granted.</summary>
    public const string ScopeClaim = "scope";

    /// <summary>The subject, which reaches the log scope.</summary>
    public const string SubjectClaim = "sub";

    /// <summary>
    /// Says why a validated principal is still not acceptable, or <c>null</c> when it is.
    /// </summary>
    /// <param name="principal">The principal a signature-valid, issuer-valid, in-date token produced.</param>
    /// <param name="acceptedClientIds">The configured client ids. Ordinal.</param>
    /// <param name="requiredScope">The one scope the token must carry.</param>
    /// <returns>A reason naming the offending claim, or <c>null</c>.</returns>
    public static string? Reject(ClaimsPrincipal principal, IReadOnlySet<string> acceptedClientIds, string requiredScope)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(acceptedClientIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredScope);

        string? tokenUse = Single(principal, TokenUseClaim);
        if (!string.Equals(tokenUse, AccessTokenUse, StringComparison.Ordinal))
        {
            return $"the {TokenUseClaim} claim is not '{AccessTokenUse}' — an ID token is not a credential here";
        }

        if (string.IsNullOrEmpty(Single(principal, SubjectClaim)))
        {
            return $"the {SubjectClaim} claim is missing";
        }

        string? clientId = Single(principal, ClientIdClaim);
        if (clientId is null || !acceptedClientIds.Contains(clientId))
        {
            return $"the {ClientIdClaim} claim is missing, duplicated, or not one of the configured client ids";
        }

        bool hasScope = principal.FindAll(ScopeClaim)
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Any(scope => string.Equals(scope, requiredScope, StringComparison.Ordinal));

        return hasScope ? null : $"the {ScopeClaim} claim does not carry the required scope";
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
