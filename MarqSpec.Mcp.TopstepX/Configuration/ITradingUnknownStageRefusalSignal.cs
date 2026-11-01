namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// Hook when a write is refused because the account stage parsed as unknown (ADR-0024 §2).
/// </summary>
/// <remarks>
/// The production signal counts <c>mcp.execution.unknown_stage</c> (gh#732). Tests may record the call.
/// </remarks>
public interface ITradingUnknownStageRefusalSignal
{
    /// <summary>Invoked after the gate refuses a write for an unknown account stage.</summary>
    /// <param name="accountId">The venue account id that was refused.</param>
    void UnknownStageWriteRefused(int accountId);
}
