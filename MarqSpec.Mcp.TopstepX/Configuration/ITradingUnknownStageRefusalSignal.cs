namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// Hook when a write is refused because the account stage parsed as unknown (ADR-0024 §2).
/// </summary>
/// <remarks>
/// OpenTelemetry metric and alert wiring lands in gh#733 (ADR-0024 §2). This interface is the seam only.
/// </remarks>
public interface ITradingUnknownStageRefusalSignal
{
    /// <summary>Invoked after the gate refuses a write for an unknown account stage.</summary>
    /// <param name="accountId">The venue account id that was refused.</param>
    void UnknownStageWriteRefused(int accountId);
}
