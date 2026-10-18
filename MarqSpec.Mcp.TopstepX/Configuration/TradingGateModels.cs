using MarqSpec.Mcp.TopstepX.Venue;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>Closed outcome for one trading gate attempt (ADR-0024 §5).</summary>
public enum TradingAttemptOutcome
{
    /// <summary>Risk checks passed; dry-run preview with no venue write.</summary>
    Preview,

    /// <summary>A gate layer refused before transmit.</summary>
    Refused,

    /// <summary>Venue write transmitted and broker-acknowledged.</summary>
    Live,

    /// <summary>Venue write ran but was not broker-acknowledged.</summary>
    Failed,

    /// <summary>Transmit outcome could not be classified or was uncertain (counts toward daily cap).</summary>
    Unknown,
}

/// <summary>Caller metadata for one gate attempt. Tool and request hash are stamped by the gate.</summary>
/// <param name="DryRun">When true, risk checks run but no gateway write occurs.</param>
/// <param name="IdempotencyKey">Reserved for gh#712; empty until tools supply one.</param>
public sealed record TradingRiskAttemptContext(bool DryRun, string IdempotencyKey = "");

/// <summary>Structured dry-run preview returned when <see cref="TradingRiskAttemptContext.DryRun"/> is true.</summary>
public sealed record TradingExecutionPreview(
    int AccountId,
    AccountStage ParsedStage,
    string Tool,
    bool WouldTransmit);

/// <summary>Result of one <see cref="TradingRiskGate"/> call.</summary>
public sealed record TradingRiskGateResult(
    TradingAttemptOutcome Outcome,
    AccountStage ParsedStage,
    TradingExecutionRefusal? Refusal,
    ExecutionWriteResult? WriteResult,
    TradingExecutionPreview? Preview)
{
    /// <summary>
    /// Whether repeating the same command without reconciliation is unlikely to duplicate a live order.
    /// </summary>
    public bool IsRetrySafe => WriteResult?.IsRetrySafe
        ?? Outcome is TradingAttemptOutcome.Refused or TradingAttemptOutcome.Preview;
}
