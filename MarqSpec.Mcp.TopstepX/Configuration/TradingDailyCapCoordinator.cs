using MarqSpec.Mcp.TopstepX.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>Postgres advisory xact lock plus conservative audit rollups for the UTC calendar day.</summary>
public sealed class TradingDailyCapCoordinator(TopstepXDbContext database, TimeProvider clock)
    : ITradingDailyCapCoordinator
{
    /// <summary>Distinct advisory lock class for trading daily caps (gh#711).</summary>
    public const int AdvisoryLockClassId = 0x0711_0024;

    private static readonly string[] _capTools =
    [
        TradingMcpTools.PlaceOrderOperation,
        TradingMcpTools.ModifyOrderOperation,
    ];

    private static readonly string[] _conservativeOutcomes =
    [
        TradingAuditOutcomes.Attempted,
        TradingAuditOutcomes.Live,
        TradingAuditOutcomes.Failed,
        TradingAuditOutcomes.Unknown,
    ];

    /// <inheritdoc />
    public async Task<IAsyncDisposable> AcquireAccountDayLockAsync(
        int accountId,
        CancellationToken cancellationToken)
    {
        IDbContextTransaction transaction = await database.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await database.Database
            .ExecuteSqlRawAsync(
                "SELECT pg_advisory_xact_lock({0}, {1})",
                [AdvisoryLockClassId, accountId],
                cancellationToken)
            .ConfigureAwait(false);

        return new TransactionScope(transaction);
    }

    /// <inheritdoc />
    public async Task<int> GetConservativeContractTotalTodayUtcAsync(
        int accountId,
        CancellationToken cancellationToken)
    {
        DateTimeOffset dayStart = UtcDayStart(clock.GetUtcNow());

        return await database.TradingAuditLog
            .AsNoTracking()
            .Where(row =>
                row.AccountId == accountId
                && row.AttemptedAt >= dayStart
                && _capTools.Contains(row.Tool)
                && _conservativeOutcomes.Contains(row.Outcome))
            .SumAsync(row => row.ContractCount, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>UTC midnight boundary used for daily caps until session-aware caps land.</summary>
    public static DateTimeOffset UtcDayStart(DateTimeOffset instant) =>
        new(instant.Year, instant.Month, instant.Day, 0, 0, 0, TimeSpan.Zero);

    private sealed class TransactionScope(IDbContextTransaction transaction) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await transaction.CommitAsync().ConfigureAwait(false);
            await transaction.DisposeAsync().ConfigureAwait(false);
        }
    }
}
