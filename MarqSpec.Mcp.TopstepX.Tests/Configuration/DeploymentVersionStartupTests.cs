using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MarqSpec.Mcp.TopstepX.Tests.Configuration;

/// <summary>
/// <c>Deployment__Version</c> is compared with the process at startup (gh#867).
/// </summary>
public sealed class DeploymentVersionStartupTests
{
    [Fact]
    public void ADifferentDeploymentVersion_WarnsOnce_AndNamesBoth()
    {
        CapturedLogs logs = new();
        IConfiguration configuration = Config("0.4.0");

        Program.WarnWhenDeploymentVersionDiffers(configuration, logs);

        string warning = logs.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Warning)
            .Which.Message;
        warning.Should().Contain("0.4.0");
        warning.Should().Contain(ProductVersion.Read());
        logs.Entries.Should().NotContain(entry => entry.Level != LogLevel.Warning);
    }

    [Fact]
    public void AMatchingDeploymentVersion_IsSilent()
    {
        CapturedLogs logs = new();
        IConfiguration configuration = Config(ProductVersion.Read());

        Program.WarnWhenDeploymentVersionDiffers(configuration, logs);

        logs.Entries.Should().BeEmpty();
    }

    [Fact]
    public void AnUnsetDeploymentVersion_IsSilent()
    {
        CapturedLogs logs = new();
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Deployment:ImageDigest"] = "sha256:9f2c1d",
            })
            .Build();

        Program.WarnWhenDeploymentVersionDiffers(configuration, logs);

        logs.Entries.Should().BeEmpty();
    }

    private static IConfiguration Config(string version) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Deployment:Version"] = version,
            })
            .Build();

    private sealed class CapturedLogs : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
