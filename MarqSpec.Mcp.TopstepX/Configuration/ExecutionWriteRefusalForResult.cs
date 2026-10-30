using MarqSpec.Mcp.TopstepX.Telemetry;
using MarqSpec.Mcp.TopstepX.Venue;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>Maps a risk-gate result to the closed <see cref="ExecutionWriteRefusal"/> telemetry vocabulary.</summary>
internal static class ExecutionWriteRefusalForResult
{
    /// <summary>Stable refusal tag for one completed gate call.</summary>
    public static string For(TradingRiskGateResult result) =>
        result.Outcome switch
        {
            TradingAttemptOutcome.Preview => ExecutionWriteRefusal.Preview,
            TradingAttemptOutcome.Refused => result.Refusal?.Code ?? ExecutionWriteRefusal.Error,
            TradingAttemptOutcome.Live => ForLiveWrite(result.WriteResult),
            TradingAttemptOutcome.Failed => ExecutionWriteRefusal.VenueRejected,
            TradingAttemptOutcome.Unknown => ExecutionWriteRefusal.OutcomeUnknown,
            _ => ExecutionWriteRefusal.Error,
        };

    private static string ForLiveWrite(ExecutionWriteResult? write) =>
        write?.Outcome switch
        {
            ExecutionWriteOutcome.BrokerAcknowledged => ExecutionWriteRefusal.Transmitted,
            ExecutionWriteOutcome.VenueRejected => ExecutionWriteRefusal.VenueRejected,
            ExecutionWriteOutcome.TransmittedOutcomeUnknown => ExecutionWriteRefusal.OutcomeUnknown,
            ExecutionWriteOutcome.BrokerSuccessWithoutOrderId => ExecutionWriteRefusal.OutcomeUnknown,
            ExecutionWriteOutcome.NotTransmitted => ExecutionWriteRefusal.Error,
            null => ExecutionWriteRefusal.Error,
            _ => ExecutionWriteRefusal.OutcomeUnknown,
        };
}
