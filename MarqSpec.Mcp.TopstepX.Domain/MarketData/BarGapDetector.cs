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
    /// <paramref name="barSize"/> is not positive, the window needs more than <see cref="MaxBucketsPerPass"/>
    /// buckets, or its bucket grid runs past the end of the representable calendar — see
    /// <see cref="AlignUp"/>, which the tool boundary bounds ahead of this (gh#110).
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
    /// <param name="storedBucketStarts">
    /// The bucket starts the store holds <b>and can attribute</b> — a bar carrying a recorded contract. Order
    /// does not matter.
    /// </param>
    /// <param name="unattributedBucketStarts">
    /// The bucket starts the store holds with <b>no recorded contract</b>. Order does not matter, and a bucket
    /// named here is reported missing <i>whether or not the calendar expects it</i> — see the remarks.
    /// </param>
    /// <param name="window">The window to cover.</param>
    /// <param name="barSize">The bar size.</param>
    /// <param name="calendar">The session calendar deciding which buckets count.</param>
    /// <returns>
    /// The missing ranges, ascending and non-overlapping. Empty when the store already covers every expected
    /// bucket — which is the case a cache-aside read must handle with <b>zero</b> gateway calls.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Coalescing is across <i>expected</i> buckets, not clock time: a Friday-evening-to-Sunday-evening stretch
    /// contains no expected buckets at all, so a gap either side of it stays two ranges only if real data sits
    /// between them. Two missing buckets separated purely by a weekend merge into one range, and fetching that
    /// range costs one paged request rather than two.
    /// </para>
    /// <para>
    /// <b>The unattributed buckets are enumerated too, on top of the calendar's grid (gh#412).</b> Every other
    /// bucket in this pass is chosen by asking the calendar what the venue owed; these are chosen by what the
    /// store is actually <i>holding</i>, which is why they are passed in rather than derived. A bucket the
    /// calendar does not expect but the store nonetheless has — the venue publishes at 16:30 Central, inside
    /// the maintenance window, and was measured doing so — would otherwise never be enumerated, never be
    /// re-asked for, and so never heal: the caller's window keeps an unattributed run in it forever and reports
    /// its contract span as <c>Unknown</c> on every read. That is a permanently degraded answer rather than a
    /// visible fetch, which is the worse direction to fail in. A bucket the store holds <i>with</i> a contract
    /// is not enumerated off-grid: it lacks nothing, and admitting it would break the run coalescing this
    /// method's saving depends on.
    /// </para>
    /// <para>
    /// The bound is the one <see cref="ExpectedBuckets"/> uses — a bucket counts only when it opens at or after
    /// the window's start and closes at or before its end — so a missing range never reaches past the window
    /// the caller asked about.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<BarRange> FindMissing(
        IReadOnlyCollection<DateTimeOffset> storedBucketStarts,
        IReadOnlyCollection<DateTimeOffset> unattributedBucketStarts,
        BarRange window,
        TimeSpan barSize,
        BarSessionCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(storedBucketStarts);
        ArgumentNullException.ThrowIfNull(unattributedBucketStarts);

        IReadOnlyList<DateTimeOffset> candidates = WithUnattributed(
            ExpectedBuckets(window, barSize, calendar), unattributedBucketStarts, window, barSize);
        if (candidates.Count == 0)
        {
            return [];
        }

        HashSet<DateTimeOffset> stored = [.. storedBucketStarts];

        List<BarRange> missing = [];
        DateTimeOffset? runStart = null;
        DateTimeOffset runEnd = default;

        foreach (DateTimeOffset bucket in candidates)
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
    /// Merges the buckets the store holds unattributed into the calendar's expected grid, ascending.
    /// </summary>
    /// <param name="expected">The calendar-expected buckets, ascending.</param>
    /// <param name="unattributedBucketStarts">The bucket starts the store holds with no recorded contract.</param>
    /// <param name="window">The window to cover.</param>
    /// <param name="barSize">The bar size.</param>
    /// <returns>The union, ascending and distinct.</returns>
    /// <remarks>
    /// A bucket the calendar already expects is a no-op here — it is in the grid already, and an unattributed
    /// bucket is absent from the stored set anyway, so it is reported missing either way. The only thing this
    /// adds is the off-grid one, which is the whole of gh#412. The result is <b>sorted</b> rather than appended,
    /// because the coalescing loop reads its input as an ascending sequence: an out-of-order bucket would split
    /// one run into two and cost a second venue request for nothing.
    /// </remarks>
    private static IReadOnlyList<DateTimeOffset> WithUnattributed(
        IReadOnlyList<DateTimeOffset> expected,
        IReadOnlyCollection<DateTimeOffset> unattributedBucketStarts,
        BarRange window,
        TimeSpan barSize)
    {
        if (unattributedBucketStarts.Count == 0)
        {
            return expected;
        }

        HashSet<DateTimeOffset> union = [.. expected];
        bool added = false;
        foreach (DateTimeOffset bucket in unattributedBucketStarts)
        {
            // Written as a subtraction rather than "bucket + barSize <= window.End" on purpose: both operands
            // are representable instants, so the difference is always a valid TimeSpan, while the addition can
            // overflow on a bucket near the end of the calendar -- the same hazard AlignUp names.
            if (bucket >= window.Start && window.End - bucket >= barSize)
            {
                added |= union.Add(bucket);
            }
        }

        return added ? [.. union.Order()] : expected;
    }

    /// <summary>
    /// Rounds an instant up to the next bucket boundary, leaving it alone if it already sits on one.
    /// </summary>
    /// <param name="instant">The instant.</param>
    /// <param name="barSize">The bar size.</param>
    /// <returns>The aligned instant.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="barSize"/> is not positive, or the next bucket boundary above
    /// <paramref name="instant"/> is past <see cref="DateTimeOffset.MaxValue"/>.
    /// </exception>
    /// <remarks>
    /// <b>Rounding up near the end of the calendar has no answer, so this throws rather than inventing one.</b>
    /// Clamping to the last representable instant would move a bucket boundary off the grid and hand back a
    /// bucket that is not one — a wrong number wearing an ordinary face. The tool boundary refuses such a
    /// window first, naming the value, so a caller sees a readable error rather than this exception
    /// (<c>ToolGuards.LastServableEnd</c>, gh#110).
    /// <para>
    /// Alignment is against a fixed UTC grid anchored at the .NET epoch (<c>UtcTicks</c>), not against the
    /// window. Aligning against the window's own start would produce a different bucket grid for every caller,
    /// and two callers asking overlapping questions would each miss the other's cached rows entirely.
    /// </para>
    /// <para>
    /// The anchor is midnight, so for any bar size dividing 1440 minutes this is the same grid the venue
    /// publishes on — which covers every resolution anyone has asked for. It is <i>not</i> the Unix epoch, as
    /// this remark said until gh#48; the two agree for those sizes and diverge for one that divides neither an
    /// hour nor a day, 7 minutes being the obvious example.
    /// </para>
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
