using MarqSpec.Mcp.TopstepX.Tools;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// A few seconds of the last recorder-health report, shared by every request (gh#756).
/// </summary>
/// <remarks>
/// The service is scoped, one per request, because it reads a scoped store. The expensive part is the store,
/// and a monitor polls faster than a session changes. This memo lives for the process, so the second poll
/// inside <see cref="Ttl"/> does not open another connection or scan the tape. One build runs at a time; a
/// second caller waits, then reads what the first stored.
/// </remarks>
public sealed class RecorderHealthMemo
{
    /// <summary>How long a stored report is reused.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _singleFlight = new(1, 1);
    private readonly object _gate = new();
    private int _waiting;
    private Entry? _entry;

    /// <summary>Callers blocked in <see cref="EnterAsync"/>, including the one acquiring the build.</summary>
    internal int CallersWaiting => Volatile.Read(ref _waiting);

    /// <summary>The stored report when it is still inside <see cref="Ttl"/>.</summary>
    /// <param name="clock">The clock the expiry is compared against. Not the wall clock.</param>
    /// <returns>
    /// The report, the outcome it was counted as, when it was stored, and how many milliseconds that
    /// instant is behind <paramref name="clock"/> at this read. <see langword="null"/> when nothing is fresh.
    /// </returns>
    public (RecorderHealthReport.Report Report, string Outcome, DateTimeOffset CachedAt, long AgeMs)? Read(
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        lock (_gate)
        {
            if (_entry is not { } entry)
            {
                return null;
            }

            DateTimeOffset readAt = clock.GetUtcNow();
            if (readAt >= entry.ExpiresAt)
            {
                return null;
            }

            // The age is this read, not the moment the report was stored and not a later clock sample.
            long ageMs = (long)(readAt - entry.CachedAt).TotalMilliseconds;
            return (entry.Report, entry.Outcome, entry.CachedAt, ageMs);
        }
    }

    /// <summary>Waits until this caller is the one allowed to build.</summary>
    /// <param name="cancellationToken">The caller's cancellation token. Cancelling here builds nothing.</param>
    /// <returns>The task that completes when this caller holds the build.</returns>
    public async Task EnterAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _waiting);
        try
        {
            await _singleFlight.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }
    }

    /// <summary>Lets the next caller build.</summary>
    public void Exit() => _singleFlight.Release();

    /// <summary>Stores a report until <see cref="Ttl"/> has passed on <paramref name="clock"/>.</summary>
    /// <param name="clock">The clock the expiry is measured on.</param>
    /// <param name="report">The report.</param>
    /// <param name="outcome">The outcome already counted for <paramref name="report"/>.</param>
    public void Remember(TimeProvider clock, RecorderHealthReport.Report report, string outcome)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrEmpty(outcome);

        lock (_gate)
        {
            DateTimeOffset cachedAt = clock.GetUtcNow();
            _entry = new Entry(cachedAt, cachedAt + Ttl, report, outcome);
        }
    }

    private sealed record Entry(
        DateTimeOffset CachedAt,
        DateTimeOffset ExpiresAt,
        RecorderHealthReport.Report Report,
        string Outcome);
}
