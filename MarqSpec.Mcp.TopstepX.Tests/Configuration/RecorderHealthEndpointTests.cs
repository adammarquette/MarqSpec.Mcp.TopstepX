using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace MarqSpec.Mcp.TopstepX.Tests.Configuration;

/// <summary>
/// <c>GET /health/recorder</c> is the monitor's copy of the tool, and it stays behind the gate (gh#756).
/// </summary>
public sealed class RecorderHealthEndpointTests
{
    private const string TokenSentinel = "sentinel-bearer-token-3a7f9c";
    private const string PasswordSentinel = "sentinel-db-password-b91c";
    private const string Es = "CON.F.US.EP.U26";

    private static readonly DateTimeOffset _tuesday =
        MarketClock.FromMarket(new DateOnly(2026, 8, 18), new TimeOnly(9, 30)).ToUniversalTime();

    private static readonly DateTimeOffset _open =
        MarketClock.FromMarket(new DateOnly(2026, 8, 17), new TimeOnly(17, 0)).ToUniversalTime();

    private static readonly Dictionary<string, string?> _baseSettings = new()
    {
        ["ConnectionStrings:Default"] = "Host=sentinel-db-host-b91c;Port=5432;Database=topstepx_mcp;"
            + "Username=topstepx;Password=" + PasswordSentinel,
        ["MarketData:Instruments"] = "ES,NQ",
        ["MarketData:SessionCloseCentral"] = "16:00",
        ["MarketData:MaxRows"] = "5000",
        ["Mcp:Transport"] = "Http",
        ["Mcp:HttpBearerToken"] = TokenSentinel,
    };

    [Fact]
    public async Task Get_WithoutAToken_IsUnauthorized()
    {
        await using Probe probe = await StartAsync();

        using HttpResponseMessage response = await probe.Client.GetAsync(RecorderHealthEndpoint.Path);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.Should().ContainSingle()
            .Which.Scheme.Should().Be("Bearer");
    }

    [Fact]
    public async Task Get_WithTheWrongToken_IsUnauthorized()
    {
        await using Probe probe = await StartAsync();
        using HttpRequestMessage request = new(HttpMethod.Get, RecorderHealthEndpoint.Path);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer not-the-configured-token");

        using HttpResponseMessage response = await probe.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Get_WithTheBearerToken_ReturnsTheRecorderReport()
    {
        await using Probe probe = await StartAsync();
        using HttpRequestMessage request = Authed();

        using HttpResponseMessage response = await probe.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        JsonElement body = await ReadJsonAsync(response);
        body.GetProperty("feed").GetString().Should().Be("ConnectedButNotSubscribed");
        body.GetProperty("feedConnected").GetBoolean().Should().BeTrue();
        body.GetProperty("feed").ValueKind.Should().Be(JsonValueKind.String);
        body.GetProperty("database").GetString().Should().Be("Available");
        JsonElement es = body.GetProperty("contracts").EnumerateArray()
            .Single(row => row.GetProperty("symbol").GetString() == "ES");
        es.GetProperty("lastTickUtc").GetDateTimeOffset().Should().Be(_tuesday);
        JsonElement nq = body.GetProperty("contracts").EnumerateArray()
            .Single(row => row.GetProperty("symbol").GetString() == "NQ");
        nq.GetProperty("listening").GetBoolean().Should().BeTrue();
        nq.TryGetProperty("lastTickUtc", out _).Should().BeFalse();
        body.GetProperty("gaps").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Get_WithTheBearerToken_DoesNotEchoTheTokenOrTheConnectionTarget()
    {
        await using Probe probe = await StartAsync();

        string body = await (await probe.Client.SendAsync(Authed())).Content.ReadAsStringAsync();

        body.Should().NotContain(TokenSentinel);
        body.Should().NotContain(PasswordSentinel);
        body.Should().NotContain("sentinel-db-host-b91c");
    }

    [Fact]
    public async Task Get_WhenTheFeedIsDown_StillReturnsOk()
    {
        await using Probe probe = await StartAsync(feedDown: true);

        using HttpResponseMessage response = await probe.Client.SendAsync(Authed());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(response)).GetProperty("feedConnected").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Get_WhenTheProbeFails_ReportsUnavailableWithoutTheConnectionTarget()
    {
        await using Probe probe = await StartAsync(probeFails: true);

        using HttpResponseMessage response = await probe.Client.SendAsync(Authed());
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement report = JsonDocument.Parse(body).RootElement;
        report.GetProperty("database").GetString().Should().Be("Unavailable");
        report.TryGetProperty("gaps", out _).Should().BeFalse();
        body.Should().NotContain(PasswordSentinel);
        body.Should().NotContain("sentinel-db-host-b91c");

        using HttpResponseMessage liveness = await probe.Client.GetAsync(HealthEndpoint.Path);
        (await ReadJsonAsync(liveness)).GetProperty("store").GetString().Should().Be("available");
    }

    [Fact]
    public async Task Get_LeavesTheLivenessProbeUnauthenticated()
    {
        await using Probe probe = await StartAsync();

        using HttpResponseMessage response = await probe.Client.GetAsync(HealthEndpoint.Path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(response)).GetProperty("status").GetString().Should().Be("ok");
    }

    private static HttpRequestMessage Authed()
    {
        HttpRequestMessage request = new(HttpMethod.Get, RecorderHealthEndpoint.Path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TokenSentinel);
        return request;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task<Probe> StartAsync(bool feedDown = false, bool probeFails = false)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(_baseSettings);

        McpOptions mcp = builder.Configuration.GetSection(McpOptions.SectionName).Get<McpOptions>()!;
        Program.ConfigureServices(builder, mcp);

        // The recorder writes the tape holder from its own lifecycle. Left running, that write lands after
        // the test publishes a connection and the report reads the recorder's answer instead.
        foreach (ServiceDescriptor descriptor in builder.Services
            .Where(candidate => candidate.ServiceType == typeof(IHostedService)
                && candidate.ImplementationType == typeof(TradeTapeRecorder))
            .ToList())
        {
            builder.Services.Remove(descriptor);
        }

        builder.Services.RemoveAll<TimeProvider>();
        builder.Services.AddSingleton<TimeProvider>(new FakeTimeProvider(_tuesday));
        builder.Services.RemoveAll<IRecorderHealthStore>();
        builder.Services.AddSingleton<IRecorderHealthStore>(new ScriptedStore(probeFails));

        WebApplication app = builder.Build();
        app.Services.GetRequiredService<StoreAvailabilityHolder>().Set(StoreAvailability.Available());

        Program.MapHttpTransport(app, mcp);
        await app.StartAsync();

        // The recorder's startup writes NeverStarted when RecordTape is off, and that write lands during
        // StartAsync. Publishing the test's answer first would be overwritten before the request.
        TapeAvailabilityHolder tape = app.Services.GetRequiredService<TapeAvailabilityHolder>();
        if (feedDown)
        {
            tape.Set(TapeAvailability.Reconnecting());
        }
        else
        {
            tape.Set(TapeAvailability.ConnectedButNotSubscribed());
            tape.Set("ES", TapeAvailability.Listening());
            tape.Set("NQ", TapeAvailability.Listening());
        }

        return new Probe(app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) });
    }

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

    private sealed class ScriptedStore(bool probeFails) : IRecorderHealthStore
    {
        public Task<bool> DatabaseConnectedAsync(CancellationToken cancellationToken)
        {
            if (probeFails)
            {
                throw new InvalidOperationException(
                    "Host=sentinel-db-host-b91c;Password=" + PasswordSentinel);
            }

            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<ContractLastTick>> LastTicksAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ContractLastTick>>([new ContractLastTick("ES", Es, _tuesday)]);

        public Task<IReadOnlyList<CoverageSlice>> CoverageAsync(
            DateTimeOffset windowStart,
            DateTimeOffset windowEnd,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CoverageSlice>>(
            [
                new CoverageSlice("ES", Es, _open, TapeCoverageRecord.StillListeningEnd),
                new CoverageSlice("NQ", "CON.F.US.ENQ.U26", _open, TapeCoverageRecord.StillListeningEnd),
            ]);
    }
}
