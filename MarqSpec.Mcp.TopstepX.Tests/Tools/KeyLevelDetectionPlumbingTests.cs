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
/// ban on per-call indicator parameters is about a storage key that cannot see them; there is no level store
/// at all — the table that never held a row was dropped under gh#276 — so there is no key for one to fall
/// out of.
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
    /// <param name="pivotRightLookback">
    /// The configured right-hand confirmation window, or null to mirror <paramref name="pivotLookback"/>.
    /// </param>
    /// <param name="maxZoneWidthPercent">The configured width cap, as a percentage of a zone's midpoint.</param>
    /// <param name="maxLevels">The configured level cap.</param>
    /// <remarks>
    /// <para>
    /// <b><see cref="KeyLevelDetectionOptions.MinSignificance"/> is zero here, deliberately.</b> Every pivot
    /// in the fixture scores exactly 0.4 — a prominence of four against an ATR of ten — so the shipped floor
    /// of 0.5 would filter all of them and every case below would be asserting about an empty list. The
    /// floor's own plumbing is pinned separately, by the pair that turns it up and watches the levels go.
    /// </para>
    /// <para>
    /// <b>The two caps are effectively off here for the same reason, and the right window MIRRORS the left
    /// unless a case names it.</b> The fixture is twenty-one bars around a price of 100 with an ATR of ten —
    /// a tenth of price, where a real five-minute future is nearer a thousandth — so the shipped 2.5% width
    /// cap would delete every zone in it, and the shipped 20/15 window would not fit the series at all. The
    /// mirror keeps every derivation in this file readable as "dominates N bars either side", which is how
    /// the fixture's own comment states them; the cases that are ABOUT the asymmetry set the two apart and
    /// say so.
    /// </para>
    /// </remarks>
    private static KeyLevelDetectionOptions Detection(
        PivotSource source = PivotSource.HighLow,
        int pivotLookback = 5,
        decimal zoneAtrMultiple = 0.5m,
        decimal minSignificance = 0m,
        int? pivotRightLookback = null,
        decimal maxZoneWidthPercent = 100m,
        int maxLevels = 1_000) =>
        new()
        {
            Source = source,
            PivotLookback = pivotLookback,
            ZoneAtrMultiple = zoneAtrMultiple,
            MinSignificance = minSignificance,
            PivotRightLookback = pivotRightLookback ?? pivotLookback,
            MaxZoneWidthPercent = maxZoneWidthPercent,
            MaxLevels = maxLevels,
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
        // Configured at 5 either side, asked for at 2 either side: the two shoulders come back as well as
        // the peak.
        ToolPayloads.LevelSet levels = await Tools(Detection(pivotLookback: 5))
            .GetKeyLevels(
                "ES", 5, Bars, pivotLookback: 2, pivotRightLookback: 2,
                cancellationToken: CancellationToken.None);

        levels.Levels.Select(l => l.Midpoint).Should().Equal(107m, 125m, 145m);
    }

    [Fact]
    public async Task ACallNamingOneEdgeOfTheWindow_LeavesTheOtherAtTheConfiguredValue()
    {
        // The consequence of an asymmetric window that a caller has to be able to see (gh#245), and it is
        // not obvious from the argument names. Naming `pivotLookback` moves the LEFT edge only; the right
        // stays at the configured 5, so the window is 2 and 5 rather than 2 and 2 — and the answer differs
        // from the case above by one whole level.
        //
        // Bar 5's shoulder at 125 is what goes. Its right window now reaches bar 10, whose high of 129 is
        // above it, so it stops dominating; bar 7's support and bar 14's peak both still clear five bars to
        // their right. Hand-checked against the fixture's midpoints, where high = mid + 5.
        ToolPayloads.LevelSet levels = await Tools(Detection(pivotLookback: 5))
            .GetKeyLevels("ES", 5, Bars, pivotLookback: 2, cancellationToken: CancellationToken.None);

        levels.Levels.Select(l => l.Midpoint).Should().Equal(107m, 145m);

        // And the payload says which window ran, which is the only way a caller tells this answer from the
        // symmetric one.
        levels.Detection.PivotLookback.Should().Be(2);
        levels.Detection.PivotRightLookback.Should().Be(5);
    }

    [Fact]
    public async Task ACallCanNameItsOwnRightLookback()
    {
        // The mirror image: the left edge stays at the configured 2 and the right is asked for at 5, which
        // is the same 2-and-5 window the case above reaches from the other side. Both arguments are
        // reachable, and neither is a synonym for the other.
        ToolPayloads.LevelSet levels = await Tools(Detection(pivotLookback: 2))
            .GetKeyLevels("ES", 5, Bars, pivotRightLookback: 5, cancellationToken: CancellationToken.None);

        levels.Levels.Select(l => l.Midpoint).Should().Equal(107m, 145m);
        levels.Detection.PivotLookback.Should().Be(2);
        levels.Detection.PivotRightLookback.Should().Be(5);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task APivotRightLookbackBelowOne_IsRefused(int asked)
    {
        // Zero is R-3.4 turned off rather than "no confirmation wanted": a pivot judged only by the bars
        // before it repaints as soon as the next one arrives. Refused at the tool boundary in the caller's
        // own vocabulary, the same way a left lookback below one is.
        Func<Task> read = () => Tools(Detection())
            .GetKeyLevels("ES", 5, Bars, pivotRightLookback: asked, cancellationToken: CancellationToken.None);

        (await read.Should().ThrowAsync<McpException>()).Which.Message
            .Should().Contain("pivotRightLookback");
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

    // ── A lookback the DETECTED window cannot satisfy is answered, and the answer says so ──────────

    [Theory]
    [InlineData(21)]
    [InlineData(500)]
    public async Task ALookbackTheStoredBarsCannotSatisfy_IsAnsweredWithTheDetectionStated(int lookbackBars)
    {
        // THE REVIEW FINDING, AS A TEST. An earlier revision refused this against `lookbackBars`, which is
        // the wrong quantity twice over: the store holds 21 bars whatever the caller asks for, so at 21 the
        // request was refused and at 500 -- the tool's OWN default -- the identical detection came back
        // empty and silent. Both rows now give the same answer, because the answer depends on what was
        // detected over and not on what was asked for.
        //
        // Not refused, because the honest refusal would have to name `detectedOverBars`, which no caller
        // controls: 21 is what the store holds. Explicable instead -- 11 + 11 + 1 = 23 needed, 21 detected
        // over, and both numbers are in the payload.
        //
        // BOTH edges are named, and since gh#245 that is load-bearing rather than tidy. The floor is
        // left + right + 1, so a left window of 11 against the configured right of 5 needs only 17 and this
        // fixture satisfies it -- the peak at bar 14 comes back and the case stops being about an
        // unsatisfiable window at all.
        ToolPayloads.LevelSet levels = await Tools(Detection())
            .GetKeyLevels(
                "ES", 5, lookbackBars, pivotLookback: 11, pivotRightLookback: 11,
                cancellationToken: CancellationToken.None);

        levels.Levels.Should().BeEmpty();
        levels.DetectedOverBars.Should().Be(Bars);
        levels.Detection.PivotLookback.Should().Be(
            11, "an empty level set is only distinguishable from no structure if the lookback is stated");
        levels.Detection.PivotRightLookback.Should().Be(
            11, "and the left edge alone does not say how many bars the window needed");
    }

    [Fact]
    public async Task AConfiguredLookbackTheSnapshotsFixedWindowCannotSatisfy_DoesNotBreakTheSnapshot()
    {
        // THE BLOCKING REVIEW FINDING. `get_market_snapshot` detects over a fixed `max(barCount, 200)` and
        // exposes NEITHER `pivotLookback` NOR `lookbackBars`. A configured lookback of 100 is legal on its
        // own range -- `[Range(1, 1_000)]`, and options validation passes it -- so bounding the lookback
        // against the requested window made the server boot clean and then fail EVERY snapshot call, with
        // advice to change two arguments this tool does not have.
        //
        // A refusal a caller cannot act on is an outage, not a refusal. So the snapshot answers, and the
        // level set explains itself.
        (_, SnapshotTools snapshot) = Compose(Detection(pivotLookback: 100));

        ToolPayloads.MarketSnapshot result =
            await snapshot.GetMarketSnapshot("ES", [5], 100, CancellationToken.None);

        ToolPayloads.LevelSet levels = result.PerResolution.Should().ContainSingle().Subject.Levels;

        levels.Levels.Should().BeEmpty();
        levels.DetectedOverBars.Should().Be(Bars);
        levels.Detection.PivotLookback.Should().Be(100);
    }

    // ── The answer reports the detection it actually ran under ───────────────────────────────────

    [Fact]
    public async Task TheReportedDetection_IsWhatRan_NotWhatWasConfigured()
    {
        // Rebuilt from configuration rather than projected from the options detection was handed, this would
        // report the operator's defaults on a call that overrode them -- a payload describing a detection
        // that did not happen, which is worse than reporting nothing.
        ToolPayloads.LevelSet levels = await Tools(Detection(
                source: PivotSource.HeikinAshiBody, pivotLookback: 5, zoneAtrMultiple: 1.5m, minSignificance: 0m,
                pivotRightLookback: 5, maxZoneWidthPercent: 100m, maxLevels: 1_000))
            .GetKeyLevels(
                "ES", 5, Bars, pivotSource: "HighLow", pivotLookback: 2, pivotRightLookback: 2,
                cancellationToken: CancellationToken.None);

        levels.Detection.Should().Be(
            new ToolPayloads.LevelDetection(PivotSource.HighLow, 2, 1.5m, 0m, 2, 100m, 1_000));
        // And the levels are the ones that detection produces, not the configured one's -- the report and the
        // answer have to come from the same options or the report is decoration.
        levels.Levels.Select(l => l.Midpoint).Should().Equal(107m, 125m, 145m);
        levels.Levels.Select(l => l.Top - l.Bottom).Should().AllBeEquivalentTo(15m);
    }

    [Fact]
    public async Task TheReportedDetection_IsStatedEvenWhenTheStoreHoldsNoBars()
    {
        // The empty-store exit returns before detection runs, and it used to return before the parameters
        // were even resolved. A caller reading `levels: []` there needs the same four numbers as anywhere
        // else -- more so, since `detectedOverBars` is 0 and every other explanation is still open.
        ToolPayloads.LevelSet levels = await Tools(Detection(source: PivotSource.Body, pivotLookback: 7))
            .GetKeyLevels("NQ", 5, Bars, cancellationToken: CancellationToken.None);

        levels.Levels.Should().BeEmpty();
        levels.DetectedOverBars.Should().Be(0);
        levels.Detection.Source.Should().Be(PivotSource.Body);
        levels.Detection.PivotLookback.Should().Be(7);
    }

    [Fact]
    public async Task TheReportedDetection_ExplainsAnEmptyAnswerThatIsNotAboutTheWindowAtAll()
    {
        // gh#247's trap from the other side. These 21 bars hold a pivot at every source, so an empty answer
        // here is the significance floor and nothing else -- and `detection.minSignificance` is the only
        // thing in the payload that says so. `detectedOverBars` is a full 21, which on its own reads as
        // "plenty of history, no structure".
        ToolPayloads.LevelSet levels = await Tools(Detection(minSignificance: 0.5m))
            .GetKeyLevels("ES", 5, Bars, cancellationToken: CancellationToken.None);

        levels.Levels.Should().BeEmpty();
        levels.DetectedOverBars.Should().Be(Bars);
        levels.Detection.MinSignificance.Should().Be(0.5m);
    }

    // ── Composition ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Builds the market-data tools over the seeded fixture, at a given detection configuration.</summary>
    /// <param name="detection">The detection defaults to build against.</param>
    /// <returns>The tools.</returns>
    private MarketDataTools Tools(KeyLevelDetectionOptions detection) => Compose(detection).MarketData;

    /// <summary>
    /// Builds the market-data tools <b>and the composed snapshot over the same store</b>.
    /// </summary>
    /// <param name="detection">The detection defaults to build against.</param>
    /// <returns>Both tools.</returns>
    /// <remarks>
    /// The snapshot is composed here rather than in its own fixture because it is the tool that reaches
    /// <c>GetKeyLevels</c> with a window <i>it</i> chose — a fixed <c>max(barCount, 200)</c> — and with
    /// neither detection argument. A bound on the requested window is invisible from
    /// <c>get_key_levels</c>'s own tests and fatal here, which is how the earlier revision got through.
    /// </remarks>
    private (MarketDataTools MarketData, SnapshotTools Snapshot) Compose(KeyLevelDetectionOptions detection)
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

        IndicatorProjector projector = new(_database, indicators, NullLogger<IndicatorProjector>.Instance);

        BarCacheService cache = new(
            _database, gateway, calendar, projector, clock, NullLogger<BarCacheService>.Instance);

        MarketDataTools marketData = new(
            cache,
            _database,
            new InstrumentRegistry(market),
            indicators,
            // gh#246's read-projection seam. Nothing here reads an indicator -- get_key_levels computes ATR
            // from the bars it just loaded rather than from the store -- but the tool takes it, and the
            // snapshot composed below DOES read indicators through it.
            new IndicatorCacheService(
                _database, indicators, projector, clock, NullLogger<IndicatorCacheService>.Instance),
            new LevelMethodCatalog(calendar),
            gateway,
            new ToolGuards(market),
            new StoreAvailabilityHolder(),
            clock,
            Options.Create(detection));

        SnapshotTools snapshot = new(
            marketData,
            new ReferenceTools(new InstrumentRegistry(market), calendar, gateway, market, clock),
            new IndicatorCatalogNames(indicators),
            clock);

        return (marketData, snapshot);
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
