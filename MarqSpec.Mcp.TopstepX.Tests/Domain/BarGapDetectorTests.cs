using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;

namespace MarqSpec.Mcp.TopstepX.Tests.Domain;

/// <summary>
/// Diffing the store against what the venue actually owed us.
/// </summary>
public sealed class BarGapDetectorTests
{
    private static readonly TimeSpan _fiveMinutes = TimeSpan.FromMinutes(5);

    private static BarSessionCalendar Calendar(params string[] holidays) =>
        BarSessionCalendar.Parse("16:00", holidays);

    private static DateTimeOffset Central(int year, int month, int day, int hour, int minute) =>
        MarketClock.FromMarket(new DateOnly(year, month, day), new TimeOnly(hour, minute));

    [Fact]
    public void ExpectedBuckets_ExcludesTheWeekend()
    {
        // Friday 15:00 Central through Monday 09:00. Most of that span is closed, and a dense clock grid
        // would report every minute of it as missing.
        BarRange window = new(Central(2026, 8, 21, 15, 0), Central(2026, 8, 24, 9, 0));

        IReadOnlyList<DateTimeOffset> expected =
            BarGapDetector.ExpectedBuckets(window, _fiveMinutes, Calendar());

        // Friday 15:00-16:00 is 12 buckets; Sunday 17:00 through Monday 09:00 is 16 hours, 192 buckets.
        expected.Should().HaveCount(12 + 192);
        expected.Should().NotContain(b => b >= Central(2026, 8, 22, 0, 0) && b < Central(2026, 8, 23, 17, 0));
    }

    [Fact]
    public void FindMissing_ReturnsNothing_WhenTheStoreHasEveryExpectedBucket()
    {
        // The case the whole design exists for. Nothing missing means the caller issues ZERO vendor calls.
        BarRange window = new(Central(2026, 8, 18, 9, 0), Central(2026, 8, 18, 10, 0));
        IReadOnlyList<DateTimeOffset> expected =
            BarGapDetector.ExpectedBuckets(window, _fiveMinutes, Calendar());

        BarGapDetector.FindMissing(expected, [], window, _fiveMinutes, Calendar()).Should().BeEmpty();
    }

    [Fact]
    public void FindMissing_ReturnsNothing_ForAWeekendWindow_EvenWithAnEmptyStore()
    {
        // The termination property, stated directly: an empty store plus a closed market is not a gap. If
        // this ever returns a range, the cache asks the vendor for the weekend on every single call, forever.
        BarRange window = new(Central(2026, 8, 22, 0, 0), Central(2026, 8, 23, 12, 0));

        BarGapDetector.FindMissing([], [], window, _fiveMinutes, Calendar()).Should().BeEmpty();
    }

    [Fact]
    public void FindMissing_CoalescesAdjacentMissingBuckets()
    {
        BarRange window = new(Central(2026, 8, 18, 9, 0), Central(2026, 8, 18, 10, 0));
        IReadOnlyList<DateTimeOffset> expected =
            BarGapDetector.ExpectedBuckets(window, _fiveMinutes, Calendar());

        // Hold everything except three consecutive buckets in the middle.
        List<DateTimeOffset> stored = [.. expected.Where((_, i) => i is < 4 or > 6)];

        IReadOnlyList<BarRange> missing =
            BarGapDetector.FindMissing(stored, [], window, _fiveMinutes, Calendar());

        missing.Should().ContainSingle();
        missing[0].Start.Should().Be(expected[4]);
        missing[0].End.Should().Be(expected[6] + _fiveMinutes);
    }

    [Fact]
    public void FindMissing_KeepsSeparateGapsSeparate()
    {
        BarRange window = new(Central(2026, 8, 18, 9, 0), Central(2026, 8, 18, 10, 0));
        IReadOnlyList<DateTimeOffset> expected =
            BarGapDetector.ExpectedBuckets(window, _fiveMinutes, Calendar());

        List<DateTimeOffset> stored = [.. expected.Where((_, i) => i is not (2 or 8))];

        BarGapDetector.FindMissing(stored, [], window, _fiveMinutes, Calendar()).Should().HaveCount(2);
    }

    [Fact]
    public void FindMissing_MergesAcrossAClosedSession()
    {
        // Coalescing is over EXPECTED buckets, not clock time. A gap on Friday afternoon and one on Monday
        // morning with nothing but the weekend between them is one range, and fetching it costs one paged
        // request rather than two.
        BarRange window = new(Central(2026, 8, 21, 15, 30), Central(2026, 8, 24, 9, 30));

        IReadOnlyList<BarRange> missing =
            BarGapDetector.FindMissing([], [], window, _fiveMinutes, Calendar());

        missing.Should().ContainSingle();
        missing[0].Start.Should().Be(Central(2026, 8, 21, 15, 30));
    }

    [Fact]
    public void AlignUp_LeavesAnAlreadyAlignedInstantAlone()
    {
        DateTimeOffset aligned = new(2026, 8, 18, 14, 30, 0, TimeSpan.Zero);
        BarGapDetector.AlignUp(aligned, _fiveMinutes).Should().Be(aligned);
    }

    [Fact]
    public void AlignUp_RoundsForwardToTheNextBoundary()
    {
        DateTimeOffset instant = new(2026, 8, 18, 14, 31, 0, TimeSpan.Zero);
        BarGapDetector.AlignUp(instant, _fiveMinutes)
            .Should().Be(new DateTimeOffset(2026, 8, 18, 14, 35, 0, TimeSpan.Zero));
    }

    [Fact]
    public void AlignDown_RoundsBackToTheContainingBoundary()
    {
        DateTimeOffset instant = new(2026, 8, 18, 14, 34, 59, TimeSpan.Zero);
        BarGapDetector.AlignDown(instant, _fiveMinutes)
            .Should().Be(new DateTimeOffset(2026, 8, 18, 14, 30, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Alignment_IsAgainstTheEpoch_NotTheWindow()
    {
        // Two callers asking overlapping questions must land on the SAME bucket grid, or each one misses the
        // other's cached rows entirely and the store fills with duplicate near-identical series.
        DateTimeOffset a = BarGapDetector.AlignUp(new DateTimeOffset(2026, 8, 18, 9, 3, 0, TimeSpan.Zero), _fiveMinutes);
        DateTimeOffset b = BarGapDetector.AlignUp(new DateTimeOffset(2026, 8, 18, 9, 1, 0, TimeSpan.Zero), _fiveMinutes);

        a.Should().Be(b);
        a.Minute.Should().Be(5);
    }

    [Fact]
    public void FindMissing_ReportsAnUnattributedBucketTheCalendarDoesNotExpect()
    {
        // gh#412. 16:30 Central is inside the 16:00-17:00 maintenance window, so the calendar does not expect
        // it -- and the venue was measured publishing a bar there. A row the store holds but cannot attribute
        // is missing PROVENANCE even though it is not missing prices, so it has to be enumerated on top of the
        // grid or it is never asked for again.
        //
        // Mid-sequence deliberately: the expected buckets either side are stored, so the run this produces is
        // closed by the "stored" branch of the loop rather than falling out of its tail. An implementation
        // that appended the off-grid buckets instead of sorting them into the sequence would report the wrong
        // range here and still look right at the end of a window.
        BarRange window = new(Central(2026, 8, 18, 9, 0), Central(2026, 8, 19, 9, 0));
        DateTimeOffset offGrid = Central(2026, 8, 18, 16, 30);
        IReadOnlyList<DateTimeOffset> stored =
            BarGapDetector.ExpectedBuckets(window, _fiveMinutes, Calendar());

        IReadOnlyList<BarRange> missing =
            BarGapDetector.FindMissing(stored, [offGrid], window, _fiveMinutes, Calendar());

        missing.Should().ContainSingle();
        missing[0].Start.Should().Be(offGrid);
        missing[0].End.Should().Be(offGrid + _fiveMinutes);
    }

    [Fact]
    public void FindMissing_MergesAnOffGridUnattributedBucketIntoTheRunAroundIt()
    {
        // The off-grid buckets are SORTED into the grid, not appended to it, and this is the test that can
        // tell the difference. The coalescing loop reads its input as one ascending sequence, so an appended
        // 16:30 arriving after 17:00 splits what is really one run into two ranges -- a second paced venue
        // request, and a range whose start runs backwards past the one before it.
        //
        // 17:00 is the first bucket of the evening session, left unstored here so there IS a run for the
        // off-grid bucket to merge into.
        BarRange window = new(Central(2026, 8, 18, 9, 0), Central(2026, 8, 19, 9, 0));
        DateTimeOffset offGrid = Central(2026, 8, 18, 16, 30);
        DateTimeOffset reopen = Central(2026, 8, 18, 17, 0);
        List<DateTimeOffset> stored =
            [.. BarGapDetector.ExpectedBuckets(window, _fiveMinutes, Calendar()).Where(b => b != reopen)];

        IReadOnlyList<BarRange> missing =
            BarGapDetector.FindMissing(stored, [offGrid], window, _fiveMinutes, Calendar());

        missing.Should().ContainSingle("the off-grid bucket and the reopen are one coalesced run");
        missing[0].Start.Should().Be(offGrid);
        missing[0].End.Should().Be(reopen + _fiveMinutes);
    }

    [Fact]
    public void FindMissing_ReportsAnUnattributedBucketAtTheCloseItself()
    {
        // The boundary the test above cannot reach. 16:00 is the FIRST bucket the calendar stops expecting --
        // it sits immediately after 15:55, which is stored -- so it exercises the transition out of the grid
        // rather than a bucket sitting comfortably inside a closed stretch. Off by one either way and this is
        // the case that moves.
        BarRange window = new(Central(2026, 8, 18, 9, 0), Central(2026, 8, 19, 9, 0));
        DateTimeOffset atTheClose = Central(2026, 8, 18, 16, 0);
        IReadOnlyList<DateTimeOffset> stored =
            BarGapDetector.ExpectedBuckets(window, _fiveMinutes, Calendar());

        stored.Should().NotContain(atTheClose, "a bucket must close inside its session to be expected");

        IReadOnlyList<BarRange> missing =
            BarGapDetector.FindMissing(stored, [atTheClose], window, _fiveMinutes, Calendar());

        missing.Should().ContainSingle();
        missing[0].Start.Should().Be(atTheClose);
    }

    [Fact]
    public void FindMissing_IgnoresAnAttributedBucketTheCalendarDoesNotExpect()
    {
        // The guard on the two above, and the thing that keeps gh#408's accepted cost from growing. A bar the
        // venue published off the grid AND stamped with a contract is complete: it lacks nothing, so it must
        // not be enumerated. Admitting every off-grid stored bucket would also break the coalescing this
        // detector's whole saving rests on -- a stored bucket CLOSES a run, so one attributed bar inside a
        // maintenance window would split a run that today merges across it, and cost a second venue request.
        BarRange window = new(Central(2026, 8, 18, 9, 0), Central(2026, 8, 19, 9, 0));
        IReadOnlyList<DateTimeOffset> stored =
            [.. BarGapDetector.ExpectedBuckets(window, _fiveMinutes, Calendar()), Central(2026, 8, 18, 16, 30)];

        BarGapDetector.FindMissing(stored, [], window, _fiveMinutes, Calendar()).Should().BeEmpty();
    }

    [Fact]
    public void FindMissing_IgnoresAnUnattributedBucketOutsideTheWindow()
    {
        // The caller hands over what the store holds; the detector is still answering about ONE window. A
        // range reaching past the window's end is a fetch the caller never asked for, and on the last bucket
        // of a window it is the difference between one bar and one bar plus a bar that is still forming.
        BarRange window = new(Central(2026, 8, 18, 9, 0), Central(2026, 8, 18, 10, 0));
        IReadOnlyList<DateTimeOffset> stored =
            BarGapDetector.ExpectedBuckets(window, _fiveMinutes, Calendar());

        IReadOnlyList<BarRange> missing = BarGapDetector.FindMissing(
            stored,
            [Central(2026, 8, 18, 8, 55), Central(2026, 8, 18, 9, 58)],
            window,
            _fiveMinutes,
            Calendar());

        missing.Should().BeEmpty(
            "one bucket opens before the window and the other would close after it, so neither is this "
            + "window's to answer for");
    }

    [Fact]
    public void ExpectedBuckets_RefusesAWindowLargerThanOnePassWillEnumerate()
    {
        // A guard, not a tuning knob: a minute-resolution year is over half a million buckets, and a caller
        // asking for it has almost certainly made a mistake. Refusing beats spending a minute in a loop and
        // then issuing hundreds of paged requests.
        DateTimeOffset start = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        BarRange window = new(start, start.AddYears(5));

        Action detect = () => BarGapDetector.ExpectedBuckets(window, TimeSpan.FromMinutes(1), Calendar());

        detect.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*buckets*");
    }

    [Fact]
    public void ExpectedBuckets_IsEmpty_ForAnInvertedWindow()
    {
        BarRange window = new(Central(2026, 8, 18, 10, 0), Central(2026, 8, 18, 9, 0));
        BarGapDetector.ExpectedBuckets(window, _fiveMinutes, Calendar()).Should().BeEmpty();
    }
}
