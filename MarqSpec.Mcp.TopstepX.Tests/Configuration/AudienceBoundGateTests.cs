using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace MarqSpec.Mcp.TopstepX.Tests.Configuration;

/// <summary>
/// The OAuth gate against an issuer that puts an audience in its access tokens (Keycloak, Auth0, Entra ID),
/// and the migration mode that also admits the static token (gh#997).
/// </summary>
/// <remarks>
/// <para>
/// <b>The audience is what binds a token to this server.</b> Every way it can be wrong that this server is
/// expected to notice has its own test: absent, another API's, a near miss by one trailing slash, and a
/// Cognito-shaped token with none at all. The Cognito shape keeps its own tests in
/// <see cref="OAuthBearerGateTests"/>, unchanged.
/// </para>
/// <para>
/// <b>The fallback admits exactly the static token and nothing near it</b>, and a refusal under the
/// migration mode carries the OAuth challenge, so a connector still finds the authorization server.
/// </para>
/// <para>
/// <b>No network.</b> The issuer is <see cref="StubIssuer"/> on a loopback port.
/// </para>
/// </remarks>
public sealed class AudienceBoundGateTests
{
    private const string ResourceUrl = "https://topstepx-mcp.example.test/mcp";
    private const string StaticToken = "static-token-sentinel-4c1e";

    /// <summary>
    /// No <c>scope</c> parameter: an audience-bound issuer requires none by default, and a connector asks for
    /// what the challenge names (WorkOS AuthKit would refuse a scope it does not define).
    /// </summary>
    private const string ExpectedChallenge =
        "Bearer resource_metadata=\"https://topstepx-mcp.example.test"
        + "/.well-known/oauth-protected-resource/mcp\"";

    private const string Initialize =
        "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\","
        + "\"params\":{\"protocolVersion\":\"2024-11-05\","
        + "\"capabilities\":{},\"clientInfo\":{\"name\":\"gate-test\",\"version\":\"1.0\"}}}";

    private sealed class Host(
        WebApplication app,
        HttpClient client,
        OAuthBearerGateTests.CapturingLoggerProvider logs,
        HostTelemetry telemetry) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public OAuthBearerGateTests.CapturingLoggerProvider Logs { get; } = logs;

        public HostTelemetry Telemetry { get; } = telemetry;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private static async Task<Host> StartAsync(string issuer, Dictionary<string, string?>? extra = null)
    {
        Dictionary<string, string?> settings = new()
        {
            ["ConnectionStrings:Default"] = "Host=sentinel-db-host-4c1e;Database=x;Username=u;Password=p",
            ["MarketData:Instruments"] = "ES,NQ",
            ["MarketData:SessionCloseCentral"] = "16:00",
            ["MarketData:MaxRows"] = "5000",
            ["Mcp:Transport"] = "Http",
            ["Mcp:Auth:Mode"] = nameof(McpAuthMode.OAuth),
            ["Mcp:OAuth:Issuer"] = issuer,
            ["Mcp:OAuth:Audience"] = ResourceUrl,
            ["Mcp:OAuth:ResourceUrl"] = ResourceUrl,
            // Audience with none of Subjects, ClientIds and RequiredScope refuses startup (gh#997).
            ["Mcp:OAuth:Subjects"] = "user-sub-1,user_01HZYX",
        };

        if (extra is not null)
        {
            foreach (KeyValuePair<string, string?> pair in extra)
            {
                settings[pair.Key] = pair.Value;
            }
        }

        OAuthBearerGateTests.CapturingLoggerProvider logs = new();
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(settings);

        McpOptions mcp = builder.Configuration.GetSection(McpOptions.SectionName).Get<McpOptions>()!;
        Program.ConfigureServices(builder, mcp);

        WebApplication app = builder.Build();
        app.Services.GetRequiredService<StoreAvailabilityHolder>().Set(StoreAvailability.Available());
        Program.MapHttpTransport(app, mcp);
        await app.StartAsync();

        HttpClient client = new()
        {
            BaseAddress = new Uri(app.Urls.First()),
            Timeout = TimeSpan.FromSeconds(30),
        };
        return new Host(app, client, logs, app.Services.GetRequiredService<HostTelemetry>());
    }

    private static Task<Host> StartMigrationAsync(string issuer)
        => StartAsync(issuer, new Dictionary<string, string?>
        {
            ["Mcp:Auth:Mode"] = nameof(McpAuthMode.OAuthWithStaticToken),
            ["Mcp:HttpBearerToken"] = StaticToken,
        });

    private static HttpRequestMessage InitializeWith(string? token)
    {
        HttpRequestMessage request = new(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(Initialize, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    private static void ShouldBeRefused(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.GetValues("WWW-Authenticate").Should().ContainSingle()
            .Which.Should().Be(ExpectedChallenge);
    }

    // ── the audience ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AKeycloakShapedToken_ForThisAudience_ReachesInitialize()
    {
        await using StubIssuer issuer = await StubIssuer.StartAsync("/realms/marqspec");
        await using Host host = await StartAsync(issuer.Issuer);

        using HttpResponseMessage response =
            await host.Client.SendAsync(InitializeWith(issuer.Mint(issuer.ValidForAudience(ResourceUrl))));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("protocolVersion");
    }

    [Fact]
    public async Task TheAudience_MayArriveAmongOthers()
    {
        await using StubIssuer issuer = await StubIssuer.StartAsync("/realms/marqspec");
        await using Host host = await StartAsync(issuer.Issuer);
        string token = issuer.Mint(new TokenShape
        {
            Issuer = issuer.Issuer,
            Audiences = ["account", ResourceUrl],
            ClientId = null,
            AuthorizedParty = "connector-client",
            TokenUse = null,
        });

        using HttpResponseMessage response = await host.Client.SendAsync(InitializeWith(token));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ADynamicallyRegisteredClient_IsAccepted_AndReachesTheLogScope()
    {
        // No Mcp__OAuth__ClientIds: a client id minted by registration is accepted, read from azp.
        await using StubIssuer issuer = await StubIssuer.StartAsync("/realms/marqspec");
        await using Host host = await StartAsync(issuer.Issuer);
        string token = issuer.Mint(issuer.ValidForAudience(ResourceUrl) with
        {
            AuthorizedParty = "dcr-7f3a9c",
        });

        using HttpResponseMessage response = await host.Client.SendAsync(InitializeWith(token));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.Logs.ScopeValues("auth.client_id").Should().Contain("dcr-7f3a9c");
        host.Logs.ScopeValues("auth.method").Should().Contain(OAuthBearerGate.OAuthMethod);
        host.Logs.Everything().Should().NotContain(token);
    }

    [Fact]
    public async Task AConfiguredClientList_StillNarrowsIt()
    {
        await using StubIssuer issuer = await StubIssuer.StartAsync("/realms/marqspec");
        await using Host host = await StartAsync(issuer.Issuer, new Dictionary<string, string?>
        {
            ["Mcp:OAuth:ClientIds"] = "connector-client",
        });
        string token = issuer.Mint(issuer.ValidForAudience(ResourceUrl) with
        {
            AuthorizedParty = "someone-else",
        });

        using HttpResponseMessage response = await host.Client.SendAsync(InitializeWith(token));

        ShouldBeRefused(response);
    }

    [Fact]
    public async Task ATokenWithNoAudience_IsRefused()
    {
        // A Cognito-shaped token: everything right except that nothing names this server.
        await using StubIssuer issuer = await StubIssuer.StartAsync("/realms/marqspec");
        await using Host host = await StartAsync(issuer.Issuer);

        using HttpResponseMessage response = await host.Client.SendAsync(
            InitializeWith(issuer.Mint(issuer.Valid())));

        ShouldBeRefused(response);
    }

    [Theory]
    [InlineData("https://another-api.example.test/mcp")]
    [InlineData("https://topstepx-mcp.example.test/mcp/")]
    [InlineData("https://TOPSTEPX-MCP.example.test/mcp")]
    [InlineData("connector-client")]
    public async Task ATokenForAnotherAudience_IsRefused(string audience)
    {
        // Another API's token, two near misses the library would otherwise forgive or fold, and an ID
        // token's aud (its client id).
        await using StubIssuer issuer = await StubIssuer.StartAsync("/realms/marqspec");
        await using Host host = await StartAsync(issuer.Issuer);

        using HttpResponseMessage response =
            await host.Client.SendAsync(InitializeWith(issuer.Mint(issuer.ValidForAudience(audience))));

        ShouldBeRefused(response);
    }

    [Fact]
    public async Task ATokenForThisAudience_FromAnotherIssuer_IsRefused()
    {
        await using StubIssuer issuer = await StubIssuer.StartAsync("/realms/marqspec");
        await using Host host = await StartAsync(issuer.Issuer);
        string token = issuer.Mint(issuer.ValidForAudience(ResourceUrl) with
        {
            Issuer = "https://auth.example.test/realms/other",
        });

        using HttpResponseMessage response = await host.Client.SendAsync(InitializeWith(token));

        ShouldBeRefused(response);
    }

    [Fact]
    public async Task AWorkOsShapedToken_ReachesInitialize_WithNoScopeOfOurOwn()
    {
        // AuthKit: iss is the AuthKit domain, aud the resource indicator, client_id the client (a CIMD
        // client's id is its metadata URL), scope only AuthKit's own scopes. No azp, no token_use.
        await using StubIssuer issuer = await StubIssuer.StartAsync("/authkit");
        await using Host host = await StartAsync(issuer.Issuer);
        string token = issuer.Mint(new TokenShape
        {
            Issuer = issuer.Issuer,
            Audiences = [ResourceUrl],
            Subject = "user_01HZYX",
            ClientId = "https://claude.example.test/oauth/client-metadata.json",
            TokenUse = null,
            Scope = "openid profile email offline_access",
        });

        using HttpResponseMessage response = await host.Client.SendAsync(InitializeWith(token));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AConfiguredScope_IsStillRequired()
    {
        await using StubIssuer issuer = await StubIssuer.StartAsync("/realms/marqspec");
        await using Host host = await StartAsync(issuer.Issuer, new Dictionary<string, string?>
        {
            ["Mcp:OAuth:RequiredScope"] = "topstepx-mcp/read",
        });
        string token = issuer.Mint(issuer.ValidForAudience(ResourceUrl) with { Scope = "openid profile" });

        using HttpResponseMessage response = await host.Client.SendAsync(InitializeWith(token));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.GetValues("WWW-Authenticate").Should().ContainSingle()
            .Which.Should().EndWith("scope=\"topstepx-mcp/read\"");
    }

    [Fact]
    public async Task AConfiguredSubjectList_AdmitsThoseSubjects()
    {
        await using StubIssuer issuer = await StubIssuer.StartAsync("/realms/marqspec");
        await using Host host = await StartAsync(issuer.Issuer, new Dictionary<string, string?>
        {
            ["Mcp:OAuth:Subjects"] = "user_01OPERATOR, user-sub-1",
        });

        using HttpResponseMessage response =
            await host.Client.SendAsync(InitializeWith(issuer.Mint(issuer.ValidForAudience(ResourceUrl))));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("user_01SOMEONEELSE")]
    [InlineData("USER-SUB-1")]
    public async Task AConfiguredSubjectList_RefusesEveryoneElse(string subject)
    {
        // Open sign-up at the issuer mints this audience for anyone; the list is what keeps them out.
        await using StubIssuer issuer = await StubIssuer.StartAsync("/realms/marqspec");
        await using Host host = await StartAsync(issuer.Issuer, new Dictionary<string, string?>
        {
            ["Mcp:OAuth:Subjects"] = "user-sub-1",
        });
        string token = issuer.Mint(issuer.ValidForAudience(ResourceUrl) with { Subject = subject });

        using HttpResponseMessage response = await host.Client.SendAsync(InitializeWith(token));

        ShouldBeRefused(response);
        host.Logs.Lines().Should().Contain(
            "Refused POST /mcp: the sub claim is not one of the configured subjects.");
    }

    [Fact]
    public async Task TheMetadataDocument_ListsNoScope_WhenNoneIsRequired()
    {
        await using StubIssuer issuer = await StubIssuer.StartAsync("/realms/marqspec");
        await using Host host = await StartAsync(issuer.Issuer);

        string body = await (await host.Client.GetAsync("/.well-known/oauth-protected-resource/mcp"))
            .Content.ReadAsStringAsync();

        body.Should().NotContain("scopes_supported");
        body.Should().Contain("\"authorization_servers\":[\"" + issuer.Issuer + "\"]");
    }

    [Fact]
    public async Task TheStaticToken_OpensNothing_UnderOAuthAlone()
    {
        await using StubIssuer issuer = await StubIssuer.StartAsync("/realms/marqspec");
        await using Host host = await StartAsync(issuer.Issuer);

        using HttpResponseMessage response = await host.Client.SendAsync(InitializeWith(StaticToken));

        ShouldBeRefused(response);
    }

    // ── the migration mode ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UnderTheMigrationMode_TheStaticToken_ReachesInitialize_AndNeverALogLine()
    {
        await using StubIssuer issuer = await StubIssuer.StartAsync("/realms/marqspec");
        await using Host host = await StartMigrationAsync(issuer.Issuer);

        using HttpResponseMessage response = await host.Client.SendAsync(InitializeWith(StaticToken));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.Logs.ScopeValues("auth.method").Should().Contain(OAuthBearerGate.StaticTokenMethod);
        host.Logs.Everything().Should().NotContain(StaticToken);
        issuer.DiscoveryRequests.Should().Be(0, "the static token is admitted before the JWT handler runs");
    }

    [Fact]
    public async Task UnderTheMigrationMode_AnAccessToken_ReachesInitializeToo()
    {
        await using StubIssuer issuer = await StubIssuer.StartAsync("/realms/marqspec");
        await using Host host = await StartMigrationAsync(issuer.Issuer);

        using HttpResponseMessage response =
            await host.Client.SendAsync(InitializeWith(issuer.Mint(issuer.ValidForAudience(ResourceUrl))));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.Logs.ScopeValues("auth.method").Should().Contain(OAuthBearerGate.OAuthMethod);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(StaticToken + "x")]
    [InlineData("static-token-sentinel-4c1")]
    [InlineData("STATIC-TOKEN-SENTINEL-4C1E")]
    public async Task UnderTheMigrationMode_AnythingNearTheStaticToken_IsRefused_WithTheOAuthChallenge(
        string? token)
    {
        await using StubIssuer issuer = await StubIssuer.StartAsync("/realms/marqspec");
        await using Host host = await StartMigrationAsync(issuer.Issuer);

        using HttpResponseMessage response = await host.Client.SendAsync(InitializeWith(token));

        ShouldBeRefused(response);
    }

    [Theory]
    [InlineData("bearer " + StaticToken)]
    [InlineData("Basic " + StaticToken)]
    [InlineData(StaticToken)]
    public async Task UnderTheMigrationMode_TheStaticTokenUnderAnotherScheme_IsRefused(string header)
    {
        await using StubIssuer issuer = await StubIssuer.StartAsync("/realms/marqspec");
        await using Host host = await StartMigrationAsync(issuer.Issuer);
        using HttpRequestMessage request = InitializeWith(null);
        request.Headers.TryAddWithoutValidation("Authorization", header);

        using HttpResponseMessage response = await host.Client.SendAsync(request);

        ShouldBeRefused(response);
    }

    [Fact]
    public async Task UnderTheMigrationMode_TheMetadataDocument_AnswersWithNoCredential()
    {
        await using StubIssuer issuer = await StubIssuer.StartAsync("/realms/marqspec");
        await using Host host = await StartMigrationAsync(issuer.Issuer);

        using HttpResponseMessage response = await host.Client.GetAsync(
            "/.well-known/oauth-protected-resource/mcp");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"resource\":\"" + ResourceUrl + "\"");
        body.Should().Contain(issuer.Issuer);
    }

    // ── cryptography and discovery on the audience path (gh#997) ────────────────────────────────────────

    [Fact]
    public async Task AnExpiredAudienceToken_IsRefused()
    {
        await using StubIssuer issuer = await StubIssuer.StartAsync("/realms/marqspec");
        await using Host host = await StartAsync(issuer.Issuer);
        string token = issuer.Mint(issuer.ValidForAudience(ResourceUrl) with
        {
            IssuedAt = DateTime.UtcNow.AddMinutes(-30),
            Expires = DateTime.UtcNow.AddSeconds(-90),
        });

        using MetricCollector<long> refusals = WatchRefusals(host);
        using HttpResponseMessage response = await host.Client.SendAsync(InitializeWith(token));

        ShouldBeRefused(response);
        refusals.LastMeasurement!.Tags[HostTelemetry.RefusalTag].Should().Be(OAuthRefusalReason.TokenExpired);
    }

    [Fact]
    public async Task ATokenSignedByAnUnpublishedKey_IsRefused()
    {
        await using StubIssuer issuer = await StubIssuer.StartAsync("/realms/marqspec");
        await using Host host = await StartAsync(issuer.Issuer);
        string token = issuer.Mint(issuer.ValidForAudience(ResourceUrl) with
        {
            Signing = StubIssuer.UnpublishedKey(),
        });

        using MetricCollector<long> refusals = WatchRefusals(host);
        using HttpResponseMessage response = await host.Client.SendAsync(InitializeWith(token));

        ShouldBeRefused(response);
        refusals.LastMeasurement!.Tags[HostTelemetry.RefusalTag].Should().Be(OAuthRefusalReason.BadSignature);
    }

    [Fact]
    public async Task AnUnsignedAudienceToken_IsRefused()
    {
        await using StubIssuer issuer = await StubIssuer.StartAsync("/realms/marqspec");
        await using Host host = await StartAsync(issuer.Issuer);
        string token = issuer.Mint(issuer.ValidForAudience(ResourceUrl) with { Unsigned = true });

        using MetricCollector<long> refusals = WatchRefusals(host);
        using HttpResponseMessage response = await host.Client.SendAsync(InitializeWith(token));

        ShouldBeRefused(response);
        refusals.LastMeasurement!.Tags[HostTelemetry.RefusalTag].Should().Be(OAuthRefusalReason.BadAlgorithm);
    }

    [Fact]
    public async Task AnHs256AudienceToken_IsRefused()
    {
        await using StubIssuer issuer = await StubIssuer.StartAsync("/realms/marqspec");
        await using Host host = await StartAsync(issuer.Issuer);
        byte[] key = new byte[32];
        SigningCredentials hmac = new(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256);
        string token = issuer.Mint(issuer.ValidForAudience(ResourceUrl) with { Signing = hmac });

        using MetricCollector<long> refusals = WatchRefusals(host);
        using HttpResponseMessage response = await host.Client.SendAsync(InitializeWith(token));

        ShouldBeRefused(response);
        // The handler reports HS256 as a signature failure. The header alg is what makes the reason
        // bad_algorithm, and the token itself is not a tag.
        refusals.LastMeasurement!.Tags[HostTelemetry.RefusalTag].Should().Be(OAuthRefusalReason.BadAlgorithm);
    }

    [Fact]
    public async Task AWrongAudience_IsCountedAsAudience_WithReplayFalse_AndNoToken()
    {
        await using StubIssuer issuer = await StubIssuer.StartAsync("/realms/marqspec");
        await using Host host = await StartAsync(issuer.Issuer);
        string token = issuer.Mint(issuer.ValidForAudience("https://another-api.example.test/mcp"));
        List<Activity> spans = [];
        using ActivityListener listener = Listen(spans, host.Telemetry.Activities);
        using MetricCollector<long> refusals = WatchRefusals(host);

        using HttpResponseMessage response = await host.Client.SendAsync(InitializeWith(token));

        ShouldBeRefused(response);
        CollectedMeasurement<long> measurement = refusals.LastMeasurement!;
        measurement.Tags[HostTelemetry.OperationTag].Should().Be(OAuthGateOperation.Authenticate);
        measurement.Tags[HostTelemetry.RefusalTag].Should().Be(OAuthRefusalReason.Audience);
        measurement.Tags[HostTelemetry.ReplayTag].Should().Be(ExecutionWriteReplay.False);
        measurement.Tags.Keys.Should().NotContain("account");
        measurement.Tags.Values.Should().NotContain(token);

        Activity span = spans.Should().ContainSingle().Subject;
        span.OperationName.Should().Be("oauth.refusal");
        span.GetTagItem(HostTelemetry.RefusalTag).Should().Be(OAuthRefusalReason.Audience);
        span.GetTagItem(HostTelemetry.ReplayTag).Should().Be(ExecutionWriteReplay.False);
        span.TagObjects.Select(tag => tag.Value?.ToString()).Should().NotContain(token);
    }

    [Fact]
    public async Task AnUnlistedSubject_IsCountedAsSubject()
    {
        await using StubIssuer issuer = await StubIssuer.StartAsync("/realms/marqspec");
        await using Host host = await StartAsync(issuer.Issuer, new Dictionary<string, string?>
        {
            ["Mcp:OAuth:Subjects"] = "user-sub-1",
        });
        string token = issuer.Mint(issuer.ValidForAudience(ResourceUrl) with
        {
            Subject = "user_01SOMEONEELSE",
        });

        using MetricCollector<long> refusals = WatchRefusals(host);
        using HttpResponseMessage response = await host.Client.SendAsync(InitializeWith(token));

        ShouldBeRefused(response);
        refusals.LastMeasurement!.Tags[HostTelemetry.RefusalTag].Should().Be(OAuthRefusalReason.Subject);
    }

    [Fact]
    public async Task AnUnlistedClient_IsCountedAsClient()
    {
        await using StubIssuer issuer = await StubIssuer.StartAsync("/realms/marqspec");
        await using Host host = await StartAsync(issuer.Issuer, new Dictionary<string, string?>
        {
            ["Mcp:OAuth:ClientIds"] = "connector-client",
        });
        string token = issuer.Mint(issuer.ValidForAudience(ResourceUrl) with
        {
            AuthorizedParty = "someone-else",
        });

        using MetricCollector<long> refusals = WatchRefusals(host);
        using HttpResponseMessage response = await host.Client.SendAsync(InitializeWith(token));

        ShouldBeRefused(response);
        refusals.LastMeasurement!.Tags[HostTelemetry.RefusalTag].Should().Be(OAuthRefusalReason.Client);
    }

    [Fact]
    public async Task ATokenUseThatIsNotAccess_IsCountedAsTokenUse()
    {
        await using StubIssuer issuer = await StubIssuer.StartAsync("/realms/marqspec");
        await using Host host = await StartAsync(issuer.Issuer);
        string token = issuer.Mint(issuer.ValidForAudience(ResourceUrl) with { TokenUse = "id" });

        using MetricCollector<long> refusals = WatchRefusals(host);
        using HttpResponseMessage response = await host.Client.SendAsync(InitializeWith(token));

        ShouldBeRefused(response);
        refusals.LastMeasurement!.Tags[HostTelemetry.RefusalTag].Should().Be(OAuthRefusalReason.TokenUse);
    }

    [Fact]
    public async Task UnderTheMigrationMode_EachAdmit_IsTaggedWithItsMethod()
    {
        await using StubIssuer issuer = await StubIssuer.StartAsync("/realms/marqspec");
        await using Host host = await StartMigrationAsync(issuer.Issuer);
        using MetricCollector<long> admits = new(
            host.Telemetry, HostTelemetry.Name, HostTelemetry.OAuthAdmitsInstrument);

        using HttpResponseMessage staticResponse = await host.Client.SendAsync(InitializeWith(StaticToken));
        staticResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage oauthResponse =
            await host.Client.SendAsync(InitializeWith(issuer.Mint(issuer.ValidForAudience(ResourceUrl))));
        oauthResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        admits.GetMeasurementSnapshot().Select(m => m.Tags[HostTelemetry.AuthMethodTag])
            .Should().Equal(OAuthAuthMethod.StaticToken, OAuthAuthMethod.OAuth);
    }

    [Fact]
    public async Task TheMigrationMode_WarnsAtStartup_AndPureOAuthDoesNot()
    {
        await using StubIssuer issuer = await StubIssuer.StartAsync("/realms/marqspec");
        await using Host migration = await StartMigrationAsync(issuer.Issuer);
        await using Host oauth = await StartAsync(issuer.Issuer);

        migration.Logs.Lines().Should().Contain(line =>
            line.Contains("OAuthWithStaticToken", StringComparison.Ordinal)
            && line.Contains("Trader Jon", StringComparison.Ordinal));
        oauth.Logs.Lines().Should().NotContain(line =>
            line.Contains("OAuthWithStaticToken", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnAudienceThatIsNotTheResourceUrl_Warns_AndStillAcceptsThatAudience()
    {
        const string audience = "https://api.example.test/mcp";
        await using StubIssuer issuer = await StubIssuer.StartAsync("/realms/marqspec");
        await using Host host = await StartAsync(issuer.Issuer, new Dictionary<string, string?>
        {
            ["Mcp:OAuth:Audience"] = audience,
        });

        host.Logs.Lines().Should().Contain(line =>
            line.Contains("does not equal Mcp__OAuth__ResourceUrl", StringComparison.Ordinal));

        using HttpResponseMessage response =
            await host.Client.SendAsync(InitializeWith(issuer.Mint(issuer.ValidForAudience(audience))));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task MetadataUrl_IsTheAddressFetched_NotOpenIdDiscovery()
    {
        await using StubIssuer issuer = await StubIssuer.StartAsync("/realms/marqspec");
        await using Host host = await StartAsync(issuer.Issuer, new Dictionary<string, string?>
        {
            ["Mcp:OAuth:MetadataUrl"] = issuer.Rfc8414Url,
        });

        using HttpResponseMessage response =
            await host.Client.SendAsync(InitializeWith(issuer.Mint(issuer.ValidForAudience(ResourceUrl))));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        issuer.Rfc8414Requests.Should().BeGreaterThan(0);
        issuer.DiscoveryRequests.Should().Be(0);
    }

    [Fact]
    public async Task AFailingMetadataUrl_FailsClosed_AndTheStaticTokenStillWorks()
    {
        await using StubIssuer issuer = await StubIssuer.StartAsync("/realms/marqspec");
        issuer.FailRfc8414 = true;
        await using Host host = await StartMigrationAsync(issuer.Issuer, issuer.Rfc8414Url);

        using HttpResponseMessage token = await host.Client.SendAsync(
            InitializeWith(issuer.Mint(issuer.ValidForAudience(ResourceUrl))));
        using HttpResponseMessage stat = await host.Client.SendAsync(InitializeWith(StaticToken));

        token.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        stat.StatusCode.Should().Be(HttpStatusCode.OK);
        issuer.DiscoveryRequests.Should().Be(0);
    }

    [Fact]
    public async Task AnUnreachableMetadataUrl_FailsClosed_UnderTheMigrationMode()
    {
        await using StubIssuer issuer = await StubIssuer.StartAsync("/realms/marqspec");
        await using Host host = await StartMigrationAsync(issuer.Issuer, "http://127.0.0.1:9/metadata");

        using HttpResponseMessage token = await host.Client.SendAsync(
            InitializeWith(issuer.Mint(issuer.ValidForAudience(ResourceUrl))));
        using HttpResponseMessage stat = await host.Client.SendAsync(InitializeWith(StaticToken));

        token.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        stat.StatusCode.Should().Be(HttpStatusCode.OK);
        issuer.DiscoveryRequests.Should().Be(0, "discovery goes to MetadataUrl, which is not this issuer");
    }

    [Fact]
    public async Task AFailingJwks_FailsClosed_UnderTheMigrationMode()
    {
        await using StubIssuer issuer = await StubIssuer.StartAsync("/realms/marqspec");
        issuer.FailJwks = true;
        await using Host host = await StartMigrationAsync(issuer.Issuer);

        using HttpResponseMessage token = await host.Client.SendAsync(
            InitializeWith(issuer.Mint(issuer.ValidForAudience(ResourceUrl))));
        using HttpResponseMessage stat = await host.Client.SendAsync(InitializeWith(StaticToken));

        token.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        stat.StatusCode.Should().Be(HttpStatusCode.OK);
        issuer.JwksRequests.Should().BeGreaterThan(0);
    }

    private static Task<Host> StartMigrationAsync(string issuer, string metadataUrl)
        => StartAsync(issuer, new Dictionary<string, string?>
        {
            ["Mcp:Auth:Mode"] = nameof(McpAuthMode.OAuthWithStaticToken),
            ["Mcp:HttpBearerToken"] = StaticToken,
            ["Mcp:OAuth:MetadataUrl"] = metadataUrl,
        });

    private static MetricCollector<long> WatchRefusals(Host host)
        => new(host.Telemetry, HostTelemetry.Name, HostTelemetry.OAuthRefusalsInstrument);

    private static ActivityListener Listen(List<Activity> into, ActivitySource source)
    {
        ActivityListener listener = new()
        {
            ShouldListenTo = candidate => ReferenceEquals(candidate, source),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = into.Add,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
