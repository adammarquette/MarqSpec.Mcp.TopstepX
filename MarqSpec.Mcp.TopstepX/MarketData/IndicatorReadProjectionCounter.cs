namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// How many times a read in this process has opened a whole-series indicator replay (gh#347).
/// </summary>
/// <remarks>
/// <para>
/// Process-lifetime, and the lifetime is the point: <see cref="IndicatorCacheService"/> is scoped, so its
/// <see cref="IndicatorCacheService.Projections"/> resets every request. Startup warmup cannot tell a
/// one-off cold read from a process that has been replaying on every call unless something outlives the
/// scope.
/// </para>
/// <para>
/// A warm read — the probe finds nothing missing — does not increment. A request that asks eleven times
/// for one series increments once: the scope memo still collapses those calls to one replay.
/// </para>
/// <para>
/// Readable as <see cref="Replays"/>. The existing information line on a replay also prints the process
/// total, so an operator does not need a debugger or a dedicated tool.
/// </para>
/// </remarks>
public sealed class IndicatorReadProjectionCounter
{
    private long _replays;

    /// <summary>How many read-triggered whole-series replays this process has opened.</summary>
    public long Replays => Interlocked.Read(ref _replays);

    /// <summary>Records that a read opened a replay.</summary>
    public void RecordReplay() => Interlocked.Increment(ref _replays);
}
