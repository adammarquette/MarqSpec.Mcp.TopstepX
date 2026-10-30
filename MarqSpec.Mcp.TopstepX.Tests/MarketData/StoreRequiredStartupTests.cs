using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// <c>Store__Required</c> decides whether an unreachable store degrades or exits (gh#843).
/// </summary>
/// <remarks>
/// The probe target is a closed port, so this tier needs no container. The password in the connection
/// string is the same sentinel <see cref="StoreStartupTests"/> already forbids in a log line. A migration
/// that drops its connection after the store has answered is the integration test: that path needs a
/// database that <c>CanConnectAsync</c> accepts.
/// </remarks>
public sealed class StoreRequiredStartupTests
{
    private const string ClosedPort =
        "Host=127.0.0.1;Port=1;Database=topstepx_mcp;Username=topstepx;Password=changeme-local;Timeout=2";

    private const string Sentinel = "changeme-local";

    /// <summary>
    /// The default stays ADR-0007. Nothing answered, the process is handed an unavailable marker, and
    /// it does not throw.
    /// </summary>
    [Fact]
    public async Task MigrateAsync_WhenTheStoreIsUnreachableAndNotRequired_Degrades()
    {
        CapturedLogs logs = new();
        await using WebApplication app = Build(required: false, logs);

        StoreAvailability availability = await Program.MigrateAsync(app);

        availability.IsAvailable.Should().BeFalse();
        availability.Explanation.Should().Be(StoreAvailability.Unavailable(string.Empty).Explanation);
        logs.Entries.Should().NotContain(entry => entry.Level == LogLevel.Error);
        logs.Text.Should().NotContain(Sentinel);
    }

    /// <summary>
    /// A required store that never answers logs an error and throws, so the host can exit non-zero
    /// instead of staying degraded for the life of the process.
    /// </summary>
    [Fact]
    public async Task MigrateAsync_WhenTheStoreIsUnreachableAndRequired_Exits()
    {
        CapturedLogs logs = new();
        await using WebApplication app = Build(required: true, logs);

        Func<Task> act = () => Program.MigrateAsync(app);

        await act.Should().ThrowAsync<StoreRequiredException>();
        logs.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Error)
            .Which.Message.Should().Contain("Store__Required");
        logs.Text.Should().NotContain(Sentinel);
    }

    /// <summary>
    /// <see cref="StoreRequiredException.Explanation"/> is what the verbs log. It carries no host,
    /// password, or connection string.
    /// </summary>
    [Fact]
    public async Task MigrateAsync_WhenTheStoreIsRequired_ExplanationOmitsTheConnection()
    {
        await using WebApplication app = Build(required: true, logs: new CapturedLogs());

        Func<Task> act = () => Program.MigrateAsync(app);

        StoreRequiredException thrown =
            (await act.Should().ThrowAsync<StoreRequiredException>()).Which;
        thrown.Explanation.Should().NotContain("127.0.0.1");
        thrown.Explanation.Should().NotContain(Sentinel);
        thrown.Explanation.Should().NotContain(ClosedPort);
    }

    private static WebApplication Build(bool required, CapturedLogs logs)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        StripAmbientEnvironment(builder);
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = ClosedPort,
            ["Store:StartupWaitSeconds"] = "0",
            ["Store:Required"] = required ? "true" : "false",
            ["Mcp:Transport"] = "Stdio",
        });
        Program.ConfigureServices(builder, new McpOptions { Transport = McpTransport.Stdio });
        return builder.Build();
    }

    private static void StripAmbientEnvironment(WebApplicationBuilder builder)
    {
        IConfigurationBuilder sources = builder.Configuration;
        foreach (EnvironmentVariablesConfigurationSource ambient in sources.Sources
            .OfType<EnvironmentVariablesConfigurationSource>()
            .Where(source => string.IsNullOrEmpty(source.Prefix))
            .ToList())
        {
            sources.Sources.Remove(ambient);
        }
    }

    private sealed class CapturedLogs : ILoggerProvider, ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public string Text => string.Join('\n', Entries.Select(entry => entry.Message));

        public ILogger CreateLogger(string categoryName) => this;

        public void Dispose()
        {
        }

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
