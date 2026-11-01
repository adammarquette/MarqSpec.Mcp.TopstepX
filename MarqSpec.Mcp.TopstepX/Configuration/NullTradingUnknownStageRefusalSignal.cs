namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>No-op. Production registers <see cref="TradingUnknownStageRefusalSignal"/> (gh#732).</summary>
public sealed class NullTradingUnknownStageRefusalSignal : ITradingUnknownStageRefusalSignal
{
    /// <inheritdoc />
    public void UnknownStageWriteRefused(int accountId)
    {
    }
}
