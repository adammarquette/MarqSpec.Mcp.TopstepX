using MarqSpec.Mcp.TopstepX.Venue;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>Append-only trading audit seam (ADR-0024 §5, gh#709).</summary>
public interface ITradingAuditLogWriter
{
    /// <summary>Inserts one audit row before the attempt outcome is returned to the caller.</summary>
    Task WriteAsync(TradingAuditWriteRequest request, CancellationToken cancellationToken);
}

/// <summary>One row to append to <c>TradingAuditLog</c>.</summary>
public sealed record TradingAuditWriteRequest(
    int AccountId,
    string ParsedStage,
    string Tool,
    string IdempotencyKey,
    string RequestHash,
    bool DryRun,
    string Outcome,
    int ContractCount,
    string? VenueErrorCode);
