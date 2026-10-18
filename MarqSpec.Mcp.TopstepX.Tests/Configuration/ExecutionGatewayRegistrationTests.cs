using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.Mcp.TopstepX.Tests.Configuration;

/// <summary>Proves execution gateway DI wiring through the real composition root (gh#710).</summary>
[Trait("Gate", "ADR-0024")]
public sealed class ExecutionGatewayRegistrationTests
{
    private static readonly Dictionary<string, string?> _baseSettings = new()
    {
        ["ConnectionStrings:Default"] = "Host=localhost;Database=x;Username=u;Password=p",
        ["MarketData:Instruments"] = "ES,NQ",
        ["MarketData:SessionCloseCentral"] = "16:00",
        ["MarketData:MaxRows"] = "5000",
    };

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
    public void TradingExecutionOptions_DefaultOff_ThroughTheCompositionRoot()
    {
        using ServiceProvider provider = Build([]);

        provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<TradingExecutionOptions>>().Value.Enabled
            .Should().BeFalse();
    }

    private static ServiceProvider Build(Dictionary<string, string?> extra)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();

        IConfigurationBuilder sources = builder.Configuration;
        foreach (EnvironmentVariablesConfigurationSource ambient in sources.Sources
            .OfType<EnvironmentVariablesConfigurationSource>()
            .Where(source => string.IsNullOrEmpty(source.Prefix))
            .ToList())
        {
            sources.Sources.Remove(ambient);
        }

        builder.Configuration.AddInMemoryCollection(_baseSettings.Concat(extra));

        Program.ConfigureServices(builder, new McpOptions { Transport = McpTransport.Stdio });

        return builder.Services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }
}
