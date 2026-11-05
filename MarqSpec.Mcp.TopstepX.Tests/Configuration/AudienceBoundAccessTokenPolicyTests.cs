using System.Security.Claims;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;

namespace MarqSpec.Mcp.TopstepX.Tests.Configuration;

/// <summary>
/// The claims check for an access token from an issuer that carries an audience — Keycloak, Auth0, Entra ID.
/// </summary>
/// <remarks>
/// The audience itself is the JWT handler's check and is pinned by the host tests. These pin what runs after
/// it, on its own, with no host, no key and no issuer: a subject, the client under <c>client_id</c> or
/// <c>azp</c> when a list is configured, any client when none is, and the scope under <c>scope</c> or
/// <c>scp</c>.
/// </remarks>
public sealed class AudienceBoundAccessTokenPolicyTests
{
    private const string Scope = "topstepx-mcp/read";

    private static readonly IReadOnlySet<string> _clients =
        new HashSet<string>(["connector-client", "deploy-check-client"], StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> _anyClient = new HashSet<string>(StringComparer.Ordinal);

    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims)
        => new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "Bearer"));

    /// <summary>A Keycloak-shaped access token: <c>sub</c>, <c>azp</c>, <c>scope</c>, no <c>token_use</c>.</summary>
    private static ClaimsPrincipal Keycloak(string? azp = "connector-client", string? scope = Scope, string? sub = "user-1")
    {
        List<(string, string)> claims = [];
        if (sub is not null)
        {
            claims.Add(("sub", sub));
        }

        if (azp is not null)
        {
            claims.Add(("azp", azp));
        }

        if (scope is not null)
        {
            claims.Add(("scope", scope));
        }

        return Principal([.. claims]);
    }

    // ── accepted ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AKeycloakShapedToken_FromAListedClient_WithTheScope_IsAccepted()
    {
        AudienceBoundAccessTokenPolicy.Reject(Keycloak(), _clients, Scope).Should().BeNull();
    }

    [Fact]
    public void AnyClient_IsAccepted_WhenNoListIsConfigured()
    {
        // Dynamic client registration mints a client id nobody configured. The audience binds the token to
        // this server; the list, when there is one, only narrows it.
        AudienceBoundAccessTokenPolicy.Reject(Keycloak(azp: "dcr-7f3a9c"), _anyClient, Scope).Should().BeNull();
    }

    [Fact]
    public void ATokenWithNoClientClaimAtAll_IsAccepted_WhenNoListIsConfigured()
    {
        AudienceBoundAccessTokenPolicy.Reject(Keycloak(azp: null), _anyClient, Scope).Should().BeNull();
    }

    [Fact]
    public void Subject_IsNull_WhenSubIsDuplicated()
    {
        ClaimsPrincipal principal = Principal(("sub", "user-1"), ("sub", "user-2"), ("scope", Scope));

        AudienceBoundAccessTokenPolicy.Subject(principal).Should().BeNull();
        AudienceBoundAccessTokenPolicy.Reject(principal, _anyClient, Scope).Should().Contain("sub");
    }

    [Fact]
    public void ClientId_IsPreferredOverAzp()
    {
        // RFC 9068 names client_id; azp is the fallback. A listed azp beside an unlisted client_id is refused.
        ClaimsPrincipal principal = Principal(
            ("sub", "user-1"), ("client_id", "someone-else"), ("azp", "connector-client"), ("scope", Scope));

        AudienceBoundAccessTokenPolicy.Reject(principal, _clients, Scope).Should().Contain("client_id");
        AudienceBoundAccessTokenPolicy.ClientId(principal).Should().Be("someone-else");
    }

    [Fact]
    public void AnEntraShapedToken_WithTheScopeUnderScp_IsAccepted()
    {
        ClaimsPrincipal principal = Principal(("sub", "user-1"), ("azp", "connector-client"), ("scp", "other " + Scope));

        AudienceBoundAccessTokenPolicy.Reject(principal, _clients, Scope).Should().BeNull();
    }

    [Fact]
    public void NoScopeRequired_AcceptsATokenWithNone()
    {
        // The default for an audience-bound issuer: AuthKit grants only its own scopes.
        AudienceBoundAccessTokenPolicy.Reject(Keycloak(scope: null), _clients, string.Empty).Should().BeNull();
    }

    [Fact]
    public void TheScope_MayArriveBesideOthers()
    {
        AudienceBoundAccessTokenPolicy.Reject(Keycloak(scope: "openid  topstepx-mcp/read profile"), _clients, Scope)
            .Should().BeNull();
    }

    [Fact]
    public void AnAccessTokenUse_IsAccepted_WhenTheIssuerWritesOne()
    {
        ClaimsPrincipal principal = Principal(
            ("sub", "user-1"), ("client_id", "connector-client"), ("token_use", "access"), ("scope", Scope));

        AudienceBoundAccessTokenPolicy.Reject(principal, _clients, Scope).Should().BeNull();
    }

    // ── refused ──────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("id")]
    [InlineData("Access")]
    [InlineData("")]
    public void ATokenUseOtherThanAccess_IsRefused(string tokenUse)
    {
        ClaimsPrincipal principal = Principal(
            ("sub", "user-1"), ("azp", "connector-client"), ("token_use", tokenUse), ("scope", Scope));

        AudienceBoundAccessTokenPolicy.Reject(principal, _clients, Scope).Should().Contain("token_use");
    }

    [Fact]
    public void ATokenWithNoSubject_IsRefused()
    {
        AudienceBoundAccessTokenPolicy.Reject(Keycloak(sub: null), _clients, Scope).Should().Contain("sub");
    }

    [Theory]
    [InlineData("not-a-listed-client")]
    [InlineData("CONNECTOR-CLIENT")]
    [InlineData("connector-client ")]
    public void AnUnlistedClient_IsRefused_WhenAListIsConfigured(string azp)
    {
        AudienceBoundAccessTokenPolicy.Reject(Keycloak(azp: azp), _clients, Scope).Should().Contain("azp");
    }

    [Fact]
    public void NoClientClaim_IsRefused_WhenAListIsConfigured()
    {
        AudienceBoundAccessTokenPolicy.Reject(Keycloak(azp: null), _clients, Scope).Should().Contain("client_id");
    }

    [Fact]
    public void ADuplicatedClientClaim_IsRefused_WhenAListIsConfigured()
    {
        ClaimsPrincipal principal = Principal(
            ("sub", "user-1"), ("azp", "connector-client"), ("azp", "someone-else"), ("scope", Scope));

        AudienceBoundAccessTokenPolicy.Reject(principal, _clients, Scope).Should().Contain("duplicated");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("openid profile")]
    [InlineData("topstepx-mcp/readwrite")]
    [InlineData("TOPSTEPX-MCP/READ")]
    public void ATokenWithoutTheRequiredScope_IsRefused(string? scope)
    {
        AudienceBoundAccessTokenPolicy.Reject(Keycloak(scope: scope), _clients, Scope).Should().Contain("scope");
    }

    [Fact]
    public void TheReason_NeverQuotesAClaimValue()
    {
        const string forged = "client\r\nINFO forged";
        string? reason = AudienceBoundAccessTokenPolicy.Reject(Keycloak(azp: forged), _clients, Scope);

        reason.Should().NotBeNull();
        reason.Should().NotContain(forged).And.NotContain("INFO forged");
    }
}
