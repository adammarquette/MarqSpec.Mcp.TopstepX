using MarqSpec.Mcp.TopstepX.Venue;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>Append-only trading audit seam (ADR-0024 §5, gh#709).</summary>
public interface ITradingAuditLogWriter
{
    /// <summary>Inserts one audit row before the attempt outcome is returned to the caller.</summary>
    Task WriteAsync(TradingAuditWriteRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Inserts the pre-send <see cref="TradingAuditOutcomes.Unknown"/> claim, or reports the claim already
    /// stored for this account, tool and key. The store rejects a second claim for the same triple.
    /// </summary>
    /// <param name="request">The claim. <see cref="TradingAuditWriteRequest.Outcome"/> is stored as Unknown.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>Claimed when this call inserted the row; otherwise the stored replay or conflict.</returns>
    Task<TradingAuditClaimResult> TryClaimUnknownAsync(
        TradingAuditWriteRequest request,
        CancellationToken cancellationToken);
}

/// <summary>One row to append to <c>TradingAuditLog</c>.</summary>
/// <param name="AccountId">The venue account id.</param>
/// <param name="ParsedStage">The stage string stored on the row.</param>
/// <param name="Tool">The MCP tool name.</param>
/// <param name="IdempotencyKey">The caller key. Scoped with <paramref name="AccountId"/> and <paramref name="Tool"/>.</param>
/// <param name="RequestHash">SHA-256 hex of the normalised request, including dry-run.</param>
/// <param name="DryRun">Whether the attempt was a preview.</param>
/// <param name="Outcome">The closed outcome name.</param>
/// <param name="ContractCount">Cap charge. The pre-send claim uses zero.</param>
/// <param name="VenueErrorCode">Venue or refusal code, or <see langword="null"/>.</param>
/// <param name="OrderId">Broker order id on a live row, or <see langword="null"/>.</param>
public sealed record TradingAuditWriteRequest(
    int AccountId,
    string ParsedStage,
    string Tool,
    string IdempotencyKey,
    string RequestHash,
    bool DryRun,
    string Outcome,
    int ContractCount,
    string? VenueErrorCode,
    long? OrderId = null);
