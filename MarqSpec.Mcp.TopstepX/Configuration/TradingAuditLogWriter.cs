using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>Persists append-only trading audit rows (gh#709, gh#750).</summary>
public sealed class TradingAuditLogWriter(TopstepXDbContext database, TimeProvider clock) : ITradingAuditLogWriter
{
    /// <inheritdoc />
    public async Task WriteAsync(TradingAuditWriteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        database.TradingAuditLog.Add(ToRecord(request));
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<TradingAuditClaimResult> TryClaimUnknownAsync(
        TradingAuditWriteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        TradingAuditClaimResult? existing = await FindAsync(request, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        TradingAuditWriteRequest claim = request with
        {
            Outcome = TradingAuditOutcomes.Unknown,
            ContractCount = 0,
            VenueErrorCode = null,
            OrderId = null,
        };

        TradingAuditLogRecord row = ToRecord(claim);
        database.TradingAuditLog.Add(row);
        try
        {
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return TradingAuditClaimResult.Claimed();
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            database.Entry(row).State = EntityState.Detached;
            TradingAuditClaimResult? raced = await FindAsync(request, cancellationToken).ConfigureAwait(false);
            return raced ?? TradingAuditClaimResult.Conflict();
        }
    }

    private async Task<TradingAuditClaimResult?> FindAsync(
        TradingAuditWriteRequest request,
        CancellationToken cancellationToken)
    {
        List<TradingAuditLogRecord> rows = await database.TradingAuditLog
            .AsNoTracking()
            .Where(row => row.AccountId == request.AccountId
                && row.Tool == request.Tool
                && row.IdempotencyKey == request.IdempotencyKey)
            .OrderBy(row => row.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return TradingAuditClaims.Find(rows.Select(ToRequest), request.RequestHash);
    }

    private TradingAuditLogRecord ToRecord(TradingAuditWriteRequest request) =>
        new()
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
            OrderId = request.OrderId,
        };

    private static TradingAuditWriteRequest ToRequest(TradingAuditLogRecord row) =>
        new(
            row.AccountId,
            row.ParsedStage,
            row.Tool,
            row.IdempotencyKey,
            row.RequestHash,
            row.DryRun,
            row.Outcome,
            row.ContractCount,
            row.VenueErrorCode,
            row.OrderId);

    /// <summary>
    /// Postgres unique violations surface as <see cref="DbUpdateException"/> whose inner exception exposes
    /// <c>SqlState</c> 23505. Reading the property keeps this project off a direct Npgsql reference.
    /// </summary>
    private static bool IsUniqueViolation(DbUpdateException exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            System.Reflection.PropertyInfo? sqlState = current.GetType().GetProperty("SqlState");
            if (sqlState?.GetValue(current) is string state && state == "23505")
            {
                return true;
            }
        }

        return false;
    }
}
