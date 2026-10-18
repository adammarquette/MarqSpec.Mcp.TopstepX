using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>Persists append-only trading audit rows (gh#709).</summary>
public sealed class TradingAuditLogWriter(TopstepXDbContext database, TimeProvider clock) : ITradingAuditLogWriter
{
    /// <inheritdoc />
    public async Task WriteAsync(TradingAuditWriteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        database.TradingAuditLog.Add(new TradingAuditLogRecord
        {
            AttemptedAt = clock.GetUtcNow(),
            AccountId = request.AccountId,
            ParsedStage = request.ParsedStage,
            Tool = request.Tool,
            IdempotencyKey = request.IdempotencyKey,
            RequestHash = request.RequestHash,
            DryRun = request.DryRun,
            Outcome = request.Outcome,
            ContractCount = request.ContractCount,
            VenueErrorCode = request.VenueErrorCode,
        });

        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
