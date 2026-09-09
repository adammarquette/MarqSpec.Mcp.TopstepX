using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// The listing cycle and candidate depth each served product carries (ADR-0020).
/// </summary>
/// <remarks>
/// <para>
/// These are <b>listing facts</b>, not configuration. The exchange lists what it lists; a knob here would
/// let an operator name a month the exchange does not, and every candidate built from it would be an id the
/// venue answers nothing for — which looks exactly like a quiet market.
/// </para>
/// <para>
/// The depth is the one number that is a judgement rather than a fact, and the gh#494 probe is what fixes
/// it: two candidates reach the front and the next quarterly on an equity index, but the metals need three
/// because the market skips October gold outright (`MGC.V26` against `Z26` measured 1 : 8 every day of
/// September 2026), and energy needs three because a monthly crude contract expires before the month it is
/// named for — on 2026-08-18 the front crude contract was October's.
/// </para>
/// </remarks>
public sealed class InstrumentRegistryCycleTests
{
    /// <summary>Every symbol this server has facts for, so the sweep below cannot go stale silently.</summary>
    private const string EveryServedSymbol = "ES,MES,NQ,MNQ,YM,MYM,CL,MCL,GC,MGC,SI,SIL";

    private static InstrumentRegistry Registry(string instruments) =>
        new(Microsoft.Extensions.Options.Options.Create(new MarketDataOptions
        {
            Instruments = instruments,
            SessionCloseCentral = "16:00",
            MaxRows = 5_000,
        }));

    [Fact]
    public void EveryServedInstrumentHasACycleAndADepth()
    {
        // The sweep, not a spot check. A product added to the specs table without a cycle would fail here
        // rather than at the first historical fetch, where a missing cycle means no candidates at all and a
        // range that answers empty for a reason nobody can see.
        InstrumentRegistry registry = Registry(EveryServedSymbol);

        registry.Instruments.Should().HaveCount(12);

        foreach (InstrumentId instrument in registry.Instruments)
        {
            ContractMonthCycle cycle = registry.CycleFor(instrument);
            cycle.Months.Should().NotBeEmpty("{0} must list at least one contract month", instrument.Symbol);

            registry.CandidateDepthFor(instrument)
                .Should().BeGreaterThan(0, "{0} must name at least one candidate", instrument.Symbol);

            // A depth deeper than the cycle is legal -- CandidatesFor wraps into the next year -- but a
            // depth of one can never see past a month the market skips, which is the whole point of the
            // number. Nothing served here is a single-candidate product.
            registry.CandidateDepthFor(instrument).Should().BeGreaterThan(1);
        }
    }

    [Theory]
    [InlineData("ES", "HMUZ", 2)]
    [InlineData("MES", "HMUZ", 2)]
    [InlineData("NQ", "HMUZ", 2)]
    [InlineData("MNQ", "HMUZ", 2)]
    [InlineData("YM", "HMUZ", 2)]
    [InlineData("MYM", "HMUZ", 2)]
    public void TheEquityIndicesAreQuarterlyAtDepthTwo(string symbol, string cycle, int depth)
    {
        // March, June, September, December. Depth two reaches the front and the next quarterly, which is
        // every contract that can carry the volume on any day of a quarter.
        InstrumentRegistry registry = Registry(symbol);
        InstrumentId instrument = new(symbol);

        registry.CycleFor(instrument).Code.Should().Be(cycle);
        registry.CandidateDepthFor(instrument).Should().Be(depth);
    }

    [Theory]
    [InlineData("CL", "FGHJKMNQUVXZ", 3)]
    [InlineData("MCL", "FGHJKMNQUVXZ", 3)]
    public void EnergyIsMonthlyAtDepthThree(string symbol, string cycle, int depth)
    {
        // Every month, and three deep: a crude contract expires before the month it is named for, so on
        // 2026-08-18 the front was October's -- two months past the trade date's own.
        InstrumentRegistry registry = Registry(symbol);
        InstrumentId instrument = new(symbol);

        registry.CycleFor(instrument).Code.Should().Be(cycle);
        registry.CandidateDepthFor(instrument).Should().Be(depth);
    }

    [Theory]
    [InlineData("GC", "GJMQVZ", 3)]
    [InlineData("MGC", "GJMQVZ", 3)]
    public void GoldListsSixMonthsAtDepthThree(string symbol, string cycle, int depth)
    {
        // February, April, June, August, October, December -- and October is listed but never front
        // (gh#494: V26 against Z26, 1 : 8, every day measured). Depth two would stop at the month the
        // market skips; depth three is what reaches past it.
        InstrumentRegistry registry = Registry(symbol);
        InstrumentId instrument = new(symbol);

        registry.CycleFor(instrument).Code.Should().Be(cycle);
        registry.CandidateDepthFor(instrument).Should().Be(depth);
    }

    [Theory]
    [InlineData("SI", "HKNUZ", 2)]
    [InlineData("SIL", "HKNUZ", 2)]
    public void SilverListsFiveMonthsAtDepthTwo(string symbol, string cycle, int depth)
    {
        // March, May, July, September, December. No skipped month has been measured on silver, so two.
        InstrumentRegistry registry = Registry(symbol);
        InstrumentId instrument = new(symbol);

        registry.CycleFor(instrument).Code.Should().Be(cycle);
        registry.CandidateDepthFor(instrument).Should().Be(depth);
    }

    [Fact]
    public void TheGoldCycleReachesPastOctober_AtItsDepth()
    {
        // The measurement turned into an assertion. Asking for a September 2026 gold trade date, the
        // candidates must include Z26 -- the contract that actually carried the volume -- and not stop at
        // V26, the one the market skipped.
        InstrumentRegistry registry = Registry("MGC");
        InstrumentId instrument = new("MGC");

        IReadOnlyList<ContractExpiry> candidates = registry.CycleFor(instrument)
            .CandidatesFor(new DateOnly(2026, 9, 15), registry.CandidateDepthFor(instrument));

        candidates.Select(c => c.Code).Should().Contain("Z26");
    }

    [Fact]
    public void TheCrudeCycleReachesTwoMonthsPastTheTradeDate_AtItsDepth()
    {
        // 2026-08-18: the front crude contract was V26, two listed months past August. Depth two would
        // name Q26 and U26 and never see it.
        InstrumentRegistry registry = Registry("MCL");
        InstrumentId instrument = new("MCL");

        IReadOnlyList<ContractExpiry> candidates = registry.CycleFor(instrument)
            .CandidatesFor(new DateOnly(2026, 8, 18), registry.CandidateDepthFor(instrument));

        candidates.Select(c => c.Code).Should().Equal("Q26", "U26", "V26");
    }

    [Fact]
    public void AnInstrumentThisServerDoesNotServe_HasNoCycleAndNoDepth()
    {
        // The same direction as every other registry accessor: a symbol that is not served is an error
        // naming what would have been valid, never a default cycle nobody chose.
        InstrumentRegistry registry = Registry("ES");
        InstrumentId absent = new("MES");

        registry.Invoking(r => r.CycleFor(absent)).Should().Throw<KeyNotFoundException>();
        registry.Invoking(r => r.CandidateDepthFor(absent)).Should().Throw<KeyNotFoundException>();
    }
}
