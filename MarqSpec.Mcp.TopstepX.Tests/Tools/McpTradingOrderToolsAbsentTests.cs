using System.Reflection;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Tools;
using MarqSpec.Mcp.TopstepX.Venue;
using ModelContextProtocol.Server;

namespace MarqSpec.Mcp.TopstepX.Tests.Tools;

/// <summary>MCP order tools ship in gh#713/#715 — this branch must not register them (gh#740).</summary>
public sealed class McpTradingOrderToolsAbsentTests
{
    [Fact]
    public void RegisteredMcpTools_DoNotTakeExecutionWriteDependencies()
    {
        Assembly assembly = typeof(ToolPayloads).Assembly;
        IEnumerable<Type> toolTypes = assembly
            .GetTypes()
            .Where(type => type.GetCustomAttribute<McpServerToolTypeAttribute>() is not null);

        foreach (Type toolType in toolTypes)
        {
            foreach (ConstructorInfo ctor in toolType.GetConstructors())
            {
                foreach (ParameterInfo parameter in ctor.GetParameters())
                {
                    parameter.ParameterType.Should().NotBe(
                        typeof(TradingRiskGate),
                        because: $"{toolType.Name} must not expose venue writes on the MCP surface");
                    parameter.ParameterType.Should().NotBe(
                        typeof(IExecutionGateway),
                        because: $"{toolType.Name} must not expose venue writes on the MCP surface");
                }
            }
        }
    }

    [Fact]
    public void RegisteredMcpToolMethods_AreReadOnlyOrLocalDatabaseOnly()
    {
        Assembly assembly = typeof(ToolPayloads).Assembly;
        IEnumerable<MethodInfo> toolMethods = assembly
            .GetTypes()
            .Where(type => type.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null);

        foreach (MethodInfo method in toolMethods)
        {
            McpServerToolAttribute attribute = method.GetCustomAttribute<McpServerToolAttribute>()!;
            if (method.DeclaringType == typeof(ObservationTools))
            {
                continue;
            }

            attribute.ReadOnly.Should().BeTrue(because: $"{method.DeclaringType!.Name}.{method.Name} must not transmit to the venue");
        }
    }
}
