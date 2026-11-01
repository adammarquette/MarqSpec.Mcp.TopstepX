using MarqSpec.Mcp.TopstepX.Telemetry;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>The last backfill pass this process finished, for <c>get_recorder_health</c> (gh#899).</summary>
public sealed class BarBackfillStatus
{
    private readonly Lock _gate = new();
    private Snapshot? _last;

    /// <summary>The last finished pass, or null when this process has not finished one.</summary>
    public Snapshot? Last
    {
        get
        {
            lock (_gate)
            {
                return _last;
            }
        }
    }

    /// <summary>Replaces the last pass. A later pass overwrites an earlier one.</summary>
    /// <param name="snapshot">The pass that just finished.</param>
    public void Record(Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            _last = snapshot;
        }
    }

    /// <summary>One finished pass. Times are UTC. The stop reason is a closed tag.</summary>
    /// <param name="StartedUtc">When the pass started.</param>
    /// <param name="EndedUtc">When it finished. Null when it had not finished.</param>
    /// <param name="StopReason">One of <see cref="BarBackfillStop"/>.</param>
    /// <param name="Pages">Pages the cache was asked for.</param>
    /// <param name="Errors">Failures counted for the pass. Zero is a real count.</param>
    public sealed record Snapshot(
        DateTimeOffset StartedUtc,
        DateTimeOffset? EndedUtc,
        string StopReason,
        int Pages,
        int Errors);
}
