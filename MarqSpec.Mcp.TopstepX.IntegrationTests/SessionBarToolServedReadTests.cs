using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// The served half of the session-bar tool suite — what <c>get_session_bars</c> and
/// <c>get_latest_session_bars</c> hand back when nothing refuses (gh#500).
/// </summary>
/// <remarks>
/// <para>
/// <c>MarqSpec.Mcp.TopstepX.Tests.Tools.SessionBarToolBoundaryTests</c> is the refusing half: an unknown
/// session name, an empty window, either cap. None of those reaches a store, so all of them stay in the unit
/// tier, container-free. These two are the opposite claim — that a request the rules allow is genuinely
/// <b>answered</b> — and serving one runs the whole write path: the base <c>ON CONFLICT … DO UPDATE</c> bar
/// upsert, the coverage ledger, and the session-bar upsert and reconcile inside their own
/// <c>RepeatableRead</c> unit of work. None of that is representable against the in-memory provider (gh#387).
/// </para>
/// <para>
/// The fixture is <c>SessionBarServiceTests</c>' — the same <c>rth</c> ramp, the same <c>Service(...)</c>
/// composition — deliberately rather than a second shape: the numbers below are hand-checked against that
/// ramp, and two fixtures for one session are two fixtures free to disagree.
/// </para>
/// </remarks>
[Collection(SeriesStoreCollection.Name)]
public sealed class SessionBarToolServedReadTests : IAsyncLifetime
{
    /// <summary>The Monday the window opens on. The venue holds nothing at all for it.</summary>
    private static readonly DateOnly _monday = new(2026, 8, 17);

    /// <summary>The Tuesday whose <c>rth</c> session the venue holds whole.</summary>
    private static readonly DateOnly _tuesday = new(2026, 8, 18);

    /// <summary>The Wednesday the venue is missing one base bucket of.</summary>
    private static readonly DateOnly _wednesday = new(2026, 8, 19);

    private readonly SeriesStoreFixture _fixture;
    private readonly TopstepXDbContext _database;

    /// <param name="fixture">The shared container.</param>
    public SessionBarToolServedReadTests(SeriesStoreFixture fixture)
    {
        _fixture = fixture;
        _database = fixture.CreateContext();
    }

    /// <summary>A week after every session named here closed, so the base ledger settles permanently.</summary>
    private static DateTimeOffset SettledNow => new(2026, 8, 25, 0, 0, 0, TimeSpan.Zero);

    /// <inheritdoc />
    public Task InitializeAsync() => _fixture.ResetAsync();

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        _database.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Answers a window with the sessions it could build, the ones it could not, and the contract behind them.
    /// </summary>
    /// <returns>The running test.</returns>
    /// <remarks>
    /// Monday is the third trade date in the window and it is <b>not</b> silently dropped: the venue holds no
    /// bar for it at all, so it is an <c>Incomplete</c> absence missing all thirteen buckets. That is the
    /// whole two-list contract — a date in neither list would be read as "not a trading day", and Monday
    /// traded.
    /// </remarks>
    [Fact]
    public async Task GetSessionBars_ReturnsBarsAndAbsences_WithContractsCoverage()
    {
        SessionBarTools tools = Tools(
            [.. RthBars(_tuesday), .. RthBars(_wednesday, skip: 5)], new FakeTimeProvider(SettledNow));

        // Monday 00:00Z to Thursday 00:00Z. Three whole `rth` sessions lie inside it -- Monday's, Tuesday's
        // and Wednesday's, each 13:30Z..20:00Z -- and Thursday's closes after the end, so it is left out
        // rather than served short.
        ToolPayloads.SessionBarSeries series = await tools.GetSessionBars(
            "ES",
            "rth",
            new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero),
            CancellationToken.None);

        series.Symbol.Should().Be("ES");
        series.Session.Should().Be("rth");
        series.BaseResolutionMinutes.Should().Be(30, "the expected-bucket counts below are counts of these");

        // Hand-checked from the ramp: bucket i is O 100+i, H 105+i, L 95+i, C 101+i, V 10, thirteen of them.
        ToolPayloads.SessionBarPoint bar = series.Bars.Should().ContainSingle().Subject;
        bar.TradeDate.Should().Be(_tuesday);
        bar.T.Should().Be(
            new DateTimeOffset(2026, 8, 18, 13, 30, 0, TimeSpan.Zero),
            "the point is stamped at the session's own UTC open, not at the first bucket that happened to print");
        bar.CloseUtc.Should().Be(new DateTimeOffset(2026, 8, 18, 20, 0, 0, TimeSpan.Zero));
        bar.O.Should().Be(100m);
        bar.H.Should().Be(117m);
        bar.L.Should().Be(95m);
        bar.C.Should().Be(113m);
        bar.V.Should().Be(130, "thirteen buckets of ten, summed rather than sampled");

        series.Absent.Select(a => a.TradeDate).Should().Equal(
            [_monday, _wednesday], "the absences arrive in the order the dates were asked about");

        ToolPayloads.SessionAbsence monday = series.Absent[0];
        monday.Reason.Should().Be(SessionBarAbsence.Incomplete);
        monday.ExpectedBuckets.Should().Be(13);
        monday.MissingBuckets.Should().Be(
            13, "the venue holds nothing for Monday, so every bucket the calendar expects is missing");

        ToolPayloads.SessionAbsence wednesday = series.Absent[1];
        wednesday.Reason.Should().Be(
            SessionBarAbsence.Incomplete, "one dropped bucket makes the session not whole, never a partial bar");
        wednesday.ExpectedBuckets.Should().Be(13);
        wednesday.MissingBuckets.Should().Be(1);

        series.Contracts.Span.Should().Be(
            ToolPayloads.ContractSpan.SingleContract,
            "the one session that could be built came from one contract");
        series.Contracts.Segments.Should().ContainSingle().Which.BarCount.Should().Be(1);
    }

    /// <summary>
    /// Anchors on the last <b>closed</b> session and never on the one in progress, and settles to a free read.
    /// </summary>
    /// <returns>The running test.</returns>
    /// <remarks>
    /// <para>
    /// <b>Three reads, not two, and the middle one is why.</b> A cold store makes the covering window one
    /// contiguous missing range, so the first read is a single request the venue answers <i>with bars</i> —
    /// non-empty, therefore nothing is memoised. Only the second read sees the holes the venue has none for
    /// (the overnight between the two sessions, and Wednesday's dropped 16:00Z bucket), asks for them, and
    /// records them as covered. <c>SessionBarServiceTests.GetAsync_IssuesZeroVenueRequests_OnceTheBaseLedgerHasSettled</c>
    /// pins that shape one layer down; the claim here is that the tool inherits it rather than re-asking on
    /// every call.
    /// </para>
    /// <para>
    /// The clock stands inside Thursday's session, so those holes are memoised with a TTL rather than
    /// permanently — and the clock does not move, so the third read is answered from the ledger.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task GetLatestSessionBars_AnchorsOnTheLastClosedSession()
    {
        // Thursday 14:00 Central, which in August is 19:00Z: `rth` opened at 13:30Z and does not close until
        // 20:00Z, so Thursday's session is running.
        SessionBarTools tools = Tools(
            [.. RthBars(_tuesday), .. RthBars(_wednesday, skip: 5)],
            new FakeTimeProvider(new DateTimeOffset(2026, 8, 20, 19, 0, 0, TimeSpan.Zero)));

        ToolPayloads.SessionBarSeries first =
            await tools.GetLatestSessionBars("ES", "rth", 2, CancellationToken.None);

        first.Bars.Select(b => b.TradeDate)
            .Concat(first.Absent.Select(a => a.TradeDate))
            .Should().Equal(
                [_tuesday, _wednesday],
                "the two most recent CLOSED sessions, oldest first -- Thursday's is still running and is "
                + "never one of them");

        first.Bars.Should().ContainSingle().Which.TradeDate.Should().Be(_tuesday);
        first.Absent.Should().ContainSingle().Which.Reason.Should().Be(SessionBarAbsence.Incomplete);

        // The read that finds the two holes the first one left unmemoised, and records them.
        ToolPayloads.SessionBarSeries second =
            await tools.GetLatestSessionBars("ES", "rth", 2, CancellationToken.None);
        second.VenueRequests.Should().Be(
            2,
            "the overnight between the two sessions and Wednesday's dropped 16:00Z bucket are asked for once "
            + "each -- stated rather than skipped over, because it is why the assertion below needs a third "
            + "read and not a second");

        ToolPayloads.SessionBarSeries settled =
            await tools.GetLatestSessionBars("ES", "rth", 2, CancellationToken.None);

        settled.VenueRequests.Should().Be(
            0, "every hole is memoised, so a repeated read reaches the venue not at all");
        settled.FetchedBuckets.Should().Be(0, "and writes nothing, because nothing came back to write");
        settled.Bars.Should().ContainSingle().Which.TradeDate.Should().Be(
            _tuesday, "and the answer is the same one, served from the store");
    }

    /// <summary>The bucket a 30-minute <c>rth</c> bar opens at, as a UTC literal rather than a conversion.</summary>
    /// <param name="tradeDate">The trade date.</param>
    /// <param name="index">The bucket index from the 13:30Z open.</param>
    /// <returns>The bucket start.</returns>
    private static DateTimeOffset RthBucket(DateOnly tradeDate, int index) =>
        new DateTimeOffset(tradeDate.Year, tradeDate.Month, tradeDate.Day, 13, 30, 0, TimeSpan.Zero)
            .AddMinutes(30 * index);

    /// <summary>
    /// One trade date's thirteen 30-minute <c>rth</c> buckets, optionally missing one.
    /// </summary>
    /// <param name="tradeDate">The trade date.</param>
    /// <param name="skip">A bucket index the venue does not have, or <see langword="null"/> for all thirteen.</param>
    /// <returns>The bars, ascending.</returns>
    /// <remarks>
    /// <c>SessionBarServiceTests</c>' ramp, copied rather than shared so the hand-checked numbers above read
    /// beside the fixture that produces them: bar <c>i</c> is <c>O 100+i, H 105+i, L 95+i, C 101+i, V 10</c>.
    /// </remarks>
    private static IReadOnlyList<Bar> RthBars(DateOnly tradeDate, int? skip = null) =>
    [
        .. Enumerable.Range(0, 13)
            .Where(i => i != skip)
            .Select(i => new Bar(
                RthBucket(tradeDate, i),
                100m + i,
                105m + i,
                95m + i,
                101m + i,
                10,
                ConcurrencyHarness.ContractId)),
    ];

    /// <summary>The tool type under test, over one context and one private venue.</summary>
    /// <param name="available">The base bars the venue is willing to serve.</param>
    /// <param name="clock">The clock the read runs against.</param>
    /// <returns>The tools.</returns>
    /// <remarks>
    /// The service half is <c>SessionBarServiceTests.Service(...)</c>, unchanged; what is added is the four
    /// collaborators the tool holds beside it — the resolver, the catalogue, the calendar and the guards.
    /// </remarks>
    private SessionBarTools Tools(IEnumerable<Bar> available, FakeTimeProvider clock)
    {
        IOptions<MarketDataOptions> options = Options.Create(new MarketDataOptions
        {
            Instruments = "ES,NQ",
            MaxRows = 5_000,
            SessionCloseCentral = "16:00",
        });

        SeriesGateway gateway = new(ConcurrencyHarness.Venue(), available);
        BarCacheService bars = new(
            _database,
            gateway,
            ConcurrencyHarness.Calendar(),
            ConcurrencyHarness.Projector(_database),
            clock,
            NullLogger<BarCacheService>.Instance);

        SessionBarService sessions = new(
            _database,
            bars,
            gateway,
            ConcurrencyHarness.Calendar(),
            clock,
            NullLogger<SessionBarService>.Instance);

        return new SessionBarTools(
            new InstrumentResolver(new InstrumentRegistry(options), new StoreAvailabilityHolder()),
            sessions,
            new SessionCatalog(options, ConcurrencyHarness.Calendar()),
            ConcurrencyHarness.Calendar(),
            new ToolGuards(options),
            clock);
    }
}
