using System.Reflection;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Logging;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// With <c>Store__Required</c> true, the CLI verbs return their store exit codes (gh#843).
/// </summary>
/// <remarks>
/// The probe target is a closed port, the same shape as <see cref="StoreRequiredStartupTests"/>.
/// <c>reselect-bars</c>, <c>export-tape</c> and <c>import-tape</c> migrate, so a required store that
/// never answers used to leave <see cref="StoreRequiredException"/> unhandled. The host path already
/// catches that exception. These verbs have to catch it too, log their cannot-run line, and return
/// the exit code their tables already document.
/// </remarks>
public sealed class StoreRequiredVerbTests
{
    private const string ClosedPort =
        "Host=127.0.0.1;Port=1;Database=topstepx_mcp;Username=topstepx;Password=changeme-local;Timeout=2";

    private const string Sentinel = "changeme-local";

    [Fact]
    public async Task ReselectBars_WhenTheStoreIsRequiredAndUnreachable_ReturnsDegraded()
    {
        CapturedLogs logs = new();
        await using WebApplication app = Build(logs);

        int code = await InvokeAsync(
            app,
            "RunReselectAsync",
            ["reselect-bars", "ES", "2026-06-16T09:00:00Z", "2026-06-16T10:00:00Z"]);

        code.Should().Be(ReselectExit.Degraded);
        logs.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Error
            && entry.Message.Contains("reselect-bars cannot run", StringComparison.Ordinal));
        logs.Text.Should().NotContain(Sentinel);
    }

    [Theory]
    [InlineData("export-tape")]
    [InlineData("import-tape")]
    public async Task TapeVerb_WhenTheStoreIsRequiredAndUnreachable_ReturnsTheStoreExit(string verb)
    {
        CapturedLogs logs = new();
        await using WebApplication app = Build(logs);

        int code = await InvokeAsync(app, "RunTapeExportAsync", [verb]);

        code.Should().Be(TapeExportCommands.ExitStore);
        logs.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Error && entry.Message.Contains("cannot run", StringComparison.Ordinal));
        logs.Text.Should().NotContain(Sentinel);
    }

    private static async Task<int> InvokeAsync(WebApplication app, string methodName, string[] args)
    {
        MethodInfo method = typeof(Program).GetMethod(
                methodName,
                BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Missing " + methodName + ".");
        Task<int> pending = (Task<int>)method.Invoke(null, [app, args])!;
        return await pending.ConfigureAwait(false);
    }

    private static WebApplication Build(CapturedLogs logs)
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
            ["Store:Required"] = "true",
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
