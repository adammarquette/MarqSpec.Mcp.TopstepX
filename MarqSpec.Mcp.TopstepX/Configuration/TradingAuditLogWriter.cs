using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>Persists append-only trading audit rows (gh#709, gh#750).</summary>
public sealed class TradingAuditLogWriter(TopstepXDbContext database, TimeProvider clock) : ITradingAuditLogWriter
{
    /// <inheritdoc />
    public async Task WriteAsync(TradingAuditWriteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        TradingAuditLogRecord row = ToRecord(request);
        database.TradingAuditLog.Add(row);
        try
        {
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A failed insert stays Added. Leaving it tracked makes the next save insert it twice.
            DetachIfAdded(row);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<TradingWriterRefusalStored> FindStoredWriterRefusalAsync(
        Guid finaliseId,
        string refusalCode,
        int reversalCount,
        CancellationToken cancellationToken)
    {
        List<TradingAuditLogRecord> rows = await database.TradingAuditLog
            .AsNoTracking()
            .Where(row => row.FinaliseId == finaliseId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        bool reversed = reversalCount == 0 || rows.Exists(row =>
            row.Outcome == TradingAuditOutcomes.Attempted
            && row.ContractCount == -reversalCount
            && row.VenueErrorCode == refusalCode);
        bool refused = rows.Exists(row =>
            row.Outcome == TradingAuditOutcomes.Refused
            && row.VenueErrorCode == refusalCode);
        return new TradingWriterRefusalStored(reversed, refused);
    }

    /// <inheritdoc />
    public async Task AppendReversalAndRefusalAsync(
        TradingAuditWriteRequest reversal,
        TradingAuditWriteRequest refusal,
        Guid finaliseId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reversal);
        ArgumentNullException.ThrowIfNull(refusal);

        TradingAuditLogRecord reversalRow = ToRecord(reversal with { FinaliseId = finaliseId });
        TradingAuditLogRecord refusalRow = ToRecord(refusal with { FinaliseId = finaliseId });
        database.TradingAuditLog.Add(reversalRow);
        database.TradingAuditLog.Add(refusalRow);
        try
        {
            // Another Added or Modified entry on this scoped context would flush in the same save.
            RejectOtherPendingChanges(reversalRow, refusalRow);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            DetachIfAdded(reversalRow);
            DetachIfAdded(refusalRow);
            throw;
        }

        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction =
            await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            DetachIfAdded(reversalRow);
            DetachIfAdded(refusalRow);
            throw;
        }
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
            FinaliseId = request.FinaliseId,
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

    private void DetachIfAdded(TradingAuditLogRecord row)
    {
        if (database.Entry(row).State == EntityState.Added)
        {
            database.Entry(row).State = EntityState.Detached;
        }
    }

    private void RejectOtherPendingChanges(
        TradingAuditLogRecord reversalRow,
        TradingAuditLogRecord refusalRow)
    {
        foreach (EntityEntry entry in database.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }

            if (ReferenceEquals(entry.Entity, reversalRow) || ReferenceEquals(entry.Entity, refusalRow))
            {
                continue;
            }

            throw new InvalidOperationException(
                "Trading audit save found a pending "
                + entry.State
                + " "
                + entry.Metadata.ClrType.Name
                + ".");
        }
    }
}
