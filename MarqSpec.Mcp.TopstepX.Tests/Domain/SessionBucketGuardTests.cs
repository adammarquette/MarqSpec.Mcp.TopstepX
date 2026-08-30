using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;

namespace MarqSpec.Mcp.TopstepX.Tests.Domain;

/// <summary>
/// A bucket that overhangs a session close is refused — at the stated resolution, not inferred from bars.
/// </summary>
/// <remarks>
/// <para>
/// gh#259's fourth routed finding, with the correction that the first 05:00/17:00 demonstration overhangs
/// the <b>maintenance window</b>, not Tuesday. The contaminating fixture is 07:00/19:00. The decision is
/// the plain rule: refuse every bucket that overhangs a close, rather than reason about which overhangs
/// contaminate. The 05:00 alignment is therefore refused too, and that is harmless.
/// </para>
/// <para>
/// The guard is handed <c>resolutionMinutes</c>. Inferring the width from the interval to the next bar is
/// exactly what <see cref="ILevelMethod"/>'s remarks refuse, and what a period's last bar cannot do —
/// its successor is in the next session, an unrelated maintenance window away.
/// </para>
/// </remarks>
public sealed class SessionBucketGuardTests
{
    private static BarSessionCalendar Calendar => new(new TimeOnly(16, 0), []);

    private static DateTimeOffset At(DateOnly date, int hour) =>
        MarketClock.FromMarket(date, new TimeOnly(hour, 0)).ToUniversalTime();

    private static Bar BarAt(DateOnly date, int hour, decimal high = 100m) =>
        new(At(date, hour), 100m, high, 99m, 100m, Volume: 1_000, ContractId: "CON.F.US.EP.U26");

    [Fact]
    public void TwelveHourBucketsAlignedToSevenAndNineteen_OverhangIntoTheNextSession()
    {
        // The corrected demonstration (gh#259, 07:00/19:00). Bar 2 opens Monday 07:00 and, at twelve
        // hours, runs to 19:00 — two hours inside Tuesday's session, which reopened at 17:00. The
        // calendar assigns it Monday's trade date because that is where it opens. Two bars cover
        // Monday, so a bar-per-period floor passes, and a high of 300 made in Tuesday's first two
        // hours would be reported as Monday's.
        DateOnly sunday = new(2026, 8, 16);
        DateOnly monday = new(2026, 8, 17);
        DateOnly tuesday = new(2026, 8, 18);

        IReadOnlyList<Bar> bars =
        [
            BarAt(sunday, 7),
            BarAt(sunday, 19, high: 120m),
            BarAt(monday, 7, high: 300m),
            BarAt(monday, 19),
            BarAt(tuesday, 7),
        ];

        SessionBucketGuard.OverhangsClose(720, Calendar, bars).Should().BeTrue(
            "Monday 07:00 + 12 hours is 19:00, past the 16:00 close and two hours into Tuesday");
    }

    [Fact]
    public void TwelveHourBucketsAlignedToTheReopen_AreAlsoRefused_EvenThoughTheyOnlyOverhangMaintenance()
    {
        // The original, incorrect demonstration: Monday 05:00 + 12 hours is 17:00, which is the
        // maintenance window, not Tuesday. Nothing trades there, so nothing contaminates. The
        // plain rule still refuses it — reasoning about which overhangs contaminate is what the
        // card rejected.
        DateOnly sunday = new(2026, 8, 16);
        DateOnly monday = new(2026, 8, 17);

        IReadOnlyList<Bar> bars =
        [
            BarAt(sunday, 17),
            BarAt(monday, 5, high: 300m),
            BarAt(monday, 17),
        ];

        SessionBucketGuard.OverhangsClose(720, Calendar, bars).Should().BeTrue(
            "Monday 05:00 + 12 hours is 17:00, an hour past the 16:00 close");
    }

    [Fact]
    public void AnHourlyBarThatEndsAtTheClose_DoesNotOverhang()
    {
        // Monday 15:00 + 60 minutes is 16:00, the close. Ending at the close is the last hour of
        // the session, not an overhang past it.
        IReadOnlyList<Bar> bars = [BarAt(new DateOnly(2026, 8, 17), 15)];

        SessionBucketGuard.OverhangsClose(60, Calendar, bars).Should().BeFalse();
    }

    [Fact]
    public void FiveMinuteBarsInsideTheSession_DoNotOverhang()
    {
        DateOnly monday = new(2026, 8, 17);
        IReadOnlyList<Bar> bars =
        [
            new(At(monday, 9).AddMinutes(0), 100m, 101m, 99m, 100m, 1_000, "CON.F.US.EP.U26"),
            new(At(monday, 9).AddMinutes(5), 100m, 101m, 99m, 100m, 1_000, "CON.F.US.EP.U26"),
        ];

        SessionBucketGuard.OverhangsClose(5, Calendar, bars).Should().BeFalse();
    }

    [Fact]
    public void ATwoHundredAndFortyMinuteResolution_OverhangsEvenWhenTheLastBucketIsNotInTheSeries()
    {
        // 17:00, 21:00, 01:00, 05:00, 09:00 all close inside the session. The next step, 13:00,
        // runs to 17:00 — an hour past the close. A store that never held that last bucket would
        // otherwise look clean. The guard walks the resolution against the calendar, so it does
        // not have to wait for the contaminating bar to be present.
        DateOnly sunday = new(2026, 8, 16);
        IReadOnlyList<Bar> barsWithoutTheOverhangingLast =
        [
            BarAt(sunday, 17),
            BarAt(sunday, 21),
        ];

        SessionBucketGuard.OverhangsClose(240, Calendar, barsWithoutTheOverhangingLast).Should().BeTrue(
            "240-minute steps from the reopen land on 13:00–17:00, which overhangs the 16:00 close");
    }

    [Fact]
    public void SundayMorningTwelveHourBar_OverhangsIntoMondaysOpen()
    {
        // Bar 0 of the corrected fixture: Sunday 07:00 carries no trade date (before the reopen)
        // and runs to 19:00, covering the first two hours of Monday's session. The same defect
        // mirrored, and the same rule closes it.
        IReadOnlyList<Bar> bars = [BarAt(new DateOnly(2026, 8, 16), 7)];

        SessionBucketGuard.OverhangsClose(720, Calendar, bars).Should().BeTrue();
    }
}
