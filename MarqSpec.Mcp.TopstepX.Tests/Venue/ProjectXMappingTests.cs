using FluentAssertions;
using MarqSpec.Client.ProjectX.Api.Models;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.Venue;

namespace MarqSpec.Mcp.TopstepX.Tests.Venue;

/// <summary>
/// The translation between the gateway's vocabulary and this server's.
/// </summary>
/// <remarks>
/// Every case here is one where getting it wrong produces a <b>plausible</b> answer rather than a failure: a
/// whole series shifted by a timezone offset, a short position reported long, a funded account read as
/// practice. None of them would announce themselves.
/// </remarks>
public sealed class ProjectXMappingTests
{
    // ── Timestamps ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AKindLessTimestamp_IsTreatedAsUtc_NotLocal()
    {
        // The gateway sends Unspecified. They ARE UTC, and letting .NET infer local would shift every bar and
        // every fill by the operator's offset -- a whole-series error that looks like nothing on a chart.
        DateTime unspecified = new(2026, 8, 18, 14, 30, 0, DateTimeKind.Unspecified);

        DateTimeOffset mapped = ProjectXMapping.ToUtc(unspecified);

        mapped.Offset.Should().Be(TimeSpan.Zero);
        mapped.UtcDateTime.Should().Be(new DateTime(2026, 8, 18, 14, 30, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void ALocalTimestamp_IsConverted_NotRelabelled()
    {
        DateTime local = new DateTime(2026, 8, 18, 14, 30, 0, DateTimeKind.Utc).ToLocalTime();
        local = DateTime.SpecifyKind(local, DateTimeKind.Local);

        ProjectXMapping.ToUtc(local).UtcDateTime
            .Should().Be(new DateTime(2026, 8, 18, 14, 30, 0, DateTimeKind.Utc));
    }

    // ── Bar units ────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(60, AggregateBarUnit.Minute, 1)]
    [InlineData(300, AggregateBarUnit.Minute, 5)]
    [InlineData(3600, AggregateBarUnit.Hour, 1)]
    [InlineData(14400, AggregateBarUnit.Hour, 4)]
    [InlineData(86400, AggregateBarUnit.Day, 1)]
    [InlineData(30, AggregateBarUnit.Second, 30)]
    public void ABarSize_IsExpressedInTheCoarsestExactUnit(int seconds, AggregateBarUnit unit, int number)
    {
        // A five-minute bar goes out as 5 minutes, not 300 seconds. Arithmetically identical; the gateway's
        // own limit is expressed in bars, and the coarse form is what its documentation uses.
        ProjectXMapping.ToBarUnit(TimeSpan.FromSeconds(seconds)).Should().Be((unit, number));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-60)]
    public void ANonPositiveBarSize_IsRefused(int seconds)
    {
        Action map = () => ProjectXMapping.ToBarUnit(TimeSpan.FromSeconds(seconds));
        map.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ASubSecondBarSize_IsRefused()
    {
        Action map = () => ProjectXMapping.ToBarUnit(TimeSpan.FromMilliseconds(500));
        map.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ── Positions ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AShortPosition_IsReportedNegative()
    {
        // The venue reports an UNSIGNED size plus a direction. Dropping the sign reports every short as a
        // long, which is the most consequential single mistake available in this file.
        VenuePosition mapped = ProjectXMapping.ToPosition(new Position
        {
            ContractId = "CON.F.US.EP.U26",
            Type = PositionType.Short,
            Size = 3,
            AveragePrice = 5000m,
            CreationTimestamp = new DateTime(2026, 8, 18, 14, 0, 0, DateTimeKind.Unspecified),
        });

        mapped.SignedSize.Should().Be(-3);
    }

    [Fact]
    public void ALongPosition_IsReportedPositive()
    {
        ProjectXMapping.ToPosition(new Position
        {
            ContractId = "CON.F.US.EP.U26",
            Type = PositionType.Long,
            Size = 3,
            AveragePrice = 5000m,
            CreationTimestamp = default,
        }).SignedSize.Should().Be(3);
    }

    [Fact]
    public void ADirectionlessNonZeroPosition_Throws_RatherThanReportingFlat()
    {
        // Reporting flat here would tell an operator they have no exposure when they do. Throwing is the only
        // answer that cannot be acted on wrongly.
        Action map = () => ProjectXMapping.ToPosition(new Position
        {
            ContractId = "CON.F.US.EP.U26",
            Type = PositionType.Undefined,
            Size = 2,
            AveragePrice = 5000m,
            CreationTimestamp = default,
        });

        map.Should().Throw<VenueException>().WithMessage("*signed exposure*");
    }

    [Fact]
    public void ADirectionlessZeroPosition_IsFlat()
    {
        ProjectXMapping.ToPosition(new Position
        {
            ContractId = "CON.F.US.EP.U26",
            Type = PositionType.Undefined,
            Size = 0,
            AveragePrice = 0m,
            CreationTimestamp = default,
        }).SignedSize.Should().Be(0);
    }

    // ── Sides ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BidIsBuy_AndAskIsSell()
    {
        // The gateway names the side of the BOOK, not the direction of the trade. Reading it the other way
        // inverts every order and fill report.
        ProjectXMapping.ToSide(OrderSide.Bid).Should().Be(VenueSide.Buy);
        ProjectXMapping.ToSide(OrderSide.Ask).Should().Be(VenueSide.Sell);
    }

    [Fact]
    public void AnUnrecognisedSide_IsUnknown_NotADefaultDirection()
    {
        ProjectXMapping.ToSide((OrderSide)99).Should().Be(VenueSide.Unknown);
    }

    [Fact]
    public void AnUnrecognisedOrderStatus_IsUnknown()
    {
        ProjectXMapping.ToStatus((OrderStatus)99).Should().Be(VenueOrderStatus.Unknown);
    }

    // ── Account stages ───────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("PRAC-50K-1234", AccountStage.Practice)]
    [InlineData("PRACTICEJUL3", AccountStage.Practice)]
    [InlineData("50KTC-V2-DLL-0000", AccountStage.Evaluation)]
    [InlineData("150KTC-V2-DLL-0001", AccountStage.Evaluation)]
    [InlineData("S1JUL4", AccountStage.Evaluation)]
    [InlineData("EXPRESS-50K-9", AccountStage.Funded)]
    [InlineData("EXPRESSJUL7", AccountStage.Funded)]
    public void AccountNameFamilies_ResolveToTheirStage(string name, AccountStage expected)
    {
        ProjectXMapping.ResolveStage(name).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("SOMETHING-ELSE")]
    [InlineData("PRACTICING")]      // near-miss on the practice family
    [InlineData("EXPRESSO")]        // near-miss on the funded family
    [InlineData("KTC-50")]          // the size is on the wrong side
    public void AnUnrecognisedAccountName_IsUnknown_NotAGuess(string name)
    {
        // Unknown is NOT a synonym for practice. Misreading a funded account as practice is the expensive
        // direction -- it is the one where a breach costs a real payout.
        ProjectXMapping.ResolveStage(name).Should().Be(AccountStage.Unknown);
    }

    [Fact]
    public void AccountNameMatchingIsCaseInsensitive()
    {
        ProjectXMapping.ResolveStage("prac-50k-1234").Should().Be(AccountStage.Practice);
    }

    [Fact]
    public void AnAccountNameIsNotCarriedIntoThePayload()
    {
        // The name is vendor free text on a channel a model reads (ADR-0008). Everything it usefully carries
        // is already in the stage.
        VenueAccount mapped = ProjectXMapping.ToAccount(new TradingAccount
        {
            Id = 9001,
            Name = "50KTC-V2-DLL-0000",
            Balance = 50_000m,
            CanTrade = true,
            IsVisible = true,
            Simulated = true,
        });

        mapped.Stage.Should().Be(AccountStage.Evaluation);
        mapped.AccountId.Should().Be(9001);

        // There is no name property to leak, and that is the assertion.
        typeof(VenueAccount).GetProperty("Name").Should().BeNull();
    }

    // ── Contracts ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AContractCarriesItsTickSizeAndValue()
    {
        VenueContract mapped = ProjectXMapping.ToContract(
            new Contract
            {
                Id = "CON.F.US.EP.U26",
                Name = "ESU6",
                TickSize = 0.25m,
                TickValue = 12.50m,
                ActiveContract = true,
            },
            new InstrumentId("ES"));

        mapped.ContractId.Should().Be("CON.F.US.EP.U26");
        mapped.Instrument.Symbol.Should().Be("ES");
        mapped.IsActive.Should().BeTrue();
        mapped.TickSize.Should().Be(0.25m);
        mapped.TickValue.Should().Be(12.50m);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.25)]
    public void AContractWithANonPositiveTickSize_IsRefused(double tickSize)
    {
        // Every money figure downstream divides by this. Zero divides by zero; negative inverts everything
        // quietly.
        Action map = () => ProjectXMapping.ToContract(
            new Contract { Id = "CON.F.US.BAD.U26", TickSize = (decimal)tickSize, TickValue = 12.50m },
            new InstrumentId("ES"));

        map.Should().Throw<VenueException>().WithMessage("*tick size*");
    }

    [Fact]
    public void ABarMapsItsOhlcvAndTimestamp()
    {
        Bar mapped = ProjectXMapping.ToBar(
            new AggregateBar
            {
                Timestamp = new DateTime(2026, 8, 18, 14, 30, 0, DateTimeKind.Unspecified),
                Open = 5000m,
                High = 5010m,
                Low = 4990m,
                Close = 5005m,
                Volume = 1234,
            },
            "CON.F.US.EP.U26");

        mapped.OpenTime.Should().Be(new DateTimeOffset(2026, 8, 18, 14, 30, 0, TimeSpan.Zero));
        mapped.Open.Should().Be(5000m);
        mapped.High.Should().Be(5010m);
        mapped.Low.Should().Be(4990m);
        mapped.Close.Should().Be(5005m);
        mapped.Volume.Should().Be(1234);
    }

    [Fact]
    public void ABarCarriesTheContractItWasFetchedFrom()
    {
        // Stamped at the mapping rather than by the caller. A history call answers for exactly one contract,
        // and this is the last point where that is structurally in hand -- above it the series is keyed by
        // the symbol and the quarter is unrecoverable (ADR-0011).
        Bar mapped = ProjectXMapping.ToBar(
            new AggregateBar
            {
                Timestamp = new DateTime(2026, 8, 18, 14, 30, 0, DateTimeKind.Unspecified),
                Open = 5000m,
                High = 5010m,
                Low = 4990m,
                Close = 5005m,
                Volume = 1234,
            },
            "CON.F.US.EP.Z26");

        mapped.ContractId.Should().Be("CON.F.US.EP.Z26");
    }

    [Fact]
    public void ABarWithNoContract_CannotBeMapped()
    {
        // Forgetting must be loud. A bar with no provenance PASSES the roll guard -- unknown is one run --
        // so a silent default here would be gh#42 returning through a different door.
        Action map = () => ProjectXMapping.ToBar(
            new AggregateBar { Timestamp = DateTime.UtcNow, Volume = 1 }, "   ");

        map.Should().Throw<ArgumentException>();
    }
}
