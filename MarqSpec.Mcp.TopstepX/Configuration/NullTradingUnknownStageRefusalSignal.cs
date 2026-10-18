namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>No-op until gh#733 wires metrics and alerts.</summary>
public sealed class NullTradingUnknownStageRefusalSignal : ITradingUnknownStageRefusalSignal
{
    /// <inheritdoc />
    public void UnknownStageWriteRefused(int accountId)
    {
    }
}
