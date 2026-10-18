using System.Reflection;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Tools;
using ModelContextProtocol.Server;

namespace MarqSpec.Mcp.TopstepX.Tests.Tools;

/// <summary>MCP order tools ship in gh#713/#715 — this branch must not register them (gh#740).</summary>
public sealed class McpTradingOrderToolsAbsentTests
{
    [Fact]
    public void Assembly_DoesNotRegisterTradingOrderTools()
    {
        typeof(ToolPayloads).Assembly
            .GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .Select(t => t.Name)
            .Should()
            .NotContain("TradingOrderTools");

        typeof(ToolPayloads)
            .GetNestedTypes()
            .Select(t => t.Name)
            .Should()
            .NotContain("TradingWriteResult");
    }
}
