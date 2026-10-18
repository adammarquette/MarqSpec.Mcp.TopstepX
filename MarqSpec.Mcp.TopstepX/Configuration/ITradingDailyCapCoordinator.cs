namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>Serialises daily cap checks and conservative contract rollups per account (ADR-0024).</summary>
public interface ITradingDailyCapCoordinator
{
    /// <summary>
    /// Holds a per-account advisory lock for the current database transaction until disposed.
    /// </summary>
    Task<IAsyncDisposable> AcquireAccountDayLockAsync(int accountId, CancellationToken cancellationToken);

    /// <summary>
    /// Contracts counted toward the UTC-day cap, including failed and in-flight transmits.
    /// </summary>
    Task<int> GetConservativeContractTotalTodayUtcAsync(int accountId, CancellationToken cancellationToken);
}
