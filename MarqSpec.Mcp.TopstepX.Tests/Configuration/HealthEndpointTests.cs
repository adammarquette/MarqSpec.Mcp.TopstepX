using System.Net;
using System.Text.Json;
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
/// Exactly one path answers without a credential, and it says what is running.
/// </summary>
/// <remarks>
/// <para>
/// <b>An Application Load Balancer cannot send a bearer token.</b> A target-group probe is a bare
/// <c>GET</c> from the load balancer itself, and a task whose every path answers 401 is a task the ALB
/// marks unhealthy and kills — so the deployment ADR-0021 describes cannot start at all without a path in
/// front of the gate. That is the whole of why this endpoint exists (gh#513, gh#509).
/// </para>
/// <para>
/// <b>Ordering is not what carves the path out, and reading the pipeline top to bottom says it is.</b>
/// <see cref="WebApplication"/> inserts <c>UseRouting</c> before every middleware the composition root adds
/// and <c>UseEndpoints</c> after all of them, so a <c>MapGet("/health", …)</c> registered <i>before</i>
/// <see cref="BearerTokenGate.UseBearerTokenGate"/> still executes <i>after</i> it — the gate would answer
/// 401 and the endpoint would never run. What actually works is a terminal branch: the health middleware
/// short-circuits the pipeline for the one path it claims, and nothing registered after it is reached.
/// These tests are what tells the difference, because both shapes read identically in the source.
/// </para>
/// <para>
/// <b>The gate had no test of its own until this file.</b> <c>git grep -ln BearerTokenGate -- '*.cs'</c>
/// named only the two product files, and ADR-0007's 2026-08-22 update records what that costs: the token
/// was required in configuration and never checked at request time, and everything said the endpoint was
/// protected except the request pipeline. Carving a hole in front of a gate is exactly the change that
/// recurrence would hide behind, so the 401 cases are pinned here beside the 200 one.
/// </para>
/// <para>
/// <b>Nothing here reaches a store.</b> The probe reads the startup answer
/// <see cref="StoreAvailabilityHolder"/> already carries; the ALB probes every 30 s, and a health check that
/// opened a connection would be load rather than a measurement of it. That is also why an unavailable store
/// is still 200 — liveness, not readiness.
/// </para>
/// </remarks>
public sealed class HealthEndpointTests
{
    /// <summary>
    /// The bearer token these hosts are configured with, and the sentinel the body must never carry.
    /// </summary>
    private const string TokenSentinel = "sentinel-bearer-token-3a7f9c";

    /// <summary>The connection string these hosts are configured with, sentinel for the same reason.</summary>
    private const string ConnectionSentinel =
        "Host=sentinel-db-host-b91c;Port=5432;Database=topstepx_mcp;Username=topstepx;"
        + "Password=sentinel-db-password-b91c";

    private static readonly Dictionary<string, string?> _baseSettings = new()
    {
        ["ConnectionStrings:Default"] = ConnectionSentinel,
        ["MarketData:Instruments"] = "ES,NQ",
        ["MarketData:SessionCloseCentral"] = "16:00",
        ["MarketData:MaxRows"] = "5000",
        ["Mcp:Transport"] = "Http",
        ["Mcp:HttpBearerToken"] = TokenSentinel,
    };

    /// <summary>A started host on an ephemeral loopback port, and a client pointed at it.</summary>
    /// <remarks>
    /// Port 0 so the OS assigns one: this suite runs beside other worktrees, and a fixed port would make two
    /// sessions unable to run the tests at once — the failure gh#392 already paid for once.
    /// </remarks>
    private sealed class Probe(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private static async Task<Probe> StartAsync(
        StoreAvailability? store = null,
        Dictionary<string, string?>? extra = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(
            extra is null ? _baseSettings : _baseSettings.Concat(extra));

        McpOptions mcp = builder.Configuration.GetSection(McpOptions.SectionName).Get<McpOptions>()!;
        Program.ConfigureServices(builder, mcp);

        WebApplication app = builder.Build();

        // What Program.Main publishes after its startup probe. Unset, the holder reports available, so the
        // unavailable case has to be said rather than assumed.
        app.Services.GetRequiredService<StoreAvailabilityHolder>()
            .Set(store ?? StoreAvailability.Available());

        Program.MapHttpTransport(app, mcp.HttpBearerToken);

        await app.StartAsync();

        return new Probe(app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) });
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task TheProbeAnswers_WithNoAuthorizationHeaderAtAll()
    {
        // The ALB's request, stated exactly: no credential, because it has none to send.
        await using Probe probe = await StartAsync();

        using HttpResponseMessage response = await probe.Client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        (await ReadJsonAsync(response)).GetProperty("status").GetString().Should().Be("ok");
    }

    [Fact]
    public async Task TheProbeReportsTheStoreUnavailable_WhenTheStartupProbeFoundNone()
    {
        // Still 200. Liveness, not readiness: the task is alive and its tool list is real -- the ones needing
        // no store answer normally -- so killing it would replace a degraded server with no server.
        await using Probe probe = await StartAsync(
            StoreAvailability.Unavailable("Nothing answered on the configured connection string."));

        using HttpResponseMessage response = await probe.Client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement body = await ReadJsonAsync(response);
        body.GetProperty("status").GetString().Should().Be("ok");
        body.GetProperty("store").GetString().Should().Be("unavailable");
    }

    [Fact]
    public async Task TheProbeReportsTheStoreAvailable_WhenTheStartupProbeFoundOne()
    {
        await using Probe probe = await StartAsync(StoreAvailability.Available());

        using HttpResponseMessage response = await probe.Client.GetAsync("/health");

        (await ReadJsonAsync(response)).GetProperty("store").GetString().Should().Be("available");
    }

    [Fact]
    public async Task TheProbeReportsUnknown_WhenNothingStampedAVersionOrADigest()
    {
        // The shipped assembly is stamped 0.0.0-alpha.0 by decision (ADR-0001), so there is no version to
        // read out of the process -- only one a deployment hands it. "unknown" is the honest answer, and a
        // missing number reported as missing is this repository's rule rather than this endpoint's habit.
        await using Probe probe = await StartAsync();

        JsonElement body = await ReadJsonAsync(await probe.Client.GetAsync("/health"));

        body.GetProperty("version").GetString().Should().Be("unknown");
        body.GetProperty("digest").GetString().Should().Be("unknown");
    }

    [Fact]
    public async Task TheProbeReportsWhatTheDeploymentStamped()
    {
        // The task definition sets these; nothing in the repository declares a version in a file.
        await using Probe probe = await StartAsync(extra: new Dictionary<string, string?>
        {
            ["Deployment:Version"] = "0.4.0",
            ["Deployment:ImageDigest"] = "sha256:9f2c1d",
        });

        JsonElement body = await ReadJsonAsync(await probe.Client.GetAsync("/health"));

        body.GetProperty("version").GetString().Should().Be("0.4.0");
        body.GetProperty("digest").GetString().Should().Be("sha256:9f2c1d");
    }

    [Fact]
    public async Task TheProbeCarriesNothingWorthStealing()
    {
        // THIS REPOSITORY IS PUBLIC and the endpoint is the one thing on it reachable with no credential.
        // Both sentinels are configured on this host and neither may appear in the answer.
        await using Probe probe = await StartAsync();

        string body = await (await probe.Client.GetAsync("/health")).Content.ReadAsStringAsync();

        body.Should().NotContain(TokenSentinel);
        body.Should().NotContain("sentinel-db-host-b91c");
        body.Should().NotContain("sentinel-db-password-b91c");
    }

    [Fact]
    public async Task TheMcpEndpointStillRefuses_WithNoToken()
    {
        // The gate's first test. ADR-0007's 2026-08-22 update is what this is here to stop recurring.
        await using Probe probe = await StartAsync();

        using HttpResponseMessage response = await probe.Client.GetAsync("/mcp");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.Should().ContainSingle()
            .Which.Scheme.Should().Be("Bearer");
    }

    [Fact]
    public async Task TheMcpEndpointStillRefuses_WithTheWrongToken()
    {
        await using Probe probe = await StartAsync();

        using HttpRequestMessage request = new(HttpMethod.Get, "/mcp");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer not-the-configured-token");

        using HttpResponseMessage response = await probe.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("/healthz")]
    [InlineData("/health/anything")]
    [InlineData("/health/")]
    [InlineData("/Health")]
    [InlineData("/")]
    public async Task EveryOtherPathIsStillBehindTheGate(string path)
    {
        // One exact path, matched ordinally. A prefix match would hand `/health/anything` out unauthenticated,
        // and a case-insensitive one would make the allow-list a family rather than a path -- both of which
        // read as harmless tidying in a diff.
        await using Probe probe = await StartAsync();

        using HttpResponseMessage response = await probe.Client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ThePathIsNotAWayIn_ForAnyOtherMethod()
    {
        // A target-group probe is a GET. Anything else on this path is a request nobody has a reason to make
        // without a token, so the gate keeps it.
        await using Probe probe = await StartAsync();

        using HttpResponseMessage response = await probe.Client.PostAsync("/health", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
