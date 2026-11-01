using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>Backfill windows stay inside the measured edge and the 1,000-bar page (gh#899).</summary>
public sealed class BarBackfillPlanTests
{
    private static readonly DateTimeOffset _now = new(2026, 8, 18, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FiveThousandMinuteBars_AreFivePagesOfOneThousand()
    {
        DateTimeOffset start = _now - TimeSpan.FromMinutes(BarBackfillPlan.WindowBars);
        IReadOnlyList<BarBackfillWindow> windows = BarBackfillPlan.Windows(1, _now, start);

        windows.Should().ContainSingle();
        windows[0].Pages.Should().HaveCount(5);
        windows[0].Pages.Should().OnlyContain(
            page => page.Duration == TimeSpan.FromMinutes(BarBackfillPlan.PageBars));
        windows[0].Pages[0].Start.Should().Be(start);
        windows[0].Pages[^1].End.Should().Be(_now);
    }

    [Fact]
    public void AShortRemainder_IsItsOwnPage()
    {
        DateTimeOffset start = _now - TimeSpan.FromMinutes(1_500);
        IReadOnlyList<BarRange> pages = BarBackfillPlan.Windows(1, _now, start)
            .SelectMany(window => window.Pages)
            .ToArray();

        pages.Should().HaveCount(2);
        pages[0].Duration.Should().Be(TimeSpan.FromMinutes(BarBackfillPlan.PageBars));
        pages[1].Duration.Should().Be(TimeSpan.FromMinutes(500));
    }

    [Fact]
    public void MinuteBars_StopAtSixtyThreeDays()
    {
        IReadOnlyList<BarBackfillWindow> windows = BarBackfillPlan.Windows(1, _now, notBeforeUtc: null);

        windows[0].Range.Start.Should().Be(_now - TimeSpan.FromDays(BarBackfillPlan.MinuteDepthDays));
        windows[^1].Range.End.Should().Be(_now);
    }

    [Fact]
    public void HourBars_StopAtOneYear()
    {
        IReadOnlyList<BarBackfillWindow> windows = BarBackfillPlan.Windows(60, _now, notBeforeUtc: null);

        windows[0].Range.Start.Should().Be(_now - TimeSpan.FromDays(BarBackfillPlan.HourDepthDays));
    }

    [Fact]
    public void AStartOlderThanTheEdge_IsClampedForward()
    {
        DateTimeOffset tooOld = _now - TimeSpan.FromDays(BarBackfillPlan.MinuteDepthDays + 10);
        IReadOnlyList<BarBackfillWindow> windows = BarBackfillPlan.Windows(1, _now, tooOld);

        windows[0].Range.Start.Should().Be(_now - TimeSpan.FromDays(BarBackfillPlan.MinuteDepthDays));
    }

    [Fact]
    public void ALaterStart_IsKept()
    {
        DateTimeOffset later = _now - TimeSpan.FromDays(2);
        IReadOnlyList<BarBackfillWindow> windows = BarBackfillPlan.Windows(1, _now, later);

        windows[0].Range.Start.Should().Be(later);
    }
}
