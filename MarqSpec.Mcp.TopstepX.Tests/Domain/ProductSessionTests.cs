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
}
