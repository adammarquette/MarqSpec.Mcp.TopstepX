using MarqSpec.Mcp.TopstepX.Configuration;

namespace MarqSpec.Mcp.TopstepX.Tests.Configuration;

/// <summary>
/// In-memory stand-in for the audit claim. Uniqueness is the same rule the database index enforces, so a
/// gate test can prove a second instance replays without a container. The index itself is an integration test.
/// </summary>
internal static class TradingAuditClaimMemory
{
    public static TradingAuditClaimResult TryClaim(
        IList<TradingAuditWriteRequest> entries,
        TradingAuditWriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(request);

        lock (entries)
        {
            TradingAuditClaimResult? existing = TradingAuditClaims.Find(
                entries.Where(row => row.AccountId == request.AccountId
                    && row.Tool == request.Tool
                    && row.IdempotencyKey == request.IdempotencyKey),
                request.RequestHash);
            if (existing is not null)
            {
                return existing;
            }

            entries.Add(request with
            {
                Outcome = TradingAuditOutcomes.Unknown,
                ContractCount = 0,
                VenueErrorCode = null,
                OrderId = null,
            });
            return TradingAuditClaimResult.Claimed();
        }
    }
}
