using System.Reflection;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// The <c>reselect-bars</c> refusal logs the operator's message through <see cref="LogText"/> (gh#747).
/// </summary>
public sealed class ReselectCommandLineLogTests
{
    [Fact]
    public async Task ACarriageReturnInTheCommandLine_IsEscapedOnTheRefusalLine()
    {
        // Parse echoes the instant the operator wrote. A CR in it is a second log line unless the
        // composition root escapes the message before it is a field. This calls that method.
        CapturingLogger logs = new();
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = "Host=sentinel;Port=5432;Database=topstepx_mcp;Username=topstepx;Password=sentinel",
            ["MarketData:Instruments"] = "ES,NQ",
            ["MarketData:SessionCloseCentral"] = "16:00",
            ["MarketData:MaxRows"] = "5000",
            ["Mcp:Transport"] = "Stdio",
        });
        McpOptions mcp = builder.Configuration.GetSection(McpOptions.SectionName).Get<McpOptions>() ?? new McpOptions();
        Program.ConfigureServices(builder, mcp);
        await using WebApplication app = builder.Build();

        MethodInfo run = typeof(Program).GetMethod("RunReselectAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        Task<int> pending = (Task<int>)run.Invoke(
            null,
            [app, new[] { "reselect-bars", "ES", "not-a-date\r\nINFO forged", "2026-06-16T10:00:00Z" }])!;

        int code = await pending;

        code.Should().Be(2);
        string line = logs.Lines
            .Should().ContainSingle(entry => entry.StartsWith("reselect-bars refused the command line:", StringComparison.Ordinal))
            .Which;
        line.Any(char.IsControl).Should().BeFalse();
        line.Should().Contain("\\u000D\\u000A");
        line.Should().Contain("INFO forged");
    }

    private sealed class CapturingLogger : ILoggerProvider, ILogger
    {
        private readonly object _gate = new();
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_gate)
                {
                    return [.. _lines];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
            => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
            {
                _lines.Add(formatter(state, exception));
            }
        }

        public void Dispose()
        {
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
