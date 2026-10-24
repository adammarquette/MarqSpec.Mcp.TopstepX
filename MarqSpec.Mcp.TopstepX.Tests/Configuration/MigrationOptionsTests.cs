using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.Tests.Configuration;

/// <summary>
/// Binding <see cref="MigrationOptions"/> — how long one migration command may run (gh#756).
/// </summary>
public sealed class MigrationOptionsTests
{
    /// <summary>An absent key is one hour, not Npgsql's 30 seconds.</summary>
    [Fact]
    public void CommandTimeoutSeconds_DefaultsToOneHour_WhenTheKeyIsAbsent()
    {
        Bind([]).CommandTimeoutSeconds.Should().Be(MigrationOptions.DefaultCommandTimeoutSeconds);
        MigrationOptions.DefaultCommandTimeoutSeconds.Should().Be(3600);
    }

    /// <summary>The key binds through the real composition root, which is what startup applies.</summary>
    [Fact]
    public void CommandTimeoutSeconds_BindsTheConfiguredValue()
    {
        Bind(new Dictionary<string, string?> { ["Migrations:CommandTimeoutSeconds"] = "123" })
            .CommandTimeoutSeconds.Should().Be(123);
    }

    /// <summary>
    /// Zero is Npgsql's infinity. Refusing it keeps a typo from becoming a migration that cannot end.
    /// </summary>
    [Fact]
    public void CommandTimeoutSeconds_RefusesZeroAndANegativeValue()
    {
        Action zero = () => Bind(new Dictionary<string, string?> { ["Migrations:CommandTimeoutSeconds"] = "0" });
        Action negative = () => Bind(new Dictionary<string, string?>
        {
            ["Migrations:CommandTimeoutSeconds"] = "-1",
        });

        zero.Should().Throw<OptionsValidationException>();
        negative.Should().Throw<OptionsValidationException>();
    }

    /// <summary>Seven days is the ceiling. The bound at the ceiling still binds.</summary>
    [Fact]
    public void CommandTimeoutSeconds_RefusesMoreThanSevenDays()
    {
        Action over = () => Bind(new Dictionary<string, string?>
        {
            ["Migrations:CommandTimeoutSeconds"] = (MigrationOptions.MaxCommandTimeoutSeconds + 1).ToString(),
        });

        over.Should().Throw<OptionsValidationException>();

        Bind(new Dictionary<string, string?>
        {
            ["Migrations:CommandTimeoutSeconds"] = MigrationOptions.MaxCommandTimeoutSeconds.ToString(),
        }).CommandTimeoutSeconds.Should().Be(MigrationOptions.MaxCommandTimeoutSeconds);
    }

    /// <summary>Binds through the real composition root, so this measures what the deployed server does.</summary>
    /// <param name="configured">The <c>Migrations:*</c> settings to add.</param>
    /// <returns>The bound and validated options.</returns>
    private static MigrationOptions Bind(Dictionary<string, string?> configured)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();

        // Same strip as StoreOptionsTests: a shell exporting Migrations__* would leak into the absent-key case.
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

        return provider.GetRequiredService<IOptions<MigrationOptions>>().Value;
    }
}
