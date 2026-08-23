using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;

namespace MarqSpec.Mcp.TopstepX.Tests.Domain;

/// <summary>
/// The session model, which is the thing that makes cache-aside terminate.
/// </summary>
/// <remarks>
/// These are the cases that are easy to get subtly wrong, and each of them is expensive in a different
/// direction: a bucket wrongly called expected is a permanent phantom gap the server re-fetches forever, and a
/// bucket wrongly called unexpected is real market data that is never asked for.
/// </remarks>
public sealed class BarSessionCalendarTests
{
    private static readonly TimeSpan _fiveMinutes = TimeSpan.FromMinutes(5);

    private static BarSessionCalendar Calendar(params string[] holidays) =>
        BarSessionCalendar.Parse("16:00", holidays);

    /// <summary>Builds an instant from Central wall-clock, which is how the exchange states its rules.</summary>
    private static DateTimeOffset Central(int year, int month, int day, int hour, int minute) =>
        MarketClock.FromMarket(new DateOnly(year, month, day), new TimeOnly(hour, minute));

    [Fact]
    public void MidSessionBucket_IsExpected()
    {
        // Tuesday 2026-08-18, 09:30 Central.
        Calendar().IsExpectedBucket(Central(2026, 8, 18, 9, 30), _fiveMinutes).Should().BeTrue();
    }

    [Fact]
    public void SundayEveningBucket_IsExpected_BecauseItsTradeDateIsMonday()
    {
        // The off-by-one-evening rule, and the single most confusing thing about futures sessions: Sunday
        // 18:00 is not "the weekend", it is the opening leg of Monday's session.
        Calendar().IsExpectedBucket(Central(2026, 8, 16, 18, 0), _fiveMinutes).Should().BeTrue();
    }

    [Fact]
    public void SundayMorningBucket_IsNotExpected()
    {
        // Its trade date would be Sunday, and Sunday is not a trade date -- the session that would have
        // opened it was Saturday evening, which does not exist.
        Calendar().IsExpectedBucket(Central(2026, 8, 16, 10, 0), _fiveMinutes).Should().BeFalse();
    }

    [Fact]
    public void FridayEveningBucket_IsNotExpected_BecauseTheWeekHasClosed()
    {
        // Symmetric to the Sunday case and the one people get wrong in the other direction: Friday evening
        // would belong to Saturday, so there is no reopen.
        Calendar().IsExpectedBucket(Central(2026, 8, 21, 18, 0), _fiveMinutes).Should().BeFalse();
    }

    [Fact]
    public void SaturdayBucket_IsNotExpected_AtAnyHour()
    {
        BarSessionCalendar calendar = Calendar();
        calendar.IsExpectedBucket(Central(2026, 8, 22, 10, 0), _fiveMinutes).Should().BeFalse();
        calendar.IsExpectedBucket(Central(2026, 8, 22, 18, 0), _fiveMinutes).Should().BeFalse();
    }

    [Fact]
    public void MaintenanceWindowBucket_IsNotExpected()
    {
        // 16:00-17:00 Central. Between the close and the reopen there is nothing to publish.
        Calendar().IsExpectedBucket(Central(2026, 8, 18, 16, 30), _fiveMinutes).Should().BeFalse();
    }

    [Fact]
    public void BucketStraddlingTheClose_IsNotExpected()
    {
        // 15:58 + 5m closes at 16:03, past the 16:00 close. The venue never publishes it as a final bar, so
        // counting it as expected would report a hole that can never be filled.
        Calendar().IsExpectedBucket(Central(2026, 8, 18, 15, 58), _fiveMinutes).Should().BeFalse();
    }

    [Fact]
    public void BucketClosingExactlyOnTheClose_IsExpected()
    {
        // The boundary the previous test brackets: 15:55 + 5m closes exactly at 16:00, which is inside.
        Calendar().IsExpectedBucket(Central(2026, 8, 18, 15, 55), _fiveMinutes).Should().BeTrue();
    }

    [Fact]
    public void HolidayBucket_IsNotExpected()
    {
        Calendar("2026-08-19").IsExpectedBucket(Central(2026, 8, 19, 9, 30), _fiveMinutes).Should().BeFalse();
    }

    [Fact]
    public void EveningBeforeAHoliday_IsNotExpected_BecauseThatEveningBelongsToTheHoliday()
    {
        // The rule that is almost always missed. Tuesday 18:00 opens WEDNESDAY's session; if Wednesday is a
        // holiday, that session does not happen.
        Calendar("2026-08-19").IsExpectedBucket(Central(2026, 8, 18, 18, 0), _fiveMinutes).Should().BeFalse();
    }

    [Fact]
    public void EveningOfAHoliday_IsExpected_BecauseItBelongsToTheNextDay()
    {
        // The other half of the same rule, and the reason it cannot be expressed as "a holiday closes 24
        // hours": the holiday's own evening opens Thursday, and Thursday trades.
        Calendar("2026-08-19").IsExpectedBucket(Central(2026, 8, 19, 18, 0), _fiveMinutes).Should().BeTrue();
    }

    [Fact]
    public void SessionRules_HoldAcrossADaylightSavingChange()
    {
        // US DST ends 2026-11-01. The close is 16:00 CENTRAL on both sides of it; a rule written as a fixed
        // UTC offset would be an hour wrong for half the year, and the error would look like a data gap.
        BarSessionCalendar calendar = Calendar();

        calendar.IsExpectedBucket(Central(2026, 10, 30, 15, 55), _fiveMinutes).Should().BeTrue();
        calendar.IsExpectedBucket(Central(2026, 11, 2, 15, 55), _fiveMinutes).Should().BeTrue();
        calendar.IsExpectedBucket(Central(2026, 10, 30, 16, 30), _fiveMinutes).Should().BeFalse();
        calendar.IsExpectedBucket(Central(2026, 11, 2, 16, 30), _fiveMinutes).Should().BeFalse();
    }

    [Fact]
    public void TradeDateFor_ReportsTheSessionABucketBelongsTo()
    {
        BarSessionCalendar calendar = Calendar();

        calendar.TradeDateFor(Central(2026, 8, 18, 9, 30)).Should().Be(new DateOnly(2026, 8, 18));
        calendar.TradeDateFor(Central(2026, 8, 17, 18, 0)).Should().Be(new DateOnly(2026, 8, 18));
        calendar.TradeDateFor(Central(2026, 8, 18, 16, 30)).Should().BeNull();
    }

    [Fact]
    public void Parse_RefusesAMalformedSessionClose()
    {
        // Guessing here would be a silent, compounding error: this value decides what counts as missing data.
        Action parse = () => BarSessionCalendar.Parse("4pm", []);
        parse.Should().Throw<FormatException>().WithMessage("*4pm*");
    }

    [Fact]
    public void Parse_RefusesAMalformedHoliday()
    {
        Action parse = () => BarSessionCalendar.Parse("16:00", ["19 August 2026"]);
        parse.Should().Throw<FormatException>().WithMessage("*19 August 2026*");
    }

    [Fact]
    public void Constructor_RefusesANonPositiveMaintenanceWindow()
    {
        Action build = () => new BarSessionCalendar(new TimeOnly(16, 0), [], TimeSpan.Zero);
        build.Should().Throw<ArgumentOutOfRangeException>();
    }
}
