using System.Globalization;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.Tests.Configuration;

/// <summary>
/// The tape-retention window (gh#763, ADR-0027). The code, <c>.env.example</c> and compose state
/// the same default.
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
        compose.Should().Contain("${TapeRetention__Days:-" + TapeRetentionOptions.DefaultDays + "}");

        // The names proposed before ADR-0027 are not a second switch.
        foreach (string line in envExample.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith('#'))
            {
                continue;
            }

            trimmed.Should().NotStartWith("Retention__");
        }
    }

    [Fact]
    public void Bind_WhenDaysAreSet_ReadsThem()
    {
        Bind(new Dictionary<string, string?> { ["TapeRetention:Days"] = "120" }).Days.Should().Be(120);
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
            .Days.Should().Be(int.Parse(days, CultureInfo.InvariantCulture));
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
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MarqSpec.Mcp.TopstepX.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not find MarqSpec.Mcp.TopstepX.slnx above " + AppContext.BaseDirectory);
    }
}
