using System.IO.Pipelines;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MarqSpec.Mcp.TopstepX.Tests.Tools;

/// <summary>
/// An argument the tool does not accept must come back named, with the arguments the tool does accept.
/// </summary>
/// <remarks>
/// The production call sent <c>unit</c>, <c>unitNumber</c> and <c>limit</c> to <c>get_latest_bars</c> and
/// received only <i>"An error occurred invoking get_latest_bars"</i> (gh#984). The SDK keeps a non-
/// <see cref="ModelContextProtocol.McpException"/> message off the wire, so the refusal has to be one.
/// </remarks>
public sealed class UnknownToolArgumentTests
{
    private static readonly Dictionary<string, string?> _settings = new()
    {
        ["ConnectionStrings:Default"] = "Host=localhost;Database=x;Username=u;Password=p",
        ["MarketData:Instruments"] = "ES,NQ",
        ["MarketData:SessionCloseCentral"] = "16:00",
        ["MarketData:MaxRows"] = "5000",
    };

    [Fact]
    public async Task GetLatestBars_NamesTheUnknownArgumentsAndTheAllowedOnes()
    {
        string reported = await CallAsync(
            [McpServerTool.Create(
                GetLatestBars,
                new McpServerToolCreateOptions { Name = "get_latest_bars" })],
            "get_latest_bars",
            new Dictionary<string, object?>
            {
                ["unit"] = "Minute",
                ["unitNumber"] = 1,
                ["limit"] = 10,
            });

        reported.Should().Contain(
            "Unexpected arguments 'unit', 'unitNumber', 'limit'. "
            + "Allowed arguments: 'symbol', 'resolutionMinutes', 'count'.");
        reported.Should().NotContain(
            "RAN:",
            "a call with arguments the tool does not accept must not run the tool");
    }

    [Fact]
    public async Task AValidCall_StillRuns()
    {
        string reported = await CallAsync(
            [McpServerTool.Create(
                GetLatestBars,
                new McpServerToolCreateOptions { Name = "get_latest_bars" })],
            "get_latest_bars",
            new Dictionary<string, object?>
            {
                ["symbol"] = "ES",
                ["resolutionMinutes"] = 5,
                ["count"] = 3,
            });

        reported.Should().Be("RAN:ES:5:3");
    }

    [Fact]
    public async Task ADifferentTool_NamesItsOwnArguments()
    {
        string reported = await CallAsync(
            [McpServerTool.Create(
                Session,
                new McpServerToolCreateOptions { Name = "get_market_session" })],
            "get_market_session",
            new Dictionary<string, object?> { ["symbol"] = "ES" });

        reported.Should().Contain(
            "Unexpected argument 'symbol'. Allowed arguments: 'session'.");
        reported.Should().NotContain("resolutionMinutes");
        reported.Should().NotContain("RAN:");
    }

    [Fact]
    public async Task AToolThatAcceptsNothing_SaysSo()
    {
        string reported = await CallAsync(
            [McpServerTool.Create(Ping, new McpServerToolCreateOptions { Name = "ping" })],
            "ping",
            new Dictionary<string, object?> { ["limit"] = 1 });

        reported.Should().Contain("Unexpected argument 'limit'. This tool accepts no arguments.");
        reported.Should().NotContain("PONG");
    }

    private static string GetLatestBars(
        string symbol,
        int resolutionMinutes,
        int count,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return "RAN:" + symbol + ":" + resolutionMinutes + ":" + count;
    }

    private static string Session(string session, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return "RAN:" + session;
    }

    private static string Ping() => "PONG";

    private static async Task<string> CallAsync(
        IEnumerable<McpServerTool> tools,
        string name,
        Dictionary<string, object?> arguments)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));

        Pipe toServer = new();
        Pipe toClient = new();

        McpServerOptions options = new()
        {
            ServerInfo = new Implementation { Name = "gh984", Version = "0.0.0" },
            ToolCollection = [.. tools],
        };

        using ServiceProvider provider = Build();
        foreach (McpRequestFilter<CallToolRequestParams, CallToolResult> filter in
            provider.GetRequiredService<IOptions<McpServerOptions>>().Value.Filters.Request.CallToolFilters)
        {
            options.Filters.Request.CallToolFilters.Add(filter);
        }

        await using McpServer server = McpServer.Create(
            new StreamServerTransport(
                toServer.Reader.AsStream(), toClient.Writer.AsStream(), "gh984", NullLoggerFactory.Instance),
            options,
            NullLoggerFactory.Instance,
            provider);

        Task running = server.RunAsync(CancellationToken.None);

        await using McpClient client = await McpClient.CreateAsync(
            new StreamClientTransport(
                toServer.Writer.AsStream(), toClient.Reader.AsStream(), NullLoggerFactory.Instance),
            cancellationToken: timeout.Token);

        CallToolResult result = await client.CallToolAsync(
            name, arguments, cancellationToken: timeout.Token);

        return string.Join(" ", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
    }

    private static ServiceProvider Build()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(_settings);
        Program.ConfigureServices(builder, new McpOptions { Transport = McpTransport.Stdio });
        return builder.Services.BuildServiceProvider();
    }
}
