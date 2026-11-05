using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.Tests.Configuration;

/// <summary>
/// Under the HTTP transport exactly one authentication mode is configured, and an incomplete one refuses at
/// startup naming the key.
/// </summary>
/// <remarks>
/// <para>
/// ADR-0021 states the coupling this enforces: <i>a target group in front of 8080 ⇒ the OAuth mode must be
/// configured, never the static token.</i> The state it must make unreachable is a public listener on the
/// static gate because a variable was left behind — so both directions refuse: OAuth keys beside
/// <c>Mcp__Auth__Mode=StaticToken</c>, and a static token beside <c>Mcp__Auth__Mode=OAuth</c>.
/// </para>
/// <para>
/// <b>Stdio is untouched.</b> Nothing under <c>Mcp__Auth</c> or <c>Mcp__OAuth</c> is read when the
/// transport is stdio, exactly as <c>Mcp__HttpBearerToken</c> never was.
/// </para>
/// </remarks>
public sealed class AuthModeValidationTests
{
    private const string Issuer = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_StubPool";

    private static readonly Dictionary<string, string?> _baseSettings = new()
    {
        ["ConnectionStrings:Default"] = "Host=localhost;Database=x;Username=u;Password=p",
        ["MarketData:Instruments"] = "ES,NQ",
        ["MarketData:SessionCloseCentral"] = "16:00",
        ["MarketData:MaxRows"] = "5000",
    };

    private static Dictionary<string, string?> Http(McpAuthMode mode, params (string Key, string? Value)[] more)
    {
        Dictionary<string, string?> settings = new()
        {
            ["Mcp:Transport"] = "Http",
            ["Mcp:Auth:Mode"] = mode.ToString(),
        };
        foreach ((string key, string? value) in more)
        {
            settings[key] = value;
        }

        return settings;
    }

    private static Dictionary<string, string?> CompleteOAuth(params (string Key, string? Value)[] overrides)
    {
        Dictionary<string, string?> settings = Http(
            McpAuthMode.OAuth,
            ("Mcp:OAuth:Issuer", Issuer),
            ("Mcp:OAuth:ClientIds", "connector-client,deploy-check-client"),
            ("Mcp:OAuth:ResourceUrl", "https://topstepx-mcp.example.test/mcp"));
        foreach ((string key, string? value) in overrides)
        {
            settings[key] = value;
        }

        return settings;
    }

    /// <summary>Exactly what startup does: bind, build, and resolve the validated options.</summary>
    private static McpOptions Start(Dictionary<string, string?> settings)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(_baseSettings.Concat(settings));

        McpOptions bound = builder.Configuration.GetSection(McpOptions.SectionName).Get<McpOptions>() ?? new McpOptions();
        Program.ConfigureServices(builder, bound);

        using ServiceProvider provider = builder.Services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        return provider.GetRequiredService<IOptions<McpOptions>>().Value;
    }

    private static Action Starting(Dictionary<string, string?> settings) => () => Start(settings);

    // ── the shape of the options ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheModeDefaultsToStaticToken_WhenNothingNamesOne()
    {
        // Today's behaviour is the default: every existing launch names no mode and must keep working.
        McpOptions options = Start(new Dictionary<string, string?>
        {
            ["Mcp:Transport"] = "Http",
            ["Mcp:HttpBearerToken"] = "a-token",
        });

        options.Auth.Mode.Should().Be(McpAuthMode.StaticToken);
    }

    [Fact]
    public void TheRequiredScope_DefaultsToTheReadScope()
    {
        Start(CompleteOAuth()).OAuth.RequiredScope.Should().Be("topstepx-mcp/read");
    }

    [Fact]
    public void TheClientIds_BindFromACommaSeparatedList()
    {
        McpOptions options = Start(CompleteOAuth(("Mcp:OAuth:ClientIds", " connector-client , deploy-check-client,, ")));

        options.OAuth.ClientIdList().Should().Equal("connector-client", "deploy-check-client");
    }

    [Fact]
    public void ACompleteOAuthConfiguration_Starts()
    {
        Starting(CompleteOAuth()).Should().NotThrow();
    }

    [Fact]
    public void ALoopbackHttpIssuer_IsAccepted_ForALocalStub()
    {
        Starting(CompleteOAuth(("Mcp:OAuth:Issuer", "http://127.0.0.1:5077/stub-pool"))).Should().NotThrow();
    }

    // ── OAuth refusals, each naming its key ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void OAuth_RefusesToStart_WithoutAnIssuer(string? issuer)
    {
        Starting(CompleteOAuth(("Mcp:OAuth:Issuer", issuer)))
            .Should().Throw<OptionsValidationException>().WithMessage("*Mcp__OAuth__Issuer*");
    }

    [Theory]
    [InlineData("cognito-idp.us-east-1.amazonaws.com/us-east-1_StubPool")]
    [InlineData("ftp://cognito-idp.us-east-1.amazonaws.com/us-east-1_StubPool")]
    [InlineData("not a url")]
    public void OAuth_RefusesToStart_WithAnIssuerThatIsNotAnAbsoluteUrl(string issuer)
    {
        Starting(CompleteOAuth(("Mcp:OAuth:Issuer", issuer)))
            .Should().Throw<OptionsValidationException>().WithMessage("*Mcp__OAuth__Issuer*");
    }

    [Fact]
    public void OAuth_RefusesToStart_WithAPlaintextIssuerOffLoopback()
    {
        // Discovery and the key set would be fetched in clear from across a network, and whoever answered
        // would choose the keys this server trusts. http is for a stub on this machine only.
        Starting(CompleteOAuth(("Mcp:OAuth:Issuer", "http://cognito-idp.us-east-1.amazonaws.com/us-east-1_StubPool")))
            .Should().Throw<OptionsValidationException>().WithMessage("*Mcp__OAuth__Issuer*");
    }

    [Theory]
    [InlineData(Issuer + "/")]
    [InlineData(Issuer + "?x=1")]
    [InlineData(Issuer + "#frag")]
    public void OAuth_RefusesToStart_WithAnIssuerThatCannotEqualAnIssClaim(string issuer)
    {
        // iss is compared byte for byte. A trailing slash would make every token fail with no hint why.
        Starting(CompleteOAuth(("Mcp:OAuth:Issuer", issuer)))
            .Should().Throw<OptionsValidationException>().WithMessage("*Mcp__OAuth__Issuer*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" , ,")]
    public void OAuth_RefusesToStart_WithoutAClientId(string? clientIds)
    {
        Starting(CompleteOAuth(("Mcp:OAuth:ClientIds", clientIds)))
            .Should().Throw<OptionsValidationException>().WithMessage("*Mcp__OAuth__ClientIds*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void OAuth_RefusesToStart_WithoutAResourceUrl(string? resourceUrl)
    {
        Starting(CompleteOAuth(("Mcp:OAuth:ResourceUrl", resourceUrl)))
            .Should().Throw<OptionsValidationException>().WithMessage("*Mcp__OAuth__ResourceUrl*");
    }

    [Theory]
    [InlineData("topstepx-mcp.example.test/mcp")]
    [InlineData("https://topstepx-mcp.example.test")]
    [InlineData("https://topstepx-mcp.example.test/")]
    [InlineData("https://topstepx-mcp.example.test/mcp/")]
    [InlineData("https://topstepx-mcp.example.test/MCP")]
    [InlineData("https://topstepx-mcp.example.test/api/mcp")]
    [InlineData("https://topstepx-mcp.example.test/mcp?x=1")]
    [InlineData("https://topstepx-mcp.example.test/mcp#f")]
    public void OAuth_RefusesToStart_WithAResourceUrlThatIsNotTheMcpEndpoint(string resourceUrl)
    {
        // The MCP endpoint is served on /mcp and nowhere else. A resource URL naming any other path is a
        // URL the connector will enter and 404 on, and a metadata document describing nothing.
        Starting(CompleteOAuth(("Mcp:OAuth:ResourceUrl", resourceUrl)))
            .Should().Throw<OptionsValidationException>().WithMessage("*Mcp__OAuth__ResourceUrl*");
    }

    [Theory]
    [InlineData("topstepx-mcp/read openid")]
    [InlineData(" topstepx-mcp/read")]
    public void OAuth_RefusesToStart_WithAScopeThatIsNotOneScope(string scope)
    {
        Starting(CompleteOAuth(("Mcp:OAuth:RequiredScope", scope)))
            .Should().Throw<OptionsValidationException>().WithMessage("*Mcp__OAuth__RequiredScope*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankScope_IsTheSameAsAnUnsetOne(string scope)
    {
        // .env.example lists the key with no value, as it lists every key, and `Mcp__OAuth__RequiredScope=`
        // exported from it used to refuse startup beside a comment saying it defaulted (gh#512 review).
        Start(CompleteOAuth(("Mcp:OAuth:RequiredScope", scope))).OAuth.RequiredScope.Should().Be("topstepx-mcp/read");
    }

    [Fact]
    public void OAuth_RefusesToStart_WhenAStaticTokenIsAlsoSet()
    {
        // Both modes configured. ADR-0021: the coupling is checked, not described.
        Starting(CompleteOAuth(("Mcp:HttpBearerToken", "left-behind")))
            .Should().Throw<OptionsValidationException>().WithMessage("*Mcp__HttpBearerToken*");
    }

    [Fact]
    public void AnUnknownMode_IsRefusedAtBinding()
    {
        // The binder refuses a value that is not one of the two, naming the key.
        Starting(Http(McpAuthMode.OAuth, ("Mcp:Auth:Mode", "Cognito")))
            .Should().Throw<InvalidOperationException>().WithMessage("*Mcp:Auth:Mode*");
    }

    // ── the static mode, unchanged ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void StaticToken_RefusesToStart_WithoutAToken()
    {
        Starting(Http(McpAuthMode.StaticToken))
            .Should().Throw<OptionsValidationException>().WithMessage("*Mcp__HttpBearerToken is required*");
    }

    [Theory]
    [InlineData("Mcp:OAuth:Issuer", Issuer)]
    [InlineData("Mcp:OAuth:ClientIds", "connector-client")]
    [InlineData("Mcp:OAuth:ResourceUrl", "https://topstepx-mcp.example.test/mcp")]
    public void StaticToken_RefusesToStart_WhenAnOAuthKeyIsAlsoSet(string key, string value)
    {
        // The dangerous direction: OAuth was intended, the mode was not flipped, and the static gate would
        // sit on a public listener with the OAuth keys silently ignored.
        Starting(Http(McpAuthMode.StaticToken, ("Mcp:HttpBearerToken", "a-token"), (key, value)))
            .Should().Throw<OptionsValidationException>().WithMessage("*" + key.Replace(":", "__", StringComparison.Ordinal) + "*");
    }

    [Fact]
    public void StaticToken_Starts_WithATokenAndNothingElse()
    {
        Starting(Http(McpAuthMode.StaticToken, ("Mcp:HttpBearerToken", "a-token"))).Should().NotThrow();
    }

    // ── stdio, untouched ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Stdio_IgnoresEveryAuthenticationKey()
    {
        // Half an OAuth configuration and a static token at once, under stdio: none of it is read, exactly as
        // Mcp__HttpBearerToken has always been ignored there.
        Starting(new Dictionary<string, string?>
        {
            ["Mcp:Transport"] = "Stdio",
            ["Mcp:Auth:Mode"] = "OAuth",
            ["Mcp:OAuth:Issuer"] = "not a url",
            ["Mcp:HttpBearerToken"] = "a-token",
        }).Should().NotThrow();
    }

    // ── an issuer that carries an audience (gh#997) ──────────────────────────────────────────────────────

    private static Dictionary<string, string?> AudienceBound(params (string Key, string? Value)[] overrides)
    {
        Dictionary<string, string?> settings = CompleteOAuth(
            ("Mcp:OAuth:Issuer", "https://auth.example.test/realms/marqspec"),
            ("Mcp:OAuth:ClientIds", null),
            ("Mcp:OAuth:Audience", "https://topstepx-mcp.example.test/mcp"),
            ("Mcp:OAuth:Subjects", "user_01OPERATOR"));
        foreach ((string key, string? value) in overrides)
        {
            settings[key] = value;
        }

        return settings;
    }

    [Fact]
    public void AnAudienceBoundConfiguration_Starts_WithNoClientIds()
    {
        // Dynamic registration mints client ids nobody can list. Subjects are what lets this start:
        // audience with no subjects, no client ids and no required scope is refused below.
        McpOptions options = Start(AudienceBound());

        options.OAuth.IsAudienceBound.Should().BeTrue();
        options.OAuth.ClientIdList().Should().BeEmpty();
    }

    [Fact]
    public void AnAudienceBoundConfiguration_Starts_WithClientIdsToo()
    {
        Starting(AudienceBound(("Mcp:OAuth:ClientIds", "connector-client"))).Should().NotThrow();
    }

    [Fact]
    public void AnAudienceWithNoSubjectNoClientAndNoScope_IsRefused()
    {
        Starting(AudienceBound(
                ("Mcp:OAuth:Subjects", null),
                ("Mcp:OAuth:ClientIds", null),
                ("Mcp:OAuth:RequiredScope", null)))
            .Should().Throw<OptionsValidationException>()
            .WithMessage("*Mcp__OAuth__Subjects*Mcp__OAuth__ClientIds*Mcp__OAuth__RequiredScope*");
    }

    [Theory]
    [InlineData(" , ,", null, null)]
    [InlineData(null, ",", null)]
    [InlineData(null, null, "   ")]
    [InlineData(" , ,", ",", "   ")]
    public void AnAudienceOfBlankOrWhitespaceLists_IsRefused(
        string? subjects, string? clientIds, string? scope)
    {
        // Empty entries and whitespace are not a restriction. " , ," and "," parse to no subjects
        // and no client ids, and a whitespace scope is the same as unset.
        Starting(AudienceBound(
                ("Mcp:OAuth:Subjects", subjects),
                ("Mcp:OAuth:ClientIds", clientIds),
                ("Mcp:OAuth:RequiredScope", scope)))
            .Should().Throw<OptionsValidationException>()
            .WithMessage("*Mcp__OAuth__Subjects*Mcp__OAuth__ClientIds*Mcp__OAuth__RequiredScope*");
    }

    [Theory]
    [InlineData("Mcp:OAuth:Subjects", "user_01A")]
    [InlineData("Mcp:OAuth:ClientIds", "connector-client")]
    [InlineData("Mcp:OAuth:RequiredScope", "mcp:read")]
    public void AnAudienceBoundConfiguration_Starts_WhenOneRestrictionIsSet(string key, string value)
    {
        Starting(AudienceBound(
                ("Mcp:OAuth:Subjects", null),
                ("Mcp:OAuth:ClientIds", null),
                ("Mcp:OAuth:RequiredScope", null),
                (key, value)))
            .Should().NotThrow();
    }

    [Fact]
    public void WithoutAnAudience_TheClientIdsAreStillRequired()
    {
        // The Cognito shape is unchanged: no aud in the token, so the list is the audience check.
        Starting(AudienceBound(("Mcp:OAuth:Audience", null)))
            .Should().Throw<OptionsValidationException>().WithMessage("*Mcp__OAuth__ClientIds*");
    }

    [Theory]
    [InlineData("https://topstepx-mcp.example.test/mcp other")]
    [InlineData("https://topstepx-mcp.example.test/mcp\t")]
    public void AnAudienceWithWhitespace_IsRefused(string audience)
    {
        Starting(AudienceBound(("Mcp:OAuth:Audience", audience)))
            .Should().Throw<OptionsValidationException>().WithMessage("*Mcp__OAuth__Audience*");
    }

    [Theory]
    [InlineData("https://tenant.us.auth0.com/")]
    [InlineData("https://tenant.us.auth0.com")]
    public void AHostRootIssuer_IsAccepted_WithOrWithoutItsTrailingSlash(string issuer)
    {
        // Auth0's iss is the tenant root with a slash; it is that issuer's spelling, kept as entered. A slash
        // after a path stays refused (the theory above).
        Starting(AudienceBound(("Mcp:OAuth:Issuer", issuer))).Should().NotThrow();
    }

    [Fact]
    public void AnAudienceBoundIssuer_RequiresNoScope_ByDefault()
    {
        McpOptions options = Start(AudienceBound(("Mcp:OAuth:RequiredScope", "")));

        options.OAuth.RequiresScope.Should().BeFalse();
        options.OAuth.Challenge().Should().NotContain("scope=");
    }

    [Fact]
    public void AnAudienceBoundIssuer_KeepsAScopeSetExplicitly()
    {
        Start(AudienceBound(("Mcp:OAuth:RequiredScope", "mcp:read")))
            .OAuth.RequiredScope.Should().Be("mcp:read");
    }

    [Fact]
    public void TheSubjects_BindFromACommaSeparatedList()
    {
        Start(AudienceBound(("Mcp:OAuth:Subjects", " user_01A , user_01B,, ")))
            .OAuth.AcceptedSubjects().Should().BeEquivalentTo(new[] { "user_01A", "user_01B" });
    }

    [Fact]
    public void AnRfc8414MetadataUrl_IsAccepted()
    {
        Starting(AudienceBound((
            "Mcp:OAuth:MetadataUrl",
            "https://auth.example.test/.well-known/oauth-authorization-server")))
            .Should().NotThrow();
    }

    [Theory]
    [InlineData("http://auth.example.test/.well-known/oauth-authorization-server")]
    [InlineData("not a url")]
    public void AMetadataUrlThatIsNotHttps_IsRefused(string metadataUrl)
    {
        Starting(AudienceBound(("Mcp:OAuth:MetadataUrl", metadataUrl)))
            .Should().Throw<OptionsValidationException>().WithMessage("*Mcp__OAuth__MetadataUrl*");
    }

    [Fact]
    public void ASubjectListBesideTheStaticMode_IsRefused()
    {
        Starting(Http(
                McpAuthMode.StaticToken,
                ("Mcp:HttpBearerToken", "a-token"),
                ("Mcp:OAuth:Subjects", "user_01A")))
            .Should().Throw<OptionsValidationException>().WithMessage("*Mcp__OAuth__Subjects*");
    }

    [Fact]
    public void AnAudienceEqualToAClientId_IsRefused()
    {
        // An ID token's aud is its client id; that audience would let ID tokens through as access tokens.
        Starting(AudienceBound(
                ("Mcp:OAuth:ClientIds", "connector-client"),
                ("Mcp:OAuth:Audience", "connector-client")))
            .Should().Throw<OptionsValidationException>().WithMessage("*Mcp__OAuth__Audience*");
    }

    [Fact]
    public void AnAudienceBesideTheStaticMode_IsRefused()
    {
        Starting(Http(
                McpAuthMode.StaticToken,
                ("Mcp:HttpBearerToken", "a-token"),
                ("Mcp:OAuth:Audience", "https://topstepx-mcp.example.test/mcp")))
            .Should().Throw<OptionsValidationException>().WithMessage("*Mcp__OAuth__Audience*");
    }

    // ── the migration mode: OAuth plus the static token, by name ─────────────────────────────────────────

    [Fact]
    public void TheMigrationMode_Starts_WithBothComplete()
    {
        McpOptions options = Start(AudienceBound(
            ("Mcp:Auth:Mode", nameof(McpAuthMode.OAuthWithStaticToken)),
            ("Mcp:HttpBearerToken", "a-token")));

        options.Auth.Mode.Should().Be(McpAuthMode.OAuthWithStaticToken);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TheMigrationMode_RefusesToStart_WithoutTheStaticToken(string? token)
    {
        Starting(AudienceBound(
                ("Mcp:Auth:Mode", nameof(McpAuthMode.OAuthWithStaticToken)),
                ("Mcp:HttpBearerToken", token)))
            .Should().Throw<OptionsValidationException>().WithMessage("*Mcp__HttpBearerToken*");
    }

    [Fact]
    public void TheMigrationMode_RefusesToStart_WithAnIncompleteOAuthSection()
    {
        Starting(AudienceBound(
                ("Mcp:Auth:Mode", nameof(McpAuthMode.OAuthWithStaticToken)),
                ("Mcp:HttpBearerToken", "a-token"),
                ("Mcp:OAuth:Issuer", null)))
            .Should().Throw<OptionsValidationException>()
            .WithMessage("*Mcp__OAuth__Issuer is required when Mcp__Auth__Mode=OAuthWithStaticToken*");
    }
}
