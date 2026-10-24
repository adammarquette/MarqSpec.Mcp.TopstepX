namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>Serialises daily cap checks and conservative contract rollups per account (ADR-0024).</summary>
public interface ITradingDailyCapCoordinator
{
    /// <summary>
    /// Holds a per-account advisory lock for the current database transaction until disposed.
    /// </summary>
    Task<IAsyncDisposable> AcquireAccountDayLockAsync(int accountId, CancellationToken cancellationToken);

    /// <summary>
    /// True when the most recent release on this instance failed. Test doubles that do not track a release
    /// keep the default, false. The gate reads this after dispose and does not let the failure become a retry.
    /// </summary>
    bool LastReleaseFailed => false;

    /// <summary>
    /// Contracts charged on <see cref="TradingAuditOutcomes.Attempted"/> rows for the current Topstep session day.
    /// </summary>
    Task<int> GetConservativeContractTotalTodayUtcAsync(int accountId, CancellationToken cancellationToken);
}
