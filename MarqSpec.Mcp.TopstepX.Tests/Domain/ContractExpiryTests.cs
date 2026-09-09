using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;

namespace MarqSpec.Mcp.TopstepX.Tests.Domain;

/// <summary>
/// A contract expiry read out of a venue id, and the order it sorts in (gh#502, ADR-0020).
/// </summary>
/// <remarks>
/// The month table is written out by hand here rather than round-tripped through the type under test:
/// <c>F G H J K M N Q U V X Z</c>, skipping <c>I</c> and <c>L</c>. One wrong letter misfiles one month a
/// year, and every historical fetch for that month would then ask the wrong contract.
/// </remarks>
public sealed class ContractExpiryTests
{
    [Theory]
    [InlineData('F', 1)]
    [InlineData('G', 2)]
    [InlineData('H', 3)]
    [InlineData('J', 4)]
    [InlineData('K', 5)]
    [InlineData('M', 6)]
    [InlineData('N', 7)]
    [InlineData('Q', 8)]
    [InlineData('U', 9)]
    [InlineData('V', 10)]
    [InlineData('X', 11)]
    [InlineData('Z', 12)]
    public void EveryMonthCode_RoundTripsThroughItsCode(char monthCode, int month)
    {
        string code = monthCode + "26";

        ContractExpiry.TryParse(code, out ContractExpiry parsed).Should().BeTrue();

        parsed.Year.Should().Be(2026);
        parsed.MonthCode.Should().Be(monthCode);
        parsed.Month.Should().Be(month);
        parsed.Code.Should().Be(code);
        parsed.Rank.Should().Be((2026 * 12) + month);
        parsed.Should().Be(new ContractExpiry(2026, monthCode));
    }

    [Fact]
    public void TheYearOutranksTheMonth()
    {
        // The defect ExpiryRank was written to fix, restated on the Domain type: December of the earlier
        // year sorts BELOW January of the later one, which a string sort of the code gets backwards.
        new ContractExpiry(2025, 'Z').Rank.Should().BeLessThan(new ContractExpiry(2026, 'F').Rank);
        new ContractExpiry(2025, 'Z').CompareTo(new ContractExpiry(2026, 'F')).Should().BeNegative();
    }

    [Fact]
    public void TheContractIdsExpirySegment_IsWhatIsRead()
    {
        ContractExpiry.TryParseContractId("CON.F.US.MES.U26", out ContractExpiry expiry).Should().BeTrue();
        expiry.Should().Be(new ContractExpiry(2026, 'U'));
    }

    [Theory]
    [InlineData("I26")]        // not a month code: skipped by the exchange to avoid 1/l/I
    [InlineData("L26")]        // nor is L
    [InlineData("Z2026")]      // a four-digit year is a shape change, not a year to guess at
    [InlineData("Z2")]         // truncated
    [InlineData("ZZ5")]        // not a year
    [InlineData("u26")]        // the venue writes the month in upper case; a near-miss is not read
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ACodeThisServerCannotRead_DoesNotParse(string? code)
    {
        ContractExpiry.TryParse(code, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("CON.F.US.MES")]     // no expiry segment
    [InlineData("CON.F.US.MES.NOPE")]
    [InlineData("")]
    [InlineData(null)]
    public void AContractIdWithoutAReadableExpiry_DoesNotParse(string? contractId)
    {
        ContractExpiry.TryParseContractId(contractId, out _).Should().BeFalse();
    }

    [Fact]
    public void AnUnknownMonthLetter_IsRefusedAtConstruction()
    {
        // Constructing one directly bypasses parsing, so the constructor has to hold the same line: a
        // ContractExpiry that exists is one whose month is on the exchange's table.
        Action construct = () => _ = new ContractExpiry(2026, 'I');

        construct.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void TheNextExpiryInACycle_IsTheNextListedMonth_AndWrapsTheYear()
    {
        ContractMonthCycle quarterlies = ContractMonthCycle.Parse("HMUZ");

        new ContractExpiry(2026, 'H').Next(quarterlies).Should().Be(new ContractExpiry(2026, 'M'));
        new ContractExpiry(2026, 'Z').Next(quarterlies).Should().Be(new ContractExpiry(2027, 'H'));
    }

    [Fact]
    public void ToString_IsTheCode()
    {
        new ContractExpiry(2026, 'U').ToString().Should().Be("U26");
    }
}
