namespace MarqSpec.Mcp.TopstepX.Telemetry;

/// <summary>Why a bar-backfill pass stopped. Closed, and safe as a metric tag (gh#899).</summary>
public static class BarBackfillStop
{
    /// <summary>The switch was off. Nothing was read.</summary>
    public const string Disabled = "backfill_disabled";

    /// <summary>Every planned page was asked.</summary>
    public const string Completed = "backfill_completed";

    /// <summary>The venue returned HTTP 429. The run does not continue.</summary>
    public const string RateLimited = "backfill_rate_limited";

    /// <summary>The host cancelled the run.</summary>
    public const string Cancelled = "backfill_cancelled";

    /// <summary>The pass threw. The host stays up.</summary>
    public const string Failed = "backfill_failed";

    /// <summary>Another process holds the backfill advisory lock.</summary>
    public const string LockHeld = "backfill_lock_held";

    /// <summary>The process is not the HTTP host. Stdio does not backfill.</summary>
    public const string SkippedTransport = "backfill_skipped_transport";

    /// <summary>The store was not reachable, so the pass did not run.</summary>
    public const string StoreUnavailable = "backfill_store_unavailable";

    /// <summary>Every value, for the cardinality gate.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Disabled,
        Completed,
        RateLimited,
        Cancelled,
        Failed,
        LockHeld,
        SkippedTransport,
        StoreUnavailable,
    ];
}
