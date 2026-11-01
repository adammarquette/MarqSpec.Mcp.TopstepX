namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// No-op. The writes counter already records an unknown-stage refusal (gh#732).
/// </summary>
public sealed class NullTradingUnknownStageRefusalSignal : ITradingUnknownStageRefusalSignal
{
    /// <inheritdoc />
    public void UnknownStageWriteRefused(int accountId)
    {
    }
}
