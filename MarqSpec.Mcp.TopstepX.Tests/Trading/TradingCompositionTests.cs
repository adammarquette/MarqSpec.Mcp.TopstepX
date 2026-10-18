// gh#720 Tier A: what the real composition root (PR #734 Program.ConfigureServices) wires, with synthetic config.
using FluentAssertions;
using MarqSpec.Mcp.TopstepX;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.Mcp.TopstepX.Tests.Trading;

[Trait("Issue", "gh#720")]
[Trait("Tier", "A-composition")]
public sealed class TradingCompositionTests
{
    [Fact(DisplayName = "TP-720-A12 without venue credentials the execution gateway refuses every verb")]
    public async Task No_credentials_resolves_the_unconfigured_gateway()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Configuration["Trading:Enabled"] = "true";
        Program.ConfigureServices(builder, new McpOptions { Transport = McpTransport.Stdio });
        await using WebApplication app = builder.Build();
        using IServiceScope scope = app.Services.CreateScope();

        IExecutionGateway gateway = scope.ServiceProvider.GetRequiredService<IExecutionGateway>();
        gateway.Should().BeOfType<UnconfiguredExecutionGateway>();
        ExecutionWriteResult r = await gateway.PlaceOrder(GatewayRig.Limit(SyntheticAccounts.PracticeId), default);
        r.Lifecycle.Should().Be(ExecutionWriteLifecycle.NotTransmitted);
        r.GateRefusal.Should().NotBeNull();
    }

    [Fact(DisplayName = "TP-720-A13 no trading tool is registered yet (gh#713-#717 open) — flips red when they land")]
    public void No_write_tools_registered_before_their_slices()
    {
        string[] writeTools = ["place_order", "modify_order", "cancel_order", "close_position",
            "partial_close_position", "confirm_trading_action"];
        string[] registered = typeof(Program).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods())
            .SelectMany(m => m.GetCustomAttributes(typeof(ModelContextProtocol.Server.McpServerToolAttribute), false)
                .Cast<ModelContextProtocol.Server.McpServerToolAttribute>()
                .Select(a => a.Name ?? m.Name))
            .ToArray();
        registered.Should().NotContain(writeTools,
            "sentinel: when this goes red, enable Tier B (IncludeTierB=true) and wire it to the real TradingTools");
    }
}
