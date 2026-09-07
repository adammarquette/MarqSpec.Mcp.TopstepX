using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MarqSpec.Mcp.TopstepX.Tests.Configuration;

/// <summary>
/// The OAuth mode of the HTTP gate: a Cognito-shaped access token opens <c>/mcp</c>, and everything else
/// is refused with the challenge a connector can act on.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deny by default, and the negatives are pinned as hard as the positive.</b> ADR-0007's 2026-08-22
/// update records what a gate that is asserted rather than checked costs. Every way a token can be wrong
/// that this server is expected to notice has its own test here — absent, malformed, expired, not yet
/// valid, unsigned, signed by an unpublished key, from another issuer, from an unlisted client, without the
/// scope, an ID token rather than an access token — so that a later loosening of any one check fails one
/// named test rather than a vague suite.
/// </para>
/// <para>
/// <b>Cognito access tokens carry <c>client_id</c> and <c>scope</c>, not <c>aud</c>.</b> Turning audience
/// validation off is therefore necessary and not sufficient: it is the claims check that stands in for the
/// audience, and these tests are what stops <c>ValidateAudience = false</c> from being the whole of it.
/// </para>
/// <para>
/// <b>No network.</b> The issuer is <see cref="StubIssuer"/> on a loopback port with a key generated for
/// the run; discovery and the key set are served in-process. gh#517's Cognito pool did not exist when this
/// was written, and the unit tier must never depend on one.
/// </para>
/// </remarks>
public sealed class OAuthBearerGateTests
{
    private const string ResourceUrl = "https://topstepx-mcp.example.test/mcp";
    private const string Scope = "topstepx-mcp/read";

    /// <summary>What the 401 must carry, byte for byte — the connector reads it, nothing else does.</summary>
    private const string ExpectedChallenge =
        "Bearer resource_metadata=\"https://topstepx-mcp.example.test/.well-known/oauth-protected-resource/mcp\", "
        + "scope=\"topstepx-mcp/read\"";

    private const string ConnectionSentinel =
        "Host=sentinel-db-host-b91c;Port=5432;Database=topstepx_mcp;Username=topstepx;"
        + "Password=sentinel-db-password-b91c";

    private const string Initialize =
        "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\","
        + "\"capabilities\":{},\"clientInfo\":{\"name\":\"gate-test\",\"version\":\"1.0\"}}}";

    private sealed class Host(WebApplication app, HttpClient client, CapturingLoggerProvider logs) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public CapturingLoggerProvider Logs { get; } = logs;

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
            ["ConnectionStrings:Default"] = ConnectionSentinel,
            ["MarketData:Instruments"] = "ES,NQ",
            ["MarketData:SessionCloseCentral"] = "16:00",
            ["MarketData:MaxRows"] = "5000",
            ["Mcp:Transport"] = "Http",
            ["Mcp:Auth:Mode"] = "OAuth",
            ["Mcp:OAuth:Issuer"] = issuer,
            ["Mcp:OAuth:ClientIds"] = "connector-client,deploy-check-client",
            ["Mcp:OAuth:ResourceUrl"] = ResourceUrl,
        };

        CapturingLoggerProvider logs = new();
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(extra is null ? settings : settings.Concat(extra));

        McpOptions mcp = builder.Configuration.GetSection(McpOptions.SectionName).Get<McpOptions>()!;
        Program.ConfigureServices(builder, mcp);

        WebApplication app = builder.Build();
        app.Services.GetRequiredService<StoreAvailabilityHolder>().Set(StoreAvailability.Available());
        Program.MapHttpTransport(app, mcp);
        await app.StartAsync();

        HttpClient client = new() { BaseAddress = new Uri(app.Urls.First()), Timeout = TimeSpan.FromSeconds(30) };
        return new Host(app, client, logs);
    }

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

    // ── the positive ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AValidAccessToken_ReachesInitialize()
    {
        await using StubIssuer issuer = await StubIssuer.StartAsync();
        await using Host host = await StartAsync(issuer.Issuer);

        using HttpResponseMessage response = await host.Client.SendAsync(InitializeWith(issuer.Mint(issuer.Valid())));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("protocolVersion");
        issuer.JwksRequests.Should().BeGreaterThan(0, "the signature was checked against the published key set");
    }

    [Fact]
    public async Task TheSecondConfiguredClient_IsAcceptedToo()
    {
        // The deploy check's client_credentials client (ADR-0023 §9) is the second id in the set.
        await using StubIssuer issuer = await StubIssuer.StartAsync();
        await using Host host = await StartAsync(issuer.Issuer);
        string token = issuer.Mint(new TokenShape { Issuer = issuer.Issuer, ClientId = "deploy-check-client", Subject = "deploy-check-client" });

        using HttpResponseMessage response = await host.Client.SendAsync(InitializeWith(token));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ATokenThirtySecondsPastExpiry_IsStillInsideTheSkew()
    {
        // The skew is at most 60 s. This pins it from below; the 90 s case below pins it from above.
        await using StubIssuer issuer = await StubIssuer.StartAsync();
        await using Host host = await StartAsync(issuer.Issuer);
        string token = issuer.Mint(new TokenShape
        {
            Issuer = issuer.Issuer,
            IssuedAt = DateTime.UtcNow.AddMinutes(-30),
            Expires = DateTime.UtcNow.AddSeconds(-30),
        });

        using HttpResponseMessage response = await host.Client.SendAsync(InitializeWith(token));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TheHealthProbeStillAnswers_WithNoCredential_AndNeverReachesTheIssuer()
    {
        // gh#513's carve-out survives the mode change: the load balancer has no credential under either
        // scheme. And a probe every 30 s must not become a discovery fetch every 30 s.
        await using StubIssuer issuer = await StubIssuer.StartAsync();
        await using Host host = await StartAsync(issuer.Issuer);

        using HttpResponseMessage response = await host.Client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("\"status\":\"ok\"");
        issuer.DiscoveryRequests.Should().Be(0);
        issuer.JwksRequests.Should().Be(0);
    }

    [Fact]
    public async Task ThePrincipalReachesTheLogScope_AndTheTokenNeverDoes()
    {
        // sub and client_id are what an operator correlates on; the token is a credential and this
        // repository is public. Both halves are asserted against everything every logger in the host saw,
        // the JwtBearer handler's own lines included.
        await using StubIssuer issuer = await StubIssuer.StartAsync();
        await using Host host = await StartAsync(issuer.Issuer);
        string token = issuer.Mint(issuer.Valid());

        using HttpResponseMessage response = await host.Client.SendAsync(InitializeWith(token));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        host.Logs.ScopeValues("auth.sub").Should().Contain("user-sub-1");
        host.Logs.ScopeValues("auth.client_id").Should().Contain("connector-client");
        host.Logs.Everything().Should().NotContain(token);
    }

    [Fact]
    public async Task ARefusedToken_NeverReachesALogLineEither()
    {
        await using StubIssuer issuer = await StubIssuer.StartAsync();
        await using Host host = await StartAsync(issuer.Issuer);
        string token = issuer.Mint(new TokenShape { Issuer = issuer.Issuer, ClientId = "not-a-listed-client" });

        using HttpResponseMessage response = await host.Client.SendAsync(InitializeWith(token));

        ShouldBeRefused(response);
        host.Logs.Everything().Should().NotContain(token);
    }

    // ── the negatives ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoToken_IsRefused_WithTheChallenge()
    {
        await using StubIssuer issuer = await StubIssuer.StartAsync();
        await using Host host = await StartAsync(issuer.Issuer);

        using HttpResponseMessage response = await host.Client.SendAsync(InitializeWith(null));

        ShouldBeRefused(response);
        issuer.DiscoveryRequests.Should().Be(0, "an absent token is refused before the issuer is consulted");
    }

    [Theory]
    [InlineData("Bearer not-a-jwt")]
    [InlineData("Bearer ")]
    [InlineData("Bearer a.b")]
    [InlineData("Basic Y29ubmVjdG9yOnNlY3JldA==")]
    [InlineData("Token eyJhbGciOiJSUzI1NiJ9.e30.x")]
    public async Task AMalformedCredential_IsRefused(string header)
    {
        await using StubIssuer issuer = await StubIssuer.StartAsync();
        await using Host host = await StartAsync(issuer.Issuer);
        using HttpRequestMessage request = InitializeWith(null);
        request.Headers.TryAddWithoutValidation("Authorization", header);

        using HttpResponseMessage response = await host.Client.SendAsync(request);

        ShouldBeRefused(response);
    }

    [Fact]
    public async Task TheStaticModesToken_OpensNothingHere()
    {
        // changeme-local is in a public repository. Under OAuth it is not a credential of any kind.
        await using StubIssuer issuer = await StubIssuer.StartAsync();
        await using Host host = await StartAsync(issuer.Issuer);

        using HttpResponseMessage response = await host.Client.SendAsync(InitializeWith("changeme-local"));

        ShouldBeRefused(response);
    }

    [Fact]
    public async Task AnExpiredToken_IsRefused()
    {
        // 90 s past exp is outside a 60 s skew. Together with the 30 s case above, the ceiling is pinned.
        await using StubIssuer issuer = await StubIssuer.StartAsync();
        await using Host host = await StartAsync(issuer.Issuer);
        string token = issuer.Mint(new TokenShape
        {
            Issuer = issuer.Issuer,
            IssuedAt = DateTime.UtcNow.AddMinutes(-30),
            Expires = DateTime.UtcNow.AddSeconds(-90),
        });

        using HttpResponseMessage response = await host.Client.SendAsync(InitializeWith(token));

        ShouldBeRefused(response);
    }

    [Fact]
    public async Task ATokenWithNoExpiry_IsRefused()
    {
        // Cognito always sets exp. A token without one is not a Cognito token, and "valid forever" is not a
        // property this gate ever grants.
        await using StubIssuer issuer = await StubIssuer.StartAsync();
        await using Host host = await StartAsync(issuer.Issuer);
        string token = issuer.Mint(new TokenShape { Issuer = issuer.Issuer, NoExpiry = true });

        using HttpResponseMessage response = await host.Client.SendAsync(InitializeWith(token));

        ShouldBeRefused(response);
    }

    [Fact]
    public async Task ATokenNotYetValid_IsRefused()
    {
        await using StubIssuer issuer = await StubIssuer.StartAsync();
        await using Host host = await StartAsync(issuer.Issuer);
        string token = issuer.Mint(new TokenShape
        {
            Issuer = issuer.Issuer,
            NotBefore = DateTime.UtcNow.AddMinutes(5),
            Expires = DateTime.UtcNow.AddHours(1),
        });

        using HttpResponseMessage response = await host.Client.SendAsync(InitializeWith(token));

        ShouldBeRefused(response);
    }

    [Fact]
    public async Task ATokenFromAnotherIssuer_IsRefused_EvenWhenSignedWithThePublishedKey()
    {
        // The signature is right and the issuer is wrong: this is what ValidIssuer being set explicitly
        // buys, rather than trusting whatever the discovery document says about itself.
        await using StubIssuer issuer = await StubIssuer.StartAsync();
        await using Host host = await StartAsync(issuer.Issuer);
        string token = issuer.Mint(new TokenShape { Issuer = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_someoneElse" });

        using HttpResponseMessage response = await host.Client.SendAsync(InitializeWith(token));

        ShouldBeRefused(response);
    }

    [Fact]
    public async Task ATokenWithNoIssuer_IsRefused()
    {
        await using StubIssuer issuer = await StubIssuer.StartAsync();
        await using Host host = await StartAsync(issuer.Issuer);
        string token = issuer.Mint(new TokenShape { Issuer = null });

        using HttpResponseMessage response = await host.Client.SendAsync(InitializeWith(token));

        ShouldBeRefused(response);
    }

    [Fact]
    public async Task ATokenFromAnUnlistedClient_IsRefused()
    {
        // Cognito would issue this to any app client on the same pool. Being on the pool is not being
        // allowed in; the client set is the audience check, and a pool shared with anything else is why.
        await using StubIssuer issuer = await StubIssuer.StartAsync();
        await using Host host = await StartAsync(issuer.Issuer);
        string token = issuer.Mint(new TokenShape { Issuer = issuer.Issuer, ClientId = "some-other-app-client" });

        using HttpResponseMessage response = await host.Client.SendAsync(InitializeWith(token));

        ShouldBeRefused(response);
    }

    [Fact]
    public async Task ATokenWithNoClientId_IsRefused()
    {
        await using StubIssuer issuer = await StubIssuer.StartAsync();
        await using Host host = await StartAsync(issuer.Issuer);
        string token = issuer.Mint(new TokenShape { Issuer = issuer.Issuer, ClientId = null });

        using HttpResponseMessage response = await host.Client.SendAsync(InitializeWith(token));

        ShouldBeRefused(response);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("openid profile")]
    [InlineData("topstepx-mcp/write")]
    [InlineData("topstepx-mcp/readwrite")]
    [InlineData("topstepx-mcp/rea")]
    [InlineData("TOPSTEPX-MCP/READ")]
    public async Task ATokenWithoutTheRequiredScope_IsRefused(string? scope)
    {
        // The scope claim is space-separated, and the check is a whole-entry ordinal match: a prefix, a
        // superstring and a case variant are each a different scope, not this one.
        await using StubIssuer issuer = await StubIssuer.StartAsync();
        await using Host host = await StartAsync(issuer.Issuer);
        string token = issuer.Mint(new TokenShape { Issuer = issuer.Issuer, Scope = scope });

        using HttpResponseMessage response = await host.Client.SendAsync(InitializeWith(token));

        ShouldBeRefused(response);
    }

    [Fact]
    public async Task ATokenCarryingTheScopeAmongOthers_IsAccepted()
    {
        // The connector client is allowed `openid topstepx-mcp/read` (ADR-0023 §9), so the required scope
        // arrives beside another one. Only its presence matters.
        await using StubIssuer issuer = await StubIssuer.StartAsync();
        await using Host host = await StartAsync(issuer.Issuer);
        string token = issuer.Mint(new TokenShape { Issuer = issuer.Issuer, Scope = "openid topstepx-mcp/read" });

        using HttpResponseMessage response = await host.Client.SendAsync(InitializeWith(token));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("Access")]
    [InlineData("")]
    [InlineData(null)]
    public async Task AnythingButAnAccessToken_IsRefused(string? tokenUse)
    {
        // A Cognito ID token is signed by the same key and carries the same issuer. It is not a credential
        // for a resource server, and token_use is the claim that says which one this is.
        await using StubIssuer issuer = await StubIssuer.StartAsync();
        await using Host host = await StartAsync(issuer.Issuer);
        string token = issuer.Mint(new TokenShape { Issuer = issuer.Issuer, TokenUse = tokenUse });

        using HttpResponseMessage response = await host.Client.SendAsync(InitializeWith(token));

        ShouldBeRefused(response);
    }

    [Fact]
    public async Task AnUnsignedToken_IsRefused()
    {
        // alg: none, with every claim right. The oldest JWT attack there is.
        await using StubIssuer issuer = await StubIssuer.StartAsync();
        await using Host host = await StartAsync(issuer.Issuer);
        string token = issuer.Mint(new TokenShape { Issuer = issuer.Issuer, Unsigned = true });

        using HttpResponseMessage response = await host.Client.SendAsync(InitializeWith(token));

        ShouldBeRefused(response);
    }

    [Fact]
    public async Task ATokenSignedByAnUnpublishedKey_IsRefused_EvenUnderThePublishedKeyId()
    {
        // Same kid as the published key, different key. The kid is a hint, not a proof.
        await using StubIssuer issuer = await StubIssuer.StartAsync();
        await using Host host = await StartAsync(issuer.Issuer);
        string token = issuer.Mint(new TokenShape { Issuer = issuer.Issuer, Signing = StubIssuer.UnpublishedKey(StubIssuer.Kid) });

        using HttpResponseMessage response = await host.Client.SendAsync(InitializeWith(token));

        ShouldBeRefused(response);
    }

    [Fact]
    public async Task ATokenSignedByAnUnpublishedKey_IsRefused_UnderAnUnknownKeyId()
    {
        await using StubIssuer issuer = await StubIssuer.StartAsync();
        await using Host host = await StartAsync(issuer.Issuer);
        string token = issuer.Mint(new TokenShape { Issuer = issuer.Issuer, Signing = StubIssuer.UnpublishedKey("some-other-kid") });

        using HttpResponseMessage response = await host.Client.SendAsync(InitializeWith(token));

        ShouldBeRefused(response);
    }

    [Fact]
    public async Task AnUnreachableIssuer_FailsClosed()
    {
        // The issuer went away after the server was configured against it. Every token is then unverifiable,
        // and unverifiable is refused — never "let it through until discovery comes back".
        StubIssuer issuer = await StubIssuer.StartAsync();
        string address = issuer.Issuer;
        string token = issuer.Mint(issuer.Valid());
        await issuer.DisposeAsync();

        await using Host host = await StartAsync(address);

        using HttpResponseMessage response = await host.Client.SendAsync(InitializeWith(token));

        ShouldBeRefused(response);
    }

    [Theory]
    [InlineData("/healthz")]
    [InlineData("/health/anything")]
    [InlineData("/Health")]
    [InlineData("/")]
    [InlineData("/.well-known/openid-configuration")]
    [InlineData("/mcp/anything")]
    public async Task EveryOtherPathIsStillBehindTheGate(string path)
    {
        await using StubIssuer issuer = await StubIssuer.StartAsync();
        await using Host host = await StartAsync(issuer.Issuer);

        using HttpResponseMessage response = await host.Client.GetAsync(path);

        ShouldBeRefused(response);
    }

    [Theory]
    [InlineData("/healthz")]
    [InlineData("/health/anything")]
    [InlineData("/")]
    public async Task AValidToken_OpensNothingButTheMcpEndpoint(string path)
    {
        // The gate is global and a valid token passes it everywhere; what it must not do is turn a path
        // nothing is mapped on into an answer. Not found, and never 200.
        await using StubIssuer issuer = await StubIssuer.StartAsync();
        await using Host host = await StartAsync(issuer.Issuer);
        using HttpRequestMessage request = new(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", issuer.Mint(issuer.Valid()));

        using HttpResponseMessage response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ThePathIsNotAWayIn_ForAnyOtherMethodOnHealth()
    {
        await using StubIssuer issuer = await StubIssuer.StartAsync();
        await using Host host = await StartAsync(issuer.Issuer);

        using HttpResponseMessage response = await host.Client.PostAsync("/health", content: null);

        ShouldBeRefused(response);
    }

    /// <summary>Every line and every scope every logger in the host produced.</summary>
    internal sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly object _gate = new();
        private readonly List<string> _lines = [];
        private readonly List<object?> _scopes = [];

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public IEnumerable<string> ScopeValues(string key)
        {
            lock (_gate)
            {
                return _scopes
                    .OfType<IEnumerable<KeyValuePair<string, object?>>>()
                    .SelectMany(scope => scope)
                    .Where(pair => string.Equals(pair.Key, key, StringComparison.Ordinal))
                    .Select(pair => pair.Value?.ToString() ?? string.Empty)
                    .ToList();
            }
        }

        public string Everything()
        {
            lock (_gate)
            {
                IEnumerable<string> scopes = _scopes.Select(scope => scope switch
                {
                    IEnumerable<KeyValuePair<string, object?>> pairs =>
                        string.Join(" ", pairs.Select(pair => pair.Key + "=" + pair.Value)),
                    _ => scope?.ToString() ?? string.Empty,
                });
                return string.Join("\n", _lines.Concat(scopes));
            }
        }

        public void Dispose()
        {
        }

        private sealed class Logger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state)
                where TState : notnull
            {
                lock (owner._gate)
                {
                    owner._scopes.Add(state);
                }

                return NullScope.Instance;
            }

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                string line = formatter(state, exception) + (exception is null ? string.Empty : " " + exception);
                lock (owner._gate)
                {
                    owner._lines.Add(line);
                }
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
