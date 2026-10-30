using MarqSpec.Mcp.TopstepX.Configuration;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.Tests.Configuration;

/// <summary>An <see cref="IOptionsMonitor{TOptions}"/> that keeps returning one instance.</summary>
internal static class TradingOptionsMonitors
{
    public static IOptionsMonitor<TradingExecutionOptions> Fixed(TradingExecutionOptions options) =>
        new FixedMonitor(options);

    private sealed class FixedMonitor(TradingExecutionOptions options) : IOptionsMonitor<TradingExecutionOptions>
    {
        public TradingExecutionOptions CurrentValue => options;

        public TradingExecutionOptions Get(string? name) => options;

        public IDisposable OnChange(Action<TradingExecutionOptions, string?> listener) => Noop.Instance;

        private sealed class Noop : IDisposable
        {
            public static readonly Noop Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
