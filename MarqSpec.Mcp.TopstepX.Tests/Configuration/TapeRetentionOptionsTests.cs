using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.Tests.Configuration;

/// <summary>
/// The tape-retention window (gh#763). The code, <c>.env.example</c> and compose state the same
/// default, because a copied environment is what an operator actually runs.
/// </summary>
public sealed class TapeRetentionOptionsTests
{
    [Fact]
    public void Defaults_WhenUnset_AreNinetyDays()
    {
        TapeRetentionOptions options = Bind([]);

        options.Days.Should().Be(TapeRetentionOptions.DefaultDays);
        options.Days.Should().Be(90);

        string root = FindRepoRoot();
        string envExample = File.ReadAllText(Path.Combine(root, ".env.example"));
        string compose = File.ReadAllText(Path.Combine(root, "docker-compose.yml"));

        envExample.Should().Contain("TapeRetention__Days=" + TapeRetentionOptions.DefaultDays);
        compose.Should().Contain(
            "${TapeRetention__Days:-" + TapeRetentionOptions.DefaultDays + "}");

        // The names proposed before the decision are not a second switch. A live key would bind
        // nothing and look like it had.
        foreach (string line in envExample.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith('#'))
            {
                continue;
            }

            trimmed.Should().NotStartWith("Retention__");
        }

        compose.Should().NotContain("Retention__Enabled");
        compose.Should().NotContain("Retention__RawTapeDays");
        compose.Should().NotContain("Retention__DailySummaryDays");
        compose.Should().NotContain("Retention__RequireExportBeforeDrop");
    }

    [Fact]
    public void Bind_WhenDaysAreSet_ReadsThem()
    {
        Bind(new Dictionary<string, string?> { ["TapeRetention:Days"] = "120" })
            .Days.Should().Be(120);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("6")]
    [InlineData("3651")]
    public void Validate_WhenDaysAreOutsideTheWindow_Refuses(string days)
    {
        Action bind = () => Bind(new Dictionary<string, string?> { ["TapeRetention:Days"] = days });

        bind.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain("TapeRetention__Days")
            .And.Contain("do not turn pruning off");
    }

    [Theory]
    [InlineData("7")]
    [InlineData("90")]
    [InlineData("3650")]
    public void Validate_WhenDaysAreInsideTheWindow_Accepts(string days)
    {
        Bind(new Dictionary<string, string?> { ["TapeRetention:Days"] = days })
            .Days.Should().Be(int.Parse(days, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task ConfigureServices_WhenTheHostBuilds_RegistersTheRetentionWorker()
    {
        // Resolving every hosted service constructs the MCP server, which is IAsyncDisposable only.
        await using ServiceProvider provider = Provider([]);

        provider.GetServices<IHostedService>().Should().Contain(service => service is TapeRetentionWorker);
        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<TapeRetentionPass>().Should().BeOfType<TapeRetentionPass>();
    }

    private static TapeRetentionOptions Bind(Dictionary<string, string?> configured)
    {
        using ServiceProvider provider = Provider(configured);
        return provider.GetRequiredService<IOptions<TapeRetentionOptions>>().Value;
    }

    private static ServiceProvider Provider(Dictionary<string, string?> configured)
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

        builder.Configuration.AddInMemoryCollection(configured);
        Program.ConfigureServices(builder, new McpOptions { Transport = McpTransport.Stdio });

        return builder.Services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MarqSpec.Mcp.TopstepX.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not find MarqSpec.Mcp.TopstepX.slnx above " + AppContext.BaseDirectory);
    }
}
