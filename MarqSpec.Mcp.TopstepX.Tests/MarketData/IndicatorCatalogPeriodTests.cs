using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// The catalogue owns one instance per configured <c>(name, period)</c>, and <c>Resolve</c> selects among
/// them without widening the vocabulary.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two collections, and the difference between them is the feature's lead risk.</b>
/// <see cref="IndicatorCatalog.All"/> is what the projection walks, so it has to carry every configured
/// period or a period an operator asked for is never computed. <see cref="IndicatorCatalog.Primaries"/> is
/// exactly one instance per name, and it exists because <c>get_market_snapshot</c>'s indicator map is keyed
/// by NAME: a map built by walking <c>All</c> would let the last instance of a name win, so a snapshot
/// configured with an additional EMA would silently report EMA at whichever period happened to be built
/// last. Same number, different window, nothing in the payload saying so.
/// </para>
/// <para>
/// <b>The vocabulary is unchanged.</b> <see cref="IndicatorCatalog.KnownNames"/> is still twelve names, and
/// <see cref="IndicatorCatalog.Resolve(string)"/> still answers with the primary — every existing call site
/// (<c>KeyLevelTools</c>'s ATR, the two indicator reads, the snapshot's key set) means "the configured one"
/// and keeps meaning it. Periods are SELECTED, never invented: a period the server does not compute would
/// read back as an empty series, which is indistinguishable from a market that produced none.
/// </para>
/// </remarks>
public sealed class IndicatorCatalogPeriodTests
{
    /// <summary>The twelve names, in the order the catalogue builds its primaries.</summary>
    private static readonly string[] _primaryNames =
    [
        "atr", "rsi", "sma", "ema", "macd", "macd-signal", "macd-histogram", "vwap",
        "bb-upper", "bb-middle", "bb-lower", "vwap-rolling",
    ];

    private static BarSessionCalendar Calendar() => BarSessionCalendar.Parse("16:00", []);

    /// <summary>
    /// Options with an additional period on every list, so no assertion below can pass by accident on a
    /// family nobody widened.
    /// </summary>
    /// <returns>The options.</returns>
    /// <remarks>
    /// EMA carries the five-long list from the brief's worked message — <c>20 (primary), 10, 13, 24, 48,
    /// 200</c> — because the refusal message names the configured periods in order, and a one-element
    /// additional list cannot show that the order is the configured one rather than a sort.
    /// </remarks>
    private static IndicatorOptions Wide() => new()
    {
        AtrPeriod = 14,
        AdditionalAtrPeriods = "7",
        RsiPeriod = 14,
        AdditionalRsiPeriods = "21",
        SmaPeriod = 20,
        AdditionalSmaPeriods = "50",
        EmaPeriod = 20,
        AdditionalEmaPeriods = "10,13,24,48,200",
        MacdSlowPeriod = 26,
        AdditionalMacdSlowPeriods = "52",
        BollingerPeriod = 20,
        AdditionalBollingerPeriods = "34",
        RollingVwapPeriod = 20,
        AdditionalRollingVwapPeriods = "60",
    };

    private static IndicatorCatalog Catalog(IndicatorOptions options) =>
        new(Options.Create(options), Calendar());

    private static IndicatorCatalog WideCatalog() => Catalog(Wide());

    /// <summary>The <c>(name, period)</c> keys of a run of instances.</summary>
    /// <param name="indicators">The instances.</param>
    /// <returns>The keys, in the order the instances were given.</returns>
    private static IReadOnlyList<(string Name, int Period)> Keys(IEnumerable<IIndicator> indicators) =>
        [.. indicators.Select(i => (i.Name, i.Period))];

    [Fact]
    public void Catalog_OwnsOneInstancePerConfiguredPeriod_PrimariesFirst()
    {
        IndicatorCatalog catalog = WideCatalog();

        // Twelve primaries, in today's order — the order the projection has always written in.
        Keys(catalog.Primaries).Select(k => k.Name).Should().Equal(_primaryNames);

        // Every configured (name, period), and no more: atr 2, rsi 2, sma 2, ema 6, the three macd legs at
        // two slow lengths each, vwap once, the three bands at two windows each, vwap-rolling 2.
        Keys(catalog.All).Should().BeEquivalentTo(
            new (string, int)[]
            {
                ("atr", 14), ("atr", 7),
                ("rsi", 14), ("rsi", 21),
                ("sma", 20), ("sma", 50),
                ("ema", 20), ("ema", 10), ("ema", 13), ("ema", 24), ("ema", 48), ("ema", 200),
                ("macd", 26), ("macd", 52),
                ("macd-signal", 26), ("macd-signal", 52),
                ("macd-histogram", 26), ("macd-histogram", 52),
                ("vwap", 0),
                ("bb-upper", 20), ("bb-upper", 34),
                ("bb-middle", 20), ("bb-middle", 34),
                ("bb-lower", 20), ("bb-lower", 34),
                ("vwap-rolling", 20), ("vwap-rolling", 60),
            },
            "the projection walks All, so a configured period missing from it is a period the operator asked "
            + "for and this server never computes — read back as an empty series, which looks exactly like a "
            + "market that produced none");

        // PRIMARIES FIRST, then the additional instances. IndicatorProjector writes in this order, and the
        // primary is the one a caller gets by omitting `period`; putting it first means the row every
        // existing reader wants is written first rather than after an arbitrary number of widenings.
        catalog.All.Take(catalog.Primaries.Count).Should().Equal(
            catalog.Primaries, "All must open with Primaries, in the same order, instance for instance");

        Keys(catalog.All).Should().OnlyHaveUniqueItems(
            "(Indicator, Period) is the storage key — two instances sharing one would write the same row "
            + "twice per bar, and the second would silently overwrite the first");
    }

    [Fact]
    public void KnownNames_StayTwelve_WhenAdditionalPeriodsAreConfigured()
    {
        // The vocabulary is a set of NAMES, not of instances. Widening the periods must not add a name: the
        // tool descriptions list these twelve, ToolSurfaceTests pins that list, and get_market_snapshot keys
        // its map by them. An operator adding an EMA at 200 has not added an indicator.
        WideCatalog().KnownNames.Should().BeEquivalentTo(
            _primaryNames, "additional periods widen the instances, never the vocabulary");

        WideCatalog().KnownNames.Should().HaveCount(12);
    }

    [Fact]
    public void Resolve_ReturnsThePrimary_WhenNoPeriodIsGiven()
    {
        IndicatorCatalog catalog = WideCatalog();

        // Both overloads, and they must agree. Resolve(name) is what KeyLevelTools' ATR and both indicator
        // reads already call; if the one-argument form ever stopped meaning "the primary", every one of
        // those call sites would change meaning without being touched.
        foreach (string name in _primaryNames)
        {
            IIndicator primary = catalog.Resolve(name);

            catalog.Resolve(name, null).Should().BeSameAs(
                primary, "an omitted period means the configured primary, on both overloads");
            catalog.Primaries.Should().Contain(primary);
        }

        catalog.Resolve("ema").Period.Should().Be(20);
        catalog.ConfiguredPeriodFor("ema").Should().Be(20, "ConfiguredPeriodFor is the PRIMARY's period");
    }

    [Fact]
    public void Resolve_ReturnsTheConfiguredInstance_WhenAnAdditionalPeriodIsGiven()
    {
        IndicatorCatalog catalog = WideCatalog();

        IIndicator ema200 = catalog.Resolve("ema", 200);

        ema200.Name.Should().Be("ema");
        ema200.Period.Should().Be(200);
        ema200.Should().NotBeSameAs(catalog.Resolve("ema"), "200 is not the primary");
        catalog.All.Should().Contain(ema200, "the instance handed back is the one the projection writes");

        // Case and whitespace are normalised on the name, exactly as the one-argument overload already does.
        catalog.Resolve("  EMA  ", 200).Should().BeSameAs(ema200);
    }

    [Fact]
    public void Resolve_ReturnsThePrimary_WhenItsOwnPeriodIsGivenExplicitly()
    {
        IndicatorCatalog catalog = WideCatalog();

        // Naming the primary explicitly is not a different request from omitting it. If these were two
        // instances, a caller who passed the period they read out of a previous response would get a second
        // object computing the same numbers — and the snapshot's per-name map would then depend on which one
        // was built last.
        catalog.Resolve("ema", 20).Should().BeSameAs(catalog.Resolve("ema"));
        catalog.Resolve("atr", 14).Should().BeSameAs(catalog.Resolve("atr"));
        catalog.Resolve("bb-lower", 20).Should().BeSameAs(catalog.Resolve("bb-lower"));
    }

    [Fact]
    public void Resolve_RefusesAnUnconfiguredPeriod_AndListsTheConfiguredOnesPrimaryFirst()
    {
        IndicatorCatalog catalog = WideCatalog();

        Action resolve = () => catalog.Resolve("ema", 50);

        // The exact message, because it is the whole value of the refusal: a caller who asked for 50 needs to
        // be told what IS computed, and which of those is the one they get by omitting the period. An empty
        // series would have been the alternative answer, and it reads as "no signal".
        resolve.Should().Throw<KeyNotFoundException>().WithMessage(
            "Indicator 'ema' is not computed at period 50. Configured periods for ema: 20 (primary), 10, 13, "
            + "24, 48, 200. A period this server does not compute would read back as an empty series, which "
            + "is indistinguishable from a market that produced none.");
    }

    [Fact]
    public void Resolve_RefusesAnUnknownName_BeforeLookingAtThePeriod()
    {
        IndicatorCatalog catalog = WideCatalog();

        // The NAME is checked first, and the message is the one Resolve(name) has always produced. A caller
        // who typo'd the name and also passed a period must be told about the name: "stochastic is not
        // computed at period 14" would send them looking for a configuration key that does not exist.
        Action resolve = () => catalog.Resolve("stochastic", 14);

        resolve.Should().Throw<KeyNotFoundException>().WithMessage(
            "Unknown indicator 'stochastic'. Known indicators: atr, bb-lower, bb-middle, bb-upper, ema, "
            + "macd, macd-histogram, macd-signal, rsi, sma, vwap, vwap-rolling.");
    }

    [Fact]
    public void Resolve_RefusesAPeriodOnVwap()
    {
        IndicatorCatalog catalog = WideCatalog();

        // Including 0, which IS vwap's stored period. Accepting 0 would tell a caller the period means
        // something here, and the next thing they try is 20 — which is a rolling VWAP, a different
        // calculation under a different name. The refusal points at the omission, not at another number.
        foreach (int period in new[] { 0, 20 })
        {
            Action resolve = () => catalog.Resolve("vwap", period);

            resolve.Should().Throw<KeyNotFoundException>().WithMessage(
                "'vwap' takes no period: it is anchored to the session, not to a window. Omit period.");
        }

        catalog.Resolve("vwap", null).Should().BeSameAs(
            catalog.Resolve("vwap"), "omitting the period is the only way to ask for VWAP, and it works");
    }

    [Fact]
    public void Resolve_AcceptsAPeriodOnVwapRolling()
    {
        IndicatorCatalog catalog = Catalog(new IndicatorOptions
        {
            RollingVwapPeriod = 20,
            AdditionalRollingVwapPeriods = "10",
        });

        // The other side of Resolve_RefusesAPeriodOnVwap, and the pair is the point. `vwap` is anchored to
        // the session and refuses a period; `vwap-rolling` IS a window, so a refusal that keyed on the name
        // looking VWAP-ish -- a prefix match, or a check on the string rather than on the anchored instance
        // -- would make every additional rolling window unreachable while every test about `vwap` stayed
        // green.
        catalog.Resolve("vwap-rolling", 10).Period.Should().Be(10);
        catalog.PeriodsFor("vwap-rolling").Should().Equal([20, 10]);

        catalog.Resolve("vwap-rolling", null).Should().BeSameAs(
            catalog.Resolve("vwap-rolling"), "omitting the period still asks for the primary window");
    }

    [Fact]
    public void MacdLegs_ShareEachAdditionalSlowPeriod()
    {
        IndicatorCatalog catalog = WideCatalog();

        // MACD is one calculation published as three series. A slow length that produced a line but no
        // signal or histogram would answer two of the three reads with an empty series, and the caller has
        // no way to tell that from a market that produced none.
        foreach (string leg in new[] { "macd", "macd-signal", "macd-histogram" })
        {
            catalog.PeriodsFor(leg).Should().Equal(
                [26, 52], leg + " must be computed at every configured slow length, not just the primary");
            catalog.Resolve(leg, 52).Period.Should().Be(52);
        }
    }

    [Fact]
    public void BollingerBands_ShareEachAdditionalPeriod()
    {
        IndicatorCatalog catalog = WideCatalog();

        // The three bands are one band set. An upper without its middle and lower is not a Bollinger
        // reading; it is a number whose envelope the caller cannot see.
        foreach (string band in new[] { "bb-upper", "bb-middle", "bb-lower" })
        {
            catalog.PeriodsFor(band).Should().Equal([20, 34]);
            catalog.Resolve(band, 34).Period.Should().Be(34);
        }
    }

    [Fact]
    public void ADuplicatePeriod_IsRefusedByTheOptions_BeforeTheCatalogueSeesIt()
    {
        // Options.Create bypasses ValidateOnStart, so a test — the only caller that can — may hand the
        // catalogue a list boot would have refused. Repeating the primary in the additional list is the shape
        // that would put two instances under one (Name, Period): the projector would write both to the same
        // storage key on every bar, and whichever ran second would overwrite the first with numbers computed
        // from a window the row does not name.
        Action duplicate = () => Catalog(new IndicatorOptions
        {
            EmaPeriod = 20,
            AdditionalEmaPeriods = "20",
        });

        duplicate.Should().Throw<InvalidOperationException>(
            "a catalogue with two instances under one storage key must never be constructed")
            .WithMessage("*Indicators__AdditionalEmaPeriods*already the primary*");

        // WHICH LAYER REFUSED IT IS STATED, not glossed. Today it is IndicatorOptions.EmaPeriods(), which
        // re-runs the rule ValidateOnStart would have applied, so no options shape can reach the catalogue's
        // own (Name, Period) guard — that one is an ArgumentException behind this, held for a construction
        // path that does not exist yet (a family whose period list overlapped another's). What is asserted
        // instead is the property both layers exist for, over every shape that DOES construct: no two
        // instances share a storage key.
        Keys(WideCatalog().All).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void PeriodsFor_ListsPrimaryFirstThenAdditionalInConfiguredOrder()
    {
        IndicatorCatalog catalog = WideCatalog();

        // Configured order, NOT sorted. The operator wrote "10,13,24,48,200" and the primary is 20, so a
        // sorted answer would put 20 in the middle and the caller would have no way to tell which one they
        // get by omitting the period. The refusal message is built from this list, and it labels the first.
        catalog.PeriodsFor("ema").Should().Equal([20, 10, 13, 24, 48, 200]);
        catalog.PeriodsFor("atr").Should().Equal([14, 7]);

        // VWAP is windowless, and its stored period is 0. It still has a list, because PeriodsFor answers
        // for every name in the vocabulary — and a caller enumerating what is configured must not have to
        // special-case a missing key.
        catalog.PeriodsFor("vwap").Should().Equal([0]);

        // An unconfigured indicator has one period, and it is the primary.
        Catalog(new IndicatorOptions()).PeriodsFor("ema").Should().Equal([20]);

        Action unknown = () => catalog.PeriodsFor("stochastic");
        unknown.Should().Throw<KeyNotFoundException>().WithMessage("Unknown indicator 'stochastic'*");
    }
}
