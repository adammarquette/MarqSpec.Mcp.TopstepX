namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// Whether the operator kill switch is engaged. Checked on every trading write attempt (ADR-0024 §5).
/// </summary>
public interface ITradingKillSwitch
{
    /// <summary>True when trading must be refused regardless of <see cref="TradingExecutionOptions.Enabled"/>.</summary>
    bool IsEngaged { get; }
}
