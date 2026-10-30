using System.Data;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>Postgres session advisory lock plus Attempted-row rollups for the Topstep session day.</summary>
public sealed class TradingDailyCapCoordinator(
    TopstepXDbContext database,
    TimeProvider clock,
    HostTelemetry telemetry)
    : ITradingDailyCapCoordinator
{
    private bool _lastReleaseFailed;

    /// <inheritdoc />
    public bool LastReleaseFailed => _lastReleaseFailed;

    /// <summary>Distinct advisory lock class for trading daily caps (gh#711).</summary>
    public const int AdvisoryLockClassId = 0x0711_0024;

    private static readonly string[] _capTools =
    [
        TradingMcpTools.PlaceOrderOperation,
        TradingMcpTools.ModifyOrderOperation,
        TradingMcpTools.PartialClosePositionOperation,
    ];

    /// <inheritdoc />
    public async Task<IAsyncDisposable> AcquireAccountDayLockAsync(
        int accountId,
        CancellationToken cancellationToken)
    {
        _lastReleaseFailed = false;
        await database.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await database.Database
                .ExecuteSqlRawAsync(
                    "SELECT pg_advisory_lock({0}, {1})",
                    [AdvisoryLockClassId, accountId],
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            if (database.Database.GetDbConnection().State == ConnectionState.Open)
            {
                await database.Database.CloseConnectionAsync().ConfigureAwait(false);
            }

            throw;
        }

        return new SessionLock(database, AdvisoryLockClassId, accountId, this);
    }

    private void NoteReleaseFailed()
    {
        _lastReleaseFailed = true;
        telemetry.RecordBookkeepingFault();
    }

    /// <inheritdoc />
    public async Task<int> GetConservativeContractTotalTodayUtcAsync(
        int accountId,
        CancellationToken cancellationToken)
    {
        DateTimeOffset sessionStart = TradingCapSessionCalendar.SessionDayStartUtc(clock.GetUtcNow());

        return await database.TradingAuditLog
            .AsNoTracking()
            .Where(row =>
                row.AccountId == accountId
                && row.AttemptedAt >= sessionStart
                && _capTools.Contains(row.Tool)
                && row.Outcome == TradingAuditOutcomes.Attempted)
            .SumAsync(row => row.ContractCount, cancellationToken)
            .ConfigureAwait(false);
    }

    private sealed class SessionLock(
        TopstepXDbContext database,
        int classId,
        int accountId,
        TradingDailyCapCoordinator owner) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            // Neither the unlock nor the connection close may throw. A database blip here is after the
            // trading result was decided, and a throw used to become SafeToRetry while a Live row existed.
            Exception? failure = null;
            try
            {
                await database.Database
                    .ExecuteSqlRawAsync("SELECT pg_advisory_unlock({0}, {1})", [classId, accountId])
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            try
            {
                if (database.Database.GetDbConnection().State == ConnectionState.Open)
                {
                    await database.Database.CloseConnectionAsync().ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }

            if (failure is not null)
            {
                owner.NoteReleaseFailed();
            }
        }
    }
}
