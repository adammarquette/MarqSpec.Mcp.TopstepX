using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.Mcp.TopstepX.Tests.Configuration;

/// <summary>Proves execution gateway DI wiring through the real composition root (gh#710).</summary>
[Trait("Gate", "ADR-0024")]
public sealed class ExecutionGatewayRegistrationTests
{
    [Fact]
    public void ExecutionGateway_IsUnconfigured_WhenTheVenueIsNotConfigured()
    {
        using ServiceProvider provider = Build([]);

        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IExecutionGateway>()
            .Should().BeOfType<UnconfiguredExecutionGateway>();
    }

    [Fact]
    public void ExecutionGateway_IsProjectXAdapter_WhenTheVenueIsConfigured()
    {
        Dictionary<string, string?> configured = new()
        {
            ["ProjectX:ApiKey"] = "a-username",
            ["ProjectX:ApiSecret"] = "an-api-key",
            ["ProjectX:DataTier"] = "Simulated",
        };

        using ServiceProvider provider = Build(configured);

        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IExecutionGateway>()
            .Should().BeOfType<ProjectXExecutionGateway>();
    }

    [Fact]
    public void TradingRiskGate_IsRegisteredScoped()
    {
        WebApplicationBuilder builder = CompositionRootTestHost.CreateIsolatedBuilder();
        builder.Configuration.AddInMemoryCollection(CompositionRootTestHost.BaseSettings);
        Program.ConfigureServices(builder, new McpOptions { Transport = McpTransport.Stdio });

        ServiceDescriptor registration = builder.Services
            .Where(descriptor => descriptor.ServiceType == typeof(TradingRiskGate))
            .Should()
            .ContainSingle()
            .Subject;

        registration.Lifetime.Should().Be(ServiceLifetime.Scoped);
    }

    [Fact]
    public void TradingExecutionOptions_DefaultOff_ThroughTheCompositionRoot()
    {
        using ServiceProvider provider = Build([]);

        provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<TradingExecutionOptions>>().Value.Enabled
            .Should().BeFalse();
    }

    private static ServiceProvider Build(Dictionary<string, string?> extra) =>
        CompositionRootTestHost.BuildVenueConfigured(extra);
}
