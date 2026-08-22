using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Tools;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.Mcp.TopstepX.Tests;

/// <summary>
/// The container has to actually build.
/// </summary>
/// <remarks>
/// <para>
/// This exists because it did not. <see cref="ProjectXMarketDataGateway"/> was registered as a singleton while
/// the vendor client registers <c>IProjectXApiClient</c> as <b>scoped</b> — a captive dependency, which the
/// container refuses outright. The process died at <c>builder.Build()</c>, before the transport existed, so an
/// MCP client saw a bare transport failure.
/// </para>
/// <para>
/// <b>Every other test in this suite constructs its subject by hand</b>, so none of them touched the
/// composition root and none of them could have caught it. Worse, the fault only appears when credentials
/// <i>are</i> configured — the unconfigured gateway has no dependencies and is safe at any lifetime — so every
/// local run without a <c>.env</c> was green.
/// </para>
/// <para>
/// <see cref="ServiceProviderOptions.ValidateOnBuild"/> and <see cref="ServiceProviderOptions.ValidateScopes"/>
/// are both on, which is what turns a startup crash into a fast unit test.
/// </para>
/// </remarks>
public sealed class CompositionRootTests
{
    private static readonly Dictionary<string, string?> _baseSettings = new()
    {
        ["ConnectionStrings:Default"] = "Host=localhost;Database=x;Username=u;Password=p",
        ["MarketData:Instruments"] = "ES,NQ",
        ["MarketData:SessionCloseCentral"] = "16:00",
        ["MarketData:MaxRows"] = "5000",
    };

    private static ServiceProvider Build(Dictionary<string, string?> extra, McpOptions mcp)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(_baseSettings.Concat(extra));

        Program.ConfigureServices(builder, mcp);

        // Exactly what the runtime does at startup, and what caught nothing until this test existed.
        return builder.Services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    [Fact]
    public void TheContainerBuilds_WhenTheVenueIsNotConfigured()
    {
        using ServiceProvider provider = Build([], new McpOptions { Transport = McpTransport.Stdio });

        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMarketDataGateway>()
            .Should().BeOfType<UnconfiguredMarketDataGateway>();
    }

    [Fact]
    public void TheContainerBuilds_WhenTheVenueIsConfigured()
    {
        // THE regression. This is the path a run without a .env never reaches, and the one the compose stack
        // takes -- so it was the first configuration anyone actually deployed, and it died on startup.
        Dictionary<string, string?> configured = new()
        {
            ["ProjectX:ApiKey"] = "a-username",
            ["ProjectX:ApiSecret"] = "an-api-key",
            ["ProjectX:DataTier"] = "Simulated",
        };

        using ServiceProvider provider = Build(configured, new McpOptions { Transport = McpTransport.Stdio });

        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMarketDataGateway>()
            .Should().BeOfType<ProjectXMarketDataGateway>();
    }

    [Fact]
    public void TheContainerBuilds_ForTheHttpTransport()
    {
        McpOptions http = new() { Transport = McpTransport.Http, HttpBearerToken = "a-token" };
        Dictionary<string, string?> settings = new()
        {
            ["Mcp:Transport"] = "Http",
            ["Mcp:HttpBearerToken"] = "a-token",
        };

        using ServiceProvider provider = Build(settings, http);
        provider.Should().NotBeNull();
    }

    [Theory]
    [InlineData(typeof(ReferenceTools))]
    [InlineData(typeof(MarketDataTools))]
    [InlineData(typeof(AccountTools))]
    [InlineData(typeof(SnapshotTools))]
    [InlineData(typeof(ObservationTools))]
    public void EveryToolTypeCanBeResolvedFromARequestScope(Type toolType)
    {
        // The MCP SDK activates a tool type per call from the request scope, and it resolves constructor
        // parameters from DI rather than activating them recursively. A tool whose dependency is not
        // registered therefore fails at CALL time, per tool, with nothing failing at startup -- so a probe
        // that exercised some tools and not others would report the server healthy.
        Dictionary<string, string?> configured = new()
        {
            ["ProjectX:ApiKey"] = "a-username",
            ["ProjectX:ApiSecret"] = "an-api-key",
            ["ProjectX:DataTier"] = "Simulated",
        };

        using ServiceProvider provider = Build(configured, new McpOptions { Transport = McpTransport.Stdio });
        using IServiceScope scope = provider.CreateScope();

        Func<object> resolve = () => ActivatorUtilities.CreateInstance(scope.ServiceProvider, toolType);

        resolve.Should().NotThrow(
            toolType.Name + " cannot be built from a request scope, so every call to its tools would fail "
            + "while the server reported itself healthy.");
    }
}
