using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// An <see cref="IOptionsMonitor{TOptions}"/> over one <see cref="IOptions{TOptions}"/> instance.
/// </summary>
/// <remarks>
/// The stage tests on <c>develop</c> construct the gate, the kill switch and the denylist with
/// <c>Options.Create</c>. Those tests are compiled into this pull request's build. This wrapper lets those
/// constructors keep that shape. It does not observe a configuration reload. The composition root passes a
/// real monitor.
/// </remarks>
internal sealed class FixedTradingOptionsMonitor(IOptions<TradingExecutionOptions> options)
    : IOptionsMonitor<TradingExecutionOptions>
{
    /// <inheritdoc />
    public TradingExecutionOptions CurrentValue => options.Value;

    /// <inheritdoc />
    public TradingExecutionOptions Get(string? name) => options.Value;

    /// <inheritdoc />
    public IDisposable OnChange(Action<TradingExecutionOptions, string?> listener) => Empty.Instance;

    private sealed class Empty : IDisposable
    {
        public static readonly Empty Instance = new();

        public void Dispose()
        {
        }
    }
}
