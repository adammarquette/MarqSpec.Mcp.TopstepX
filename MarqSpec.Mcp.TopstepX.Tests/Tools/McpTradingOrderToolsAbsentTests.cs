using System.Reflection;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Tools;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;

namespace MarqSpec.Mcp.TopstepX.Tests.Tools;

/// <summary>
/// Order tools ship in gh#713/#715. The composition root must not register them (gh#740, gh#751).
/// </summary>
public sealed class McpTradingOrderToolsAbsentTests
{
    [Theory]
    [InlineData(TradingMcpTools.PlaceOrderOperation)]
    [InlineData(TradingMcpTools.ModifyOrderOperation)]
    [InlineData(TradingMcpTools.CancelOrderOperation)]
    [InlineData(TradingMcpTools.ClosePositionOperation)]
    [InlineData(TradingMcpTools.PartialClosePositionOperation)]
    public void OrderToolNames_AreRejected(string toolName) =>
        IsOrderToolName(toolName).Should().BeTrue();

    [Fact]
    public void RecordObservation_IsNotAnOrderToolName() =>
        IsOrderToolName("record_observation").Should().BeFalse();

    [Fact]
    public void RegisteredMcpTools_DoNotIncludeOrderTools()
    {
        using ServiceProvider provider = Build();

        List<McpServerTool> tools = [.. provider.GetServices<McpServerTool>()];
        tools.Should().NotBeEmpty();

        McpServerTool observation = tools.Should()
            .ContainSingle(tool => tool.ProtocolTool.Name == "record_observation")
            .Subject;
        MethodInfo record = RegisteredMethod(observation);
        record.DeclaringType.Should().Be(typeof(ObservationTools));
        record.Name.Should().Be(nameof(ObservationTools.RecordObservation));

        foreach (McpServerTool tool in tools)
        {
            IsOrderToolName(tool.ProtocolTool.Name).Should().BeFalse(
                because: $"{tool.ProtocolTool.Name} must not be registered on the MCP server");

            MethodInfo method = RegisteredMethod(tool);
            foreach (ConstructorInfo ctor in method.DeclaringType!.GetConstructors())
            {
                foreach (ParameterInfo parameter in ctor.GetParameters())
                {
                    parameter.ParameterType.Should().NotBe(
                        typeof(TradingRiskGate),
                        because: $"{method.DeclaringType.Name}.{method.Name} must not expose venue writes");
                    parameter.ParameterType.Should().NotBe(
                        typeof(IExecutionGateway),
                        because: $"{method.DeclaringType.Name}.{method.Name} must not expose venue writes");
                }
            }
        }
    }

    private static MethodInfo RegisteredMethod(McpServerTool tool) =>
        tool.Metadata.OfType<MethodInfo>().Should().ContainSingle(
            because: $"{tool.ProtocolTool.Name} must expose the method the server registered").Subject;

    private static bool IsOrderToolName(string toolName) =>
        toolName is TradingMcpTools.PlaceOrderOperation
            or TradingMcpTools.ModifyOrderOperation
            or TradingMcpTools.CancelOrderOperation
            or TradingMcpTools.ClosePositionOperation
            or TradingMcpTools.PartialClosePositionOperation;

    private static ServiceProvider Build()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = "Host=localhost;Database=x;Username=u;Password=p",
            ["MarketData:Instruments"] = "ES,NQ",
            ["MarketData:SessionCloseCentral"] = "16:00",
            ["MarketData:MaxRows"] = "5000",
        });
        Program.ConfigureServices(builder, new McpOptions { Transport = McpTransport.Stdio });
        return builder.Services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }
}
