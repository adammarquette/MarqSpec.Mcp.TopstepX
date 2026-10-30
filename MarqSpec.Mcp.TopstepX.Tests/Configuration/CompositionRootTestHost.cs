using MarqSpec.Mcp.TopstepX.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MarqSpec.Mcp.TopstepX.Tests.Configuration;

/// <summary>Builds the real composition root on isolated in-memory configuration (gh#734).</summary>
internal static class CompositionRootTestHost
{
    internal static readonly Dictionary<string, string?> BaseSettings = new()
    {
        ["ConnectionStrings:Default"] = "Host=localhost;Database=x;Username=u;Password=p",
        ["MarketData:Instruments"] = "ES,NQ",
        ["MarketData:SessionCloseCentral"] = "16:00",
        ["MarketData:MaxRows"] = "5000",
    };

    internal static WebApplicationBuilder CreateIsolatedBuilder()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production,
        });

        StripAmbientConfiguration(builder.Configuration);
        return builder;
    }

    internal static void StripAmbientConfiguration(IConfigurationBuilder sources)
    {
        for (int index = sources.Sources.Count - 1; index >= 0; index--)
        {
            if (sources.Sources[index] is EnvironmentVariablesConfigurationSource
                or JsonConfigurationSource
                || sources.Sources[index].GetType().Name == "UserSecretsConfigurationSource")
            {
                sources.Sources.RemoveAt(index);
            }
        }
    }

    internal static ServiceProvider BuildVenueConfigured(
        Dictionary<string, string?> extra,
        Action<IServiceCollection>? configureServices = null)
    {
        WebApplicationBuilder builder = CreateIsolatedBuilder();
        builder.Configuration.AddInMemoryCollection(BaseSettings.Concat(extra));

        Program.ConfigureServices(builder, new McpOptions { Transport = McpTransport.Stdio });

        configureServices?.Invoke(builder.Services);

        return builder.Services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }
}
