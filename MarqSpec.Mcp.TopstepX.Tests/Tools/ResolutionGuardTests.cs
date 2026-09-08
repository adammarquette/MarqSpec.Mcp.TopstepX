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
        // bar asked for. THE CEILING here, by the constant: at 690 minutes, 500,000 bars SPAN about 656
        // years, so they REACH about 2,624 -- past year one, and `end - reach` throws exactly the way
        // int.MaxValue did. The 4x is the whole finding: it is what carries a pair that is legal on both axes
        // past a calendar neither axis knows about, and it puts the real boundary near 386,000 bars rather
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

    // ── A bucket wider than half a session cannot be guaranteed to fit one (gh#538) ──────────────────

    [Fact]
    public void TheCeiling_IsHalfASession_AndItIsDerivedRatherThanChosen()
    {
        // 690, and the number is a consequence of two facts already in the code rather than a preference.
        //
        // Buckets are anchored on a fixed UTC grid (BarGapDetector.AlignUp), NOT on the session open, and
        // BarSessionCalendar.IsExpectedBucket expects a bucket only when it both opens inside the session
        // and closes at or before that session's close. So a session S minutes long admits an r-minute
        // bucket exactly when some multiple of r lands in [open, close - r] -- a run of S - r + 1
        // consecutive whole minutes. A run of n consecutive integers is CERTAIN to contain a multiple of r
        // only while n >= r, so the guarantee holds exactly while S - r + 1 >= r, i.e. r <= (S + 1) / 2.
        //
        // At S = 1,380 that is 690. It is also 1,380's largest proper divisor, so the two derivations the
        // card offered -- the pigeonhole bound and "the largest divisor of the session length" -- agree on
        // the same number, which is why neither is quoted alone.
        const int session = (24 * 60) - 60;

        session.Should().Be(1_380, "a session is 24 hours less the venue's one-hour maintenance window");

        ToolGuards.MaxResolutionMinutes.Should().Be(
            (session + 1) / 2, "the ceiling is the pigeonhole bound on the session length");
        ToolGuards.MaxResolutionMinutes.Should().Be(
            session / 2, "which at 1,380 is also the session's largest proper divisor");
        ToolGuards.MaxResolutionMinutes.Should().Be(690, "and both of those are 690");
    }

    [Theory]
    [InlineData(691)]
    [InlineData(692)]
    [InlineData(720)]
    [InlineData(1_000)]
    [InlineData(1_379)]
    public async Task AResolutionTooCoarseForTheGrid_IsRefused_NamingTheRule(int resolutionMinutes)
    {
        // The residue gh#498 recorded and did not close. 1,379 sits INSIDE the old ceiling and is expected
        // only when the UTC grid happens to land within a minute of the session open -- so get_bars at 1,379
        // answered [] with venueRequests: 0 on 99.86% of trade dates, which is the very shape gh#498
        // abolished one minute higher. 691 is the first value the guarantee does not cover; 692 is one of the
        // four in this band that DO fit every day at a 16:00 Central close, and it is refused with the rest
        // because the bound is a guarantee and not a table of coincidences (see
        // AboveTheCeiling_TheGuaranteeFails_AndTheCoincidencesAreNamed, which measures both claims).
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
    public void EveryServableResolution_FitsInsideEverySession()
    {
        // The over-reach half, swept rather than sampled. #593's reviewer built an exhaustive sweep over a
        // refusal that looked right on examples and found it wrong on 6.3% of them; examples are not
        // evidence about a boundary.
        //
        // Every r from 1 to the ceiling, against every trade date over three years -- both DST directions,
        // several times each. The claim is that the guarantee is real: a servable resolution is one the
        // calendar expects a bucket at on EVERY session, so a caller who is inside the ceiling never gets
        // the silent empty series. Three years rather than one because the grid's phase against the session
        // repeats on a period that grows with r, and at 690 minutes that period is 23 days.
        BarSessionCalendar calendar = BarSessionCalendar.Parse("16:00", []);
        IReadOnlyList<(DateOnly Date, DateTimeOffset Open, DateTimeOffset Close)> sessions =
            Sessions(calendar, new DateOnly(2024, 1, 1), new DateOnly(2027, 1, 1));

        sessions.Should().HaveCountGreaterThan(
            700, "three years of weekdays, or the sweep is measuring nothing");

        List<string> misses = [];
        for (int resolution = 1; resolution <= ToolGuards.MaxResolutionMinutes; resolution++)
        {
            foreach ((DateOnly date, DateTimeOffset open, DateTimeOffset close) in sessions)
            {
                if (!FitsTheSession(calendar, open, close, resolution))
                {
                    misses.Add(
                        resolution.ToString(CultureInfo.InvariantCulture) + " min on " + date.ToString("O"));
                }
            }
        }

        misses.Should().BeEmpty(
            "every resolution at or below the ceiling must produce an expected bucket on every trade date");
    }

    [Fact]
    public void AboveTheCeiling_TheGuaranteeFails_AndTheCoincidencesAreNamed()
    {
        // The other half, and the one that keeps the refusal honest. Above 690 the guarantee is gone, but
        // "gone" is not "never fits": four widths in the band -- 692, 696, 700 and 720 -- happen to fit on
        // every trade date over sixteen years at a 16:00 Central close, because their phase against the
        // session never drifts far enough. They are refused anyway, and the refusal must not claim they
        // never work. What it claims is that the bound is a guarantee that holds for ANY session close,
        // whereas those four are an accident of this one.
        //
        // Sixteen years, because the phase of an r-minute grid against the UTC day repeats every
        // lcm(r, 1440) / 1440 days -- up to 1,379 days near the top of the band -- and the two DST offsets
        // double that again.
        BarSessionCalendar calendar = BarSessionCalendar.Parse("16:00", []);
        IReadOnlyList<(DateOnly Date, DateTimeOffset Open, DateTimeOffset Close)> sessions =
            Sessions(calendar, new DateOnly(2020, 1, 1), new DateOnly(2036, 1, 1));

        List<int> alwaysFit = [];
        for (int resolution = ToolGuards.MaxResolutionMinutes + 1; resolution < 1_380; resolution++)
        {
            if (sessions.All(s => FitsTheSession(calendar, s.Open, s.Close, resolution)))
            {
                alwaysFit.Add(resolution);
            }
        }

        alwaysFit.Should().Equal(
            [692, 696, 700, 720],
            "these four are refused despite always fitting at a 16:00 Central close, and the refusal says so "
            + "rather than claiming nothing in this band ever produces a bar");

        // And the value the card names, measured rather than asserted. A 1,379-minute bucket needs the grid
        // to land within one minute of the session open, so it fits on a handful of scattered trade dates
        // and answers [] on the rest -- which is what makes it indistinguishable from an instrument with no
        // data, and what puts it on the wrong side of this repository's third non-negotiable.
        int fitting = sessions.Count(s => FitsTheSession(calendar, s.Open, s.Close, 1_379));

        fitting.Should().BeLessThan(
            sessions.Count / 100,
            "a 1,379-minute bar fits on well under one trade date in a hundred");
        fitting.Should().BeGreaterThan(
            0, "and 'almost never' is the honest word for it — not 'never', which the refusal must not say");
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
    /// ones — and it is worth noting what that measurement shows: a transition always falls on a Sunday at
    /// 02:00 Central, which is inside no session, so every session is exactly 1,380 minutes long. The
    /// pigeonhole derivation depends on that.
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
    /// date, which this sweep asks about on its own turn.
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
