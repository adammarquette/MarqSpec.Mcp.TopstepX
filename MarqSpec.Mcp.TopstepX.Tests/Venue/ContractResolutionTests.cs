using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Venue;

namespace MarqSpec.Mcp.TopstepX.Tests.Venue;

/// <summary>
/// Picking the right contract out of a fuzzy search.
/// </summary>
/// <remarks>
/// <para>
/// This is the most consequential thing the adapter does, and it was wrong. The gateway's contract search
/// matches loosely and flags <b>every</b> result active, so ordering by <c>ActiveContract</c> and taking the
/// first was effectively arbitrary.
/// </para>
/// <para>
/// Observed against the live gateway: searching <c>ES</c> returns six contracts — <c>EP</c> (correct),
/// <c>FVA</c> (a Treasury note), <c>JY6</c> (Japanese Yen), <c>MX6</c>, <c>TYA</c> and <c>MES</c> — all
/// active. Searching <c>YM</c> returns the full contract and the micro, whose point values differ tenfold.
/// </para>
/// <para>
/// The failure is silent and total: Yen bars stored under <c>ES</c>, with every indicator, key level and
/// money figure computed from them, and a chart that looks perfectly ordinary.
/// </para>
/// </remarks>
public sealed class ContractResolutionTests
{
    // ── The product-code segment test ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("CON.F.US.EP.U26", "EP", true)]
    [InlineData("CON.F.US.ENQ.U26", "ENQ", true)]
    [InlineData("CON.F.US.MCLE.U26", "MCLE", true)]
    public void AContractIdMatchesItsOwnProductCode(string contractId, string code, bool expected)
    {
        ProjectXMarketDataGateway.HasProductCode(contractId, code).Should().Be(expected);
    }

    [Theory]
    [InlineData("CON.F.US.MES.U26", "ES")]   // a substring test would match the MICRO for a full-size request
    [InlineData("CON.F.US.MCLE.U26", "CLE")] // and the micro crude for full-size crude
    [InlineData("CON.F.US.MGC.U26", "GC")]
    [InlineData("CON.F.US.MNQ.U26", "NQ")]
    public void AMicroContractDoesNotMatchTheFullSizeProductCode(string contractId, string fullSizeCode)
    {
        // Segment equality, not Contains. A micro selected for a full-size request is a tenfold error in
        // every money figure, and it looks entirely plausible on a chart.
        ProjectXMarketDataGateway.HasProductCode(contractId, fullSizeCode).Should().BeFalse();
    }

    [Theory]
    [InlineData("CON.F.US.JY6.U26", "EP")]   // Japanese Yen, returned by a search for ES
    [InlineData("CON.F.US.FVA.U26", "EP")]   // a Treasury note, likewise
    [InlineData("CON.F.US.TYA.U26", "EP")]
    public void AnUnrelatedInstrumentDoesNotMatch(string contractId, string code)
    {
        ProjectXMarketDataGateway.HasProductCode(contractId, code).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("EP")]
    public void AMalformedContractIdMatchesNothing(string contractId)
    {
        ProjectXMarketDataGateway.HasProductCode(contractId, "EP").Should().BeFalse();
    }

    // ── The registry's half of the contract ──────────────────────────────────────────────────────────

    private static InstrumentRegistry Registry(string instruments) =>
        new(Microsoft.Extensions.Options.Options.Create(new MarketDataOptions
        {
            Instruments = instruments,
            SessionCloseCentral = "16:00",
            MaxRows = 5_000,
        }));

    [Theory]
    [InlineData("ES", "EP")]
    [InlineData("NQ", "ENQ")]
    [InlineData("CL", "CLE")]
    [InlineData("GC", "GCE")]
    [InlineData("SI", "SIE")]
    [InlineData("YM", "YM")]
    [InlineData("MES", "MES")]
    [InlineData("MCL", "MCLE")]
    public void EveryServedInstrumentHasItsVerifiedProductCode(string symbol, string expected)
    {
        // Each of these was read off a LIVE contract search, not guessed. ES is EP and NQ is ENQ -- the code
        // is not derivable from the symbol, and a guess resolves to a real contract in the wrong instrument.
        InstrumentRegistry registry = Registry(symbol);
        registry.ProductCodeFor(new InstrumentId(symbol)).Should().Be(expected);
    }

    [Theory]
    [InlineData("RTY")]
    [InlineData("M2K")]
    [InlineData("NG")]
    [InlineData("HG")]
    [InlineData("ZB")]
    public void AnInstrumentWithNoVerifiedProductCode_FailsAtStartup(string symbol)
    {
        // Deliberately absent rather than guessed. A loud startup failure is the safe direction; a guessed
        // code would resolve to something, and that something would be priced in the wrong instrument.
        Action build = () => Registry(symbol);

        build.Should().Throw<InvalidOperationException>().WithMessage("*" + symbol + "*");
    }

    [Fact]
    public void TheFullSizeAndMicroOfAProductHaveDifferentCodesAndPointValues()
    {
        // The YM/MYM pair is the one that would hurt quietly: same tick size, same tick, point values a
        // factor of ten apart.
        InstrumentRegistry registry = Registry("YM,MYM");

        registry.ProductCodeFor(new InstrumentId("YM")).Should().Be("YM");
        registry.ProductCodeFor(new InstrumentId("MYM")).Should().Be("MYM");

        registry.SpecFor(new InstrumentId("YM")).PointValue.Should().Be(5m);
        registry.SpecFor(new InstrumentId("MYM")).PointValue.Should().Be(0.5m);
        registry.SpecFor(new InstrumentId("YM")).TickSize
            .Should().Be(registry.SpecFor(new InstrumentId("MYM")).TickSize);
    }
}
