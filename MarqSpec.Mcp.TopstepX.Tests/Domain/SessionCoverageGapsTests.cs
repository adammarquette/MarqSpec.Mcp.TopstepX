using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;

namespace MarqSpec.Mcp.TopstepX.Tests.Domain;

/// <summary>
/// Holes left once covered ranges are taken out of a half-open window.
/// </summary>
public sealed class SessionCoverageGapsTests
{
    private static DateTimeOffset T(int minute) =>
        new(2026, 8, 18, 14, minute, 0, TimeSpan.Zero);

    private static SessionCoverageGaps.Interval Range(int start, int end) => new(T(start), T(end));

    [Fact]
    public void Find_WhenRangesMeet_ReportsNoGap()
    {
        // Adjacent ranges are one stretch. A one-instant hole where they touch would page a monitor for a
        // recorder that never stopped.
        IReadOnlyList<SessionCoverageGaps.Interval> gaps = SessionCoverageGaps.Find(
            T(0),
            T(10),
            [Range(0, 5), Range(5, 10)]);

        gaps.Should().BeEmpty();
    }

    [Fact]
    public void Find_WhenAHoleSitsBetweenRanges_ReportsThatHole()
    {
        IReadOnlyList<SessionCoverageGaps.Interval> gaps = SessionCoverageGaps.Find(
            T(0),
            T(10),
            [Range(0, 4), Range(6, 10)]);

        gaps.Should().Equal(new SessionCoverageGaps.Interval(T(4), T(6)));
    }

    [Fact]
    public void Find_WhenRangesOverlap_ReportsNoGap()
    {
        IReadOnlyList<SessionCoverageGaps.Interval> gaps = SessionCoverageGaps.Find(
            T(0),
            T(10),
            [Range(0, 7), Range(3, 10)]);

        gaps.Should().BeEmpty();
    }

    [Fact]
    public void Find_WhenTheWindowIsEmpty_ReportsNoGap()
    {
        IReadOnlyList<SessionCoverageGaps.Interval> gaps = SessionCoverageGaps.Find(T(5), T(5), [Range(0, 10)]);

        gaps.Should().BeEmpty();
    }

    [Fact]
    public void Find_WhenARangeFallsOutsideTheWindow_ReportsTheWholeWindowAsAGap()
    {
        IReadOnlyList<SessionCoverageGaps.Interval> gaps = SessionCoverageGaps.Find(
            T(10),
            T(20),
            [Range(0, 5)]);

        gaps.Should().Equal(new SessionCoverageGaps.Interval(T(10), T(20)));
    }

    [Fact]
    public void Find_WhenCoverageExtendsPastTheWindow_ReportsNoGap()
    {
        IReadOnlyList<SessionCoverageGaps.Interval> gaps = SessionCoverageGaps.Find(
            T(5),
            T(10),
            [Range(0, 20)]);

        gaps.Should().BeEmpty();
    }
}
