using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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

        Program.MapHttpTransport(app, mcp);

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
    public async Task TheProbeReportsTheReleaseVersion_AndAnUnknownDigest_WhenTheDeploymentStampedNothing()
    {
        // version is the assembly informational version (gh#867). The digest still has nowhere to
        // come from but the deployment, so an unset one stays unknown.
        await using Probe probe = await StartAsync();

        JsonElement body = await ReadJsonAsync(await probe.Client.GetAsync("/health"));

        body.GetProperty("version").GetString().Should().Be(ProductVersion.Read());
        body.GetProperty("version").GetString().Should().NotBe("0.0.0.0");
        body.GetProperty("digest").GetString().Should().Be("unknown");
    }

    [Fact]
    public async Task TheProbeReportsTheDigestTheDeploymentStamped_AndNotThatStampAsTheVersion()
    {
        // A Deployment__Version beside the process does not replace the assembly stamp (gh#867).
        // The digest still comes from the deployment, because the assembly does not carry one.
        await using Probe probe = await StartAsync(extra: new Dictionary<string, string?>
        {
            ["Deployment:Version"] = "0.4.0",
            ["Deployment:ImageDigest"] = "sha256:9f2c1d",
        });

        JsonElement body = await ReadJsonAsync(await probe.Client.GetAsync("/health"));

        body.GetProperty("version").GetString().Should().Be(ProductVersion.Read());
        body.GetProperty("version").GetString().Should().NotBe("0.4.0");
        body.GetProperty("digest").GetString().Should().Be("sha256:9f2c1d");
    }

    [Fact]
    public async Task InitializeReportsTheSameVersionHealthDoes_AndNotTheUnstampedAssembly()
    {
        // gh#867. A production initialize answered serverInfo.version 0.0.0.0 while /health
        // answered the release. The SDK fills serverInfo from AssemblyVersion when nothing
        // sets it, and an image built without git history stamps that 0.0.0.0. The two
        // answers are one number, and that number is not the unstamped assembly.
        await using Probe probe = await StartAsync();

        string? healthVersion = (await ReadJsonAsync(await probe.Client.GetAsync("/health")))
            .GetProperty("version").GetString();

        using HttpRequestMessage initialize = new(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{"
                + "\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},"
                + "\"clientInfo\":{\"name\":\"version-test\",\"version\":\"1.0\"}}}",
                Encoding.UTF8,
                "application/json"),
        };
        initialize.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        initialize.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        initialize.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TokenSentinel);

        using HttpResponseMessage response = await probe.Client.SendAsync(initialize);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string? serverVersion = ServerInfoVersion(await response.Content.ReadAsStringAsync());

        serverVersion.Should().Be(healthVersion);
        serverVersion.Should().NotBe("0.0.0.0");
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

    [Fact]
    public void IsTraced_WhenThePathIsTheProbe_DoesNotTraceIt()
    {
        DefaultHttpContext context = new();
        context.Request.Path = HealthEndpoint.Path;

        Program.IsTraced(context).Should().BeFalse();
        HealthEndpoint.IsProbePath(context.Request.Path).Should().BeTrue();
    }

    [Fact]
    public void IsTraced_WhenTheProbePathDiffersByCase_StillTracesIt()
    {
        DefaultHttpContext context = new();
        context.Request.Path = "/Health";

        HealthEndpoint.IsProbePath(context.Request.Path).Should().BeFalse();
        Program.IsTraced(context).Should().BeTrue();
    }

    /// <summary>
    /// The <c>serverInfo.version</c> from an initialize body, whether the transport wrote JSON or an SSE
    /// <c>data:</c> frame.
    /// </summary>
    private static string? ServerInfoVersion(string body)
    {
        foreach (string candidate in JsonCandidates(body))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(candidate);
                if (document.RootElement.TryGetProperty("result", out JsonElement result)
                    && result.TryGetProperty("serverInfo", out JsonElement serverInfo)
                    && serverInfo.TryGetProperty("version", out JsonElement version))
                {
                    return version.GetString();
                }
            }
            catch (JsonException)
            {
                // A leading SSE event line is not the frame.
            }
        }

        return null;
    }

    private static IEnumerable<string> JsonCandidates(string body)
    {
        yield return body.Trim();

        foreach (string line in body.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("data:", StringComparison.Ordinal))
            {
                yield return trimmed["data:".Length..].Trim();
            }
        }
    }
}
