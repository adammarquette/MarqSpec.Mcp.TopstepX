using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
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

namespace MarqSpec.Mcp.TopstepX.Tests.Tools;

/// <summary>
/// What <c>get_session_bars</c> and <c>get_latest_session_bars</c> refuse, and that they refuse it before
/// they spend anything (gh#500).
/// </summary>
/// <remarks>
/// <para>
/// <c>SessionGuardTests</c> pins the guards themselves; this suite pins the <b>boundary</b> — that the tools
/// actually call them, in the order that keeps a caller mistake off the venue, and that a name the catalogue
/// does not know arrives as an <see cref="McpException"/> rather than as a raw
/// <see cref="KeyNotFoundException"/>. The two are not the same claim: a guard nothing calls is green
/// forever.
/// </para>
/// <para>
/// <b>No container, and none needed (gh#387).</b> Every case here refuses, and the two counter assertions on
/// every test say what they measure and no more: <b>the VENUE was not reached</b>. Neither is a statement
/// about the store, and there is no cheap honest one to make here — the in-memory provider counts no reads,
/// and "no session bar was written" would be green under a failure too, because the raw upsert cannot run
/// against that provider at all. A gate that passes for the wrong reason is worse than an absent one, so the
/// store claim is left to the tier that can observe it.
/// </para>
/// <para>
/// The served half, where a request the rules allow really is answered — against a real store, so a read and
/// a write are both observable — is
/// <c>MarqSpec.Mcp.TopstepX.IntegrationTests.SessionBarToolServedReadTests</c>: a boundary proven only to
/// refuse is a boundary nobody has checked for over-reach.
/// </para>
/// </remarks>
public sealed class SessionBarToolBoundaryTests : IDisposable
{
    /// <summary>A count inside a small <c>MaxRows</c> that a Fridays-only calendar still cannot satisfy.</summary>
    private const int SparseCount = 100;

    /// <summary>
    /// A count inside the row cap whose covering base read is wider than one gap-detection pass.
    /// </summary>
    /// <remarks>
    /// About 3,720 <c>rth</c> sessions is where 30-minute base buckets first exceed the pass; 4,000 is past
    /// it and still well inside the default <c>MaxRows</c> of 5,000, which is what makes the two caps
    /// visibly different bounds rather than one implying the other.
    /// </remarks>
    private const int OverThePassCount = 4_000;

    /// <summary>
    /// The span the sparse calendar declares holidays over — the closed-session walk's, plus a margin.
    /// </summary>
    /// <remarks>
    /// Read off <see cref="SessionWindows.LastClosedWalkSpanDays"/> rather than restated: the span used to be
    /// a local inside <c>LastClosedTradeDates</c> and this fixture wrote the arithmetic out again. The margin
    /// covers the cursor's own day, since the walk starts one day <i>ahead</i> of <c>now</c>'s market date.
    /// </remarks>
    private static int SparseCalendarDays =>
        SessionWindows.LastClosedWalkSpanDays(SparseCount) + 85;

    private readonly TopstepXDbContext _database;
    private readonly CountingGateway _gateway;
    private readonly IndicatorCatalog _indicators;
    private readonly FakeTimeProvider _clock;
    private readonly SessionBarTools _sessions;
    private readonly HostTelemetry _telemetry = new();

    public SessionBarToolBoundaryTests()
    {
        _database = new TopstepXDbContext(
            new DbContextOptionsBuilder<TopstepXDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics
                    .InMemoryEventId.TransactionIgnoredWarning))
                .Options);

        _gateway = new CountingGateway([]);
        _indicators = new IndicatorCatalog(
            Options.Create(new IndicatorOptions { AtrPeriod = 3, RsiPeriod = 3 }),
            BarSessionCalendar.Parse("16:00", []));
        _clock = new FakeTimeProvider(SettledNow);

        _sessions = Tools(5_000, BarSessionCalendar.Parse("16:00", []), _clock);
    }

    /// <summary>A Monday in August 2026, at midnight UTC — before that day's <c>rth</c> session opens.</summary>
    private static DateTimeOffset MondayStart => new(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The Monday a fortnight later, at midnight UTC.</summary>
    private static DateTimeOffset FortnightLater => new(2026, 8, 17, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A clock instant well after every session named here has closed.</summary>
    private static DateTimeOffset SettledNow => new(2026, 8, 25, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The shipped <c>rth</c> session — 08:30–15:00 Central off 30-minute base bars.</summary>
    private static SessionDefinition Rth => SessionDefinition.Defaults.Single(static d => d.Name == "rth");

    /// <summary>The end of a window one base bucket wider than a single gap-detection pass.</summary>
    private static DateTimeOffset OverTheBucketCapTo =>
        MondayStart.AddTicks(
            ((BarGapDetector.MaxBucketsPerPass + 1L) * Rth.BaseResolutionMinutes) * TimeSpan.TicksPerMinute);

    public void Dispose()
    {
        _database.Dispose();
        _telemetry.Dispose();
    }

    [Fact]
    public async Task AnUnknownSession_IsAnError_NamingTheConfiguredOnes()
    {
        // A typo answered with an empty series is indistinguishable from a session that produced none, so the
        // vocabulary is closed and a miss is an error. The catalogue throws a KeyNotFoundException, which is a
        // .NET type rather than a statement to a caller -- the tool translates it, and the known names come
        // with it so the call can be fixed without reading the configuration.
        Func<Task> call = () =>
            _sessions.GetSessionBars("ES", "rht", MondayStart, FortnightLater, CancellationToken.None);

        (await call.Should().ThrowAsync<McpException>())
            .WithMessage("*rht*", "the refusal quotes what was asked for")
            .WithMessage(
                "*asia, europe, full, rth*",
                "and lists every configured name, in the catalogue's ordinal order");

        NothingWasSpent();
    }

    [Fact]
    public async Task AWindowExceedingTheBaseDetectionCap_IsRefused_BeforeTheVenue()
    {
        // The detection cap binds in BASE buckets -- the 30-minute bars `rth` is derived from -- because those
        // are what the read underneath a session bar enumerates. One bucket over is refused, and the remedy is
        // the only lever this caller has: there is no resolution argument on this tool.
        Func<Task> call = () =>
            _sessions.GetSessionBars("ES", "rth", MondayStart, OverTheBucketCapTo, CancellationToken.None);

        (await call.Should().ThrowAsync<McpException>())
            .WithMessage("*gap-detection pass*", "which is the bound the window is over")
            .WithMessage("*Narrow the window*", "and the one remedy a session-bar caller can act on");

        NothingWasSpent();
    }

    [Fact]
    public async Task AWindowNamingMoreTradeDatesThanMaxRows_IsRefused()
    {
        // Ten weekday `rth` sessions between the two Mondays -- the 3rd to the 14th, the 17th closing after
        // the window ends. Refused rather than truncated: a series shortened to fit arrives looking exactly
        // like a complete one.
        SessionBarTools capped = Tools(3, BarSessionCalendar.Parse("16:00", []), _clock);

        Func<Task> call = () =>
            capped.GetSessionBars("ES", "rth", MondayStart, FortnightLater, CancellationToken.None);

        (await call.Should().ThrowAsync<McpException>())
            .WithMessage(
                "*names 10 rth trade dates*",
                "the caller is told the real count, not merely that it is too many")
            .WithMessage("*cap of 3*", "and the cap it is over");

        NothingWasSpent();
    }

    [Fact]
    public async Task AnEmptyWindow_IsRefused()
    {
        // TradeDatesIn answers an empty window with an empty list, which on this surface reads as "no session
        // traded then" rather than "you asked for nothing" -- an absence indistinguishable from an answer.
        Func<Task> call = () =>
            _sessions.GetSessionBars("ES", "rth", MondayStart, MondayStart, CancellationToken.None);

        (await call.Should().ThrowAsync<McpException>())
            .WithMessage(
                "*empty or inverted*",
                "the window is named as the fault, rather than answered with no rows");

        NothingWasSpent();
    }

    [Fact]
    public async Task AWindowThatClipsEverySession_IsRefused_NamingTheNearestWhole()
    {
        // gh#568: nine hours of Monday over an `rth` session that runs 13:30Z to 20:00Z holds no session
        // that both opens and closes inside it. Left unrefused this answers exactly like an empty window --
        // bars: [] and absent: [] both, reading as "ES did not trade" -- so it must be refused before the
        // venue is touched, same as every other guard on this boundary.
        //
        // What NothingWasSpent pins here, and what it cannot: it proves the venue was not reached, and it
        // reds if the guard is deleted. It would NOT red if the guard were merely moved BEHIND the read,
        // because SessionBarService.GetAsync short-circuits on an empty trade-date list before it reaches
        // the gateway -- so the counters stay zero either way, and no assertion available in this tier can
        // separate the two orderings. That is the same limit every sibling case here carries, and the class
        // remark is honest about it; this is not an ordering proof.
        DateTimeOffset to = MondayStart.AddHours(18);

        Func<Task> call = () =>
            _sessions.GetSessionBars("ES", "rth", MondayStart.AddHours(9), to, CancellationToken.None);

        (await call.Should().ThrowAsync<McpException>())
            .WithMessage("*no whole rth session*", "the refusal names what the window failed to contain")
            .WithMessage(
                "*nearest whole rth session is 2026-08-03*",
                "and the nearest whole session the caller could widen to");

        NothingWasSpent();
    }

    [Fact]
    public async Task ACountAboveMaxRows_IsRefused()
    {
        // The row cap is one cap for the whole surface, and `count` is how this tool asks for rows.
        Func<Task> call = () =>
            _sessions.GetLatestSessionBars("ES", "rth", 5_001, CancellationToken.None);

        (await call.Should().ThrowAsync<McpException>())
            .WithMessage("*count*", "the refusal names the parameter the caller can change")
            .WithMessage("*5001*", "and the value that was asked for");

        NothingWasSpent();
    }

    [Fact]
    public async Task ACountWhoseBaseBucketsExceedThePass_IsRefused_BeforeTheVenue()
    {
        // The row cap is not the only cap a count is under. One call answers a count with ONE covering base
        // read -- the first session's open to the last one's close -- so a count MaxRows admits can still span
        // more 30-minute buckets than a single gap-detection pass enumerates. Before this guard existed the
        // fault landed inside BarGapDetector, after the store had been opened, and reached the caller as
        // "An error occurred invoking get_latest_session_bars".
        Func<Task> call = () =>
            _sessions.GetLatestSessionBars("ES", "rth", OverThePassCount, CancellationToken.None);

        (await call.Should().ThrowAsync<McpException>())
            .WithMessage("*count 4000 rth sessions*", "the refusal names the parameter and the value")
            .WithMessage("*gap-detection pass*", "and the bound it is over")
            .WithMessage("*Ask for fewer sessions*", "and says what to do about it");

        NothingWasSpent();
    }

    [Fact]
    public async Task AnUnsatisfiableCount_IsRefusedNamingCount_NotThrownRaw()
    {
        // MaxRows and the bounded calendar walk are two different bounds, and they disagree only when the
        // calendar is sparse. This one trades Fridays alone, so a count the row cap admits runs the walk out
        // of sessions -- and the Domain answers that with a raw ArgumentOutOfRangeException, which is exactly
        // the shape this boundary must never show a caller.
        DateTimeOffset now = MarketClock.FromMarket(new DateOnly(2026, 8, 6), new TimeOnly(20, 0));
        SessionBarTools sparse = Tools(SparseCount, FridaysOnly(now), new FakeTimeProvider(now));

        Func<Task> call = () =>
            sparse.GetLatestSessionBars("ES", "rth", SparseCount, CancellationToken.None);

        (await call.Should().ThrowAsync<McpException>())
            .WithMessage("*count 100*", "the refusal names the parameter and the value")
            .WithMessage("*Ask for fewer*", "and says what to do about it");

        NothingWasSpent();
    }

    /// <summary>Asserts the refusal landed before the venue was touched at all.</summary>
    private void NothingWasSpent()
    {
        // Refused before any VENUE work, like every other guard on this boundary. The resolver runs first and
        // touches neither the venue nor the store, so resolver-then-guard still satisfies this. The store
        // half of the claim is NOT made here -- see the class remark for why the in-memory provider cannot
        // make it honestly.
        _gateway.BarRequests.Should().Be(0, "the request is judged before the first base page is read");
        _gateway.ContractRequests.Should().Be(0, "and before the contract behind it is resolved");
    }

    /// <summary>Builds the tool type against a chosen row cap, calendar and clock.</summary>
    /// <param name="maxRows">The row cap the guards read.</param>
    /// <param name="calendar">The session calendar the trade dates come from.</param>
    /// <param name="clock">The clock <c>get_latest_session_bars</c> anchors on.</param>
    /// <returns>The tools.</returns>
    private SessionBarTools Tools(int maxRows, BarSessionCalendar calendar, FakeTimeProvider clock)
    {
        IOptions<MarketDataOptions> options = Options.Create(new MarketDataOptions
        {
            Instruments = "ES,NQ",
            MaxRows = maxRows,
            SessionCloseCentral = "16:00",
        });

        IndicatorProjector projector =
            new(_database, _indicators, NullLogger<IndicatorProjector>.Instance, _telemetry);
        BarCacheService cache = new(
            _database,
            _gateway,
            calendar,
            projector,
            new InstrumentRegistry(options),
            new ContractDirectory(clock),
            clock,
            NullLogger<BarCacheService>.Instance,
            _telemetry);

        SessionBarService service = new(
            _database, cache, _gateway, calendar, clock, NullLogger<SessionBarService>.Instance);

        return new SessionBarTools(
            new InstrumentResolver(new InstrumentRegistry(options), new StoreAvailabilityHolder()),
            service,
            new SessionCatalog(options, calendar),
            calendar,
            new ToolGuards(options),
            clock);
    }

    /// <summary>
    /// A calendar whose only trading day is Friday, for the whole span the count walk can reach back over.
    /// </summary>
    /// <param name="now">The instant the walk starts from.</param>
    /// <returns>The calendar.</returns>
    /// <remarks>
    /// Declared as holidays because that is the only lever a calendar has, and it is the real shape of the
    /// fault: a venue closed for most of a stretch carries fewer closed sessions than the walk day span
    /// suggests. The span is widened at both ends so the walk cannot step off the declared range.
    /// </remarks>
    private static BarSessionCalendar FridaysOnly(DateTimeOffset now)
    {
        DateOnly cursor = MarketClock.MarketDate(now).AddDays(1);
        List<DateOnly> holidays = [];

        for (int i = 0; i < SparseCalendarDays; i++)
        {
            DateOnly day = cursor.AddDays(-i);
            if (day.DayOfWeek is not DayOfWeek.Friday)
            {
                holidays.Add(day);
            }
        }

        return new BarSessionCalendar(new TimeOnly(16, 0), holidays);
    }
}
