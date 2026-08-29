using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;

namespace MarqSpec.Mcp.TopstepX.Tests.Domain;

/// <summary>
/// Volume-derived levels from a fixed cell/profile fixture — hand-checked, never a bar-spread POC.
/// </summary>
/// <remarks>
/// <para>
/// The cells and the 70% value area are the ones <c>VolumeProfileTests.Poc_IsThePriceWithTheMostVolume</c>
/// already derived: POC <c>5000.00</c>, VAL <c>5000.00</c>, VAH <c>5000.25</c>, volumes 3 / 10 / 4.
/// What this file adds is the line-to-zone step gh#257 settled, and the refusals a volume method
/// must make that a profile aggregate does not.
/// </para>
/// <para>
/// Every number below is worked from that table and the production defaults. Nothing here captures
/// an output and pins it.
/// </para>
/// </remarks>
public sealed class VolumeLevelMethodTests
{
    private const string Es = "ES";
    private const string Front = "CON.F.US.EP.U26";
    private const string Next = "CON.F.US.EP.Z26";
    private const int FiveMinutes = 5;

    private static readonly DateTimeOffset _bucket1430 = new(2026, 8, 18, 14, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _bucket1435 = new(2026, 8, 18, 14, 35, 0, TimeSpan.Zero);

    /// <summary>
    /// The production defaults, so the zones below are the zones the tool serves.
    /// </summary>
    private static KeyLevelOptions Options => new();

    // ──────────────────────────────────────────────────────────────────────────────────────────────────
    //  THE WORKED FIXTURE — the same three cells VolumeProfileTests already added by hand.
    //
    //    14:30  4999.75 × (2+1) =  3
    //           5000.00 × (8+2) = 10   ← POC and VAL
    //           5000.25 × (4+0) =  4   ← VAH
    //    Total = 17. Three prices. Mean volume = 17 / 3.
    //
    //    significance = volume × 3 / 17
    //      POC / VAL   10 × 3 / 17 = 30/17
    //      VAH          4 × 3 / 17 = 12/17
    //      4999.75      3 × 3 / 17 =  9/17
    //
    //    Bars: two five-minute buckets, one contract. ATR 2 at both.
    //    Last close is 4999, below every zone.
    //    half-band = ATR × ZoneAtrMultiple / 2 = 2 × 0.5 / 2 = 0.5
    //    every zone is its price ± 0.5
    //
    //    Bar.Volume is 99_999 on purpose. A spreading rule that used it would move the answer.
    // ──────────────────────────────────────────────────────────────────────────────────────────────────
    private static IReadOnlyList<FootprintCell> Cells =>
    [
        new(Es, FiveMinutes, _bucket1430, 4999.75m, BuyVolume: 2, SellVolume: 1),
        new(Es, FiveMinutes, _bucket1430, 5000.00m, BuyVolume: 8, SellVolume: 2),
        new(Es, FiveMinutes, _bucket1430, 5000.25m, BuyVolume: 4, SellVolume: 0),
    ];

    private static VolumeProfile Profile => VolumeProfileAggregator.From(Cells);

    private static IReadOnlyList<Bar> Fixture =>
    [
        new(_bucket1430, 5000m, 5001m, 4999m, 5000m, 99_999, Front),
        new(_bucket1435, 5000m, 5000m, 4998m, 4999m, 99_999, Front),
    ];

    private static IReadOnlyList<decimal?> FlatAtr => [2m, 2m];

    private static IReadOnlyList<KeyLevelZone> Detect(VolumeLevelKind kind, IReadOnlyList<Bar>? bars = null)
    {
        using VolumeProfileScope scope = new(Profile);
        return new VolumeLevelMethod(kind).Detect(bars ?? Fixture, FlatAtr, Options);
    }

    [Fact]
    public void VolumePoc_IsAZoneCentredOnTheTapePoc_NotOnABarSpread()
    {
        // 5000.00 ± 0.5. Last close 4999 is under the bottom, so ApplyClose keeps it resistance.
        // Formed at the last bar that has ATR — both do, so the last bar's open.
        IReadOnlyList<KeyLevelZone> zones = Detect(VolumeLevelKind.PointOfControl);

        zones.Should().ContainSingle();
        KeyLevelZone zone = zones[0];
        zone.Bottom.Should().Be(4999.50m);
        zone.Top.Should().Be(5000.50m);
        zone.Midpoint.Should().Be(5000.00m);
        zone.Significance.Should().Be(30m / 17m);
        zone.Kind.Should().Be(KeyLevelKind.Resistance);
        zone.TouchCount.Should().Be(1);
        zone.FormedAtBucket.Should().Be(_bucket1435);
        zone.Period.Should().Be("poc");
    }

    [Fact]
    public void VolumeVah_IsAZoneCentredOnTheValueAreaHigh()
    {
        IReadOnlyList<KeyLevelZone> zones = Detect(VolumeLevelKind.ValueAreaHigh);

        zones.Should().ContainSingle();
        KeyLevelZone zone = zones[0];
        zone.Bottom.Should().Be(4999.75m);
        zone.Top.Should().Be(5000.75m);
        zone.Midpoint.Should().Be(5000.25m);
        zone.Significance.Should().Be(12m / 17m);
        zone.Kind.Should().Be(KeyLevelKind.Resistance);
        zone.Period.Should().Be("vah");
    }

    [Fact]
    public void VolumeVal_IsAZoneCentredOnTheValueAreaLow()
    {
        // VAL is the POC on this fixture, so the zone is the POC zone again.
        IReadOnlyList<KeyLevelZone> zones = Detect(VolumeLevelKind.ValueAreaLow);

        zones.Should().ContainSingle();
        KeyLevelZone zone = zones[0];
        zone.Bottom.Should().Be(4999.50m);
        zone.Top.Should().Be(5000.50m);
        zone.Midpoint.Should().Be(5000.00m);
        zone.Significance.Should().Be(30m / 17m);
        zone.Period.Should().Be("val");
    }

    [Fact]
    public void VolumeTraded_IsEveryOtherPriceTheTapePrinted()
    {
        // POC/VAL 5000.00 and VAH 5000.25 are the named methods. The leftover print is 4999.75.
        // 4999.75 ± 0.5 = [4999.25, 5000.25]. Last close 4999 is under the bottom → resistance.
        IReadOnlyList<KeyLevelZone> zones = Detect(VolumeLevelKind.Traded);

        zones.Should().ContainSingle();
        KeyLevelZone zone = zones[0];
        zone.Bottom.Should().Be(4999.25m);
        zone.Top.Should().Be(5000.25m);
        zone.Midpoint.Should().Be(4999.75m);
        zone.Significance.Should().Be(9m / 17m);
        zone.Kind.Should().Be(KeyLevelKind.Resistance);
        zone.Period.Should().BeNull();
    }

    [Fact]
    public void ChangingBarVolume_DoesNotMoveAVolumeLevel()
    {
        // The spreading-rule failure: a POC that walks when the bar's Volume changes. The profile
        // is fixed; only the OHLCV volume moved.
        IReadOnlyList<Bar> quiet =
        [
            Fixture[0] with { Volume = 1 },
            Fixture[1] with { Volume = 1 },
        ];

        Detect(VolumeLevelKind.PointOfControl, quiet).Single().Midpoint
            .Should().Be(Detect(VolumeLevelKind.PointOfControl).Single().Midpoint);
    }

    [Fact]
    public void ASplicedSeries_IsRefused_EvenWhenASingleContractProfileIsBound()
    {
        // The profile is honest — one contract's cells. The bars are not. Answering from the
        // profile anyway would hide a roll behind a well-formed POC.
        IReadOnlyList<Bar> spliced =
        [
            Fixture[0],
            new(_bucket1435, 5140m, 5141m, 5139m, 5140m, 99_999, Next),
        ];

        Action detect = () => Detect(VolumeLevelKind.PointOfControl, spliced);

        detect.Should().Throw<ArgumentException>().WithMessage("*contract roll*");
    }

    [Fact]
    public void DetectWithoutABoundProfile_Refuses_RatherThanSpreadingBarVolume()
    {
        Action detect = () => new VolumeLevelMethod(VolumeLevelKind.PointOfControl)
            .Detect(Fixture, FlatAtr, Options);

        detect.Should().Throw<InvalidOperationException>().WithMessage("*spreading*");
    }

    [Fact]
    public void EachKindCarriesItsOwnStableLowercaseName_AndTheyShareOneFamily()
    {
        VolumeLevelKind[] kinds =
        [
            VolumeLevelKind.PointOfControl,
            VolumeLevelKind.ValueAreaHigh,
            VolumeLevelKind.ValueAreaLow,
            VolumeLevelKind.Traded,
        ];

        kinds.Select(VolumeLevels.NameOf).Should().Equal(
            "volume-poc", "volume-vah", "volume-val", "volume-traded");
        kinds.Select(k => new VolumeLevelMethod(k).Family).Should().OnlyContain(f => f == "volume");
    }

    [Fact]
    public void Compute_IsAPureFunctionOfTheProfileItIsHanded()
    {
        // Same bars, same ATR, same options, two profiles. The POC moves with the tape, not the bars.
        VolumeProfile other = VolumeProfileAggregator.From(
        [
            new FootprintCell(Es, FiveMinutes, _bucket1430, 4990m, 50, 0),
            new FootprintCell(Es, FiveMinutes, _bucket1430, 5000m, 5, 0),
        ]);

        IReadOnlyList<KeyLevelZone> fromTape = VolumeLevels.Compute(
            Fixture, FlatAtr, Options, Profile, VolumeLevelKind.PointOfControl);
        IReadOnlyList<KeyLevelZone> fromOther = VolumeLevels.Compute(
            Fixture, FlatAtr, Options, other, VolumeLevelKind.PointOfControl);

        fromTape.Single().Midpoint.Should().Be(5000.00m);
        fromOther.Single().Midpoint.Should().Be(4990m);
    }
}
