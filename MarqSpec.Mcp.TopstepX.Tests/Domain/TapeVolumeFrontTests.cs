using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;

namespace MarqSpec.Mcp.TopstepX.Tests.Domain;

/// <summary>
/// Per-session tape volume names the front month. Numbers are added by hand, not captured
/// from the function under test (gh#219).
/// </summary>
/// <remarks>
/// <para>
/// Volume is <c>Size</c>, including <see cref="TradeDirection.Unknown"/>. Footprint cells
/// refuse Unknown so an unstated side cannot look like buying pressure; a front-month
/// choice that dropped those prints would pick the quieter contract during a roll.
/// </para>
/// <para>
/// A test that never creates two contracts cannot prove a roll. Every case below that
/// claims a winner or a changeover seeds both <c>U26</c> and <c>Z26</c>.
/// </para>
/// </remarks>
public sealed class TapeVolumeFrontTests
{
    private const string Es = "ES";
    private const string Front = "CON.F.US.EP.U26";
    private const string Next = "CON.F.US.EP.Z26";

    private static readonly BarSessionCalendar _calendar = BarSessionCalendar.Parse("16:00", []);

    private static readonly DateOnly _tuesday = new(2026, 8, 18);
    private static readonly DateOnly _wednesday = new(2026, 8, 19);

    [Fact]
    public void HighestVolumeWins_InOneSession()
    {
        // Tuesday: U26 prints 10 + 15 = 25. Z26 prints 40. 40 > 25, so Z26 is the front.
        VolumeFront measured = TapeVolumeFront.Measure(
            [
                Print(Central(2026, 8, 18, 10, 0), Front, 10, TradeDirection.Buy),
                Print(Central(2026, 8, 18, 10, 1), Front, 15, TradeDirection.Sell),
                Print(Central(2026, 8, 18, 11, 0), Next, 40, TradeDirection.Buy),
            ],
            _calendar);

        measured.ActiveContractId.Should().Be(Next);
        measured.ActiveSessionDate.Should().Be(_tuesday);
        measured.Changeover.Should().BeNull("one session cannot flip from a previous winner");
        measured.SessionVolumes.Should().BeEquivalentTo(
        [
            new ContractSessionVolume(_tuesday, Front, 25),
            new ContractSessionVolume(_tuesday, Next, 40),
        ]);
    }

    [Fact]
    public void UnknownDirection_CountsAsSize()
    {
        // U26 buys 50. Z26 has no stated side but prints 100. If Unknown were dropped —
        // the footprint rule — U26 would win 50 to 0. Size is still size.
        VolumeFront measured = TapeVolumeFront.Measure(
            [
                Print(Central(2026, 8, 18, 10, 0), Front, 50, TradeDirection.Buy),
                Print(Central(2026, 8, 18, 10, 1), Next, 100, TradeDirection.Unknown),
            ],
            _calendar);

        measured.ActiveContractId.Should().Be(Next);
        measured.SessionVolumes.Should().ContainSingle(v => v.ContractId == Next)
            .Which.Volume.Should().Be(100);
    }

    [Fact]
    public void ChangeoverIsTheSessionAndInstantTheFrontFlipped()
    {
        // Tuesday U26 leads 100 to 20.
        // Wednesday: 10:00 U26 +30 (30–0), 10:05 Z26 +20 (30–20), 10:10 Z26 +60 (30–80).
        // The session winner flips to Z26; the overtake is the 10:10 print.
        DateTimeOffset flip = Central(2026, 8, 19, 10, 10);

        VolumeFront measured = TapeVolumeFront.Measure(
            [
                Print(Central(2026, 8, 18, 10, 0), Front, 100, TradeDirection.Buy),
                Print(Central(2026, 8, 18, 11, 0), Next, 20, TradeDirection.Sell),
                Print(Central(2026, 8, 19, 10, 0), Front, 30, TradeDirection.Buy),
                Print(Central(2026, 8, 19, 10, 5), Next, 20, TradeDirection.Buy),
                Print(flip, Next, 60, TradeDirection.Unknown),
            ],
            _calendar);

        measured.ActiveContractId.Should().Be(Next);
        measured.ActiveSessionDate.Should().Be(_wednesday);
        measured.Changeover.Should().Be(new VolumeFrontChangeover(_wednesday, flip, Front, Next));
        measured.SessionVolumes.Should().BeEquivalentTo(
        [
            new ContractSessionVolume(_tuesday, Front, 100),
            new ContractSessionVolume(_tuesday, Next, 20),
            new ContractSessionVolume(_wednesday, Front, 30),
            new ContractSessionVolume(_wednesday, Next, 80),
        ]);
    }

    [Fact]
    public void ATie_HasNoUniqueFront()
    {
        // 50 and 50. Picking either would invent a front the tape did not name.
        VolumeFront measured = TapeVolumeFront.Measure(
            [
                Print(Central(2026, 8, 18, 10, 0), Front, 50, TradeDirection.Buy),
                Print(Central(2026, 8, 18, 10, 1), Next, 50, TradeDirection.Sell),
            ],
            _calendar);

        measured.ActiveContractId.Should().BeNull();
        measured.Changeover.Should().BeNull();
        measured.SessionVolumes.Should().BeEquivalentTo(
        [
            new ContractSessionVolume(_tuesday, Front, 50),
            new ContractSessionVolume(_tuesday, Next, 50),
        ]);
    }

    [Fact]
    public void APrintInTheMaintenanceWindow_DoesNotJoinASession()
    {
        // 16:30 Central is between the close and the reopen. A thousand Z26 contracts
        // there are still on the tape; they are not Tuesday's session volume.
        VolumeFront measured = TapeVolumeFront.Measure(
            [
                Print(Central(2026, 8, 18, 10, 0), Front, 1, TradeDirection.Buy),
                Print(Central(2026, 8, 18, 16, 30), Next, 1000, TradeDirection.Unknown),
            ],
            _calendar);

        measured.ActiveContractId.Should().Be(Front);
        measured.SessionVolumes.Should().ContainSingle()
            .Which.Should().Be(new ContractSessionVolume(_tuesday, Front, 1));
    }

    [Fact]
    public void EmptyPrints_HaveNoFront()
    {
        VolumeFront measured = TapeVolumeFront.Measure([], _calendar);

        measured.ActiveContractId.Should().BeNull();
        measured.ActiveSessionDate.Should().BeNull();
        measured.Changeover.Should().BeNull();
        measured.SessionVolumes.Should().BeEmpty();
    }

    [Fact]
    public void MixedInstruments_AreRefused()
    {
        Action measure = () => TapeVolumeFront.Measure(
            [
                Print(Central(2026, 8, 18, 10, 0), Front, 1, TradeDirection.Buy),
                new TradePrint(
                    "NQ",
                    "CON.F.US.ENQ.U26",
                    Central(2026, 8, 18, 10, 1),
                    18000m,
                    1,
                    TradeDirection.Buy),
            ],
            _calendar);

        measure.Should().Throw<ArgumentException>().WithMessage("*one instrument*");
    }

    [Fact]
    public void AZeroSizePrint_DoesNotMoveVolume()
    {
        VolumeFront measured = TapeVolumeFront.Measure(
            [
                Print(Central(2026, 8, 18, 10, 0), Front, 5, TradeDirection.Buy),
                Print(Central(2026, 8, 18, 10, 1), Next, 0, TradeDirection.Buy),
            ],
            _calendar);

        measured.ActiveContractId.Should().Be(Front);
        measured.SessionVolumes.Should().ContainSingle()
            .Which.Should().Be(new ContractSessionVolume(_tuesday, Front, 5));
    }

    private static DateTimeOffset Central(int year, int month, int day, int hour, int minute) =>
        MarketClock.FromMarket(new DateOnly(year, month, day), new TimeOnly(hour, minute));

    private static TradePrint Print(
        DateTimeOffset when,
        string contractId,
        long size,
        TradeDirection direction) =>
        new(Es, contractId, when, 5000m, size, direction);
}
