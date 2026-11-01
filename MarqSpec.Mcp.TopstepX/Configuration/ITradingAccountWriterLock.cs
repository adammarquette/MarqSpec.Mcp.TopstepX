namespace MarqSpec.Mcp.TopstepX.Configuration;

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
    /// True when this process holds the lock. False when another session holds it, or this session
    /// dropped it and could not take it back.
    /// </returns>
    Task<bool> TryHoldAsync(int accountId, CancellationToken cancellationToken);
}
