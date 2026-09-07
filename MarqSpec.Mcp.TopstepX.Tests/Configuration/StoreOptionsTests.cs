using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.Tests.Configuration;

/// <summary>
/// Binding <see cref="StoreOptions"/> — how long startup waits for a store that is not there yet (gh#514).
/// </summary>
/// <remarks>
/// The default is zero on purpose: one probe, no delay, which is what every stdio launch and every documented
/// local recipe already does. A deployment that has no cross-service ordering sets it; nothing else has to
/// know it exists.
/// </remarks>
public sealed class StoreOptionsTests
{
    /// <summary>An absent key leaves today's behaviour standing.</summary>
    [Fact]
    public void StartupWaitSeconds_DefaultsToZero_WhenTheKeyIsAbsent()
    {
        Bind([]).StartupWaitSeconds.Should().Be(0);
    }

    /// <summary>The key binds through the real composition root.</summary>
    [Fact]
    public void StartupWaitSeconds_BindsTheConfiguredValue()
    {
        Bind(new Dictionary<string, string?> { ["Store:StartupWaitSeconds"] = "90" })
            .StartupWaitSeconds.Should().Be(90);
    }

    /// <summary>
    /// A negative bound is refused rather than clamped. "Wait -1 seconds" is a typo, and silently reading it
    /// as zero would leave the operator believing a wait is configured.
    /// </summary>
    [Fact]
    public void StartupWaitSeconds_RefusesANegativeValue()
    {
        Action bind = () => Bind(new Dictionary<string, string?> { ["Store:StartupWaitSeconds"] = "-1" });

        bind.Should().Throw<OptionsValidationException>();
    }

    /// <summary>
    /// Ten minutes is the ceiling. A bound longer than that is a container that never reports a problem, and
    /// an orchestrator's own start-up grace period is the better tool past that point.
    /// </summary>
    [Fact]
    public void StartupWaitSeconds_RefusesMoreThanTenMinutes()
    {
        Action bind = () => Bind(new Dictionary<string, string?> { ["Store:StartupWaitSeconds"] = "601" });

        bind.Should().Throw<OptionsValidationException>();

        Bind(new Dictionary<string, string?> { ["Store:StartupWaitSeconds"] = "600" })
            .StartupWaitSeconds.Should().Be(600);
    }

    /// <summary>Binds through the real composition root, so this measures what the deployed server does.</summary>
    /// <param name="configured">The <c>Store:*</c> settings to add.</param>
    /// <returns>The bound and validated options.</returns>
    private static StoreOptions Bind(Dictionary<string, string?> configured)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();

        // Same reasoning as IndicatorOptionsPeriodListTests.Bind: a shell exporting a Store__* variable would
        // otherwise leak into the "absent key" case above.
        IConfigurationBuilder sources = builder.Configuration;
        foreach (EnvironmentVariablesConfigurationSource ambient in sources.Sources
            .OfType<EnvironmentVariablesConfigurationSource>()
            .Where(source => string.IsNullOrEmpty(source.Prefix))
            .ToList())
        {
            sources.Sources.Remove(ambient);
        }

        builder.Configuration.AddInMemoryCollection(configured);

        Program.ConfigureServices(builder, new McpOptions { Transport = McpTransport.Stdio });

        using ServiceProvider provider = builder.Services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        return provider.GetRequiredService<IOptions<StoreOptions>>().Value;
    }
}
