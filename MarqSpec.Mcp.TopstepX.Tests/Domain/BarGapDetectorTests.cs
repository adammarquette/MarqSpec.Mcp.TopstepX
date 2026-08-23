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

        BarGapDetector.FindMissing(expected, window, _fiveMinutes, Calendar()).Should().BeEmpty();
    }

    [Fact]
    public void FindMissing_ReturnsNothing_ForAWeekendWindow_EvenWithAnEmptyStore()
    {
        // The termination property, stated directly: an empty store plus a closed market is not a gap. If
        // this ever returns a range, the cache asks the vendor for the weekend on every single call, forever.
        BarRange window = new(Central(2026, 8, 22, 0, 0), Central(2026, 8, 23, 12, 0));

        BarGapDetector.FindMissing([], window, _fiveMinutes, Calendar()).Should().BeEmpty();
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
            BarGapDetector.FindMissing(stored, window, _fiveMinutes, Calendar());

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

        BarGapDetector.FindMissing(stored, window, _fiveMinutes, Calendar()).Should().HaveCount(2);
    }

    [Fact]
    public void FindMissing_MergesAcrossAClosedSession()
    {
        // Coalescing is over EXPECTED buckets, not clock time. A gap on Friday afternoon and one on Monday
        // morning with nothing but the weekend between them is one range, and fetching it costs one paged
        // request rather than two.
        BarRange window = new(Central(2026, 8, 21, 15, 30), Central(2026, 8, 24, 9, 30));

        IReadOnlyList<BarRange> missing =
            BarGapDetector.FindMissing([], window, _fiveMinutes, Calendar());

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
