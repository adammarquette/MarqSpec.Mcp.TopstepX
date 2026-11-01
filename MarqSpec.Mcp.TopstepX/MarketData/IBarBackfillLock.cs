namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>A held backfill lock. Disposing it releases the lock.</summary>
public interface IBarBackfillLockHold : IAsyncDisposable, IBarBackfillLockProbe
{
}

/// <summary>
/// One Postgres session advisory lock for a backfill pass (gh#899). A second process skips.
/// </summary>
public interface IBarBackfillLock
{
    /// <summary>
    /// Tries to take the lock on a connection this call opens. Null when another session holds it.
    /// </summary>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The hold. Disposing it releases the lock.</returns>
    Task<IBarBackfillLockHold?> TryAcquireAsync(CancellationToken cancellationToken);
}
