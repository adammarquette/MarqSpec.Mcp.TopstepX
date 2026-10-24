namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>How a durable idempotency claim resolved (ADR-0024 §5, gh#750).</summary>
public enum TradingAuditClaimStatus
{
    /// <summary>This call inserted the pre-send <c>Unknown</c> row and may transmit.</summary>
    Claimed,

    /// <summary>The same account, tool and key already has a claim for this request hash. Do not transmit.</summary>
    Replay,

    /// <summary>The same account, tool and key is already claimed for a different request hash. Do not transmit.</summary>
    Conflict,
}

/// <summary>The audit row a replay should return. Null fields apply only to <see cref="TradingAuditClaimStatus.Claimed"/>.</summary>
/// <param name="Status">Whether this call owns the send.</param>
/// <param name="ParsedStage">Stage stored on the claim, when replaying.</param>
/// <param name="BestOutcome">
/// <see cref="TradingAuditOutcomes.Live"/>, <see cref="TradingAuditOutcomes.Failed"/>,
/// <see cref="TradingAuditOutcomes.Refused"/>, or <see cref="TradingAuditOutcomes.Unknown"/>.
/// </param>
/// <param name="OrderId">Broker order id from a later <c>Live</c> row, when one was stored.</param>
/// <param name="VenueErrorCode">Venue code from the best row, when one was stored.</param>
public sealed record TradingAuditClaimResult(
    TradingAuditClaimStatus Status,
    string? ParsedStage,
    string? BestOutcome,
    long? OrderId,
    string? VenueErrorCode)
{
    /// <summary>A claim this call just inserted.</summary>
    /// <returns>The claimed result.</returns>
    public static TradingAuditClaimResult Claimed() =>
        new(TradingAuditClaimStatus.Claimed, null, null, null, null);

    /// <summary>The key is already stored for a different request hash.</summary>
    /// <returns>The conflict result.</returns>
    public static TradingAuditClaimResult Conflict() =>
        new(TradingAuditClaimStatus.Conflict, null, null, null, null);
}

/// <summary>
/// Durable idempotency over the append-only audit log (ADR-0024 §5).
/// </summary>
/// <remarks>
/// The key is <c>(AccountId, Tool, IdempotencyKey)</c>. The same key on another account or another tool is a
/// different claim. <see cref="MaxKeyLength"/> matches the <c>IdempotencyKey</c> column. A pre-send row with
/// outcome <see cref="TradingAuditOutcomes.Unknown"/> is the claim; a later <c>Live</c> or <c>Failed</c> row
/// is what a replay prefers. A <c>Refused</c> row recorded after that claim is preferred over the bare
/// <c>Unknown</c> row, so a refusal is not replayed as an unknown transmit. Previews are not claims.
/// </remarks>
public static class TradingAuditClaims
{
    /// <summary>The longest caller key the audit column will store. Longer keys are refused before insert.</summary>
    public const int MaxKeyLength = 256;

    /// <summary>
    /// Reads an existing claim for this account, tool and key.
    /// </summary>
    /// <param name="rows">Every audit row already stored for that triple.</param>
    /// <param name="requestHash">The hash of the request being claimed, including <c>dryRun</c>.</param>
    /// <returns>A replay or a conflict, or <see langword="null"/> when no claim exists yet.</returns>
    public static TradingAuditClaimResult? Find(IEnumerable<TradingAuditWriteRequest> rows, string requestHash)
    {
        ArgumentNullException.ThrowIfNull(rows);

        List<TradingAuditWriteRequest> matches = [.. rows];
        TradingAuditWriteRequest? claim = matches.FirstOrDefault(
            row => row.Outcome == TradingAuditOutcomes.Unknown);
        if (claim is null)
        {
            return null;
        }

        if (!string.Equals(claim.RequestHash, requestHash, StringComparison.Ordinal))
        {
            return TradingAuditClaimResult.Conflict();
        }

        TradingAuditWriteRequest? live = matches.LastOrDefault(row => row.Outcome == TradingAuditOutcomes.Live);
        if (live is not null)
        {
            return new TradingAuditClaimResult(
                TradingAuditClaimStatus.Replay,
                live.ParsedStage,
                TradingAuditOutcomes.Live,
                live.OrderId,
                live.VenueErrorCode);
        }

        TradingAuditWriteRequest? failed = matches.LastOrDefault(
            row => row.Outcome == TradingAuditOutcomes.Failed);
        if (failed is not null)
        {
            return new TradingAuditClaimResult(
                TradingAuditClaimStatus.Replay,
                failed.ParsedStage,
                TradingAuditOutcomes.Failed,
                OrderId: null,
                failed.VenueErrorCode);
        }

        TradingAuditWriteRequest? refused = matches.LastOrDefault(
            row => row.Outcome == TradingAuditOutcomes.Refused);
        if (refused is not null)
        {
            return new TradingAuditClaimResult(
                TradingAuditClaimStatus.Replay,
                refused.ParsedStage,
                TradingAuditOutcomes.Refused,
                OrderId: null,
                refused.VenueErrorCode);
        }

        return new TradingAuditClaimResult(
            TradingAuditClaimStatus.Replay,
            claim.ParsedStage,
            TradingAuditOutcomes.Unknown,
            OrderId: null,
            claim.VenueErrorCode);
    }
}
