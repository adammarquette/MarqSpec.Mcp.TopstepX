using System.IO.Pipelines;
using System.Text.Json;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
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
/// tools/list and tools/call for the five order tools, over a real MCP pipe, with trading left disabled.
/// </summary>
[Trait("Gate", "ADR-0024")]
public sealed class TradingOrderToolsPipelineTests
{
    private static readonly Dictionary<string, string?> _settings = new()
    {
        ["ConnectionStrings:Default"] = "Host=localhost;Database=x;Username=u;Password=p",
        ["MarketData:Instruments"] = "ES,NQ,MES",
        ["MarketData:SessionCloseCentral"] = "16:00",
        ["MarketData:MaxRows"] = "5000",
    };

    [Fact]
    public async Task TradingRiskGate_OrderTools_ListAndCall_OverTheMcpPipeline()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        using ServiceProvider provider = Build();
        McpServerOptions configured = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;

        McpServerOptions options = new()
        {
            ServerInfo = configured.ServerInfo ?? new Implementation { Name = "orders", Version = "0" },
            ToolCollection = configured.ToolCollection,
        };
        foreach (McpRequestFilter<CallToolRequestParams, CallToolResult> filter in
            configured.Filters.Request.CallToolFilters)
        {
            options.Filters.Request.CallToolFilters.Add(filter);
        }

        Pipe toServer = new();
        Pipe toClient = new();
        await using McpServer server = McpServer.Create(
            new StreamServerTransport(
                toServer.Reader.AsStream(), toClient.Writer.AsStream(), "orders", NullLoggerFactory.Instance),
            options,
            NullLoggerFactory.Instance,
            provider);
        Task running = server.RunAsync(timeout.Token);

        await using McpClient client = await McpClient.CreateAsync(
            new StreamClientTransport(
                toServer.Writer.AsStream(), toClient.Reader.AsStream(), NullLoggerFactory.Instance),
            cancellationToken: timeout.Token);

        IList<McpClientTool> listed = await client.ListToolsAsync(cancellationToken: timeout.Token);
        listed.Select(tool => tool.Name).Should().Contain(
        [
            TradingMcpTools.PlaceOrderOperation,
            TradingMcpTools.ModifyOrderOperation,
            TradingMcpTools.CancelOrderOperation,
            TradingMcpTools.ClosePositionOperation,
            TradingMcpTools.PartialClosePositionOperation,
        ]);

        McpClientTool place = listed.Should().ContainSingle(tool => tool.Name == TradingMcpTools.PlaceOrderOperation).Subject;
        place.Description.Should().Contain("live practice account");
        place.Description.Should().Contain("never retry OutcomeUnknown");

        CallToolResult called = await client.CallToolAsync(
            TradingMcpTools.PlaceOrderOperation,
            new Dictionary<string, object?>
            {
                ["accountId"] = 1,
                ["contractId"] = "CON.F.US.MES.Z26",
                ["orderType"] = "Market",
                ["side"] = "Buy",
                ["size"] = 1,
                ["idempotencyKey"] = "pipeline-key",
            },
            cancellationToken: timeout.Token);

        called.IsError.Should().NotBe(true);
        string body = string.Join(" ", called.Content.OfType<TextContentBlock>().Select(block => block.Text));
        using JsonDocument payload = JsonDocument.Parse(body);
        payload.RootElement.GetProperty("refusalCode").GetString().Should().Be("trading_disabled");
        payload.RootElement.GetProperty("retrySafety").GetString().Should().Be("SafeToRetry");
        payload.RootElement.GetProperty("status").GetString().Should().Be("Refused");

        await server.DisposeAsync();
        await running;
    }

    private static ServiceProvider Build()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(_settings);
        Program.ConfigureServices(builder, new McpOptions { Transport = McpTransport.Stdio });
        return builder.Services.BuildServiceProvider();
    }
}
