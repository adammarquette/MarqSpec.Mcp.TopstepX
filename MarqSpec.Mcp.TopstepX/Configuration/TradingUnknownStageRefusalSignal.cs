using MarqSpec.Mcp.TopstepX.Telemetry;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// Counts one unknown-stage refusal (gh#732, ADR-0024 §2). The account id is not a tag and is not logged.
/// </summary>
public sealed class TradingUnknownStageRefusalSignal : ITradingUnknownStageRefusalSignal
{
    private readonly HostTelemetry _telemetry;

    /// <summary>Creates the signal on the host meter.</summary>
    /// <param name="telemetry">The meter. Untagged.</param>
    public TradingUnknownStageRefusalSignal(HostTelemetry telemetry)
    {
        _telemetry = telemetry;
    }

    /// <inheritdoc />
    public void UnknownStageWriteRefused(int accountId)
    {
        // The id is the caller's. A series per account would be a new dimension, and it is PII-shaped.
        _ = accountId;
        try
        {
            _telemetry.UnknownStageRefused();
        }
        catch (Exception)
        {
            // The refusal is already decided. A listener that throws must not replace it.
            _telemetry.RecordBookkeepingFault();
        }
    }
}
