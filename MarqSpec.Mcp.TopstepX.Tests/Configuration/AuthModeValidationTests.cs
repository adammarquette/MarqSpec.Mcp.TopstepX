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
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("topstepx-mcp/read openid")]
    public void OAuth_RefusesToStart_WithAScopeThatIsNotOneScope(string scope)
    {
        Starting(CompleteOAuth(("Mcp:OAuth:RequiredScope", scope)))
            .Should().Throw<OptionsValidationException>().WithMessage("*Mcp__OAuth__RequiredScope*");
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
}
