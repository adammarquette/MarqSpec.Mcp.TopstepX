namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>What a try for the per-account writer lock found (gh#772).</summary>
public enum TradingWriterLockTake
{
    /// <summary>This process holds the lock.</summary>
    Held,

    /// <summary>Another session holds the lock.</summary>
    HeldByOther,

    /// <summary>The lock could not be confirmed. The write must not be sent.</summary>
    Unavailable,
}

/// <summary>
/// One trading writer per account (gh#772). Reads and the recorder do not take this lock.
/// </summary>
public interface ITradingAccountWriterLock
{
    /// <summary>
    /// Takes this process's session lock for <paramref name="accountId"/>, or confirms it is still held.
    /// </summary>
    /// <param name="accountId">The venue account a trading write would touch.</param>
    /// <param name="cancellationToken">Cancels the probe. A cancel is not a lost lock.</param>
    /// <returns>
    /// <see cref="TradingWriterLockTake.Held"/> when this process holds it.
    /// <see cref="TradingWriterLockTake.HeldByOther"/> when another session holds it.
    /// <see cref="TradingWriterLockTake.Unavailable"/> when the session cannot be confirmed.
    /// </returns>
    Task<TradingWriterLockTake> TryHoldAsync(int accountId, CancellationToken cancellationToken);
}
