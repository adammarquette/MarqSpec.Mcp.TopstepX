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
/// The RFC 9728 protected-resource metadata: two exact paths, unauthenticated, answering with the URL exactly
/// as it was configured.
/// </summary>
/// <remarks>
/// <para>
/// <b>The connector reads this before it has a token</b>, following the <c>resource_metadata</c> the 401
/// names, so it cannot sit behind the gate. Like <c>/health</c> it is a terminal branch in front of the
/// gate — one method, exact paths, ordinal — and like <c>/health</c> the negatives are pinned because a
/// prefix or case-insensitive match reads as tidying in a diff.
/// </para>
/// <para>
/// <b><c>resource</c> is the string that was configured, not a normalised form of it.</b> Anthropic's
/// connector documentation says the value must match the URL the user entered, path included; a
/// <c>Uri</c> round trip lowercases the host and drops a default port, and either is a mismatch a client is
/// entitled to refuse. The value goes out byte for byte.
/// </para>
/// </remarks>
public sealed class ProtectedResourceMetadataTests
{
    private const string Issuer = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_StubPool";
    private const string ConnectionSentinel =
        "Host=sentinel-db-host-b91c;Port=5432;Database=topstepx_mcp;Username=topstepx;"
        + "Password=sentinel-db-password-b91c";

    private sealed class Host(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private static async Task<Host> StartAsync(Dictionary<string, string?> auth)
    {
        Dictionary<string, string?> settings = new()
        {
            ["ConnectionStrings:Default"] = ConnectionSentinel,
            ["MarketData:Instruments"] = "ES,NQ",
            ["MarketData:SessionCloseCentral"] = "16:00",
            ["MarketData:MaxRows"] = "5000",
            ["Mcp:Transport"] = "Http",
        };

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(settings.Concat(auth));

        McpOptions mcp = builder.Configuration.GetSection(McpOptions.SectionName).Get<McpOptions>()!;
        Program.ConfigureServices(builder, mcp);

        WebApplication app = builder.Build();
        app.Services.GetRequiredService<StoreAvailabilityHolder>().Set(StoreAvailability.Available());
        Program.MapHttpTransport(app, mcp);
        await app.StartAsync();

        return new Host(app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) });
    }

    private static Task<Host> StartOAuthAsync(string resourceUrl = "https://topstepx-mcp.example.test/mcp")
        => StartAsync(new Dictionary<string, string?>
        {
            ["Mcp:Auth:Mode"] = "OAuth",
            ["Mcp:OAuth:Issuer"] = Issuer,
            ["Mcp:OAuth:ClientIds"] = "connector-client",
            ["Mcp:OAuth:ResourceUrl"] = resourceUrl,
        });

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    [Theory]
    [InlineData("/.well-known/oauth-protected-resource")]
    [InlineData("/.well-known/oauth-protected-resource/mcp")]
    public async Task BothMetadataPaths_AnswerWithNoCredential(string path)
    {
        await using Host host = await StartOAuthAsync();

        using HttpResponseMessage response = await host.Client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");

        JsonElement body = await ReadJsonAsync(response);
        body.GetProperty("resource").GetString().Should().Be("https://topstepx-mcp.example.test/mcp");
        body.GetProperty("authorization_servers").EnumerateArray().Select(e => e.GetString())
            .Should().Equal(Issuer);
        body.GetProperty("scopes_supported").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("topstepx-mcp/read");
        body.GetProperty("bearer_methods_supported").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("header");
    }

    [Fact]
    public async Task TheResource_IsTheConfiguredStringByteForByte()
    {
        // A port that Uri would keep and a host casing it would not. What was typed is what goes out.
        const string entered = "https://TopstepX-MCP.Staging.marqspec.com:8443/mcp";
        await using Host host = await StartOAuthAsync(entered);

        JsonElement body = await ReadJsonAsync(await host.Client.GetAsync("/.well-known/oauth-protected-resource/mcp"));

        body.GetProperty("resource").GetString().Should().Be(entered);
    }

    [Fact]
    public async Task TheChallenge_NamesTheMetadataDocumentUnderTheSameOrigin_AsEntered()
    {
        const string entered = "https://TopstepX-MCP.Staging.marqspec.com:8443/mcp";
        await using Host host = await StartOAuthAsync(entered);

        using HttpResponseMessage response = await host.Client.GetAsync("/mcp");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.GetValues("WWW-Authenticate").Should().ContainSingle().Which.Should().Be(
            "Bearer resource_metadata=\"https://TopstepX-MCP.Staging.marqspec.com:8443/.well-known/oauth-protected-resource/mcp\", "
            + "scope=\"topstepx-mcp/read\"");
    }

    [Fact]
    public async Task AConfiguredScope_ReachesBothTheDocumentAndTheChallenge()
    {
        await using Host host = await StartAsync(new Dictionary<string, string?>
        {
            ["Mcp:Auth:Mode"] = "OAuth",
            ["Mcp:OAuth:Issuer"] = Issuer,
            ["Mcp:OAuth:ClientIds"] = "connector-client",
            ["Mcp:OAuth:ResourceUrl"] = "https://topstepx-mcp.example.test/mcp",
            ["Mcp:OAuth:RequiredScope"] = "topstepx-mcp/read-staging",
        });

        JsonElement body = await ReadJsonAsync(await host.Client.GetAsync("/.well-known/oauth-protected-resource"));
        body.GetProperty("scopes_supported").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("topstepx-mcp/read-staging");

        using HttpResponseMessage refused = await host.Client.GetAsync("/mcp");
        refused.Headers.GetValues("WWW-Authenticate").Single().Should().EndWith("scope=\"topstepx-mcp/read-staging\"");
    }

    [Theory]
    [InlineData("/.well-known/oauth-protected-resource/")]
    [InlineData("/.well-known/oauth-protected-resource/mcp/")]
    [InlineData("/.well-known/oauth-protected-resource/other")]
    [InlineData("/.well-known/OAuth-Protected-Resource")]
    [InlineData("/.well-known/oauth-protected-resource/MCP")]
    [InlineData("/.well-known/oauth-authorization-server")]
    [InlineData("/.well-known")]
    public async Task EveryNeighbouringPath_IsStillBehindTheGate(string path)
    {
        await using Host host = await StartOAuthAsync();

        using HttpResponseMessage response = await host.Client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TheMetadataPath_IsNotAWayIn_ForAnyOtherMethod()
    {
        await using Host host = await StartOAuthAsync();

        using HttpResponseMessage response = await host.Client.PostAsync("/.well-known/oauth-protected-resource/mcp", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TheDocument_CarriesNothingWorthStealing()
    {
        // Reachable with no credential, in a public repository: the connection string is configured on
        // this host and must not be in the answer.
        await using Host host = await StartOAuthAsync();

        string body = await (await host.Client.GetAsync("/.well-known/oauth-protected-resource/mcp")).Content.ReadAsStringAsync();

        body.Should().NotContain("sentinel-db-host-b91c");
        body.Should().NotContain("sentinel-db-password-b91c");
    }

    [Theory]
    [InlineData("/.well-known/oauth-protected-resource")]
    [InlineData("/.well-known/oauth-protected-resource/mcp")]
    public async Task UnderTheStaticMode_TheMetadataPathsDoNotExist(string path)
    {
        // The static mode is byte for byte what it was: no metadata, and a bare `Bearer` challenge.
        await using Host host = await StartAsync(new Dictionary<string, string?>
        {
            ["Mcp:HttpBearerToken"] = "sentinel-bearer-token-3a7f9c",
        });

        using HttpResponseMessage response = await host.Client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.GetValues("WWW-Authenticate").Should().ContainSingle().Which.Should().Be("Bearer");
    }
}
