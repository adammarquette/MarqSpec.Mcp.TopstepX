using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Tools;

namespace MarqSpec.Mcp.TopstepX.Tests.Tools;

/// <summary>
/// The earliest and latest bar a series actually contains — the fact a requested window past the vendor's
/// depth was otherwise silent about (gh#695).
/// </summary>
public sealed class AvailableBarRangeTests
{
    [Fact]
    public void AnEmptySeries_HasNoEarliestOrLatest()
    {
        ToolPayloads.AvailableBarRange available = ToolPayloads.ToAvailableRange(
            Array.Empty<ToolPayloads.BarPoint>());

        available.Earliest.Should().BeNull();
        available.Latest.Should().BeNull();
    }

    [Fact]
    public void ASingleBar_IsBothEndsOfTheRange()
    {
        DateTimeOffset t = new(2026, 8, 18, 14, 30, 0, TimeSpan.Zero);

        ToolPayloads.AvailableBarRange available = ToolPayloads.ToAvailableRange(
            [Point(t)]);

        available.Earliest.Should().Be(t);
        available.Latest.Should().Be(t);
    }

    [Fact]
    public void TwoBars_NameTheFirstAndLastOpen_NotTheRequestedWindow()
    {
        DateTimeOffset first = new(2026, 8, 18, 14, 30, 0, TimeSpan.Zero);
        DateTimeOffset last = first.AddMinutes(5);

        ToolPayloads.AvailableBarRange available = ToolPayloads.ToAvailableRange(
            [Point(first), Point(last)]);

        available.Earliest.Should().Be(first);
        available.Latest.Should().Be(last);
    }

    [Fact]
    public void AnEmptySessionSeries_HasNoEarliestOrLatest()
    {
        ToolPayloads.AvailableBarRange available = ToolPayloads.ToAvailableRange(
            Array.Empty<ToolPayloads.SessionBarPoint>());

        available.Earliest.Should().BeNull();
        available.Latest.Should().BeNull();
    }

    [Fact]
    public void SessionBars_UseTheSessionOpen_TheSameThingTMeansOnEverySeries()
    {
        DateTimeOffset first = new(2026, 8, 17, 22, 0, 0, TimeSpan.Zero);
        DateTimeOffset last = new(2026, 8, 18, 22, 0, 0, TimeSpan.Zero);

        ToolPayloads.AvailableBarRange available = ToolPayloads.ToAvailableRange(
        [
            new ToolPayloads.SessionBarPoint(
                new DateOnly(2026, 8, 18), first, first.AddHours(23), 1m, 2m, 0m, 1m, 10),
            new ToolPayloads.SessionBarPoint(
                new DateOnly(2026, 8, 19), last, last.AddHours(23), 1m, 2m, 0m, 1m, 10),
        ]);

        available.Earliest.Should().Be(first);
        available.Latest.Should().Be(last);
    }

    private static ToolPayloads.BarPoint Point(DateTimeOffset t) =>
        new(t, 1m, 2m, 0m, 1m, 10);
}
