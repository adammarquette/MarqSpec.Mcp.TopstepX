using MarqSpec.Mcp.TopstepX.Venue;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>Append-only trading audit seam (ADR-0024 §5, gh#709).</summary>
public interface ITradingAuditLogWriter
{
    /// <summary>Inserts one audit row before the attempt outcome is returned to the caller.</summary>
    Task WriteAsync(TradingAuditWriteRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Reads whether this claim already has the cap reversal and the <c>Refused</c> row. The default is
    /// "neither": an in-memory double that throws before it stores a row still retries cleanly.
    /// </summary>
    /// <param name="accountId">The venue account id.</param>
    /// <param name="tool">The MCP tool name.</param>
    /// <param name="idempotencyKey">The caller key.</param>
    /// <param name="requestHash">The hash of the request being finalised.</param>
    /// <param name="refusalCode">The refusal code stored on the reversal and the refusal row.</param>
    /// <param name="reversalCount">The positive cap charge. Zero means there is no reversal to find.</param>
    /// <param name="cancellationToken">The caller's cancellation token. The re-check passes
    /// <see cref="CancellationToken.None"/>.</param>
    /// <returns>Which of the two rows are already stored.</returns>
    Task<TradingWriterRefusalStored> FindStoredWriterRefusalAsync(
        int accountId,
        string tool,
        string idempotencyKey,
        string requestHash,
        string refusalCode,
        int reversalCount,
        CancellationToken cancellationToken)
    {
        _ = accountId;
        _ = tool;
        _ = idempotencyKey;
        _ = requestHash;
        _ = refusalCode;
        _ = reversalCount;
        _ = cancellationToken;
        return Task.FromResult(TradingWriterRefusalStored.None);
    }

    /// <summary>
    /// Inserts the cap reversal and the <c>Refused</c> row. The production writer commits them in one
    /// save. The default writes them one after another so a double that only implements
    /// <see cref="WriteAsync"/> still finalises.
    /// </summary>
    /// <param name="reversal">The negative <c>Attempted</c> row.</param>
    /// <param name="refusal">The <c>Refused</c> row.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    async Task AppendReversalAndRefusalAsync(
        TradingAuditWriteRequest reversal,
        TradingAuditWriteRequest refusal,
        CancellationToken cancellationToken)
    {
        await WriteAsync(reversal, cancellationToken).ConfigureAwait(false);
        await WriteAsync(refusal, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Inserts the pre-send <see cref="TradingAuditOutcomes.Unknown"/> claim, or reports the claim already
    /// stored for this account, tool and key. The store rejects a second claim for the same triple.
    /// </summary>
    /// <param name="request">The claim. <see cref="TradingAuditWriteRequest.Outcome"/> is stored as Unknown.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>Claimed when this call inserted the row; otherwise the stored replay or conflict.</returns>
    /// <remarks>
    /// The body fails closed. The production writer and the gate's test doubles override it. A double that
    /// never reaches a keyed send, including the stage tests that landed on <c>develop</c> after this branch
    /// forked, still compiles, and a keyed send through that double does not pretend a claim was stored.
    /// </remarks>
    Task<TradingAuditClaimResult> TryClaimUnknownAsync(
        TradingAuditWriteRequest request,
        CancellationToken cancellationToken)
    {
        _ = request;
        _ = cancellationToken;
        throw new NotSupportedException("A trading audit writer stores the pre-send claim.");
    }
}

/// <summary>What a writer-lock refusal has already stored for one claim.</summary>
/// <param name="Reversed">The negative cap charge is already stored, or there was no charge.</param>
/// <param name="Refused">A <c>Refused</c> row for this request hash and code is already stored.</param>
public readonly record struct TradingWriterRefusalStored(bool Reversed, bool Refused)
{
    /// <summary>Neither row is stored.</summary>
    public static TradingWriterRefusalStored None => new(false, false);
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
