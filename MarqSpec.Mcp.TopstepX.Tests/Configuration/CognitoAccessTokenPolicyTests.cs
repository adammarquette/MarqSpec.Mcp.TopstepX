using System.Security.Claims;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;

namespace MarqSpec.Mcp.TopstepX.Tests.Configuration;

/// <summary>
/// The claims check that stands in for audience validation on a Cognito access token.
/// </summary>
/// <remarks>
/// Cognito access tokens carry <c>client_id</c> and <c>scope</c>, not <c>aud</c>. These tests are the check
/// on its own, with no host, no key and no issuer: what it accepts, and the exact shape of each thing it
/// refuses. The host tests prove the check is wired in front of <c>/mcp</c>; these prove what it is.
/// </remarks>
public sealed class CognitoAccessTokenPolicyTests
{
    private static readonly IReadOnlySet<string> _clients =
        new HashSet<string>(["connector-client", "deploy-check-client"], StringComparer.Ordinal);

    private const string Scope = "topstepx-mcp/read";

    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims)
        => new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "Bearer"));

    private static ClaimsPrincipal Valid(string? tokenUse = "access", string? clientId = "connector-client", string? scope = Scope, string? sub = "user-1")
    {
        List<(string, string)> claims = [];
        if (sub is not null)
        {
            claims.Add(("sub", sub));
        }

        if (tokenUse is not null)
        {
            claims.Add(("token_use", tokenUse));
        }

        if (clientId is not null)
        {
            claims.Add(("client_id", clientId));
        }

        if (scope is not null)
        {
            claims.Add(("scope", scope));
        }

        return Principal([.. claims]);
    }

    [Fact]
    public void AnAccessToken_FromAListedClient_WithTheScope_IsAccepted()
    {
        CognitoAccessTokenPolicy.Reject(Valid(), _clients, Scope).Should().BeNull();
    }

    [Fact]
    public void TheScope_MayArriveBesideOthers()
    {
        CognitoAccessTokenPolicy.Reject(Valid(scope: "openid  topstepx-mcp/read profile"), _clients, Scope).Should().BeNull();
    }

    [Theory]
    [InlineData("id")]
    [InlineData("Access")]
    [InlineData("access ")]
    [InlineData("")]
    public void AnythingButAccess_IsRefused(string tokenUse)
    {
        CognitoAccessTokenPolicy.Reject(Valid(tokenUse: tokenUse), _clients, Scope).Should().Contain("token_use");
    }

    [Fact]
    public void AMissingTokenUse_IsRefused()
    {
        CognitoAccessTokenPolicy.Reject(Valid(tokenUse: null), _clients, Scope).Should().Contain("token_use");
    }

    [Theory]
    [InlineData("some-other-client")]
    [InlineData("Connector-Client")]
    [InlineData("")]
    public void AnUnlistedClient_IsRefused(string clientId)
    {
        CognitoAccessTokenPolicy.Reject(Valid(clientId: clientId), _clients, Scope).Should().Contain("client_id");
    }

    [Fact]
    public void AMissingClientId_IsRefused()
    {
        CognitoAccessTokenPolicy.Reject(Valid(clientId: null), _clients, Scope).Should().Contain("client_id");
    }

    [Fact]
    public void TwoClientIdClaims_AreRefused_EvenWhenOneIsListed()
    {
        // A token is issued to one client. Two claims is a token nobody minted honestly.
        ClaimsPrincipal principal = Principal(
            ("sub", "user-1"),
            ("token_use", "access"),
            ("client_id", "connector-client"),
            ("client_id", "some-other-client"),
            ("scope", Scope));

        CognitoAccessTokenPolicy.Reject(principal, _clients, Scope).Should().Contain("client_id");
    }

    [Theory]
    [InlineData("")]
    [InlineData("openid")]
    [InlineData("topstepx-mcp/readwrite")]
    [InlineData("topstepx-mcp/rea")]
    [InlineData("TOPSTEPX-MCP/READ")]
    [InlineData("topstepx-mcp/read/")]
    public void AScopeThatIsNotTheRequiredOne_IsRefused(string scope)
    {
        CognitoAccessTokenPolicy.Reject(Valid(scope: scope), _clients, Scope).Should().Contain("scope");
    }

    [Fact]
    public void AMissingScope_IsRefused()
    {
        CognitoAccessTokenPolicy.Reject(Valid(scope: null), _clients, Scope).Should().Contain("scope");
    }

    [Fact]
    public void AMissingSubject_IsRefused()
    {
        // sub is what reaches the log scope; a token without one is not attributable and is not accepted.
        CognitoAccessTokenPolicy.Reject(Valid(sub: null), _clients, Scope).Should().Contain("sub");
    }

    [Fact]
    public void TheReason_NeverQuotesAClaimValue()
    {
        // Reasons reach a log line. The values in a refused token are untrusted input; none of them may.
        string? reason = CognitoAccessTokenPolicy.Reject(
            Valid(clientId: "attacker-supplied-value-9f1c", scope: "attacker-scope-7e2a"),
            _clients,
            Scope);

        reason.Should().NotBeNull();
        reason.Should().NotContain("attacker-supplied-value-9f1c");
        reason.Should().NotContain("attacker-scope-7e2a");
    }
}
