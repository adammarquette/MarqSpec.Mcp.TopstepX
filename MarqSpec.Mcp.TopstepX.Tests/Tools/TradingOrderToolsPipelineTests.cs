using System.IO.Pipelines;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Tools;
using MarqSpec.Mcp.TopstepX.Venue;
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
/// tools/list for the five order tools, over a real MCP pipe. Disabled servers omit them. Enabled
/// servers list them. The in-tool refusal is the second layer and is called directly.
/// </summary>
[Trait("Gate", "ADR-0024")]
public sealed class TradingOrderToolsPipelineTests
{
    private static readonly string[] _orderTools =
    [
        TradingMcpTools.PlaceOrderOperation,
        TradingMcpTools.ModifyOrderOperation,
        TradingMcpTools.CancelOrderOperation,
        TradingMcpTools.ClosePositionOperation,
        TradingMcpTools.PartialClosePositionOperation,
    ];

    private static readonly Dictionary<string, string?> _settings = new()
    {
        ["ConnectionStrings:Default"] = "Host=localhost;Database=x;Username=u;Password=p",
        ["MarketData:Instruments"] = "ES,NQ,MES",
        ["MarketData:SessionCloseCentral"] = "16:00",
        ["MarketData:MaxRows"] = "5000",
    };

    [Fact]
    public async Task TradingRiskGate_OrderTools_AreAbsentFromTheList_WhenTradingIsDisabled()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        using ServiceProvider provider = Build(tradingEnabled: false);
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
        listed.Select(tool => tool.Name).Intersect(_orderTools).Should().BeEmpty();

        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        TradingOrderTools tools = scope.ServiceProvider.GetRequiredService<TradingOrderTools>();
        ToolPayloads.TradingWriteResult refused = await tools.SubmitPlace(
            1,
            "CON.F.US.MES.Z26",
            ExecutionOrderType.Market,
            VenueSide.Buy,
            1,
            "pipeline-key");
        refused.RefusalCode.Should().Be("trading_disabled");
        refused.RetrySafety.Should().Be(ToolPayloads.TradingRetrySafety.SafeToRetry);
        refused.Status.Should().Be("Refused");

        await server.DisposeAsync();
        await running;
    }

    [Fact]
    public async Task TradingRiskGate_OrderTools_AreListed_WhenTradingIsEnabled()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        using ServiceProvider provider = Build(tradingEnabled: true);
        McpServerOptions configured = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;

        McpServerOptions options = new()
        {
            ServerInfo = configured.ServerInfo ?? new Implementation { Name = "orders", Version = "0" },
            ToolCollection = configured.ToolCollection,
        };

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
        listed.Select(tool => tool.Name).Should().Contain(_orderTools);

        McpClientTool place = listed.Should().ContainSingle(tool => tool.Name == TradingMcpTools.PlaceOrderOperation).Subject;
        place.Description.Should().Contain("the stakes are real");
        place.Description.Should().Contain("get_orders");
        place.Description.Should().Contain("get_positions");
        place.Description.Should().Contain("get_trades");
        place.Description.Should().NotContain("list_open_orders");
        place.Description.Should().NotContain("list_positions");

        await server.DisposeAsync();
        await running;
    }

    private static ServiceProvider Build(bool tradingEnabled)
    {
        Dictionary<string, string?> settings = new(_settings);
        if (tradingEnabled)
        {
            settings["Trading:Enabled"] = "true";
            settings["Trading:AllowedAccountIds"] = "1";
        }

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(settings);
        Program.ConfigureServices(builder, new McpOptions { Transport = McpTransport.Stdio });
        return builder.Services.BuildServiceProvider();
    }
}
