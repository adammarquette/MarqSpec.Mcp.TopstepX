using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;

namespace MarqSpec.Mcp.TopstepX.Tests.Domain;

/// <summary>
/// The months a product lists, and which of them are candidates for a trade date (gh#502, ADR-0020).
/// </summary>
/// <remarks>
/// <para>
/// Search and available-contracts return only the active expiry (gh#494, measured 2026-09-06), so a
/// historical candidate cannot be discovered — it has to be <i>constructed</i> from the cycle and confirmed
/// by id. This type is the construction. Every expected list below is written by hand from the exchange's
/// listing conventions, not read back from the type.
/// </para>
/// <para>
/// The depth is the caller's, because the cycle alone cannot say which candidate the market is actually
/// trading: October gold is listed and never front, and a monthly energy contract expires before the month
/// it is named for. The third case below is why a monthly cycle needs three candidates rather than two.
/// </para>
/// </remarks>
public sealed class ContractMonthCycleTests
{
    private static readonly ContractMonthCycle _quarterlies = ContractMonthCycle.Parse("HMUZ");
    private static readonly ContractMonthCycle _metals = ContractMonthCycle.Parse("GJMQVZ");
    private static readonly ContractMonthCycle _monthly = ContractMonthCycle.Parse("FGHJKMNQUVXZ");

    [Fact]
    public void TheTwoNearestQuarterlies_ForJanuary_AreMarchAndJune()
    {
        // 2026-01-05 is the first Monday of January. No quarterly is listed for January itself, so the
        // nearest listed month at or after it is March, then June.
        _quarterlies.CandidatesFor(new DateOnly(2026, 1, 5), depth: 2)
            .Should().Equal(new ContractExpiry(2026, 'H'), new ContractExpiry(2026, 'M'));
    }

    [Fact]
    public void TheYearRollsOver_DecemberBeforeMarch()
    {
        // December is itself a quarterly, so it is the first candidate; the second is March OF THE NEXT
        // YEAR. A cycle that only counted months inside the trade date's year would answer Z25 alone, and a
        // string comparison of the codes would file H26 before Z25.
        _quarterlies.CandidatesFor(new DateOnly(2025, 12, 1), depth: 2)
            .Should().Equal(new ContractExpiry(2025, 'Z'), new ContractExpiry(2026, 'H'));
    }

    [Fact]
    public void MonthlyEnergy_NeedsDepthThree_BecauseAContractExpiresBeforeItsMonth()
    {
        // MCL changed over from U26 to V26 on 2026-08-18 (gh#494 table f): on the 18th of August the front
        // month is OCTOBER's contract, because the September one expires around the 20th of August and the
        // market has already moved on. Two candidates from August reach only Q26 and U26 — neither is the
        // contract carrying the volume. Three reach V26.
        DateOnly changeover = new(2026, 8, 18);

        _monthly.CandidatesFor(changeover, depth: 2)
            .Should().Equal(new ContractExpiry(2026, 'Q'), new ContractExpiry(2026, 'U'));

        _monthly.CandidatesFor(changeover, depth: 3)
            .Should().Equal(
                new ContractExpiry(2026, 'Q'),
                new ContractExpiry(2026, 'U'),
                new ContractExpiry(2026, 'V'));
    }

    [Fact]
    public void MetalsInAugust_ReachDecember_OnlyAtDepthThree()
    {
        // MGC V26 vs Z26 in early September is 1 : 8 in Z26's favour and October gold is never front
        // (gh#494 table f). From August the listed months at or after it are August, October and December;
        // a depth of two reaches Q26 and V26 — the expiring contract and the one the market skips — and
        // misses Z26 entirely. Depth three is what reaches the contract the market trades from any month of
        // the cycle.
        _metals.CandidatesFor(new DateOnly(2026, 8, 3), depth: 3)
            .Should().Equal(
                new ContractExpiry(2026, 'Q'),
                new ContractExpiry(2026, 'V'),
                new ContractExpiry(2026, 'Z'));
    }

    [Fact]
    public void ATradeDateInAListedMonth_StartsFromThatMonth()
    {
        // The rule is "month >= the trade date's month", not "after". Most of March trades the March
        // contract; excluding it would skip the front month for three weeks of every quarter.
        _quarterlies.CandidatesFor(new DateOnly(2026, 3, 2), depth: 2)
            .Should().Equal(new ContractExpiry(2026, 'H'), new ContractExpiry(2026, 'M'));
    }

    [Fact]
    public void ADepthBeyondOneYear_KeepsCounting()
    {
        // Nothing about the cycle limits the depth to a year. Six quarterlies from January span into the
        // year after next.
        _quarterlies.CandidatesFor(new DateOnly(2026, 1, 5), depth: 6)
            .Should().Equal(
                new ContractExpiry(2026, 'H'),
                new ContractExpiry(2026, 'M'),
                new ContractExpiry(2026, 'U'),
                new ContractExpiry(2026, 'Z'),
                new ContractExpiry(2027, 'H'),
                new ContractExpiry(2027, 'M'));
    }

    [Fact]
    public void TheCycleIsASetOfMonths_InCalendarOrder_HoweverItWasWritten()
    {
        // A cycle is the set of months listed, and the code renders it in calendar order so two spellings
        // of one cycle are one value.
        ContractMonthCycle cycle = ContractMonthCycle.Parse("ZHUM");

        cycle.Code.Should().Be("HMUZ");
        cycle.Months.Should().Equal(3, 6, 9, 12);
        cycle.Should().Be(_quarterlies);
        cycle.Contains('H').Should().BeTrue();
        cycle.Contains('F').Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("HMUZI")]     // I is not a month code
    [InlineData("hmuz")]      // the exchange writes them in upper case; a near-miss is not read
    [InlineData("HMUZZ")]     // a month listed twice is a typo, not a cycle
    [InlineData("H M U Z")]
    public void ACycleThisServerCannotRead_IsRefused(string code)
    {
        Action parse = () => ContractMonthCycle.Parse(code);

        parse.Should().Throw<FormatException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ADepthBelowOne_IsRefused(int depth)
    {
        Action candidates = () => _quarterlies.CandidatesFor(new DateOnly(2026, 1, 5), depth);

        candidates.Should().Throw<ArgumentOutOfRangeException>();
    }
}
