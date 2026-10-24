using FluentAssertions;
using MarqSpec.Mcp.TopstepX.MarketData;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>When a UTC day may be copied out, and when the daily pass next runs.</summary>
public sealed class TapeExportPolicyTests
{
    private static readonly DateOnly _day = new(2026, 10, 1);

    [Fact]
    public void NextRun_WhenNowIsBeforeSchedule_ReturnsTheSameDay()
    {
        DateTimeOffset now = new(2026, 10, 17, 5, 59, 0, TimeSpan.Zero);

        TapeExportPolicy.NextRun(new TimeOnly(6, 0), now)
            .Should().Be(new DateTimeOffset(2026, 10, 17, 6, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void NextRun_WhenNowIsAtSchedule_ReturnsTheNextDay()
    {
        DateTimeOffset now = new(2026, 10, 17, 6, 0, 0, TimeSpan.Zero);

        TapeExportPolicy.NextRun(new TimeOnly(6, 0), now)
            .Should().Be(new DateTimeOffset(2026, 10, 18, 6, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void NextRun_WhenNowIsAfterSchedule_ReturnsTheNextDay()
    {
        DateTimeOffset now = new(2026, 10, 17, 6, 0, 1, TimeSpan.Zero);

        TapeExportPolicy.NextRun(new TimeOnly(6, 0), now)
            .Should().Be(new DateTimeOffset(2026, 10, 18, 6, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void IsExportable_WhenTheDayHasNotEnded_IsFalse()
    {
        DateTimeOffset justBefore = TapeExportPolicy.DayEndUtc(_day) - TimeSpan.FromTicks(1);

        TapeExportPolicy.IsExportable(_day, justBefore).Should().BeFalse();
    }

    [Fact]
    public void IsExportable_WhenTheDayHasEnded_IsTrue()
    {
        DateTimeOffset atEnd = TapeExportPolicy.DayEndUtc(_day);

        TapeExportPolicy.IsExportable(_day, atEnd).Should().BeTrue();
    }
}
