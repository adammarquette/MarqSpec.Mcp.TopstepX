using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Telemetry;
using MarqSpec.Mcp.TopstepX.Tests.MarketData;
using MarqSpec.Mcp.TopstepX.Tools;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MarqSpec.Mcp.TopstepX.Tests.Tools;

/// <summary>
/// A non-positive <c>resolutionMinutes</c> is refused by every tool that takes one (gh#69).
/// </summary>
/// <remarks>
/// <para>
/// The rule already existed — inside <c>ToolGuards.ValidateWindow</c>, which only the windowed tools call. The
/// four tools that build their own window never reached it, so a <c>0</c> ran on into
/// <c>BarGapDetector.AlignDown</c> and crossed the tool boundary as a raw
/// <see cref="ArgumentOutOfRangeException"/>: a stack-shaped failure with no statement of what the caller did
/// wrong, for a mistake this server can name exactly. Two of the four did not even fail — they matched no row
/// and answered "cannot measure", which is a wrong question rendered as an answer.
/// </para>
/// <para>
/// The last test is the one that matters most. It walks the surface by reflection rather than naming tools, so
/// the guard cannot quietly fall off a tool added tomorrow — the exact failure <c>ToolGuards</c> was written
/// to prevent, and then suffered itself.
/// </para>
/// <para>
/// <b>That sweep keys on the parameter <i>name</i>, so name yours <c>resolutionMinutes</c>.</b> A rename on one
/// of today's six trips the count floor and fails loudly; a <i>new</i> tool spelling the same concept
/// <c>timeframeMinutes</c> or <c>barSizeMinutes</c> leaves the count at six and is silently uncovered. It is
/// the one door left open, and the only part of this fixture that fails quietly — <c>Instance</c> and
/// <c>Blank</c> both throw and say what to add. A marker attribute would close it properly, and costs more
/// than it buys while the surface is six methods.
/// </para>
/// <para>
/// <b>The case that proves the guard does not over-reject is not in this file.</b> Everything here refuses,
/// and a refusal never reaches a store — which is what lets these keep running on the in-memory provider with
/// no container. Showing that a <i>valid</i> resolution still answers means serving the read, and serving one
/// now runs the real <c>ON CONFLICT … DO UPDATE</c> bar and coverage writes, so it moved down to
/// <c>MarqSpec.Mcp.TopstepX.IntegrationTests.ResolutionGuardServedReadTests</c> (gh#387). Read the two
/// together: a guard proven only to refuse is a guard nobody has checked for over-reach.
/// </para>
/// </remarks>
public sealed class ResolutionGuardTests : IDisposable
{
    private const string Contract = "CON.F.US.EP.Z26";
    private const int SeededBars = 40;

    private readonly TopstepXDbContext _database;
    private readonly CountingGateway _gateway;
    private readonly BarTools _bars;
    private readonly IndicatorTools _indicators;
    private readonly KeyLevelTools _keyLevels;
    private readonly TapeTools _tape;
    private readonly ContractRollTools _roll;
    private readonly SnapshotTools _snapshot;
    private readonly BarCacheService _cache;
    private readonly IndicatorCatalog _catalog;
    private readonly FakeTimeProvider _clock;
    private readonly HostTelemetry _telemetry = new();

    public ResolutionGuardTests()
    {
        _database = new TopstepXDbContext(
            new DbContextOptionsBuilder<TopstepXDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics
                    .InMemoryEventId.TransactionIgnoredWarning))
                .Options);

        for (int i = 0; i < SeededBars; i++)
        {
            _database.Bars.Add(new BarRecord
            {
                Venue = "test",
                Instrument = "ES",
                ResolutionMinutes = 5,
                BucketStart = Bucket(i),
                Open = 100m,
                High = 101m,
                Low = 99m,
                Close = 100m,
                Volume = 1_000,
                ContractId = Contract,
                RecordedAt = SessionStart,
            });
        }

        _database.SaveChanges();

        IOptions<MarketDataOptions> options = Options.Create(new MarketDataOptions
        {
            Instruments = "ES,NQ",
            MaxRows = 5_000,
            SessionCloseCentral = "16:00",
        });

        BarSessionCalendar calendar = BarSessionCalendar.Parse("16:00", []);
        _catalog = new IndicatorCatalog(
            Options.Create(new IndicatorOptions { AtrPeriod = 3, RsiPeriod = 3 }), calendar);
        _clock = new FakeTimeProvider(Bucket(SeededBars).AddHours(2));

        _gateway = new CountingGateway([]);

        IndicatorProjector projector =
            new(_database, _catalog, NullLogger<IndicatorProjector>.Instance, _telemetry);
        _cache = new BarCacheService(
            _database,
            _gateway,
            calendar,
            projector,
            new InstrumentRegistry(options),
            new ContractDirectory(_clock),
            _clock,
            NullLogger<BarCacheService>.Instance,
            _telemetry);

        // Five market-data tool types now, not one (gh#414). The sweep below walks the surface by
        // reflection and maps a declaring type to an instance, so EVERY one of them has to be built here --
        // a type the map cannot build throws by name rather than dropping out of the sweep.
        InstrumentResolver resolver = new(new InstrumentRegistry(options), new StoreAvailabilityHolder());
        ToolGuards guards = new(options);
        VolumeFrontReader front = new(new TapeVolumeFrontService(_database, _gateway, calendar));

        _bars = new BarTools(resolver, _cache, guards, _clock);

        _indicators = new IndicatorTools(
            resolver,
            _database,
            _catalog,
            new IndicatorCacheService(
                _database,
                _catalog,
                new IndicatorProjector(_database, _catalog, NullLogger<IndicatorProjector>.Instance, _telemetry),
                _clock,
                NullLogger<IndicatorCacheService>.Instance,
                _telemetry),
            _gateway,
            guards);

        _keyLevels = new KeyLevelTools(
            resolver,
            _database,
            _catalog,
            new LevelMethodCatalog(calendar),
            _gateway,
            guards,
            new VolumeProfileService(_database),
            Options.Create(new KeyLevelDetectionOptions()));

        _tape = new TapeTools(
            resolver,
            _database,
            _gateway,
            guards,
            new TapeAvailabilityHolder(),
            new VolumeProfileService(_database),
            front,
            new FootprintCacheService(
                _database,
                new FootprintProjector(_database, NullLogger<FootprintProjector>.Instance),
                _clock,
                NullLogger<FootprintCacheService>.Instance,
                _telemetry));

        _roll = new ContractRollTools(
            resolver, _database, _gateway, new LevelMethodCatalog(calendar), front, _clock);

        _snapshot = new SnapshotTools(
            _bars,
            _indicators,
            _keyLevels,
            new ReferenceTools(new InstrumentRegistry(options), calendar, _gateway, options, _clock),
            new IndicatorCatalogNames(_catalog),
            _clock);
    }

    public void Dispose()
    {
        _database.Dispose();
        _telemetry.Dispose();
    }

    private static DateTimeOffset SessionStart =>
        MarketClock.FromMarket(new DateOnly(2026, 8, 18), new TimeOnly(9, 0)).ToUniversalTime();

    private static DateTimeOffset Bucket(int index) => SessionStart.AddMinutes(5 * index);

    // ── The rule itself, with no window in sight ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ANonPositiveResolution_IsRefused_WithNoWindowToValidateItAgainst(int resolutionMinutes)
    {
        // The rule's new home. Inside ValidateWindow it was reachable only when a window was ALSO being
        // validated, which is why four tools never met it.
        Action validate = () => ToolGuards.ValidateResolution(resolutionMinutes);

        validate.Should().Throw<McpException>()
            .WithMessage("*resolutionMinutes*")
            .WithMessage("*" + resolutionMinutes.ToString(CultureInfo.InvariantCulture) + "*");
    }

    [Fact]
    public void APositiveResolution_PassesThroughUnchanged()
    {
        ToolGuards.ValidateResolution(5).Should().Be(5);
    }

    [Fact]
    public void TheWindowedRule_StillRefusesToo()
    {
        // Moving the check must not lose it. ValidateWindow still rejects, by delegating rather than by
        // carrying its own copy.
        ToolGuards guards = new(Options.Create(new MarketDataOptions
        {
            Instruments = "ES",
            MaxRows = 5_000,
            SessionCloseCentral = "16:00",
        }));

        Action validate = () => guards.ValidateWindow(Bucket(0), Bucket(8), 0);

        validate.Should().Throw<McpException>().WithMessage("*resolutionMinutes*");
    }

    // ── The four tools that build their own window ───────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task GetLatestBars_RefusesANonPositiveResolution(int resolutionMinutes)
    {
        // The reported symptom. TimeSpan.FromMinutes(0) reached BarGapDetector.AlignDown and left the tool
        // boundary as an ArgumentOutOfRangeException -- an unhandled fault where a tool error belongs.
        Func<Task> call = () =>
            _bars.GetLatestBars("ES", resolutionMinutes, 10, CancellationToken.None);

        (await call.Should().ThrowAsync<McpException>()).WithMessage("*resolutionMinutes*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task GetIndicatorAt_RefusesANonPositiveResolution(int resolutionMinutes)
    {
        // Worse than a crash: this one never threw. The query matched no row and the tool answered
        // { value: null } -- "cannot measure", which is what a genuine warm-up gap says. An impossible
        // timeframe and an honest absence must not be the same reply.
        Func<Task> call = () => _indicators.GetIndicatorAt(
            "ES", resolutionMinutes, "atr", Bucket(SeededBars), cancellationToken: CancellationToken.None);

        (await call.Should().ThrowAsync<McpException>()).WithMessage("*resolutionMinutes*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task GetKeyLevels_RefusesANonPositiveResolution(int resolutionMinutes)
    {
        // The same silence in a different shape: no bars matched, so it returned an empty level set. Nothing
        // in "no levels here" tells the caller the timeframe it asked for cannot exist.
        Func<Task> call = () =>
            _keyLevels.GetKeyLevels("ES", resolutionMinutes, 100, cancellationToken: CancellationToken.None);

        (await call.Should().ThrowAsync<McpException>()).WithMessage("*resolutionMinutes*");
    }

    // ── The composed read, which fails as a set or not at all ────────────────────────────────────────

    [Fact]
    public void ResolveResolutions_RefusesTheWholeSet_WhenOneMemberIsNotPositive()
    {
        // The named home for resolution policy is where the set is judged. Pinned here as a pure function,
        // because that is where the whole-set property lives -- no store, no venue, no ordering.
        Action resolve = () => SnapshotTools.ResolveResolutions([5, 0, 60]);

        resolve.Should().Throw<McpException>()
            .WithMessage("*resolutionMinutes*")
            .WithMessage("*0*");
    }

    [Fact]
    public void ResolveResolutions_StillReturnsAValidSet()
    {
        // The guard must not change what a good set resolves to.
        SnapshotTools.ResolveResolutions([15, 60]).Should().Equal(15, 60);
        SnapshotTools.ResolveResolutions(null).Should().Equal(5, 60);
    }

    [Fact]
    public async Task GetMarketSnapshot_RefusesAMixedSet_WithoutFetchingTheGoodResolutionsFirst()
    {
        // The case that separates a real fix from four patched call sites. Judging each resolution as its
        // turn came round would let [5, 0, 60] fetch a whole five-minute slice and then throw -- the caller
        // holds half a snapshot AND an error, which is worse than either on its own.
        Func<Task> call = () =>
            _snapshot.GetMarketSnapshot("ES", [5, 0, 60], 10, CancellationToken.None);

        (await call.Should().ThrowAsync<McpException>()).WithMessage("*resolutionMinutes*");

        _gateway.BarRequests.Should().Be(0, "the set is judged before the first slice is read");
        _gateway.ContractRequests.Should().Be(0, "and before the contract behind it is resolved");
    }

    // ── The other end of the same axis (gh#81) ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(ToolGuards.MaxResolutionMinutes + 1)]
    public void AResolutionPastTheCeiling_IsRefused(int resolutionMinutes)
    {
        // The rule's other direction. gh#69 gave it a floor and left the wording exhaustive; it was not.
        Action validate = () => ToolGuards.ValidateResolution(resolutionMinutes);

        validate.Should().Throw<McpException>()
            .WithMessage("*resolutionMinutes*")
            .WithMessage("*" + resolutionMinutes.ToString(CultureInfo.InvariantCulture) + "*");
    }

    [Fact]
    public void AResolutionAtTheCeiling_PassesThroughUnchanged()
    {
        // A guard that is red on correct input is not a guard, it is an outage. The ceiling is inclusive.
        ToolGuards.ValidateResolution(ToolGuards.MaxResolutionMinutes)
            .Should().Be(ToolGuards.MaxResolutionMinutes);
    }

    [Fact]
    public void TheLookbackReach_IsUnchangedForAnOrdinaryRequest()
    {
        // The arithmetic moved into ToolGuards, so pin what it produces rather than trusting the move. Four
        // bar spans per bar wanted, plus four days -- hand-computed, not read back from the implementation.
        DateTimeOffset end = Bucket(SeededBars);

        BarRange window = ToolGuards.LookbackWindow(end, 5, 10);

        window.End.Should().Be(end);
        window.Start.Should().Be(end - TimeSpan.FromMinutes(5 * 10 * 4) - TimeSpan.FromDays(4));
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(ToolGuards.MaxResolutionMinutes + 1)]
    public async Task GetLatestBars_RefusesAResolutionPastTheCeiling(int resolutionMinutes)
    {
        // The reported symptom. `barSize.Ticks * wanted * 4` is long arithmetic in an UNCHECKED context: at
        // int.MaxValue minutes barSize.Ticks is already ~1.3e18, so the product wraps and `end - reach` lands
        // outside DateTime's range -- a raw ArgumentOutOfRangeException where a tool error belongs. It
        // survives gh#69's guard for the one reason that guard cannot help with: the value is positive.
        Func<Task> call = () =>
            _bars.GetLatestBars("ES", resolutionMinutes, 10, CancellationToken.None);

        (await call.Should().ThrowAsync<McpException>()).WithMessage("*resolutionMinutes*");
    }

    // The boundary from the SERVED side — that the ceiling still answers, with bars — is
    // MarqSpec.Mcp.TopstepX.IntegrationTests.ResolutionGuardServedReadTests.AResolutionAtTheCeiling_StillAnswers.
    // It lived here as `NotThrowAsync` until gh#538, which is a weaker claim than it reads as: a guard that
    // over-rejects throws, and so does nothing else, so "did not throw" is satisfied by a tool that answers
    // an empty series — the exact shape this whole boundary exists to abolish. Asserting bars means serving
    // the read, and serving one needs the container (gh#387).

    [Fact]
    public async Task ACountThatWouldReachBeforeTheCalendar_IsRefused_NotFaulted()
    {
        // The ceiling alone does NOT close this bug, and this is the proof. MaxRows is operator
        // configuration -- [Range(1, 1_000_000)] on MarketDataOptions -- and the reach is FOUR bar spans per
        // bar asked for. THE CEILING here, by the constant: at 660 minutes, 500,000 bars SPAN about 627
        // years, so they REACH about 2,510 -- past year one, and `end - reach` throws exactly the way
        // int.MaxValue did. The 4x is the whole finding: it is what carries a pair that is legal on both axes
        // past a calendar neither axis knows about, and it puts the real boundary near 403,000 bars rather
        // than 500,000. Nothing about this request is out of range on either axis taken alone.
        BarTools capped = WithRowCap(1_000_000);

        Func<Task> call = () =>
            capped.GetLatestBars("ES", ToolGuards.MaxResolutionMinutes, 500_000, CancellationToken.None);

        (await call.Should().ThrowAsync<McpException>()).WithMessage("*resolutionMinutes*");

        // Refused, not quietly shortened. Clamping the window to year one would answer with however many
        // bars the store happened to hold -- a series indistinguishable from a complete one, which is the
        // failure mode ValidateWindow already refuses to commit for an over-cap window.
        _gateway.BarRequests.Should().Be(0, "the reach is judged before the first page is read");
    }

    [Fact]
    public void ANegativeCount_IsRefused_RatherThanWrappingBackThroughTheNarrowingCast()
    {
        // The guard bounds only the UPPER side of the reach, and the widened Int128 is narrowed back with an
        // UNCHECKED cast. A negative count makes the product negative, so `reach > end.UtcTicks` is false and
        // `(long)reach` wraps -- reintroducing, inside the new guard, the raw fault this guard exists to
        // remove. Not reachable through a tool today because ValidateCount runs first, but LookbackWindow is
        // public static and its only stated defence is a <param> comment saying "already validated". The
        // resolution is THE CEILING, by the constant -- the sign of the count is what is on trial here, and
        // the coarsest servable bar is where the product is largest (gh#498).
        Action size = () =>
            ToolGuards.LookbackWindow(Bucket(SeededBars), ToolGuards.MaxResolutionMinutes, -1_000_000);

        size.Should().Throw<McpException>()
            .WithMessage("*count*")
            .WithMessage("*-1000000*");
    }

    // ── A session-length bar is not a bar resolution at all (gh#498) ─────────────────────────────────

    [Theory]
    [InlineData(1_380)]
    [InlineData(1_440)]
    [InlineData(10_080)]
    public async Task ADailyResolution_IsRefused_NamingSessionBars(int resolutionMinutes)
    {
        // The day and the week were INSIDE the old ceiling and answered with an empty series. A session runs
        // 24 hours less the venue's one-hour maintenance window -- 1,380 minutes -- and
        // BarSessionCalendar.IsExpectedBucket only ever expects a bucket that closes at or before the
        // session's close, while BarGapDetector.AlignUp anchors buckets on a fixed UTC-midnight grid. So no
        // bucket of 1,380 minutes or more is ever expected, every one of them is a gap that is never a bar,
        // and get_bars at 1440 returned [] with nothing said. An empty series where the question was simply
        // the wrong shape is the failure this repository refuses to commit: it is refused at the boundary,
        // and the refusal names where the answer actually lives.
        //
        // 1,380 is the session's own length -- the first value past the ceiling, and the boundary case.
        Func<Task> call = () =>
            _bars.GetLatestBars("ES", resolutionMinutes, 10, CancellationToken.None);

        (await call.Should().ThrowAsync<McpException>())
            .WithMessage("*resolutionMinutes*", "the refusal names the parameter the caller can change")
            .WithMessage(
                "*" + resolutionMinutes.ToString(CultureInfo.InvariantCulture) + "*",
                "and the value that was asked for")
            .WithMessage(
                "*session bar*",
                "and says what a bar this long IS, so the caller is not left thinking it is unavailable");

        // Refused before any store or venue work, like every other guard on this boundary.
        _gateway.BarRequests.Should().Be(0, "the resolution is judged before the first page is read");
        _gateway.ContractRequests.Should().Be(0, "and before the contract behind it is resolved");
    }

    // ── A bucket wider than half the SHORTEST session cannot be guaranteed to fit one (gh#538) ───────

    [Fact]
    public void TheCeiling_IsHalfTheShortestSession_AndItIsDerivedRatherThanChosen()
    {
        // 660, and the number is a consequence of two facts already in the code rather than a preference.
        //
        // Buckets are anchored on a fixed UTC grid (BarGapDetector.AlignUp), NOT on the session open, and
        // BarSessionCalendar.IsExpectedBucket expects a bucket only when it both opens inside the session
        // and closes at or before that session's close. So a session S minutes long admits an r-minute
        // bucket exactly when some multiple of r lands in [open, close - r] -- a run of S - r + 1
        // consecutive whole minutes. A run of n consecutive integers is CERTAIN to contain a multiple of r
        // only while n >= r, so the guarantee holds exactly while S - r + 1 >= r, i.e. r <= (S + 1) / 2.
        //
        // S IS THE SHORTEST SESSION AND NOT THE NOMINAL ONE, which is the whole of the PR #607 review's
        // first finding. A session is 1,380 minutes of Central WALL CLOCK, and at a close before 01:00 the
        // reopen is at or before 02:00, so the spring-forward transition falls INSIDE Monday's session and
        // it is 1,320 minutes of elapsed time. TheSessionLengthCensus below measures that; this only
        // depends on it.
        //
        // The two derivations gh#538 offered -- the pigeonhole bound, and "the largest divisor of the
        // session length" -- agree at 1,320 because it is even. They do NOT agree in general: at S = 1,379
        // the pigeonhole form gives 690 and the largest-proper-divisor form gives 197. The general form is
        // the one implemented, so the agreement is a note rather than a second derivation.
        ToolGuards.SessionMinutes.Should().Be(
            1_380, "a session is 24 hours of wall clock less the venue's one-hour maintenance window");
        ToolGuards.ShortestSessionMinutes.Should().Be(
            1_320, "and it loses an hour when it contains the spring-forward transition");

        ToolGuards.MaxResolutionMinutes.Should().Be(
            (ToolGuards.ShortestSessionMinutes + 1) / 2,
            "the ceiling is the pigeonhole bound on the SHORTEST session");
        ToolGuards.MaxResolutionMinutes.Should().Be(660, "which is 660");

        // C#'s integer division truncates, and that is what makes (S + 1) / 2 the largest admissible
        // INTEGER rather than merely a rounding of the real bound -- for an odd S as well as an even one.
        foreach (int session in new[] { 1_319, 1_320, 1_321, 1_379, 1_380 })
        {
            int bound = (session + 1) / 2;

            (session - bound + 1).Should().BeGreaterThanOrEqualTo(
                bound, "the bound itself must satisfy S - r + 1 >= r, at S = " + session);
            (session - (bound + 1) + 1).Should().BeLessThan(
                bound + 1, "and one wider must not, at S = " + session);
        }
    }

    [Fact]
    public void TheSessionLengthCensus_ShowsAShortestSessionOf1320_AtEveryCloseBefore0200()
    {
        // The fact the ceiling rests on, measured across the whole configuration surface rather than at the
        // shipped close. `SessionCloseCentral` is operator configuration with NO range validation, so
        // "1,380 minutes" is a claim about every value it can hold, and PR #607's first review found it
        // false: the reviewer's example is `SessionCloseCentral = "00:30"`, trade date 2030-03-11.
        //
        // ONE YEAR is enough and the reason is not "it seemed like plenty": whether a session contains a
        // daylight-saving transition is a WALL-CLOCK property of the close -- the transition is 02:00
        // Central on a Sunday every year -- so a year containing both transitions settles it for every
        // year. What a longer window buys is more chances for a GRID PHASE to fail, which is a different
        // question, asked by the sweeps below.
        //
        // ONLY 1,320 AND 1,380 OCCUR, and the missing 1,440 is worth stating because a model of this
        // written by hand gets it wrong. The autumn transition would lengthen a session that contained it,
        // but at these closes the reopen lands in the AMBIGUOUS hour, and MarketClock.FromMarket resolves
        // an ambiguous wall-clock time to STANDARD time -- its own documented behaviour -- so the reopen
        // takes the later of the two instants and the session stays 23 elapsed hours. This is measured
        // against the real converter for exactly that reason.
        (int Min, HashSet<int> Lengths, List<int> Shortening) census = LengthCensus(2025);

        census.Lengths.Should().BeEquivalentTo(
            new[] { 1_320, 1_380 },
            "a session is 23 wall-clock hours, and the only thing that moves it is a spring-forward "
            + "transition inside it — the autumn one does NOT lengthen it, because the reopen then lands in "
            + "the ambiguous hour and MarketClock.FromMarket resolves that to standard time, which is its "
            + "documented behaviour and is what keeps the session 23 elapsed hours");
        census.Min.Should().Be(
            ToolGuards.ShortestSessionMinutes, "which is the number the ceiling is derived from");

        List<int> everyCloseBeforeTwo = [.. Enumerable.Range(0, 120)];

        census.Shortening.Should().Equal(
            everyCloseBeforeTwo,
            "exactly the closes from 00:00 to 01:59 put the skipped hour inside a session: the reopen is "
            + "one maintenance window after the close, and spring-forward removes the wall-clock hour "
            + "[02:00, 03:00), so any reopen before 03:00 loses time");
    }

    [Fact]
    public void EveryServableResolution_FitsInsideEverySession_AtEveryConfigurableClose()
    {
        // The over-reach half, swept rather than sampled. #593's reviewer built an exhaustive sweep over a
        // refusal that looked right on examples and found it wrong on 6.3% of them; examples are not
        // evidence about a boundary. PR #607's reviewer then did the same to THIS card and found the claim
        // "fits on every trade date whichever session close is configured" false at 690.
        //
        // WHAT MAKES THIS COMPLETE RATHER THAN A SAMPLE. The pigeonhole argument is offset-independent: for
        // a session of a given LENGTH it holds whatever the phase, so the only thing that varies between
        // one close and another is the length. TheSessionLengthCensus enumerates every length that occurs
        // (1,320 and 1,380) and the closes that produce them, so sweeping every width against the
        // shipped close AND against every session that is not 1,380 minutes long covers the space --
        // 1,380-minute sessions at other closes differ only in phase, which the guarantee does not use.
        List<string> misses = [];

        void Sweep(IReadOnlyList<(DateOnly Date, DateTimeOffset Open, DateTimeOffset Close)> sessions,
                   BarSessionCalendar calendar,
                   string where)
        {
            for (int resolution = 1; resolution <= ToolGuards.MaxResolutionMinutes; resolution++)
            {
                foreach ((DateOnly date, DateTimeOffset open, DateTimeOffset close) in sessions)
                {
                    if (!FitsTheSession(calendar, open, close, resolution))
                    {
                        misses.Add(
                            resolution.ToString(CultureInfo.InvariantCulture) + " min on "
                            + date.ToString("O") + " at " + where);
                    }
                }
            }
        }

        BarSessionCalendar shipped = BarSessionCalendar.Parse("16:00", []);
        IReadOnlyList<(DateOnly Date, DateTimeOffset Open, DateTimeOffset Close)> atShipped =
            Sessions(shipped, new DateOnly(2024, 1, 1), new DateOnly(2027, 1, 1));

        atShipped.Should().HaveCountGreaterThan(700, "three years of weekdays, or this measures nothing");
        Sweep(atShipped, shipped, "16:00");

        int oddLengths = 0;
        for (int minute = 0; minute < 120; minute++)
        {
            string close = ShorteningClose(minute);
            BarSessionCalendar calendar = BarSessionCalendar.Parse(close, []);
            List<(DateOnly, DateTimeOffset, DateTimeOffset)> odd =
            [
                .. Sessions(calendar, new DateOnly(2020, 1, 1), new DateOnly(2036, 1, 1))
                    .Where(s => (s.Close - s.Open) != TimeSpan.FromMinutes(ToolGuards.SessionMinutes)),
            ];

            oddLengths += odd.Count;
            Sweep(odd, calendar, close);
        }

        oddLengths.Should().Be(
            120 * 16,
            "one shortened session a year for sixteen years, at each of the hundred and twenty closes that "
            + "admit the spring-forward transition — the autumn one is absorbed by the ambiguous-hour "
            + "resolution");

        misses.Should().BeEmpty(
            "every resolution at or below the ceiling must produce an expected bucket on every trade date, "
            + "at every session close an operator can configure");
    }

    [Fact]
    public void TheCeilingIsTight_AndTheOldOneWasWrongOnTheReviewersOwnExample()
    {
        // Both halves of "660, not 690", driven rather than argued.
        //
        // The upper half is the defect PR #607's review found: 690 was this card's own ceiling and it
        // answers an EMPTY SERIES on the reviewer's example -- the exact silent shape gh#538 exists to
        // abolish, reintroduced by the fix at a width the fix permitted.
        BarSessionCalendar halfPast = BarSessionCalendar.Parse("00:30", []);
        (DateOnly Date, DateTimeOffset Open, DateTimeOffset Close) shrunk =
            Sessions(halfPast, new DateOnly(2030, 3, 11), new DateOnly(2030, 3, 12)).Single();

        (shrunk.Close - shrunk.Open).Should().Be(
            TimeSpan.FromMinutes(ToolGuards.ShortestSessionMinutes),
            "the spring-forward transition falls inside this session");
        FitsTheSession(halfPast, shrunk.Open, shrunk.Close, 690).Should().BeFalse(
            "690 -- the ceiling before this review -- produces no expected bucket here at all");
        FitsTheSession(halfPast, shrunk.Open, shrunk.Close, ToolGuards.MaxResolutionMinutes)
            .Should().BeTrue("and 660 does");

        // The lower half: the bound is TIGHT, so it is not merely safe. 661 is the first width the
        // guarantee stops covering, and it misses in fact and not only in theory.
        BarSessionCalendar twenty = BarSessionCalendar.Parse("00:20", []);
        (DateOnly Date, DateTimeOffset Open, DateTimeOffset Close) firstMiss =
            Sessions(twenty, new DateOnly(2027, 3, 15), new DateOnly(2027, 3, 16)).Single();

        FitsTheSession(twenty, firstMiss.Open, firstMiss.Close, ToolGuards.MaxResolutionMinutes + 1)
            .Should().BeFalse("661 already misses, so 660 is the largest the guarantee reaches");
    }

    [Theory]
    [InlineData(661)]
    [InlineData(664)]
    [InlineData(690)]
    [InlineData(720)]
    [InlineData(1_000)]
    [InlineData(1_379)]
    public async Task AResolutionTooCoarseForTheGrid_IsRefused_NamingTheRule(int resolutionMinutes)
    {
        // The residue gh#498 recorded and did not close. 1,379 sits INSIDE the old ceiling and is expected
        // only when the UTC grid happens to land within a minute of the session open -- so get_bars at 1,379
        // answered [] with venueRequests: 0 on 99.86% of trade dates, which is the very shape gh#498
        // abolished one minute higher. 661 is the first value the guarantee does not cover; 664 and 690 are
        // in the band that PR #607's first ceiling served, and 720 is one that fits every day at the shipped
        // close. All of them are refused, because the bound is a guarantee rather than a table of
        // coincidences -- see AboveTheCeiling_TheGuaranteeFails_AndTheCoincidencesAreNamed, which measures
        // exactly how far that table shifts when the sweep behind it widens.
        Func<Task> call = () =>
            _bars.GetLatestBars("ES", resolutionMinutes, 10, CancellationToken.None);

        (await call.Should().ThrowAsync<McpException>())
            .WithMessage("*resolutionMinutes*", "the refusal names the parameter the caller can change")
            .WithMessage(
                "*" + resolutionMinutes.ToString(CultureInfo.InvariantCulture) + "*",
                "and the value that was asked for")
            .WithMessage(
                "*" + ToolGuards.MaxResolutionMinutes.ToString(CultureInfo.InvariantCulture) + "*",
                "and the widest bar it will serve, so the caller has somewhere to go")
            .WithMessage("*grid*", "and says WHY, which is where the buckets are anchored");

        _gateway.BarRequests.Should().Be(0, "the resolution is judged before the first page is read");
        _gateway.ContractRequests.Should().Be(0, "and before the contract behind it is resolved");
    }

    [Fact]
    public async Task TheGridRefusal_ConcedesTheBandSometimesFits_RatherThanClaimingItNeverDoes()
    {
        // BLOCKING FINDING 2 of PR #607's review, and the reason it was found: the reviewer mutated the
        // concession to "never produce a bar at all" -- the exact overclaim PR #593 spent four rounds
        // removing -- and the whole suite stayed green. The one sentence the argument rests on was pinned
        // by nothing.
        //
        // It is pinned two ways, because either alone is weak. The POSITIVE half requires the concession to
        // be present AND TRUE: the widths it names are re-measured here against the real calendar, so a
        // message that concedes a falsehood is as red as one that concedes nothing. The NEGATIVE half is a
        // blocklist, and its limit is stated rather than glossed -- it catches the FAMILY of overclaim that
        // has actually been written twice now, not every possible one. A blocklist cannot be exhaustive;
        // what makes this test hard to fool is that the positive half forces the sentence to exist at all.
        string message =
            (await ((Func<Task>)(() => _bars.GetLatestBars("ES", 1_379, 10, CancellationToken.None)))
                .Should().ThrowAsync<McpException>()).Which.Message;

        BarSessionCalendar shipped = BarSessionCalendar.Parse("16:00", []);
        IReadOnlyList<(DateOnly Date, DateTimeOffset Open, DateTimeOffset Close)> sessions =
            Sessions(shipped, new DateOnly(2020, 1, 1), new DateOnly(2036, 1, 1));

        foreach (int conceded in new[] { 661, 690, 692, 696, 700, 720 })
        {
            sessions.Should().OnlyContain(
                s => FitsTheSession(shipped, s.Open, s.Close, conceded),
                "the refusal names " + conceded.ToString(CultureInfo.InvariantCulture)
                + " as a width that fits every trade date at the shipped close, so it had better");

            message.Should().Contain(
                conceded.ToString(CultureInfo.InvariantCulture),
                "and the message must actually name it rather than round the concession away");
        }

        message.Should().Contain(
            "not all useless",
            "the concession is the load-bearing sentence: the band DOES produce bars at some widths, and "
            + "the reason they are refused is that WHICH ones depends on configuration this check never "
            + "reads -- not that they never work");

        foreach (string overclaim in new[]
                 {
                     "never produce", "never produces", "never fit", "never fits", "never a bar",
                     "cannot produce a bar", "no bucket is ever", "produces no bar",
                 })
        {
            message.Should().NotContain(
                overclaim,
                "a refusal that overstates what it measured is how #593's message needed four review "
                + "rounds; this blocklist catches that family and is not claimed to catch every phrasing");
        }
    }

    [Fact]
    public void AboveTheCeiling_TheGuaranteeFails_AndTheCoincidencesAreNamed()
    {
        // The other half, and the one that keeps the refusal honest. Above the ceiling the guarantee is
        // gone, but "gone" is not "never fits": at the shipped 16:00 close a whole run of widths above it
        // fits on every trade date over sixteen years.
        //
        // AND THE SET SHRINKS AS THE SWEEP WIDENS, which is the real argument for refusing all of them. A
        // sweep can only ever report that it found no failure. Widen it -- more trade dates, or more of the
        // session closes an operator may configure -- and widths drop out. That is why the bound is derived
        // from the shortest session rather than read off a table: ValidateResolution is deliberately static
        // and reads no configuration, so serving these would make the servable set depend invisibly on
        // SessionCloseCentral (PR #607 review).
        BarSessionCalendar shipped = BarSessionCalendar.Parse("16:00", []);
        IReadOnlyList<(DateOnly Date, DateTimeOffset Open, DateTimeOffset Close)> atShipped =
            Sessions(shipped, new DateOnly(2020, 1, 1), new DateOnly(2036, 1, 1));

        List<int> alwaysFitAtTheShippedClose =
        [
            .. Enumerable.Range(ToolGuards.MaxResolutionMinutes + 1, 1_379 - ToolGuards.MaxResolutionMinutes)
                .Where(r => atShipped.All(s => FitsTheSession(shipped, s.Open, s.Close, r))),
        ];

        List<int> thirtyFour = [.. Enumerable.Range(661, 30), 692, 696, 700, 720];

        alwaysFitAtTheShippedClose.Should().Equal(
            thirtyFour,
            "every width from 661 to 690 fits at 16:00, and so do four more — thirty-four in all");

        // Widen to the closes whose session can be an hour shorter, and two survive. Only the
        // sessions that are NOT the nominal length need sweeping for the widths at or below 690: the
        // pigeonhole bound already covers a 1,380-minute session for any of those, whatever its phase.
        List<(DateOnly Date, DateTimeOffset Open, DateTimeOffset Close)> odd = [];
        List<BarSessionCalendar> calendars = [];
        for (int minute = 0; minute < 120; minute++)
        {
            BarSessionCalendar calendar = BarSessionCalendar.Parse(ShorteningClose(minute), []);
            calendars.Add(calendar);
            odd.AddRange(
                Sessions(calendar, new DateOnly(2020, 1, 1), new DateOnly(2036, 1, 1))
                    .Where(s => (s.Close - s.Open) != TimeSpan.FromMinutes(ToolGuards.SessionMinutes)));
        }

        // The calendar only enters FitsTheSession through IsExpectedBucket, and every calendar here agrees
        // about the bucket that matters because the bounds are passed in; the first is used for all of them.
        List<int> alsoAtTheShorteningCloses =
        [
            .. alwaysFitAtTheShippedClose
                .Where(r => odd.All(s => FitsTheSession(calendars[0], s.Open, s.Close, r))),
        ];

        alsoAtTheShorteningCloses.Should().Equal(
            [672, 720],
            "thirty-four falls to two once the sweep includes the sessions that are 1,320 minutes long — "
            + "and two is not a licence, it is what this sweep happened to leave");

        // And the shrinking does not stop there. At ONE of those closes, holding the sweep's shape fixed
        // and only lengthening the window, the survivor count keeps falling — so the set is an artefact of
        // how far the sweep looked, not a property of the widths.
        BarSessionCalendar halfPast = BarSessionCalendar.Parse("00:30", []);
        int SurvivorsOver(int years)
        {
            IReadOnlyList<(DateOnly Date, DateTimeOffset Open, DateTimeOffset Close)> window =
                Sessions(halfPast, new DateOnly(2020, 1, 1), new DateOnly(2020 + years, 1, 1));

            return alwaysFitAtTheShippedClose
                .Count(r => window.All(s => FitsTheSession(halfPast, s.Open, s.Close, r)));
        }

        SurvivorsOver(16).Should().BeLessThan(
            SurvivorsOver(4),
            "a longer sweep at the same close leaves fewer survivors, which is what makes a table of them "
            + "worthless as a bound");
        SurvivorsOver(4).Should().BeLessThan(
            SurvivorsOver(1), "and it is not a one-off step");

        // And the value the card names, measured rather than asserted. A 1,379-minute bucket needs the grid
        // to land within one minute of the session open, so it fits on a handful of scattered trade dates
        // and answers [] on the rest -- which is what makes it indistinguishable from an instrument with no
        // data, and what puts it on the wrong side of this repository's third non-negotiable.
        int fitting = atShipped.Count(s => FitsTheSession(shipped, s.Open, s.Close, 1_379));

        fitting.Should().BeLessThan(
            atShipped.Count / 100,
            "a 1,379-minute bar fits on well under one trade date in a hundred");
        fitting.Should().BeGreaterThan(
            0, "and 'almost never' is the honest word for it — not 'never', which the refusal must not say");

        // The worst of the band fits on NOTHING, which is the other end the ADR quotes.
        foreach (int never in new[] { 1_360, 1_368, 1_376 })
        {
            atShipped.Should().NotContain(
                s => FitsTheSession(shipped, s.Open, s.Close, never),
                never.ToString(CultureInfo.InvariantCulture) + " fits on no trade date at all");
        }
    }

    [Fact]
    public void ABucketExactlyASessionLong_IsSometimesExpected_SoTheSessionBarRefusalIsDefinitional()
    {
        // The first non-blocking finding of PR #607's review, and it had been re-asserted in four places:
        // "a bucket that long or longer can never close inside a single session" is FALSE at exactly the
        // session's length. IsExpectedBucket admits a bucket that closes AT the close, so a 1,380-minute
        // bucket is expected whenever the grid lands exactly on the session open.
        //
        // The refusal stands, on the claim that actually carries it: a bar covering a whole session is a
        // SESSION BAR by definition -- defined on the CME trade date rather than on the bucket grid -- and
        // that is true on the 4% of trade dates where the grid does line up as much as on the 96% where it
        // does not.
        BarSessionCalendar shipped = BarSessionCalendar.Parse("16:00", []);
        IReadOnlyList<(DateOnly Date, DateTimeOffset Open, DateTimeOffset Close)> sessions =
            Sessions(shipped, new DateOnly(2020, 1, 1), new DateOnly(2036, 1, 1));

        int expected = sessions.Count(
            s => FitsTheSession(shipped, s.Open, s.Close, ToolGuards.SessionMinutes));

        expected.Should().BeGreaterThan(
            0, "a session-length bucket IS expected when the grid lands on the session open");
        expected.Should().BeLessThan(
            sessions.Count / 10, "on a small minority of trade dates — about one in twenty-three");

        // Longer than a session, though, really never fits: there is no run of minutes to land in.
        sessions.Should().NotContain(
            s => FitsTheSession(shipped, s.Open, s.Close, ToolGuards.SessionMinutes + 1),
            "a bucket LONGER than a session can never close inside one, and that half of the claim holds");
    }

    /// <summary>Every session in a date range, as the UTC instants the bucket grid is compared against.</summary>
    /// <param name="calendar">The session calendar.</param>
    /// <param name="from">The first trade date to consider, inclusive.</param>
    /// <param name="to">The last trade date to consider, exclusive.</param>
    /// <returns>The trade date and its session bounds, for every date that trades.</returns>
    /// <remarks>
    /// A session for trade date D opens at <c>SessionOpen</c> on D-1 and closes at <c>SessionClose</c> on D,
    /// which is <see cref="BarSessionCalendar.TradeDateFor"/> read forwards. Both bounds are converted with
    /// <see cref="MarketClock"/> rather than by adding a fixed offset, so the DST transitions are the real
    /// ones — and the elapsed length is <b>not</b> always 1,380 minutes. At the shipped 16:00 close it is,
    /// because the reopen is 17:00 and the 02:00 Central transition sits before every session's start; at a
    /// close before 01:00 the reopen is at or before 02:00 and the transition falls inside, making that
    /// session 1,320 minutes. An earlier version of this remark asserted the first case as though it were
    /// general, and the resolution ceiling was derived from it (PR #607 review, finding 1).
    /// </remarks>
    private static IReadOnlyList<(DateOnly Date, DateTimeOffset Open, DateTimeOffset Close)> Sessions(
        BarSessionCalendar calendar, DateOnly from, DateOnly to)
    {
        List<(DateOnly, DateTimeOffset, DateTimeOffset)> sessions = [];

        for (DateOnly date = from; date < to; date = date.AddDays(1))
        {
            if (!calendar.IsTradingDay(date))
            {
                continue;
            }

            sessions.Add((
                date,
                MarketClock.FromMarket(date.AddDays(-1), calendar.SessionOpen).ToUniversalTime(),
                MarketClock.FromMarket(date, calendar.SessionClose).ToUniversalTime()));
        }

        return sessions;
    }

    /// <summary>One of the hundred and twenty closes whose session can lose an hour.</summary>
    /// <param name="minute">Minutes past midnight, 0 to 119.</param>
    /// <returns>The close in <c>HH:mm</c> form.</returns>
    /// <remarks>
    /// 00:00 to 01:59, because spring-forward deletes the wall-clock hour [02:00, 03:00) and the reopen is
    /// one maintenance window after the close — so any close before 02:00 has its session start inside or
    /// before that hour and loses time to it. It was 00:00 to 00:59 in the first draft of this sweep, which
    /// is what a hand-written model of the transition gives; the census is what corrected it.
    /// </remarks>
    private static string ShorteningClose(int minute) =>
        (minute / 60).ToString("D2", CultureInfo.InvariantCulture) + ":"
        + (minute % 60).ToString("D2", CultureInfo.InvariantCulture);

    /// <summary>The session lengths every whole-minute close produces over one year.</summary>
    /// <param name="year">The year to walk.</param>
    /// <returns>
    /// The shortest length seen anywhere, the set of lengths seen, and the close-minutes that ever produce
    /// a session shorter than <see cref="ToolGuards.SessionMinutes"/>.
    /// </returns>
    /// <remarks>
    /// Closes from 00:00 to 22:59 only. Past that the reopen wraps past midnight and
    /// <see cref="BarSessionCalendar.TradeDateFor"/>'s two branches overlap, so the calendar has no
    /// maintenance window at all — a degenerate configuration this sweep does not claim to describe, and one
    /// whose sessions are <i>longer</i> rather than shorter, which cannot lower the bound.
    /// </remarks>
    private static (int Min, HashSet<int> Lengths, List<int> Shortening) LengthCensus(int year)
    {
        HashSet<int> lengths = [];
        List<int> shortening = [];

        for (int close = 0; close < (23 * 60); close++)
        {
            BarSessionCalendar calendar = BarSessionCalendar.Parse(
                (close / 60).ToString("D2", CultureInfo.InvariantCulture) + ":"
                + (close % 60).ToString("D2", CultureInfo.InvariantCulture),
                []);

            bool shrinks = false;
            foreach ((DateOnly _, DateTimeOffset open, DateTimeOffset closeAt) in
                     Sessions(calendar, new DateOnly(year, 1, 1), new DateOnly(year + 1, 1, 1)))
            {
                int minutes = (int)(closeAt - open).TotalMinutes;
                lengths.Add(minutes);
                shrinks |= minutes < ToolGuards.SessionMinutes;
            }

            if (shrinks)
            {
                shortening.Add(close);
            }
        }

        return (lengths.Min(), lengths, shortening);
    }

    /// <summary>Whether one session admits a bucket of a given width on the UTC grid.</summary>
    /// <param name="calendar">The session calendar.</param>
    /// <param name="open">When the session opens.</param>
    /// <param name="close">When the session closes.</param>
    /// <param name="resolutionMinutes">The bucket width.</param>
    /// <returns><see langword="true"/> when the calendar expects at least one bucket inside that session.</returns>
    /// <remarks>
    /// The <b>real</b> grid and the <b>real</b> predicate, not a model of them: the first candidate is
    /// <see cref="BarGapDetector.AlignUp"/> from the open, and whether it counts is
    /// <see cref="BarSessionCalendar.IsExpectedBucket"/>. Only the first candidate needs testing — every
    /// later one closes later, so if the earliest does not fit, none does. The <c>bucket &lt; close</c> guard
    /// keeps the question about <i>this</i> session: a bucket at or past the close belongs to the next trade
    /// date, which these sweeps ask about on its own turn.
    /// </remarks>
    private static bool FitsTheSession(
        BarSessionCalendar calendar, DateTimeOffset open, DateTimeOffset close, int resolutionMinutes)
    {
        TimeSpan bar = TimeSpan.FromMinutes(resolutionMinutes);
        DateTimeOffset bucket = BarGapDetector.AlignUp(open, bar);

        return bucket < close && calendar.IsExpectedBucket(bucket, bar);
    }

    /// <summary>Rebuilds the bar tools against a different row cap.</summary>
    /// <param name="maxRows">The cap to build against.</param>
    /// <returns>The tools.</returns>
    private BarTools WithRowCap(int maxRows)
    {
        IOptions<MarketDataOptions> capped = Options.Create(new MarketDataOptions
        {
            Instruments = "ES,NQ",
            MaxRows = maxRows,
            SessionCloseCentral = "16:00",
        });

        return new BarTools(
            new InstrumentResolver(new InstrumentRegistry(capped), new StoreAvailabilityHolder()),
            _cache,
            new ToolGuards(capped),
            _clock);
    }

    // ── The drift guard ──────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(ToolGuards.MaxResolutionMinutes + 1)]
    [InlineData(int.MaxValue)]
    public async Task NoToolTakingAResolution_LetsAnUnservableOneThrough(int bad)
    {
        // "A cap enforced in three of four places is not a cap; it is a cap plus one tool that quietly
        // returns everything." -- ToolGuards' own XML docs, and exactly what happened to this rule. So the
        // sweep walks the surface by reflection rather than naming tools: a tool added tomorrow is covered
        // without anyone remembering to come back here.
        //
        // It walks the whole AXIS as well as the whole surface (gh#81). Driven by 0 alone it stayed green
        // through a ceiling that did not exist, because every tool refused the value it was handed -- the
        // sweep proved the floor and read as though it proved the parameter.
        const BindingFlags Surface = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        List<MethodInfo> takingAResolution =
        [
            .. typeof(BarTools).Assembly.GetTypes()
                .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
                .SelectMany(t => t.GetMethods(Surface))
                .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
                .Where(m => m.GetParameters().Any(p => p.Name == "resolutionMinutes")),
        ];

        takingAResolution.Should().HaveCountGreaterThanOrEqualTo(
            6, "the reflection filter must actually match the surface it is guarding");

        foreach (MethodInfo tool in takingAResolution)
        {
            _gateway.ResetCounters();

            Func<Task> call = () => Invoke(tool, Instance(tool.DeclaringType!), bad);

            // The EXCEPTION TYPE is the assertion, not merely that something threw. An
            // ArgumentOutOfRangeException out of the arithmetic also "throws", and it is exactly the failure
            // this fixture exists to keep off the tool boundary.
            (await call.Should().ThrowAsync<McpException>(
                tool.Name + " accepts a resolutionMinutes and does not refuse "
                + bad.ToString(CultureInfo.InvariantCulture) + ", so a caller mistake reaches it as a fault "
                + "or as a plausible-looking empty answer."))
                .WithMessage("*resolutionMinutes*");

            // Refusing is only half of it, and the half that a per-call-site patch also satisfies. Deleting
            // the whole-set loop from ResolveResolutions still leaves every tool throwing an McpException --
            // GetLatestBars' own guard catches the bad member one slice late -- so the throw assertion alone
            // cannot tell the two designs apart. These two can: a tool that reached the venue before refusing
            // has already spent the request it was refusing to justify.
            _gateway.BarRequests.Should().Be(
                0, tool.Name + " read bars from the venue before refusing the resolution");
            _gateway.ContractRequests.Should().Be(
                0, tool.Name + " resolved a contract at the venue before refusing the resolution");
        }
    }

    /// <summary>Builds the tool type under test, or says what has to be added here.</summary>
    /// <param name="type">The tool type the sweep found.</param>
    /// <returns>An instance to invoke.</returns>
    private object Instance(Type type) =>
        type == typeof(BarTools) ? _bars
        : type == typeof(IndicatorTools) ? _indicators
        : type == typeof(KeyLevelTools) ? _keyLevels
        : type == typeof(TapeTools) ? _tape
        : type == typeof(ContractRollTools) ? _roll
        : type == typeof(SnapshotTools) ? _snapshot
        : throw new InvalidOperationException(
            type.Name + " takes a resolutionMinutes and this fixture cannot build it. Add it here rather "
            + "than narrowing the sweep -- the sweep is the point.");

    /// <summary>Invokes a tool with a bad resolution and every other argument filled plausibly.</summary>
    /// <param name="tool">The tool method.</param>
    /// <param name="instance">The tool instance.</param>
    /// <param name="bad">The unservable resolution to drive it with.</param>
    /// <returns>The completed call.</returns>
    private static async Task Invoke(MethodInfo tool, object instance, int bad)
    {
        object?[] arguments = [.. tool.GetParameters().Select(p => Filler(p, bad))];

        try
        {
            if (tool.Invoke(instance, arguments) is Task running)
            {
                await running;
            }
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            // Reflection wraps whatever the tool threw. The wrapper is not the fact under test, and rethrowing
            // this way keeps the original stack.
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
        }
    }

    /// <summary>A plausible value for one tool argument — and a deliberately bad resolution.</summary>
    /// <param name="parameter">The parameter to fill.</param>
    /// <param name="bad">The unservable resolution to drive it with.</param>
    /// <returns>The value to pass.</returns>
    private static object? Filler(ParameterInfo parameter, int bad) => parameter.Name switch
    {
        // A MIXED set, not a bare [0]. On its own that proves less than it looks: a per-slice check still
        // throws an McpException, only one slice later, so the throw assertion cannot tell the two designs
        // apart either way. It is the BarRequests == 0 assertion in the sweep that separates them, and the
        // mixed set is what gives that assertion something to catch -- a bare [0] would refuse on the first
        // member under both designs and spend nothing under either.
        "resolutionMinutes" when parameter.ParameterType == typeof(int[]) => new[] { 5, bad },
        "resolutionMinutes" => bad,
        "indicator" => "atr",
        "symbol" => "ES",
        _ => Blank(parameter),
    };

    /// <summary>A value for an argument the sweep has no opinion about.</summary>
    /// <param name="parameter">The parameter to fill.</param>
    /// <returns>The value to pass.</returns>
    private static object? Blank(ParameterInfo parameter)
    {
        Type type = parameter.ParameterType;

        return type == typeof(CancellationToken) ? CancellationToken.None
            : type == typeof(int) ? 10
            : type == typeof(bool) ? true
            : type == typeof(string) ? "ES"
            : type == typeof(DateTimeOffset) ? (parameter.Name == "toUtc" ? Bucket(8) : Bucket(0))
            : Nullable.GetUnderlyingType(type) is not null || !type.IsValueType ? null
            : throw new InvalidOperationException(
                "No filler for " + type.Name + " " + parameter.Name + ". Add one rather than skipping the "
                + "tool: an unfilled argument is a tool the sweep silently stops covering.");
    }
}
