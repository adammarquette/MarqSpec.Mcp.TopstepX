using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;

namespace MarqSpec.Mcp.TopstepX.Tests.Domain;

/// <summary>
/// Which product session an instant belongs to, and which one has already closed.
/// </summary>
public sealed class ProductSessionTests
{
    private static readonly BarSessionCalendar _calendar = BarSessionCalendar.Parse("16:00", []);

    private static DateTimeOffset At(int year, int month, int day, int hour, int minute) =>
        MarketClock.FromMarket(new DateOnly(year, month, day), new TimeOnly(hour, minute)).ToUniversalTime();

    [Fact]
    public void Containing_WhenTheMarketIsOpen_ReturnsThatSession()
    {
        // Tuesday 09:30 Central is the session that opened Monday evening and closes Tuesday afternoon.
        ProductSession? session = ProductSessions.Containing(_calendar, At(2026, 8, 18, 9, 30));

        session.Should().NotBeNull();
        session!.Value.TradeDate.Should().Be(new DateOnly(2026, 8, 18));
        session.Value.OpenUtc.Should().Be(At(2026, 8, 17, 17, 0));
        session.Value.CloseUtc.Should().Be(At(2026, 8, 18, 16, 0));
    }

    [Fact]
    public void Containing_WhenTheMarketIsInMaintenance_ReturnsNull()
    {
        ProductSession? session = ProductSessions.Containing(_calendar, At(2026, 8, 18, 16, 30));

        session.Should().BeNull();
    }

    [Fact]
    public void LatestClosed_WhenTheMarketIsShut_ReturnsTheSessionThatEnded()
    {
        // Saturday noon is shut. The session that closed is Friday's, which opened Thursday evening.
        ProductSession? session = ProductSessions.LatestClosed(_calendar, At(2026, 8, 22, 12, 0));

        session.Should().NotBeNull();
        session!.Value.TradeDate.Should().Be(new DateOnly(2026, 8, 21));
        session.Value.OpenUtc.Should().Be(At(2026, 8, 20, 17, 0));
        session.Value.CloseUtc.Should().Be(At(2026, 8, 21, 16, 0));
    }

    [Fact]
    public void LatestClosed_WhenTheMarketIsOpen_ReturnsNull()
    {
        ProductSession? session = ProductSessions.LatestClosed(_calendar, At(2026, 8, 18, 9, 30));

        session.Should().BeNull();
    }

    [Fact]
    public void Containing_WhenTheInstantIsSaturday_ReturnsNull()
    {
        ProductSessions.Containing(_calendar, At(2026, 8, 22, 12, 0)).Should().BeNull();
    }

    [Fact]
    public void Containing_WhenItIsSundayEvening_ReturnsMondaysSession()
    {
        ProductSession? session = ProductSessions.Containing(_calendar, At(2026, 8, 16, 18, 0));

        session.Should().NotBeNull();
        session!.Value.TradeDate.Should().Be(new DateOnly(2026, 8, 17));
        session.Value.OpenUtc.Should().Be(At(2026, 8, 16, 17, 0));
    }

    [Fact]
    public void Containing_WhenItIsSundayMorning_ReturnsNull()
    {
        ProductSessions.Containing(_calendar, At(2026, 8, 16, 10, 0)).Should().BeNull();
    }

    [Fact]
    public void Containing_WhenThePreviousEveningBelongsToAHoliday_ReturnsNull()
    {
        BarSessionCalendar calendar = BarSessionCalendar.Parse("16:00", ["2026-08-19"]);

        ProductSessions.Containing(calendar, At(2026, 8, 18, 18, 0)).Should().BeNull();
    }

    [Fact]
    public void Containing_WhenTheHolidayEveningOpensTheNextSession_ReturnsThatSession()
    {
        BarSessionCalendar calendar = BarSessionCalendar.Parse("16:00", ["2026-08-19"]);

        ProductSession? session = ProductSessions.Containing(calendar, At(2026, 8, 19, 18, 0));

        session.Should().NotBeNull();
        session!.Value.TradeDate.Should().Be(new DateOnly(2026, 8, 20));
        session.Value.OpenUtc.Should().Be(At(2026, 8, 19, 17, 0));
    }

    [Fact]
    public void For_WhenDaylightSavingStartsAndEnds_KeepsTheWallClockClose()
    {
        ProductSession spring = ProductSessions.For(_calendar, new DateOnly(2030, 3, 11));
        ProductSession winter = ProductSessions.For(_calendar, new DateOnly(2026, 1, 5));
        ProductSession fall = ProductSessions.For(_calendar, new DateOnly(2026, 11, 2));

        spring.OpenUtc.Should().Be(At(2030, 3, 10, 17, 0));
        spring.CloseUtc.Should().Be(At(2030, 3, 11, 16, 0));
        spring.OpenUtc.UtcDateTime.Hour.Should().Be(22);
        spring.CloseUtc.UtcDateTime.Hour.Should().Be(21);

        winter.OpenUtc.Should().Be(At(2026, 1, 4, 17, 0));
        winter.CloseUtc.UtcDateTime.Hour.Should().Be(22);
        fall.OpenUtc.UtcDateTime.Hour.Should().Be(23);
        fall.CloseUtc.UtcDateTime.Hour.Should().Be(22);
        winter.CloseUtc.UtcDateTime.Hour.Should().NotBe(spring.CloseUtc.UtcDateTime.Hour);
    }

    [Fact]
    public void Containing_WhenTheSessionClosedEarly_StopsAtThatClose()
    {
        BarSessionCalendar calendar = BarSessionCalendar.Parse(
            "16:00",
            [],
            earlyCloses: ["2026-11-27 12:00"]);

        ProductSession? open = ProductSessions.Containing(calendar, At(2026, 11, 27, 11, 0));
        open.Should().NotBeNull();
        open!.Value.CloseUtc.Should().Be(At(2026, 11, 27, 12, 0));
        ProductSessions.Containing(calendar, At(2026, 11, 27, 13, 0)).Should().BeNull();
    }

    [Fact]
    public void LatestClosed_WhenTheSessionClosedEarly_ReturnsTheShortenedSession()
    {
        BarSessionCalendar calendar = BarSessionCalendar.Parse(
            "16:00",
            [],
            earlyCloses: ["2026-11-27 12:00"]);

        ProductSession? session = ProductSessions.LatestClosed(calendar, At(2026, 11, 27, 13, 0));

        session.Should().NotBeNull();
        session!.Value.TradeDate.Should().Be(new DateOnly(2026, 11, 27));
        session.Value.OpenUtc.Should().Be(At(2026, 11, 26, 17, 0));
        session.Value.CloseUtc.Should().Be(At(2026, 11, 27, 12, 0));
        session.Value.CloseUtc.Should().NotBe(At(2026, 11, 27, 16, 0));
    }
}
