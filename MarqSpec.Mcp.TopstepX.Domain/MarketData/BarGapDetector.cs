namespace MarqSpec.Mcp.TopstepX.Domain.MarketData;

/// <summary>
/// Works out which parts of a window the store is genuinely missing — the read half of cache-aside.
/// </summary>
/// <remarks>
/// <para>
/// "Missing" means <b>the venue was expected to publish a bar and the store does not have it</b>. That
/// qualifier is the whole point: a naive diff against a dense grid reports every weekend, every overnight
/// maintenance window and every holiday as a gap, so a cache built on it never stops asking the gateway for
/// data that does not exist.
/// </para>
/// <para>
/// This is a pure function of its arguments — no clock, no store, no venue. Given the same stored buckets and
/// the same calendar it returns the same ranges, which is what lets a test pin the behaviour exactly.
/// </para>
/// </remarks>
public static class BarGapDetector
{
    /// <summary>
    /// The largest number of buckets a single detection pass will enumerate.
    /// </summary>
    /// <remarks>
    /// A guard, not a tuning knob. One minute-resolution year is over half a million buckets, and a caller that
    /// asks for it has almost certainly made a mistake — refusing is better than spending a minute in a loop and
    /// then issuing hundreds of gateway pages.
    /// </remarks>
    public const int MaxBucketsPerPass = 250_000;

    /// <summary>
    /// Enumerates the bucket starts the venue is expected to have published in a window.
    /// </summary>
    /// <param name="window">The window to cover.</param>
    /// <param name="barSize">The bar size.</param>
    /// <param name="calendar">The session calendar deciding which buckets count.</param>
    /// <returns>The expected bucket starts, ascending.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="barSize"/> is not positive, or the window needs more than <see cref="MaxBucketsPerPass"/> buckets.
    /// </exception>
    public static IReadOnlyList<DateTimeOffset> ExpectedBuckets(
        BarRange window,
        TimeSpan barSize,
        BarSessionCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(calendar);

        if (barSize <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(barSize), barSize, "A bar size must be positive.");
        }

        if (window.IsEmpty)
        {
            return [];
        }

        long candidates = (window.End - window.Start).Ticks / barSize.Ticks;
        if (candidates > MaxBucketsPerPass)
        {
            throw new ArgumentOutOfRangeException(
                nameof(window),
                window,
                "The window spans " + candidates.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " buckets at this resolution, over the " + MaxBucketsPerPass.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " a single pass will enumerate. Narrow the window or use a coarser resolution.");
        }

        List<DateTimeOffset> expected = [];
        for (DateTimeOffset bucket = AlignUp(window.Start, barSize);
             bucket + barSize <= window.End;
             bucket += barSize)
        {
            if (calendar.IsExpectedBucket(bucket, barSize))
            {
                expected.Add(bucket);
            }
        }

        return expected;
    }

    /// <summary>
    /// Finds the ranges of a window the store is missing, coalescing adjacent missing buckets.
    /// </summary>
    /// <param name="storedBucketStarts">The bucket starts the store already holds. Order does not matter.</param>
    /// <param name="window">The window to cover.</param>
    /// <param name="barSize">The bar size.</param>
    /// <param name="calendar">The session calendar deciding which buckets count.</param>
    /// <returns>
    /// The missing ranges, ascending and non-overlapping. Empty when the store already covers every expected
    /// bucket — which is the case a cache-aside read must handle with <b>zero</b> gateway calls.
    /// </returns>
    /// <remarks>
    /// Coalescing is across <i>expected</i> buckets, not clock time: a Friday-evening-to-Sunday-evening stretch
    /// contains no expected buckets at all, so a gap either side of it stays two ranges only if real data sits
    /// between them. Two missing buckets separated purely by a weekend merge into one range, and fetching that
    /// range costs one paged request rather than two.
    /// </remarks>
    public static IReadOnlyList<BarRange> FindMissing(
        IReadOnlyCollection<DateTimeOffset> storedBucketStarts,
        BarRange window,
        TimeSpan barSize,
        BarSessionCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(storedBucketStarts);

        IReadOnlyList<DateTimeOffset> expected = ExpectedBuckets(window, barSize, calendar);
        if (expected.Count == 0)
        {
            return [];
        }

        HashSet<DateTimeOffset> stored = [.. storedBucketStarts];

        List<BarRange> missing = [];
        DateTimeOffset? runStart = null;
        DateTimeOffset runEnd = default;

        foreach (DateTimeOffset bucket in expected)
        {
            if (stored.Contains(bucket))
            {
                if (runStart is { } start)
                {
                    missing.Add(new BarRange(start, runEnd));
                    runStart = null;
                }

                continue;
            }

            runStart ??= bucket;
            runEnd = bucket + barSize;
        }

        if (runStart is { } tail)
        {
            missing.Add(new BarRange(tail, runEnd));
        }

        return missing;
    }

    /// <summary>
    /// Rounds an instant up to the next bucket boundary, leaving it alone if it already sits on one.
    /// </summary>
    /// <param name="instant">The instant.</param>
    /// <param name="barSize">The bar size.</param>
    /// <returns>The aligned instant.</returns>
    /// <remarks>
    /// Alignment is against the Unix epoch in UTC, which is the grid the venue publishes on. Aligning against
    /// the window's own start instead would produce a different bucket grid for every caller, and two callers
    /// asking overlapping questions would each miss the other's cached rows entirely.
    /// </remarks>
    public static DateTimeOffset AlignUp(DateTimeOffset instant, TimeSpan barSize)
    {
        if (barSize <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(barSize), barSize, "A bar size must be positive.");
        }

        long ticks = instant.UtcTicks;
        long remainder = ticks % barSize.Ticks;
        long aligned = remainder == 0 ? ticks : ticks + (barSize.Ticks - remainder);
        return new DateTimeOffset(aligned, TimeSpan.Zero);
    }

    /// <summary>
    /// Rounds an instant down to the bucket boundary containing it.
    /// </summary>
    /// <param name="instant">The instant.</param>
    /// <param name="barSize">The bar size.</param>
    /// <returns>The aligned instant.</returns>
    public static DateTimeOffset AlignDown(DateTimeOffset instant, TimeSpan barSize)
    {
        if (barSize <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(barSize), barSize, "A bar size must be positive.");
        }

        long ticks = instant.UtcTicks;
        return new DateTimeOffset(ticks - (ticks % barSize.Ticks), TimeSpan.Zero);
    }
}
