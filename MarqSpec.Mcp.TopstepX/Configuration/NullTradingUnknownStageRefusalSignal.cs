namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>No-op until gh#732 wires metrics and alerts (gh#733 was a duplicate).</summary>
public sealed class NullTradingUnknownStageRefusalSignal : ITradingUnknownStageRefusalSignal
{
    /// <inheritdoc />
    public void UnknownStageWriteRefused(int accountId)
    {
    }
}
