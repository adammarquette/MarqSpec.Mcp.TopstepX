using FluentAssertions;
using MarqSpec.Client.ProjectX.Api.Models;
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

    // ── Front-month ordering, and the year boundary that inverts it ──────────────────────────────────

    /// <summary>An ES contract for a given expiry, at the real EP tick size so the tick guard is silent.</summary>
    private static Contract Ep(string expiry, bool active = true) => new()
    {
        Id = "CON.F.US.EP." + expiry,
        ActiveContract = active,
        TickSize = 0.25m,
        TickValue = 12.50m,
    };

    private static IEnumerable<string> OrderedIds(params Contract[] contracts) =>
        ProjectXMarketDataGateway.InFrontMonthOrder(contracts).Select(c => c.Id);

    [Fact]
    public void TheDecemberFrontMonthSortsFirst_NotLast_WhenTheSetSpansAYearBoundary()
    {
        // THE CASE THIS WHOLE CARD IS ABOUT. Every December the live ES quarterlies span a year boundary.
        // A sort on the id STRING compares the month letter before the year, and the month letters ascend
        // alphabetically in calendar order -- so 'H' < 'M' < 'U' < 'Z' files Z25 LAST, precisely when Z25 is
        // the front month a caller asking for "ES" means. BarCacheService takes contracts[0], so last here
        // is a whole quarter of the wrong contract's bars, stored under ES, with every indicator and key
        // level computed from them and nothing erroring.
        OrderedIds(Ep("H26"), Ep("M26"), Ep("U26"), Ep("Z25"))
            .Should().Equal(
                "CON.F.US.EP.Z25",
                "CON.F.US.EP.H26",
                "CON.F.US.EP.M26",
                "CON.F.US.EP.U26");
    }

    [Fact]
    public void TheTightestCrossYearPair_DecemberBeforeTheFollowingJanuary()
    {
        // 'F' (January) is the LOWEST month letter and 'Z' (December) the highest, so this adjacent pair is
        // the widest the string sort can be wrong by: one month apart in fact, eleven ranks apart ordinally.
        OrderedIds(Ep("F26"), Ep("Z25"))
            .Should().Equal("CON.F.US.EP.Z25", "CON.F.US.EP.F26");
    }

    [Fact]
    public void WithinOneYearTheOrderIsUnchanged()
    {
        // The awkward CORRECT input: inside a single calendar year the string sort was already right, and
        // this run is what proves the fix did not buy the boundary case by breaking the common one.
        OrderedIds(Ep("Z26"), Ep("H26"), Ep("U26"), Ep("M26"))
            .Should().Equal(
                "CON.F.US.EP.H26",
                "CON.F.US.EP.M26",
                "CON.F.US.EP.U26",
                "CON.F.US.EP.Z26");
    }

    [Fact]
    public void ActiveStillOutranksTheNearerExpiry()
    {
        // Expiry is the TIE-BREAK, never the primary key. A nearer expiry the venue has stopped flagging
        // active must not displace an active one -- ordering is the only thing separating them here, and
        // reversing these two is the same silent wrong-contract failure by a different route.
        OrderedIds(Ep("Z25", active: false), Ep("H26", active: true))
            .Should().Equal("CON.F.US.EP.H26", "CON.F.US.EP.Z25");
    }

    [Fact]
    public void AnUnreadableExpirySortsLast_NeverFirst()
    {
        // An id whose expiry cannot be read means CANNOT ORDER, and the safe direction for "unknown" is last:
        // first would hand contracts[0] -- the contract every bar is fetched for -- to the one id here nobody
        // understands. Worth its own case because the natural spelling gets it BACKWARDS: OrderBy on a
        // nullable int puts null FIRST, since null compares less than every value.
        OrderedIds(Ep("NOPE"), Ep("Z25"), Ep("H26"))
            .Should().Equal("CON.F.US.EP.Z25", "CON.F.US.EP.H26", "CON.F.US.EP.NOPE");
    }

    // ── The expiry rank itself ───────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("CON.F.US.EP.F26", 26 * 12 + 1)]   // January
    [InlineData("CON.F.US.EP.G26", 26 * 12 + 2)]
    [InlineData("CON.F.US.EP.H26", 26 * 12 + 3)]   // March    -- an ES quarterly
    [InlineData("CON.F.US.EP.J26", 26 * 12 + 4)]
    [InlineData("CON.F.US.EP.K26", 26 * 12 + 5)]
    [InlineData("CON.F.US.EP.M26", 26 * 12 + 6)]   // June     -- an ES quarterly
    [InlineData("CON.F.US.EP.N26", 26 * 12 + 7)]
    [InlineData("CON.F.US.EP.Q26", 26 * 12 + 8)]
    [InlineData("CON.F.US.EP.U26", 26 * 12 + 9)]   // September -- an ES quarterly
    [InlineData("CON.F.US.EP.V26", 26 * 12 + 10)]
    [InlineData("CON.F.US.EP.X26", 26 * 12 + 11)]
    [InlineData("CON.F.US.EP.Z26", 26 * 12 + 12)]  // December -- an ES quarterly
    public void EveryFuturesMonthLetterRanksAsItsCalendarMonth(string contractId, int expected)
    {
        // All twelve, hand-written from the exchange codes rather than round-tripped through the lookup:
        // F G H J K M N Q U V X Z, skipping I and L. Getting one letter wrong misfiles one month a year.
        ProjectXMarketDataGateway.ExpiryRank(contractId).Should().Be(expected);
    }

    [Fact]
    public void TheYearOutranksTheMonth()
    {
        // The whole defect in one assertion: December of the earlier year must rank BELOW January of the
        // later one, which is the comparison a string sort gets backwards.
        ProjectXMarketDataGateway.ExpiryRank("CON.F.US.EP.Z25")
            .Should().BeLessThan(ProjectXMarketDataGateway.ExpiryRank("CON.F.US.EP.F26")!.Value);
    }

    [Theory]
    [InlineData("CON.F.US.EP.I26")]      // 'I' is not a futures month code -- it is skipped to avoid 1/l/I
    [InlineData("CON.F.US.EP.L26")]      // nor is 'L'
    [InlineData("CON.F.US.EP.Z2026")]    // a four-digit year is a shape change, not a year we may guess at
    [InlineData("CON.F.US.EP.Z2")]       // truncated
    [InlineData("CON.F.US.EP.ZZ5")]      // not a year
    [InlineData("CON.F.US.EP")]          // no expiry segment at all
    [InlineData("")]
    [InlineData("   ")]
    public void AnExpiryThisServerCannotReadHasNoRank(string contractId)
    {
        // Null is "cannot order", and it is returned rather than defaulted for the same reason a null
        // indicator is: a rank invented here would be indistinguishable from one that was actually read.
        // The CALLER decides where unknown goes, and says so.
        ProjectXMarketDataGateway.ExpiryRank(contractId).Should().BeNull();
    }
}
