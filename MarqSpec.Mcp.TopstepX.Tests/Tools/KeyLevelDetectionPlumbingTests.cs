using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Tests.MarketData;
using MarqSpec.Mcp.TopstepX.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol;

namespace MarqSpec.Mcp.TopstepX.Tests.Tools;

/// <summary>
/// What <c>get_key_levels</c> detects with — the configured defaults, and the two arguments that override
/// them for one call (gh#244).
/// </summary>
/// <remarks>
/// <para>
/// <b>The tool built <c>new KeyLevelOptions()</c> and threw the configuration away</b>, so
/// <see cref="PivotSource.Body"/> and <see cref="PivotSource.HighLow"/> were unreachable from the surface:
/// two of the three documented sources were dead code from a caller's point of view. These pin that they are
/// reachable, that they change the answer, and that an operator's setting is what an omitted argument falls
/// back to.
/// </para>
/// <para>
/// <b>Every reachability case asserts a level, not an acceptance.</b> gh#247 found that <c>Body</c> returns
/// <i>zero</i> pivots on a gapless series — where each bar opens at the previous close, a body high ties with
/// its neighbour's on every bar and nothing dominates — so a test that merely proved the argument was
/// accepted could pass with an empty level set and read as success. The fixture below gaps deliberately, and
/// each source is pinned to the price it actually produces.
/// </para>
/// <para>
/// <b>Per-call detection parameters are sound here because nothing stores a level</b> (ADR-0013). ADR-0006's
/// ban on per-call indicator parameters is about a storage key that cannot see them; <c>PriceLevels</c> has
/// no rows, so there is no key for one to fall out of.
/// </para>
/// </remarks>
public sealed class KeyLevelDetectionPlumbingTests : IDisposable
{
    private const string Contract = "CON.F.US.EP.U26";

    /// <summary>How many bars the fixture seeds, and the look-back every case asks for.</summary>
    private const int Bars = 21;

    private readonly TopstepXDbContext _database = new(
        new DbContextOptionsBuilder<TopstepXDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics
                .InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    public void Dispose() => _database.Dispose();

    // ──────────────────────────────────────────────────────────────────────────────────────────────────
    //  THE FIXTURE — one contract, 21 five-minute bars, two shoulders and a peak.
    //
    //  Each bar is written around a midpoint: open = mid - 1, close = mid + 1, high = mid + 5, low = mid - 5.
    //  The body is two points wide and the wicks reach four points past it either side, so the three sources
    //  read three different prices off the same bar. The open is NOT the previous close, which is what lets
    //  `Body` find anything at all (gh#247).
    //
    //    i:     0    1    2    3    4    5    6    7    8    9   10   11   12   13   14   15   16   17   18   19   20
    //    mid: 100  104  108  112  116  120  116  112  116  120  124  128  132  136  140  136  132  128  124  120  116
    //
    //  Two structures, deliberately at different scales:
    //    i = 5   a shoulder — dominates two bars either side, but bar 10 is higher, so it dies at lookback 5.
    //    i = 7   the mirrored support shoulder — bar 2 is lower, so it dies at lookback 5 too.
    //    i = 14  the peak — dominates five bars either side, so it survives both lookbacks.
    //
    //  TRUE RANGE IS EXACTLY 10 ON EVERY BAR, which is what makes every number below exact. With a step of
    //  four: high - low = 10; |high - prevClose| = |±4 + 4| is 8 or 0; |low - prevClose| = |±4 - 6| is 2 or
    //  10. So ATR(3) seeds at 10 and Wilder's smoothing keeps it there. A zone is 10 × multiple wide, and a
    //  prominence of 4 scores 0.4.
    // ──────────────────────────────────────────────────────────────────────────────────────────────────
    private static readonly int[] _midpoints =
        [100, 104, 108, 112, 116, 120, 116, 112, 116, 120, 124, 128, 132, 136, 140, 136, 132, 128, 124, 120, 116];

    private static DateTimeOffset SessionStart =>
        MarketClock.FromMarket(new DateOnly(2026, 8, 18), new TimeOnly(9, 0)).ToUniversalTime();

    private static DateTimeOffset Bucket(int index) => SessionStart.AddMinutes(5 * index);

    /// <summary>The detection defaults these tests run under unless a case says otherwise.</summary>
    /// <param name="source">The configured pivot source.</param>
    /// <param name="pivotLookback">The configured pivot lookback.</param>
    /// <param name="zoneAtrMultiple">The configured zone width, in ATR multiples.</param>
    /// <param name="minSignificance">The configured significance floor, in ATR multiples.</param>
    /// <returns>The options.</returns>
    /// <remarks>
    /// <b><see cref="KeyLevelDetectionOptions.MinSignificance"/> is zero here, deliberately.</b> Every pivot
    /// in the fixture scores exactly 0.4 — a prominence of four against an ATR of ten — so the shipped floor
    /// of 0.5 would filter all of them and every case below would be asserting about an empty list. The
    /// floor's own plumbing is pinned separately, by the pair that turns it up and watches the levels go.
    /// </remarks>
    private static KeyLevelDetectionOptions Detection(
        PivotSource source = PivotSource.HighLow,
        int pivotLookback = 5,
        decimal zoneAtrMultiple = 0.5m,
        decimal minSignificance = 0m) =>
        new()
        {
            Source = source,
            PivotLookback = pivotLookback,
            ZoneAtrMultiple = zoneAtrMultiple,
            MinSignificance = minSignificance,
        };

    // ── The configured defaults are what an omitted argument falls back to ───────────────────────────

    [Theory]
    [InlineData(PivotSource.HighLow, 145)]
    [InlineData(PivotSource.Body, 141)]
    [InlineData(PivotSource.HeikinAshiBody, 140)]
    public async Task TheConfiguredSource_IsWhatDetects_WhenTheCallDoesNotNameOne(PivotSource configured, int midpoint)
    {
        // Bar 14 is the peak at a midpoint of 140. HighLow reads its wick at 145, Body reads the top of its
        // body at 141, and Heikin-Ashi reads its own smoothed close, which for these bars is the midpoint
        // itself at 140 — three prices off one bar, and the level lands on whichever one was configured.
        // The zone is the pivot price ± ATR × multiple ÷ 2, so its MIDPOINT is the pivot price exactly.
        ToolPayloads.LevelSet levels = await Tools(Detection(source: configured))
            .GetKeyLevels("ES", 5, Bars, cancellationToken: CancellationToken.None);

        levels.Levels.Should().ContainSingle(
            "the peak at bar 14 dominates five bars either side under every source")
            .Which.Midpoint.Should().Be(midpoint);
    }

    [Fact]
    public async Task TheConfiguredPivotLookback_IsWhatDetects_WhenTheCallDoesNotNameOne()
    {
        // Lookback 2 admits the two shoulders as well as the peak; lookback 5 admits only the peak. Both
        // answers are non-empty, so the difference is a DIFFERENT set of levels rather than the presence or
        // absence of any -- an assertion that "the override emptied the list" would pass just as well if the
        // request had been silently refused.
        ToolPayloads.LevelSet wide = await Tools(Detection(pivotLookback: 5))
            .GetKeyLevels("ES", 5, Bars, cancellationToken: CancellationToken.None);

        ToolPayloads.LevelSet narrow = await Tools(Detection(pivotLookback: 2))
            .GetKeyLevels("ES", 5, Bars, cancellationToken: CancellationToken.None);

        wide.Levels.Select(l => l.Midpoint).Should().Equal(145m);
        narrow.Levels.Select(l => l.Midpoint).Should().Equal(107m, 125m, 145m);
    }

    [Fact]
    public async Task TheConfiguredZoneWidth_SizesEveryZone_AndIsNotAskableForPerCall()
    {
        // Half an ATR by default; the fixture's ATR is exactly 10, so the zone is five points wide. The
        // width is an operator setting only, because gh#232's confluence compares zones across methods and a
        // width that moved per request would make two scores incomparable without either being wrong.
        ToolPayloads.LevelInfo narrow = (await Tools(Detection(zoneAtrMultiple: 0.5m))
            .GetKeyLevels("ES", 5, Bars, cancellationToken: CancellationToken.None)).Levels.Single();

        ToolPayloads.LevelInfo wide = (await Tools(Detection(zoneAtrMultiple: 1.5m))
            .GetKeyLevels("ES", 5, Bars, cancellationToken: CancellationToken.None)).Levels.Single();

        (narrow.Top - narrow.Bottom).Should().Be(5m);
        (wide.Top - wide.Bottom).Should().Be(15m);

        typeof(MarketDataTools).GetMethod(nameof(MarketDataTools.GetKeyLevels))!
            .GetParameters().Select(p => p.Name)
            .Should().NotContain("zoneAtrMultiple",
                "the width is server-wide so that two levels this server reports can be compared");
    }

    [Fact]
    public async Task TheConfiguredSignificanceFloor_FiltersEveryZone_AndIsNotAskableForPerCall()
    {
        // Every pivot here scores exactly 0.4 -- prominence 4 against ATR 10 -- so a floor of 0.4 admits them
        // and the shipped 0.5 does not. The floor is the parameter whose wrong value is least visible: an
        // empty level set reads as "this market has no structure", which is a conclusion, and nothing in the
        // payload would say the floor had moved. It stays an operator setting for that reason.
        ToolPayloads.LevelSet admitted = await Tools(Detection(minSignificance: 0.4m))
            .GetKeyLevels("ES", 5, Bars, cancellationToken: CancellationToken.None);

        ToolPayloads.LevelSet filtered = await Tools(Detection(minSignificance: 0.5m))
            .GetKeyLevels("ES", 5, Bars, cancellationToken: CancellationToken.None);

        admitted.Levels.Should().ContainSingle().Which.Significance.Should().Be(0.4m);
        filtered.Levels.Should().BeEmpty();

        typeof(MarketDataTools).GetMethod(nameof(MarketDataTools.GetKeyLevels))!
            .GetParameters().Select(p => p.Name)
            .Should().NotContain("minSignificance");
    }

    // ── The two arguments a call may override ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("HighLow", 145)]
    [InlineData("Body", 141)]
    [InlineData("HeikinAshiBody", 140)]
    [InlineData("  body  ", 141)]
    [InlineData("HIGHLOW", 145)]
    public async Task ACallCanNameItsOwnSource_AndBodyAndHighLowActuallyProduceLevels(string named, int midpoint)
    {
        // The acceptance criterion, and the reason it is written as a price rather than as "did not throw":
        // Body finds NO pivots at all on a gapless series, so "the argument was accepted" is a green a
        // reader would misread as "the source works". The configured source is deliberately the one this
        // call is NOT asking for in two of the five rows.
        ToolPayloads.LevelSet levels = await Tools(Detection(source: PivotSource.HeikinAshiBody))
            .GetKeyLevels("ES", 5, Bars, pivotSource: named, cancellationToken: CancellationToken.None);

        levels.Levels.Should().ContainSingle().Which.Midpoint.Should().Be(midpoint);
    }

    [Fact]
    public async Task ACallCanNameItsOwnPivotLookback()
    {
        // Configured at 5, asked for at 2: the two shoulders come back as well as the peak.
        ToolPayloads.LevelSet levels = await Tools(Detection(pivotLookback: 5))
            .GetKeyLevels("ES", 5, Bars, pivotLookback: 2, cancellationToken: CancellationToken.None);

        levels.Levels.Select(l => l.Midpoint).Should().Equal(107m, 125m, 145m);
    }

    [Fact]
    public async Task OmittingBothArguments_TakesBothConfiguredValues_RatherThanTheRecordsOwnDefaults()
    {
        // `KeyLevelOptions` carries C# defaults of lookback 5 and HeikinAshiBody, which is exactly what the
        // tool used to construct and is why this card exists. Configuring something else and omitting both
        // arguments is the only way to tell "the configuration was read" from "the record's defaults happen
        // to agree with it".
        ToolPayloads.LevelSet levels = await Tools(Detection(source: PivotSource.HighLow, pivotLookback: 2))
            .GetKeyLevels("ES", 5, Bars, cancellationToken: CancellationToken.None);

        levels.Levels.Select(l => l.Midpoint).Should().Equal(107m, 125m, 145m);
    }

    // ── Unknown = 0 is refused on every path into the tool ───────────────────────────────────────────

    [Theory]
    [InlineData("fibonacci")]
    [InlineData("Unknown")]
    [InlineData("0")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ASourceOutsideTheVocabulary_IsAnError_NamingTheValidOnes(string named)
    {
        Func<Task> read = () => Tools(Detection())
            .GetKeyLevels("ES", 5, Bars, pivotSource: named, cancellationToken: CancellationToken.None);

        (await read.Should().ThrowAsync<McpException>()).Which.Message
            .Should().Contain("HeikinAshiBody, Body, HighLow");
    }

    [Fact]
    public async Task AnUnsetConfiguredSource_IsRefused_NotSilentlyDetectedThrough()
    {
        // The path the card calls out: a value arriving from CONFIGURATION rather than from the caller.
        // Startup validation refuses this one too, and this is the second door on the same room -- the two
        // are opened by different keys, and `KeyLevels.PivotPrices` reads anything it does not recognise as
        // Heikin-Ashi, so an unvalidated Unknown would have produced an ordinary-looking level set.
        Func<Task> read = () => Tools(Detection(source: PivotSource.Unknown))
            .GetKeyLevels("ES", 5, Bars, cancellationToken: CancellationToken.None);

        (await read.Should().ThrowAsync<McpException>()).Which.Message
            .Should().Contain("HeikinAshiBody, Body, HighLow")
            .And.Contain("KeyLevels__Source", "the refusal has to name the setting an operator can change");
    }

    [Fact]
    public async Task AnUnsetConfiguredSource_IsRefused_EvenWhenTheStoreHoldsNoBarsForTheSymbol()
    {
        // The reason the whole resolution happens BEFORE the read. `get_key_levels` returns an empty level
        // set for a symbol with no bars, so validating afterwards would answer a misconfigured server with
        // "no levels" -- which is what an unfetched symbol looks like anyway. NQ is configured and has no
        // rows in this fixture.
        Func<Task> read = () => Tools(Detection(source: PivotSource.Unknown))
            .GetKeyLevels("NQ", 5, Bars, cancellationToken: CancellationToken.None);

        (await read.Should().ThrowAsync<McpException>()).Which.Message
            .Should().Contain("HeikinAshiBody, Body, HighLow");
    }

    // ── A lookback the window cannot satisfy is refused, not answered with nothing ───────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task APivotLookbackBelowOne_IsRefused(int asked)
    {
        Func<Task> read = () => Tools(Detection())
            .GetKeyLevels("ES", 5, Bars, pivotLookback: asked, cancellationToken: CancellationToken.None);

        (await read.Should().ThrowAsync<McpException>()).Which.Message
            .Should().Contain("pivotLookback");
    }

    [Fact]
    public async Task APivotLookbackNoWindowThisWideCanSatisfy_IsRefused_RatherThanAnsweredWithNoLevels()
    {
        // 21 bars can hold a pivot at a lookback of 10 and cannot at 11: a pivot dominates the lookback on
        // BOTH sides, so it needs 2n + 1 bars. Refused rather than answered, because an empty level set is
        // the same shape as "this market has no structure" and only one of the two is a conclusion.
        Func<Task> impossible = () => Tools(Detection())
            .GetKeyLevels("ES", 5, Bars, pivotLookback: 11, cancellationToken: CancellationToken.None);

        (await impossible.Should().ThrowAsync<McpException>()).Which.Message
            .Should().Contain("23", "the refusal states how many bars that lookback would need")
            .And.Contain("21", "and how many were asked for");

        // The counterweight: the largest lookback this window CAN satisfy is not refused. A bound that
        // rejected the boundary case would be a bound nobody could work out from its own message.
        Func<Task> possible = () => Tools(Detection())
            .GetKeyLevels("ES", 5, Bars, pivotLookback: 10, cancellationToken: CancellationToken.None);

        await possible.Should().NotThrowAsync();
    }

    // ── Composition ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Builds the market-data tools over the seeded fixture, at a given detection configuration.</summary>
    /// <param name="detection">The detection defaults to build against.</param>
    /// <returns>The tools.</returns>
    private MarketDataTools Tools(KeyLevelDetectionOptions detection)
    {
        if (!_database.Bars.Any())
        {
            Seed();
        }

        IOptions<MarketDataOptions> market = Options.Create(new MarketDataOptions
        {
            Instruments = "ES,NQ",
            MaxRows = 5_000,
            SessionCloseCentral = "16:00",
        });

        BarSessionCalendar calendar = BarSessionCalendar.Parse("16:00", []);

        // ATR(3), so a value exists from bar 3 onward and every pivot in the fixture is scaled by a real
        // one. At the shipped 14 the two shoulders would carry no ATR and be SKIPPED -- silently, since a
        // pivot with no scale yields no zone -- and the lookback cases would be asserting about that instead.
        IndicatorCatalog indicators = new(
            Options.Create(new IndicatorOptions { AtrPeriod = 3 }), calendar);

        CountingGateway gateway = new([]);
        FakeTimeProvider clock = new(Bucket(Bars).AddHours(2));

        BarCacheService cache = new(
            _database,
            gateway,
            calendar,
            new IndicatorProjector(_database, indicators, NullLogger<IndicatorProjector>.Instance),
            clock,
            NullLogger<BarCacheService>.Instance);

        return new MarketDataTools(
            cache,
            _database,
            new InstrumentRegistry(market),
            indicators,
            new LevelMethodCatalog(),
            gateway,
            new ToolGuards(market),
            new StoreAvailabilityHolder(),
            clock,
            Options.Create(detection));
    }

    private void Seed()
    {
        for (int i = 0; i < _midpoints.Length; i++)
        {
            decimal mid = _midpoints[i];
            _database.Bars.Add(new BarRecord
            {
                Venue = "test",
                Instrument = "ES",
                ResolutionMinutes = 5,
                BucketStart = Bucket(i),
                Open = mid - 1m,
                High = mid + 5m,
                Low = mid - 5m,
                Close = mid + 1m,
                Volume = 1_000,
                ContractId = Contract,
                RecordedAt = SessionStart,
            });
        }

        _database.SaveChanges();
    }
}
