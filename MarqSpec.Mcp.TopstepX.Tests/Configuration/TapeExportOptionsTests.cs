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
/// The tape-export configuration contract (gh#755). The code, <c>.env.example</c> and compose state
/// the same defaults, because a Railway template copies them.
/// </summary>
public sealed class TapeExportOptionsTests
{
    [Fact]
    public void Defaults_WhenUnset_MatchTheConfigContract()
    {
        TapeExportOptions options = Bind([]);

        options.Enabled.Should().BeFalse();
        options.Schedule.Should().Be(TapeExportOptions.DefaultSchedule);
        options.Path.Should().Be(TapeExportOptions.DefaultPath);
        options.Format.Should().Be(TapeExportOptions.DefaultFormat);
        options.ParsedSchedule().Should().Be(new TimeOnly(6, 0));
        options.Drive.FolderId.Should().BeEmpty();
        options.Drive.ServiceAccountJson.Should().BeEmpty();
        options.Drive.ServiceAccountJsonPath.Should().BeEmpty();
        options.Drive.IsConfigured.Should().BeFalse();

        string root = FindRepoRoot();
        string envExample = File.ReadAllText(Path.Combine(root, ".env.example"));
        string compose = File.ReadAllText(Path.Combine(root, "docker-compose.yml"));
        string program = File.ReadAllText(Path.Combine(root, "MarqSpec.Mcp.TopstepX", "Program.cs"));

        envExample.Should().Contain("TapeExport__Enabled=false");
        envExample.Should().Contain("TapeExport__Schedule=" + TapeExportOptions.DefaultSchedule);
        envExample.Should().Contain("TapeExport__Path=" + TapeExportOptions.DefaultPath);
        envExample.Should().Contain("TapeExport__Format=" + TapeExportOptions.DefaultFormat);
        envExample.Should().Contain("TapeExport__Drive__FolderId=");
        envExample.Should().Contain("TapeExport__Drive__ServiceAccountJson=");
        envExample.Should().Contain("TapeExport__Drive__ServiceAccountJsonPath=");
        envExample.Should().NotContain("private_key");
        envExample.Should().NotContain("TapeExport__Retention");
        envExample.Should().NotContain("TapeExport__Prune");
        envExample.Should().NotContain("TapeExport__SafeAge");
        envExample.Should().NotContain("Retention__");

        compose.Should().Contain("${TapeExport__Enabled:-false}");
        compose.Should().Contain("${TapeExport__Schedule:-" + TapeExportOptions.DefaultSchedule + "}");
        compose.Should().Contain("${TapeExport__Path:-" + TapeExportOptions.DefaultPath + "}");
        compose.Should().Contain("${TapeExport__Format:-" + TapeExportOptions.DefaultFormat + "}");
        compose.Should().Contain("${TapeExport__Drive__FolderId:-}");
        compose.Should().Contain("${TapeExport__Drive__ServiceAccountJson:-}");
        compose.Should().Contain("${TapeExport__Drive__ServiceAccountJsonPath:-}");
        compose.Should().NotContain("private_key");
        compose.Should().NotContain("TapeExport__Retention");
        compose.Should().NotContain("TapeExport__Prune");
        compose.Should().NotContain("Retention__");

        program.Should().Contain("TapeExportCommands.ExportVerb");
        program.Should().Contain("TapeExportCommands.ImportVerb");
        TapeExportCommands.ExportVerb.Should().Be("export-tape");
        TapeExportCommands.ImportVerb.Should().Be("import-tape");
    }

    [Fact]
    public void Bind_WhenScheduleAndPathAreSet_ReadsThem()
    {
        TapeExportOptions options = Bind(new Dictionary<string, string?>
        {
            ["TapeExport:Schedule"] = "18:30",
            ["TapeExport:Path"] = "/mnt/tape",
            ["TapeExport:Enabled"] = "true",
            ["TapeExport:Format"] = TapeExportOptions.DefaultFormat,
        });

        options.ParsedSchedule().Should().Be(new TimeOnly(18, 30));
        options.Path.Should().Be("/mnt/tape");
        options.Enabled.Should().BeTrue();
        options.Format.Should().Be(TapeExportOptions.DefaultFormat);
    }

    [Fact]
    public void Validate_WhenScheduleIsNotHourAndMinute_Refuses()
    {
        Action bind = () => Bind(new Dictionary<string, string?>
        {
            ["TapeExport:Schedule"] = "06:00:00",
        });

        bind.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain("TapeExport__Schedule");
    }

    [Fact]
    public void Validate_WhenPathIsAnObjectUri_Refuses()
    {
        Action bind = () => Bind(new Dictionary<string, string?>
        {
            ["TapeExport:Path"] = "s3://bucket/tape",
        });

        bind.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain("TapeExport__Path");
    }

    [Fact]
    public void Validate_WhenPathIsRelative_Refuses()
    {
        Action bind = () => Bind(new Dictionary<string, string?>
        {
            ["TapeExport:Path"] = "data/tape-exports",
        });

        bind.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain("mounted volume");
    }

    [Fact]
    public void Validate_WhenFormatIsParquet_Refuses()
    {
        Action bind = () => Bind(new Dictionary<string, string?>
        {
            ["TapeExport:Format"] = "parquet",
        });

        bind.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain("csv.gz");
    }

    [Fact]
    public async Task ConfigureServices_WhenTheHostBuilds_RegistersTheExportWorker()
    {
        // Resolving every hosted service constructs the MCP server, which is IAsyncDisposable only.
        await using ServiceProvider provider = Provider([]);

        provider.GetServices<IHostedService>().Should().Contain(service => service is TapeExportWorker);
        provider.GetRequiredService<ITapeDriveUploader>().Should().BeOfType<DisabledTapeDriveUploader>();
        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITapeExportPass>().Should().BeOfType<TapeExportPass>();
    }

    [Fact]
    public void Drive_WhenFolderAndCredentialAreUnset_StaysOff()
    {
        TapeExportOptions folderOnly = Bind(new Dictionary<string, string?>
        {
            ["TapeExport:Drive:FolderId"] = "folder-placeholder",
        });
        TapeExportOptions configured = Bind(new Dictionary<string, string?>
        {
            ["TapeExport:Drive:FolderId"] = "folder-placeholder",
            ["TapeExport:Drive:ServiceAccountJsonPath"] = "/secrets/drive-account.json",
        });

        folderOnly.Drive.IsConfigured.Should().BeFalse();
        configured.Drive.IsConfigured.Should().BeTrue();
        configured.Drive.ServiceAccountJson.Should().BeEmpty();
    }

    [Fact]
    public void Validate_WhenTheCredentialPathIsRelative_Refuses()
    {
        Action bind = () => Bind(new Dictionary<string, string?>
        {
            ["TapeExport:Drive:ServiceAccountJsonPath"] = "secrets/drive-account.json",
        });

        bind.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain("TapeExport__Drive__ServiceAccountJsonPath")
            .And.NotContain("private_key");
    }

    private static TapeExportOptions Bind(Dictionary<string, string?> configured)
    {
        using ServiceProvider provider = Provider(configured);
        return provider.GetRequiredService<IOptions<TapeExportOptions>>().Value;
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
