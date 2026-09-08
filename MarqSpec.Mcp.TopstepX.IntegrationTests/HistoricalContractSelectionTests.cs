using System.Globalization;
using System.Reflection;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// Which contract a range is fetched from — the present band from the venue's own pick, history from the
/// contract that carried the volume (ADR-0020, gh#505).
/// </summary>
/// <remarks>
/// <para>
/// Here rather than in the unit tier because the answer is read off <b>stored provenance</b>: where the
/// present band starts is the first bucket of the store's trailing run of the venue front, and that is a
/// query over rows a raw-SQL upsert wrote (gh#387).
/// </para>
/// <para>
/// Every instant is a <b>literal</b>, never <c>now - BarCacheService.PresentHorizon</c>. Derived from the
/// constant, an expectation moves with it, and the test that exists to catch a change to the horizon would
/// follow it and stay green — the reason <c>BarCacheServiceTests.SettledNow</c> gives for the same rule.
/// </para>
/// </remarks>
[Collection(SeriesStoreCollection.Name)]
public sealed class HistoricalContractSelectionTests : IAsyncLifetime
{
    /// <summary>The contract the venue marks active.</summary>
    private const string Front = "CON.F.US.MES.U26";

    /// <summary>
    /// A January MES expiry — listed by no quarterly cycle, so PlanAsync cannot read it against HMUZ.
    /// </summary>
    private const string OffCycleFront = "CON.F.US.MES.F26";

    /// <summary>The venue-active ES contract. Product code is EP, not ES.</summary>
    private const string EsFront = "CON.F.US.EP.U26";

    /// <summary>The contract that was front a roll ago.</summary>
    private const string Previous = "CON.F.US.MES.H26";

    /// <summary>
    /// The contract that carried June's volume — the front's nearer neighbour on the quarterly cycle.
    /// </summary>
    /// <remarks>
    /// A June trade date on <c>HMUZ</c> at depth two names <c>M26</c> and <c>U26</c>: the contract that was
    /// actually trading, and the one the venue marks active <i>today</i>. That pairing is the whole subject —
    /// the venue's pick answers a June range with a thin, entirely plausible series, and the policy has to
    /// prefer the fat one.
    /// </remarks>
    private const string Liquid = "CON.F.US.MES.M26";

    private static readonly InstrumentId _mes = new("MES");

    private readonly SeriesStoreFixture _fixture;

    private readonly TopstepXDbContext _database;
    private readonly HostTelemetry _telemetry = new();

    /// <param name="fixture">The shared container.</param>
    public HistoricalContractSelectionTests(SeriesStoreFixture fixture)
    {
        _fixture = fixture;
        _database = fixture.CreateContext();
    }

    /// <inheritdoc />
    public Task InitializeAsync() => _fixture.ResetAsync();

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        _database.Dispose();
        _telemetry.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>A Tuesday mid-session, so every bucket seeded below is one the venue owed us.</summary>
    private static DateTimeOffset SessionStart =>
        MarketClock.FromMarket(new DateOnly(2026, 8, 18), new TimeOnly(9, 0)).ToUniversalTime();

    /// <summary>The instant the reads below are made at. A literal, three hours past the seeded run.</summary>
    private static DateTimeOffset Now => SessionStart.AddHours(3);

    /// <summary>
    /// A Tuesday mid-session in <b>June</b> — the range the venue's own pick answers wrongly.
    /// </summary>
    /// <remarks>
    /// June, deliberately. On <c>HMUZ</c> at depth two a June trade date names <c>M26</c> and <c>U26</c>, so
    /// the venue front is one of the two candidates and can be shown <i>losing</i> rather than merely being
    /// absent from the set. A literal, and not derived from <see cref="SessionStart"/> minus
    /// <see cref="BarCacheService.PresentHorizon"/>.
    /// </remarks>
    private static DateTimeOffset HistoryStart => Market(2026, 6, 16, 9);

    /// <summary>A Tuesday mid-session in March, whose candidates the venue below lists none of.</summary>
    private static DateTimeOffset UnlistedStart => Market(2026, 3, 17, 9);

    /// <summary>The session calendar every fixture here shares.</summary>
    private static BarSessionCalendar Calendar => BarSessionCalendar.Parse("16:00", []);

    /// <summary>A market-time instant, on the hour.</summary>
    /// <param name="year">The year.</param>
    /// <param name="month">The month.</param>
    /// <param name="day">The day.</param>
    /// <param name="hour">The hour, Central.</param>
    /// <returns>The instant, in UTC.</returns>
    private static DateTimeOffset Market(int year, int month, int day, int hour) =>
        MarketClock.FromMarket(new DateOnly(year, month, day), new TimeOnly(hour, 0)).ToUniversalTime();

    [Fact]
    public async Task TheTenureStart_IsTheTrailingRunOfTheFront_NotItsEarliestBucket()
    {
        // A store that has lived through a roll holds the front's id in more than one place: the run it is
        // laying down now, and older runs a backfill stamped with it before the previous contract's bars were
        // healed back in (ADR-0011's interleaving). Anchoring the present band on the front's EARLIEST bucket
        // would hand the whole interleaved stretch to the venue's pick and never ask the volume winner about
        // it -- which is the defect, wearing the fix's clothes.
        //
        // The seeded shape, five-minute buckets from the session start:
        //   H26  +0 .. +15     the previous front
        //   U26  +20 .. +35    an early, INTERRUPTED run of the front
        //   H26  +40 .. +55    the interleaved backfill
        //   U26  +60 .. +95    the trailing run
        // and, at FIFTEEN minutes, one more H26 bucket at +75 -- inside the five-minute trailing run.
        //
        // That last row is the point of "across all stored resolutions". The front's tenure is uninterrupted
        // only from +80; a query scoped to the resolution being read would answer +60 and hand four buckets
        // the previous contract still holds provenance for to the wrong contract.
        await SeedAsync(Previous, 5, 0, 4);
        await SeedAsync(Front, 5, 20, 4);
        await SeedAsync(Previous, 5, 40, 4);
        await SeedAsync(Front, 5, 60, 8);
        await SeedAsync(Previous, 15, 75, 1);

        BarCacheService cache = Build(SessionStart.AddHours(4));

        DateTimeOffset tenureStart = await cache.TenureStartAsync(
            "test", _mes, Front, SessionStart.AddHours(4), CancellationToken.None);

        tenureStart.Should().Be(SessionStart.AddMinutes(80));
    }

    [Fact]
    public async Task AColdStore_UsesThePresentHorizon()
    {
        // Nothing stored is not evidence that the venue's pick traded forever. Answering a cold store with
        // the beginning of time would make every first read a present-band read on one contract -- the
        // thin-series defect, arrived at from the other direction -- and answering it with `now` would send
        // a week of "latest bars" down the historical path for no gain. Seven days, and no store touched.
        //
        // Both instants are LITERALS. Written as `now - BarCacheService.PresentHorizon` the expectation
        // would move with the constant, and this test -- which exists to catch exactly that change -- would
        // follow it and stay green.
        DateTimeOffset now =
            MarketClock.FromMarket(new DateOnly(2026, 8, 21), new TimeOnly(9, 0)).ToUniversalTime();
        DateTimeOffset sevenDaysBack =
            MarketClock.FromMarket(new DateOnly(2026, 8, 14), new TimeOnly(9, 0)).ToUniversalTime();

        BarCacheService cache = Build(now);

        DateTimeOffset tenureStart = await cache.TenureStartAsync(
            "test", _mes, Front, now, CancellationToken.None);

        tenureStart.Should().Be(sevenDaysBack);
    }

    [Fact]
    public async Task AWarmStore_FetchesThePresentFromTheVenueFrontOnly_WithZeroContractLookups()
    {
        // THE COST GUARD. Every poll this server actually serves lands here, and the whole hybrid is only
        // affordable because this path is byte-identical to what it was before ADR-0020: the same pages, the
        // same requests++, and NOT ONE contract lookup. A present band that quietly resolved its candidates
        // would put a vendor call on the hottest read for a question the venue's own pick already answers.
        //
        // The store's trailing run of the front is what anchors it. Seeded here, so the band starts at
        // SessionStart rather than at the seven-day horizon -- which is the difference between this read
        // being present-band by measurement and being present-band by luck.
        await SeedTenureAnchorAsync();

        CountingGateway gateway = Venue(new Dictionary<string, IEnumerable<Bar>>(StringComparer.Ordinal)
        {
            [Front] = Series(SessionStart, 24, 5, 1_000),
            [Liquid] = [],
        });

        BarCacheService cache = BuildAround(gateway, Now);

        BarReadResult result = await cache.GetBarsAsync(
            _mes, 5, new BarRange(SessionStart, SessionStart.AddHours(2)), CancellationToken.None);

        result.Bars.Should().HaveCount(24);
        gateway.BarRequests.Should().Be(
            1,
            "the second hour is one page on the venue's own pick -- the count this read cost before ADR-0020 "
            + "and the count it must still cost");
        result.VenueRequests.Should().Be(1);

        // A PRESENT-BAND READ DECIDES NO HISTORY, AND MUST NOT CLAIM IT DID (gh#592). There is no candidate
        // set here to have narrowed, so the honest answer is "not decided here" -- not "the cycle was whole",
        // which would be a confident statement about a decision that never ran.
        result.History.Selection.Should().Be(HistorySelection.NotDecidedHere);
        gateway.ContractLookups.Should().Be(
            0, "nothing in the present band is a constructed expiry, so nothing needs confirming by id");

        (await _database.Bars
            .AsNoTracking()
            .Where(b => b.BucketStart >= SessionStart.AddHours(1))
            .ToListAsync())
            .Should().AllSatisfy(b => b.ContractId.Should().Be(Front));
    }

    [Fact]
    public async Task ARangeBeforeTheFrontsTenure_IsFetchedFromEveryCandidate_AndStoresTheHigherVolumeOne()
    {
        // The defect, and the fix, in one read. Measured through the live server on 2026-09-06, a January
        // window answered two hourly bars carrying volume 2 and 5 on the venue-active contract while the
        // contract that was actually trading answered a full session. Nothing errored; the series was
        // contiguous, the prices were real, and a year of history was a year of the thinnest listed month.
        //
        // So the range is asked of EVERY candidate the cycle names for its trade date, and the answer with
        // the volume is the one that is stored. Both candidates really answer here -- the front is thin, not
        // absent -- because an absent front would let a "first non-empty wins" implementation pass.
        await SeedTenureAnchorAsync();

        CountingGateway gateway = Venue(new Dictionary<string, IEnumerable<Bar>>(StringComparer.Ordinal)
        {
            [Front] = Series(HistoryStart, 12, 5, 10),
            [Liquid] = Series(HistoryStart, 12, 5, 5_000),
        });

        BarCacheService cache = BuildAround(gateway, Now);

        BarReadResult result = await cache.GetBarsAsync(
            _mes, 5, new BarRange(HistoryStart, HistoryStart.AddHours(1)), CancellationToken.None);

        // THE UNDEGRADED DIRECTION (gh#592). Every expiry the cycle named for these trade dates resolved, so
        // the volume decision ran over the cycle rather than over a survivor set -- and the payload says so
        // in its own words rather than by staying silent. A flag that is always set is the same as no flag,
        // which is why this assertion sits on the case where nothing went wrong.
        result.History.Selection.Should().Be(HistorySelection.AsTheCycleNames);
        result.History.Unresolved.Should().BeEmpty();

        gateway.BarRequests.Should().Be(
            2, "one page each for the two candidates a June trade date names on a quarterly cycle");
        gateway.ContractLookups.Should().Be(
            2, "a constructed expiry is a guess until the venue confirms it by id");

        List<BarRecord> stored = await StoredAsync(HistoryStart, HistoryStart.AddHours(1));

        stored.Should().HaveCount(12);
        stored.Should().AllSatisfy(b => b.ContractId.Should().Be(Liquid));
        stored.Should().AllSatisfy(b => b.Volume.Should().Be(
            5_000, "the winner's bars are stored, not the loser's numbers under the winner's id"));
    }

    [Fact]
    public async Task TheWinnerCanChangeMidRange_AndTheSeamFallsOnTheTradeDate()
    {
        // A roll happens INSIDE a range, and the policy decides per trade date rather than per range -- so
        // one fetch has to be able to store two contracts, and the seam has to land where the market's day
        // turns rather than where the clock's does. 17:00 Central opens the next trade date, so a bar opening
        // at 17:00 on the Tuesday belongs to Wednesday's session and to whichever contract traded it.
        //
        // A seam placed at UTC midnight, or at the range's midpoint, would satisfy "two segments" just as
        // well -- which is why the instant is asserted rather than the count.
        await SeedTenureAnchorAsync();

        BarRange window = new(Market(2026, 6, 16, 9), Market(2026, 6, 17, 9));
        DateTimeOffset seam = Market(2026, 6, 16, 17);
        IReadOnlyList<DateTimeOffset> grid =
            BarGapDetector.ExpectedBuckets(window, TimeSpan.FromHours(1), Calendar);

        CountingGateway gateway = Venue(new Dictionary<string, IEnumerable<Bar>>(StringComparer.Ordinal)
        {
            [Liquid] = grid.Select(b => Flat(b, b < seam ? 5_000 : 1)),
            [Front] = grid.Select(b => Flat(b, b < seam ? 1 : 5_000)),
        });

        BarCacheService cache = BuildAround(gateway, Now);

        BarReadResult result = await cache.GetBarsAsync(_mes, 60, window, CancellationToken.None);

        IReadOnlyList<ContractSegment> segments = ContractRollDetector.Segment(result.Bars);

        segments.Should().HaveCount(2, "one range, two trade-date runs, two contracts");
        segments[0].ContractId.Should().Be(Liquid);
        segments[1].ContractId.Should().Be(Front);
        segments[1].FirstBucket.Should().Be(
            seam, "the trade date turns at the session open, and that is where the winner may change");

        List<BarRecord> stored = await StoredAsync(window.Start, window.End, 60);

        stored.Where(b => b.BucketStart < seam).Should().AllSatisfy(b => b.ContractId.Should().Be(Liquid));
        stored.Where(b => b.BucketStart >= seam).Should().AllSatisfy(b => b.ContractId.Should().Be(Front));
    }

    [Fact]
    public async Task AThinCandidate_NeverBeatsALiquidOne_EvenWhenItIsTheVenueFront()
    {
        // The venue's active flag is a fact about NOW, and this read is about June. The front answers -- it
        // is listed, it is asked, and it hands back a full twelve bars -- and it still loses, because what
        // decides a historical trade date is volume and nothing else. An implementation that preferred the
        // front on any tie, or that stopped at the first candidate with bars, passes every other case here.
        await SeedTenureAnchorAsync();

        CountingGateway gateway = Venue(new Dictionary<string, IEnumerable<Bar>>(StringComparer.Ordinal)
        {
            [Front] = Series(HistoryStart, 12, 5, 10),
            [Liquid] = Series(HistoryStart, 12, 5, 5_000),
        });

        BarRange window = new(HistoryStart, HistoryStart.AddHours(1));

        // Asserted rather than assumed: a front that answered nothing would make this test about an absence.
        (await gateway.GetBarsAsync(Front, window, TimeSpan.FromMinutes(5), CancellationToken.None))
            .Should().HaveCount(12, "the front is thin over this range, not silent");
        gateway.ResetCounters();

        BarCacheService cache = BuildAround(gateway, Now);

        BarReadResult result = await cache.GetBarsAsync(_mes, 5, window, CancellationToken.None);

        result.Bars.Should().AllSatisfy(b => b.ContractId.Should().Be(Liquid));
        (await StoredAsync(window.Start, window.End))
            .Should().NotContain(b => b.ContractId == Front, "the venue's pick lost this range on volume");
    }

    [Fact]
    public async Task ACandidateTheVenueDoesNotKnow_IsDropped_AndNoCandidateFallsBackToTheFront_Loudly()
    {
        // Both halves of the degradation rule. A March trade date names H26 and M26; this venue lists
        // neither, so both are dropped -- and a slice with nothing left to ask must NOT become a slice that
        // reports a quiet market. It falls back to today's behaviour, the venue's own pick, and says so.
        //
        // And it earns NO permanent memo. A memo written under the front for a range the front was never the
        // right contract for is exactly the shape gh#504 closed: an "empty" recorded on behalf of contracts
        // nobody asked. Re-asking is the acceptable cost; a permanent hole is not.
        await SeedTenureAnchorAsync();

        CountingGateway gateway = Venue(new Dictionary<string, IEnumerable<Bar>>(StringComparer.Ordinal)
        {
            [Front] = [],
        });

        CapturingLogger<BarCacheService> log = new();
        BarCacheService cache = BuildAround(gateway, Now, log);

        BarReadResult result = await cache.GetBarsAsync(
            _mes, 5, new BarRange(UnlistedStart, UnlistedStart.AddHours(1)), CancellationToken.None);

        // A FOURTH STATE, BECAUSE THIS IS NOT THE NARROWED ONE (gh#592). A narrowed set had a survivor and
        // the volume decision ran over it; here nothing the cycle named was listed, so ADR-0020's rule did
        // not run at all and the whole stretch is the pre-ADR-0020 answer. Reporting both degradations under
        // one value would make them indistinguishable on the wire -- the same defect one level down.
        result.History.Selection.Should().Be(HistorySelection.FellBackToTheFront);
        result.History.Unresolved.Should().Equal("H26", "M26");

        gateway.ContractLookups.Should().Be(
            2, "both constructed March expiries are existence-checked before either is fetched");
        gateway.BarRequests.Should().Be(
            1, "nothing survived the check, so the slice is fetched from the venue's pick exactly as before");

        log.Messages.Should().ContainMatch(
            "*no listed candidate*",
            "a degraded answer that says nothing is indistinguishable from a correct one");

        (await _database.BarCoverage.AsNoTracking().ToListAsync()).Should().BeEmpty(
            "a slice nobody could answer for records no permanent claim that it is empty");
    }

    [Fact]
    public async Task AVenueNegativeOnTheLiquidCandidate_IsLoud_AndReAskedAfterTheNegativeLapses()
    {
        // gh#570, and the whole point is that NOTHING GOES WRONG VISIBLY. A June trade date names M26 and
        // U26; one ContractDirectory negative on M26 -- a venue hiccup, remembered for NegativeLifetime --
        // leaves a candidate set of exactly the venue's own pick. The read then answers from the front alone,
        // which is the pre-ADR-0020 behaviour, and until this issue said nothing at all: an operator could
        // not tell that stretch from a genuine one-candidate cycle month.
        //
        // So the degradation is named, with the expiries that did not resolve, and the ledger is left able to
        // re-ask: the contract that WAS asked earns its memo, and the dropped one earns none. That asymmetry
        // is what makes the second read below fetch anything at all -- a range is answered only when EVERY
        // candidate of the slice answered it (gh#504), so the memo the front holds stops covering the range
        // the moment M26 rejoins the set.
        await SeedTenureAnchorAsync();

        // The front is silent over June rather than thin, so the stored series cannot mask the ledger claim
        // this case is about. The thin-series half is the case below.
        CountingGateway gateway = Venue(new Dictionary<string, IEnumerable<Bar>>(StringComparer.Ordinal)
        {
            [Front] = [],
            [Liquid] = Series(HistoryStart, 12, 5, 5_000),
        });

        gateway.Unlisted.Add("M26");

        FakeTimeProvider clock = new(Now);
        CapturingLogger<BarCacheService> log = new();
        BarCacheService cache = BuildAround(gateway, Now, log, clock);

        BarRange window = new(HistoryStart, HistoryStart.AddHours(1));

        BarReadResult degraded = await cache.GetBarsAsync(_mes, 5, window, CancellationToken.None);

        // THE CALLER-FACING HALF (gh#592). The operator's warning below is not on the caller's path -- an MCP
        // client never sees a log line -- so until this state existed the payload of this read and the
        // payload of the undegraded read further up were the same object.
        degraded.History.Selection.Should().Be(HistorySelection.NarrowedByTheVenue);
        degraded.History.Unresolved.Should().Equal(
            new[] { "M26" },
            "the caller is told WHICH expiry fell away, not merely that something did");

        gateway.ContractLookups.Should().Be(
            2, "both of June's constructed expiries are existence-checked before either is fetched");
        gateway.BarRequests.Should().Be(
            1, "M26 did not resolve, so only the front survived to be asked");

        // RED before the fix, and red for the RIGHT reason: the plan is identical either way, the bars are
        // identical either way, and the only thing that was missing was anybody saying so.
        log.Messages.Should().ContainMatch(
            "*M26*",
            "a candidate set narrowed by a venue negative is a degraded answer, and a degraded answer that "
            + "names nothing is indistinguishable from a correct one");

        List<BarCoverageRecord> memos = await _database.BarCoverage.AsNoTracking().ToListAsync();

        memos.Should().ContainSingle().Which.ContractId.Should().Be(
            Front, "only the contract that was actually asked can have answered nothing");
        memos.Should().NotContain(
            memo => memo.ContractId == Liquid,
            "a memo for the candidate nobody could ask would answer this range for ever, and the lapse of "
            + "the directory's negative would never be able to change the answer");

        // THE RE-ASK. The negative lapses, the venue lists M26 again, and the range the front's permanent
        // memo covers is still outstanding because M26's side of it is not.
        gateway.Unlisted.Remove("M26");
        clock.Advance(ContractDirectory.NegativeLifetime);
        gateway.ResetCounters();

        BarReadResult second = await cache.GetBarsAsync(_mes, 5, window, CancellationToken.None);

        gateway.ContractLookups.Should().Be(
            1, "U26's positive is permanent for the life of the process; only the stale negative is re-asked");
        gateway.BarRequests.Should().Be(
            2, "the slice names both candidates again, and the front's memo alone does not answer it");

        second.Bars.Should().HaveCount(12);
        second.Bars.Should().AllSatisfy(bar => bar.ContractId.Should().Be(
            Liquid, "the volume decision ADR-0020 exists for finally ran, and the liquid contract won"));

        // BOTH DIRECTIONS, ON THE SAME STORE AND THE SAME WINDOW. The only thing that changed between the two
        // reads is whether the venue listed M26, and the payload changes with it -- which is what makes this
        // a report of a fact rather than a flag that is always set.
        second.History.Selection.Should().Be(HistorySelection.AsTheCycleNames);
        second.History.Unresolved.Should().BeEmpty();
    }

    [Fact]
    public async Task AVenueNegativeOnTheLiquidCandidate_StoresTheFrontsThinSeries_WhichNoLaterReadRewrites()
    {
        // The other half of gh#570, and the one that says what this card does NOT do. When the front is thin
        // rather than silent over the stretch, the degraded read stores its bars -- a real series, from a
        // real contract, just not the one that carried June -- and those rows are attributed history from
        // that moment on. A later read does not revisit them: it finds the buckets present and never reaches
        // the venue, so the lapse of the directory's negative cannot heal what was already written.
        //
        // That is deliberate (ADR-0020 §5, and ATradeDateAlreadyHeld_IsNotInterleaved above): a read that
        // re-decided attributed history would splice a second contract into the middle of a day already
        // recorded. Rewriting a defective run is an operator's verb -- reselect-bars, gh#506 -- and this card
        // ships no migration for rows an earlier degraded read already laid down, for the reason gh#571 gave:
        // the rebuild pass is the remedy. What changes here is that the operator is TOLD.
        await SeedTenureAnchorAsync();

        CountingGateway gateway = Venue(new Dictionary<string, IEnumerable<Bar>>(StringComparer.Ordinal)
        {
            [Front] = Series(HistoryStart, 12, 5, 10),
            [Liquid] = Series(HistoryStart, 12, 5, 5_000),
        });

        gateway.Unlisted.Add("M26");

        FakeTimeProvider clock = new(Now);
        CapturingLogger<BarCacheService> log = new();
        BarCacheService cache = BuildAround(gateway, Now, log, clock);

        BarRange window = new(HistoryStart, HistoryStart.AddHours(1));

        BarReadResult first = await cache.GetBarsAsync(_mes, 5, window, CancellationToken.None);

        first.History.Selection.Should().Be(
            HistorySelection.NarrowedByTheVenue, "the read that narrowed is the read that can say so");

        log.Messages.Should().ContainMatch(
            "*M26*", "the stretch was decided by default, and that is the only warning an operator will get");

        (await StoredAsync(window.Start, window.End)).Should().AllSatisfy(bar => bar.ContractId.Should().Be(
            Front, "the front was the only candidate left, so its thin series is what the store now holds"));

        gateway.Unlisted.Remove("M26");
        clock.Advance(ContractDirectory.NegativeLifetime);
        gateway.ResetCounters();

        BarReadResult later = await cache.GetBarsAsync(_mes, 5, window, CancellationToken.None);

        gateway.BarRequests.Should().Be(
            0, "every bucket is present, so the read is answered from the store and never re-asks");

        (await StoredAsync(window.Start, window.End)).Should().AllSatisfy(bar => bar.ContractId.Should().Be(
            Front, "a read does not rewrite attributed history -- reselect-bars (gh#506) is the remedy"));

        // WHERE THE STATE STOPS, PINNED RATHER THAN LEFT TO BE DISCOVERED (gh#592). This read plans nothing,
        // so it knows nothing: nothing in Bars records that a bucket was written under a narrowed set, and
        // ADR-0020 §5 forbids re-deciding attributed history to find out. NotDecidedHere is the honest
        // answer, and it is emphatically not AsTheCycleNames -- a caller must not read silence as a whole
        // cycle. Making the store able to answer this is a persisted fact, an ADR and a migration; the verb
        // that repairs the run underneath it is reselect-bars (gh#506).
        later.History.Selection.Should().Be(HistorySelection.NotDecidedHere);
        later.History.Unresolved.Should().BeEmpty();
    }

    [Fact]
    public async Task ASliceTheLedgerAlreadyAnswered_ReportsNothing_BecauseThisReadDecidedNothing()
    {
        // THE `outstanding` BOUNDARY, WHICH IS THE ONE `venueRequests` USES (gh#592). The state is derived
        // from the slices this read actually asks the venue about, not from the slices it planned, and the
        // difference is a whole read.
        //
        // Here the front is silent over a settled June range, so the first read memoises it permanently under
        // the front -- the only candidate the narrowed set left. The SECOND read still finds every bucket
        // missing and still plans the same narrowed slice, but the ledger answers it, so nothing is fetched.
        // Derived from the plan, that read would report `venueRequests: 0` beside a populated
        // NarrowedByTheVenue: a payload saying in one field that it reached no venue and in another that a
        // decision it never made went badly. Derived from `outstanding`, it says NotDecidedHere, which is
        // what actually happened.
        await SeedTenureAnchorAsync();

        CountingGateway gateway = Venue(new Dictionary<string, IEnumerable<Bar>>(StringComparer.Ordinal)
        {
            [Front] = [],
        });

        gateway.Unlisted.Add("M26");

        FakeTimeProvider clock = new(Now);
        BarCacheService cache = BuildAround(gateway, Now, clock: clock);

        BarRange window = new(HistoryStart, HistoryStart.AddHours(1));

        BarReadResult first = await cache.GetBarsAsync(_mes, 5, window, CancellationToken.None);

        first.History.Selection.Should().Be(
            HistorySelection.NarrowedByTheVenue, "the read that asked is the read that decided");

        (await _database.BarCoverage.AsNoTracking().ToListAsync())
            .Should().NotBeEmpty("the front answered this settled range empty, so it is memoised permanently");

        gateway.ResetCounters();

        // The clock does not move and M26 stays unlisted, so the plan is identical -- same narrowed slice,
        // same dropped expiry. Only the ledger is different.
        BarReadResult second = await cache.GetBarsAsync(_mes, 5, window, CancellationToken.None);

        second.VenueRequests.Should().Be(
            0, "every slice the plan named is answered by the ledger, so no history request is issued");
        second.History.Selection.Should().Be(
            HistorySelection.NotDecidedHere,
            "a read that fetched nothing decided nothing, and a degradation reported beside venueRequests "
            + "of zero would be two halves of one payload contradicting each other");
        second.History.Unresolved.Should().BeEmpty();
    }

    [Fact]
    public async Task GetBarsPayload_CarriesTheNarrowing_NotOnlyTheServiceResult()
    {
        // ON THE PAYLOAD, WHICH IS WHERE gh#592'S ACCEPTANCE PUTS IT. BarReadResult is one layer below the
        // wire: a tool that dropped the field on the floor, or hard-wired it to NotDecidedHere, would leave
        // every assertion on the service green and every MCP client exactly as blind as before this card.
        await SeedTenureAnchorAsync();

        CountingGateway gateway = Venue(new Dictionary<string, IEnumerable<Bar>>(StringComparer.Ordinal)
        {
            [Front] = Series(HistoryStart, 12, 5, 10),
            [Liquid] = Series(HistoryStart, 12, 5, 5_000),
        });

        gateway.Unlisted.Add("M26");

        ToolPayloads.BarSeries series = await Tools(gateway, Now).Bars.GetBars(
            "MES", 5, HistoryStart, HistoryStart.AddHours(1), CancellationToken.None);

        series.Bars.Should().HaveCount(12, "the bars are returned either way -- that is the whole trap");
        series.Contracts.Span.Should().Be(
            ToolPayloads.ContractSpan.SingleContract,
            "one contract answered this window, which says nothing at all about which contracts it was "
            + "chosen from -- the two fields are independent");

        series.History.Selection.Should().Be(HistorySelection.NarrowedByTheVenue);
        series.History.Unresolved.Should().Equal(new[] { "M26" });
    }

    [Fact]
    public async Task GetLatestBarsPayload_CarriesTheNarrowingOfItsHistoricalHalf()
    {
        // The same claim for the other bar tool, and it is not a copy: get_latest_bars builds its own window
        // from the clock and hands ToCoverage a TAIL of the series rather than all of it, so the two payload
        // sites are two places the field can be dropped. This look-back reaches back past the front's tenure
        // into August trade dates, whose second candidate Z26 the venue does not list.
        await SeedTenureAnchorAsync();

        CountingGateway gateway = Venue(new Dictionary<string, IEnumerable<Bar>>(StringComparer.Ordinal)
        {
            [Front] = Series(Market(2026, 8, 10, 9), 200, 60, 1_000),
        });

        ToolPayloads.BarSeries series = await Tools(gateway, Now).Bars.GetLatestBars(
            "MES", 60, 24, CancellationToken.None);

        series.History.Selection.Should().Be(
            HistorySelection.NarrowedByTheVenue,
            "the look-back crosses the tenure start, so its older half is history the venue narrowed");
        series.History.Unresolved.Should().Equal(
            new[] { "Z26" }, "the August candidate the venue does not list is what narrowed the set");
    }

    [Fact]
    public async Task GetSessionBarsPayload_CarriesTheNarrowingOfItsBaseRead()
    {
        // A session bar is DERIVED from base bars read through the same cache-aside call, so a narrowed base
        // read produces sessions aggregated over exactly that stretch -- and nothing else on the session
        // payload says so. `contracts.span` cannot: a session whose base bars disagreed is absent rather than
        // spliced, so span describes the sessions that survived, not how their bars were chosen.
        //
        // The venue is silent over this June session, so the trade date is an absence. That is deliberate:
        // the degradation is a fact about the READ, and it must survive an answer with no bars in it.
        await SeedTenureAnchorAsync();

        CountingGateway gateway = Venue(new Dictionary<string, IEnumerable<Bar>>(StringComparer.Ordinal)
        {
            [Front] = [],
        });

        gateway.Unlisted.Add("M26");

        ToolPayloads.SessionBarSeries series = await Tools(gateway, Now).Sessions.GetSessionBars(
            "MES",
            "rth",
            new DateTimeOffset(2026, 6, 16, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 6, 17, 0, 0, 0, TimeSpan.Zero),
            CancellationToken.None);

        series.Absent.Should().ContainSingle().Which.Reason.Should().Be(
            SessionBarAbsence.Incomplete, "the venue held nothing for this session");

        series.History.Selection.Should().Be(HistorySelection.NarrowedByTheVenue);
        series.History.Unresolved.Should().Equal(new[] { "M26" });
    }

    [Fact]
    public async Task GetBarsPayload_ReportsAsTheFrontAlone_WhenTheRegistryDoesNotServeTheInstrument()
    {
        // ARM 1, get_bars (gh#598). The resolver serves ES so the tool accepts the call; the cache's
        // registry does not, which is the whole-read condition PlanAsync records. Replacing
        // AsTheFrontAlone with NotDecidedHere reddens this test.
        CountingGateway gateway = Venue(
            new Dictionary<string, IEnumerable<Bar>>(StringComparer.Ordinal)
            {
                [EsFront] = Series(HistoryStart, 12, 5, 10),
            },
            EsFront);

        ToolPayloads.BarSeries series = await Tools(gateway, Now, resolverInstruments: "MES,ES")
            .Bars.GetBars("ES", 5, HistoryStart, HistoryStart.AddHours(1), CancellationToken.None);

        series.History.Selection.Should().Be(HistorySelection.AsTheFrontAlone);
        series.History.Unresolved.Should().BeEmpty();
        series.Bars.Should().HaveCount(12);
    }

    [Fact]
    public async Task GetBarsPayload_ReportsAsTheFrontAlone_WhenTheFrontExpiryDoesNotReadAgainstTheCycle()
    {
        // ARM 2, get_bars (gh#598). HMUZ does not list January. Replacing AsTheFrontAlone with
        // NotDecidedHere reddens this test.
        CountingGateway gateway = Venue(
            new Dictionary<string, IEnumerable<Bar>>(StringComparer.Ordinal)
            {
                [OffCycleFront] = Series(HistoryStart, 12, 5, 10),
            },
            OffCycleFront);

        ToolPayloads.BarSeries series = await Tools(gateway, Now).Bars.GetBars(
            "MES", 5, HistoryStart, HistoryStart.AddHours(1), CancellationToken.None);

        series.History.Selection.Should().Be(HistorySelection.AsTheFrontAlone);
        series.History.Unresolved.Should().BeEmpty();
        series.Bars.Should().HaveCount(12);
    }

    [Fact]
    public async Task GetLatestBarsPayload_ReportsAsTheFrontAlone_WhenTheRegistryDoesNotServeTheInstrument()
    {
        // ARM 1, get_latest_bars — its own payload site, not a copy of get_bars.
        CountingGateway gateway = Venue(
            new Dictionary<string, IEnumerable<Bar>>(StringComparer.Ordinal)
            {
                [EsFront] = Series(Market(2026, 8, 10, 9), 200, 60, 1_000),
            },
            EsFront);

        ToolPayloads.BarSeries series = await Tools(gateway, Now, resolverInstruments: "MES,ES")
            .Bars.GetLatestBars("ES", 60, 24, CancellationToken.None);

        series.History.Selection.Should().Be(HistorySelection.AsTheFrontAlone);
        series.History.Unresolved.Should().BeEmpty();
    }

    [Fact]
    public async Task GetLatestBarsPayload_ReportsAsTheFrontAlone_WhenTheFrontExpiryDoesNotReadAgainstTheCycle()
    {
        // ARM 2, get_latest_bars.
        CountingGateway gateway = Venue(
            new Dictionary<string, IEnumerable<Bar>>(StringComparer.Ordinal)
            {
                [OffCycleFront] = Series(Market(2026, 8, 10, 9), 200, 60, 1_000),
            },
            OffCycleFront);

        ToolPayloads.BarSeries series = await Tools(gateway, Now).Bars.GetLatestBars(
            "MES", 60, 24, CancellationToken.None);

        series.History.Selection.Should().Be(HistorySelection.AsTheFrontAlone);
        series.History.Unresolved.Should().BeEmpty();
    }

    [Fact]
    public async Task GetSessionBarsPayload_ReportsAsTheFrontAlone_WhenTheRegistryDoesNotServeTheInstrument()
    {
        // ARM 1, get_session_bars — the field arrives through SessionBarService, a third drop site.
        CountingGateway gateway = Venue(
            new Dictionary<string, IEnumerable<Bar>>(StringComparer.Ordinal)
            {
                [EsFront] = [],
            },
            EsFront);

        ToolPayloads.SessionBarSeries series = await Tools(gateway, Now, resolverInstruments: "MES,ES")
            .Sessions.GetSessionBars(
                "ES",
                "rth",
                new DateTimeOffset(2026, 6, 16, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 6, 17, 0, 0, 0, TimeSpan.Zero),
                CancellationToken.None);

        series.History.Selection.Should().Be(HistorySelection.AsTheFrontAlone);
        series.History.Unresolved.Should().BeEmpty();
    }

    [Fact]
    public async Task GetSessionBarsPayload_ReportsAsTheFrontAlone_WhenTheFrontExpiryDoesNotReadAgainstTheCycle()
    {
        // ARM 2, get_session_bars.
        CountingGateway gateway = Venue(
            new Dictionary<string, IEnumerable<Bar>>(StringComparer.Ordinal)
            {
                [OffCycleFront] = [],
            },
            OffCycleFront);

        ToolPayloads.SessionBarSeries series = await Tools(gateway, Now).Sessions.GetSessionBars(
            "MES",
            "rth",
            new DateTimeOffset(2026, 6, 16, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 6, 17, 0, 0, 0, TimeSpan.Zero),
            CancellationToken.None);

        series.History.Selection.Should().Be(HistorySelection.AsTheFrontAlone);
        series.History.Unresolved.Should().BeEmpty();
    }

    [Fact]
    public async Task AWholeReadFallback_StillEarnsTheEmptyRangeMemo()
    {
        // R-1.14: an empty answer from F is a true statement about F, so the whole-read fallback earns
        // the memo the per-slice fallback withholds. A card that reclassifies the range must not silently
        // withdraw that. June is settled at the August clock, so the claim is permanent.
        CountingGateway gateway = Venue(
            new Dictionary<string, IEnumerable<Bar>>(StringComparer.Ordinal)
            {
                [OffCycleFront] = [],
            },
            OffCycleFront);

        BarCacheService cache = BuildAround(gateway, Now);
        BarRange window = new(HistoryStart, HistoryStart.AddHours(1));

        BarReadResult first = await cache.GetBarsAsync(_mes, 5, window, CancellationToken.None);

        first.History.Selection.Should().Be(HistorySelection.AsTheFrontAlone);
        (await _database.BarCoverage.AsNoTracking().ToListAsync())
            .Should().ContainSingle().Which.ContractId.Should().Be(OffCycleFront);

        gateway.ResetCounters();
        BarReadResult second = await cache.GetBarsAsync(_mes, 5, window, CancellationToken.None);

        second.VenueRequests.Should().Be(0);
        gateway.BarRequests.Should().Be(0, "the front's empty answer was memoised, as it was before this card");
        second.History.Selection.Should().Be(
            HistorySelection.NotDecidedHere,
            "the second read fetched nothing, so it decided nothing -- the memo must not move the state");
    }

    [Fact]
    public async Task AStraddlingRange_WhoseHistoricalHalfNarrowedToTheFront_IsNotFoldedIntoThePresentBand()
    {
        // The fold is where the silence came from. Adjacent slices that both come down to the front alone are
        // merged into one PRESENT slice, so that a range cut at the tenure start does not silently buy a page
        // boundary -- and a historical half whose set narrowed to the front by a venue negative satisfies
        // that test exactly as a cycle-named one does. Merged, it stops being history at all: no candidate
        // set, no volume decision, and no warning, for a stretch the front is not the answer for.
        //
        // RED before the fix at ONE bar request -- the whole four hours as one present page.
        //
        // The anchor is seeded at five minutes and the read is at sixty, so the store's trailing run of the
        // front fixes T(F) at the session start while leaving the whole read outstanding: one contiguous
        // missing range with history on one side of T(F) and the present band on the other.
        await SeedTenureAnchorAsync();

        DateTimeOffset from = Market(2026, 8, 18, 7);
        BarRange window = new(from, Market(2026, 8, 18, 11));

        CountingGateway gateway = Venue(new Dictionary<string, IEnumerable<Bar>>(StringComparer.Ordinal)
        {
            [Front] = Enumerable.Range(0, 4).Select(i => Flat(from.AddHours(i), 1_000)),
        });

        CapturingLogger<BarCacheService> log = new();
        BarCacheService cache = BuildAround(gateway, Now, log);

        await cache.GetBarsAsync(_mes, 60, window, CancellationToken.None);

        gateway.BarRequests.Should().Be(
            2,
            "the two hours before the front's tenure are history whose candidate set the venue narrowed, and "
            + "history is not the present band however identical the contract id happens to look");

        log.Messages.Should().ContainMatch(
            "*Z26*", "the August candidate the venue does not list is what narrowed the set");

        (await StoredAsync(window.Start, window.End, 60)).Should().HaveCount(4);
    }

    [Fact]
    public async Task AllCandidatesEmpty_RecordsOnePermanentMemoPerContract()
    {
        // "Empty" is a fact about a range AND a contract (gh#504), so a range every candidate answered empty
        // leaves one memo per candidate -- not one memo, and not none. One memo would assert the emptiness on
        // behalf of a contract nobody asked; none would put the whole candidate set back on the venue on
        // every read, which is gh#408's unbounded cost re-opened at K times the width.
        await SeedTenureAnchorAsync();

        CountingGateway gateway = Venue(new Dictionary<string, IEnumerable<Bar>>(StringComparer.Ordinal)
        {
            [Front] = [],
            [Liquid] = [],
        });

        BarCacheService cache = BuildAround(gateway, Now);
        BarRange window = new(HistoryStart, HistoryStart.AddHours(1));

        await cache.GetBarsAsync(_mes, 5, window, CancellationToken.None);

        gateway.BarRequests.Should().Be(2);

        List<BarCoverageRecord> memos = await _database.BarCoverage.AsNoTracking().ToListAsync();

        memos.Select(m => m.ContractId).Should().BeEquivalentTo([Front, Liquid]);
        memos.Should().AllSatisfy(m => m.ExpiresAt.Should().BeNull(
            "June is settled history at an August clock, so an empty answer over it is believed permanently"));

        gateway.ResetCounters();
        BarReadResult second = await cache.GetBarsAsync(_mes, 5, window, CancellationToken.None);

        second.VenueRequests.Should().Be(0);
        gateway.BarRequests.Should().Be(
            0, "every candidate of the slice has a memo covering it, so the range is answered");
        gateway.ContractLookups.Should().Be(
            0,
            "this cache holds one ContractDirectory, and a positive answer it has already given is not asked "
            + "of the venue again");
    }

    [Fact]
    public async Task OneCandidateEmpty_RecordsNoMemo_ForTheWinner()
    {
        // The other half of the ledger rule. A candidate that answered with bars is not a candidate that
        // answered empty, and a memo written for the winner would claim the range holds nothing under the
        // very contract whose bars were just stored -- which would then answer the range for every future
        // read and hide the rest of it forever.
        await SeedTenureAnchorAsync();

        CountingGateway gateway = Venue(new Dictionary<string, IEnumerable<Bar>>(StringComparer.Ordinal)
        {
            [Front] = [],
            [Liquid] = Series(HistoryStart, 12, 5, 5_000),
        });

        BarCacheService cache = BuildAround(gateway, Now);

        await cache.GetBarsAsync(
            _mes, 5, new BarRange(HistoryStart, HistoryStart.AddHours(1)), CancellationToken.None);

        (await _database.BarCoverage.AsNoTracking().ToListAsync())
            .Should().ContainSingle().Which.ContractId.Should().Be(
                Front, "only the contract that answered nothing is recorded as having answered nothing");

        (await StoredAsync(HistoryStart, HistoryStart.AddHours(1)))
            .Should().AllSatisfy(b => b.ContractId.Should().Be(Liquid));
    }

    [Fact]
    public async Task ATradeDateAlreadyHeld_IsNotInterleaved()
    {
        // ADR-0020 part 5, and ADR-0011's interleaving consequence discharged. A trade date the store already
        // holds attributed bars for keeps its contract, even when another candidate carried more volume that
        // day -- because a read that re-decided history would splice a second contract into the middle of a
        // day already recorded, and ContractRollDetector reports contiguous RUNS: a contract that reappears
        // after another is a third segment, which is what a backfill under the wrong month looks like.
        //
        // Rewriting a defective run is an operator's decision and has its own verb (reselect-bars, gh#506).
        await SeedTenureAnchorAsync();

        BarRange window = new(Market(2026, 6, 16, 9), Market(2026, 6, 16, 16));
        DateTimeOffset held = Market(2026, 6, 16, 13);
        IReadOnlyList<DateTimeOffset> grid =
            BarGapDetector.ExpectedBuckets(window, TimeSpan.FromHours(1), Calendar);

        // The first four hours of the June session, already stored under the venue's pick -- the thin run an
        // operator's store carries from before this record.
        foreach (DateTimeOffset bucket in grid.Where(b => b < held))
        {
            await SeedBucketAsync(Front, 60, bucket, 10);
        }

        CountingGateway gateway = Venue(new Dictionary<string, IEnumerable<Bar>>(StringComparer.Ordinal)
        {
            [Front] = grid.Where(b => b >= held).Select(b => Flat(b, 10)),
            [Liquid] = grid.Where(b => b >= held).Select(b => Flat(b, 5_000)),
        });

        BarCacheService cache = BuildAround(gateway, Now);

        BarReadResult result = await cache.GetBarsAsync(_mes, 60, window, CancellationToken.None);

        // Asserted, because without it this passes for the wrong reason: a read that never asked the fat
        // candidate at all would also store the front's bars, and that is the pre-ADR-0020 behaviour rather
        // than the pin. Both candidates are asked; the stored contract is what decides between them.
        gateway.BarRequests.Should().Be(
            2, "the range is historical, so every candidate the June trade date names is asked");

        ContractRollDetector.Segment(result.Bars).Should().ContainSingle(
            "the trade date keeps the contract the store already attributed it to, so the day is one run");

        (await StoredAsync(window.Start, window.End, 60))
            .Should().AllSatisfy(b => b.ContractId.Should().Be(Front));
    }

    [Fact]
    public async Task ATradeDateAlreadyHeld_IsNotInterleaved_EvenWhenTheWindowCoversOnlyPartOfIt()
    {
        // THE PIN IS A FACT ABOUT THE TRADE DATE, NOT ABOUT THE WINDOW. The case above asks for the whole
        // June session, so the morning that already carries a contract is inside the window either way. A
        // caller asking only for the afternoon -- get_latest_bars, a chart scrolling forward, any narrower
        // read -- would have found no attributed row inside its own window, kept nothing, and let the volume
        // decide: the day would end up half the venue's pick and half the liquid contract, which is
        // ADR-0011's interleaving arriving INSIDE one trade date rather than across a roll.
        //
        // RED against a pin built from the step-1 window read: the afternoon is stored under Liquid, the day
        // reports two runs, and the morning the store already held is stranded on the other side of a seam
        // no market event produced.
        await SeedTenureAnchorAsync();

        BarRange day = new(Market(2026, 6, 16, 9), Market(2026, 6, 16, 16));
        DateTimeOffset held = Market(2026, 6, 16, 13);
        IReadOnlyList<DateTimeOffset> grid =
            BarGapDetector.ExpectedBuckets(day, TimeSpan.FromHours(1), Calendar);

        foreach (DateTimeOffset bucket in grid.Where(b => b < held))
        {
            await SeedBucketAsync(Front, 60, bucket, 10);
        }

        CountingGateway gateway = Venue(new Dictionary<string, IEnumerable<Bar>>(StringComparer.Ordinal)
        {
            [Front] = grid.Where(b => b >= held).Select(b => Flat(b, 10)),
            [Liquid] = grid.Where(b => b >= held).Select(b => Flat(b, 5_000)),
        });

        BarCacheService cache = BuildAround(gateway, Now);

        // The afternoon alone. Nothing the store already holds is inside this window.
        await cache.GetBarsAsync(_mes, 60, new BarRange(held, day.End), CancellationToken.None);

        gateway.BarRequests.Should().Be(
            2, "the range is historical, so every candidate the June trade date names is asked");

        List<BarRecord> stored = await StoredAsync(day.Start, day.End, 60);

        stored.Should().HaveCount(grid.Count);
        stored.Should().AllSatisfy(b => b.ContractId.Should().Be(
            Front, "the trade date was already recorded under the venue's pick, and a read does not rewrite "
            + "history it was not even asked about"));

        ContractRollDetector.Segment([.. stored.Select(IndicatorProjector.ToBar)]).Should().ContainSingle(
            "one trade date, one contract -- a seam here would be a bookkeeping artefact of how wide the "
            + "caller's window happened to be");
    }

    [Fact]
    public async Task AnEmptyHistoricalSlice_ThatEndsInsideTheSettledAge_IsMemoisedPermanentlyUpToIt()
    {
        // A historical slice is as wide as its candidate set holds -- up to a whole quarter -- and it is
        // memoised as ONE row, while RecordEmptyAsync decides permanence from the row's END alone. A slice
        // that runs up to a tenure start younger than SettledHistoryAge therefore takes a fifteen-minute TTL
        // over the WHOLE stretch: every candidate's every page is re-fetched, paced, four times an hour,
        // until T(F) drifts past two days old. That is gh#408's unbounded per-read cost multiplied by the
        // candidate depth.
        //
        // So the empty answer is cut at `now - SettledHistoryAge`: the settled part is believed for good,
        // and only the young remainder is re-asked. The two rows TOUCH, so Union merges them and the slice
        // is still answered whole while both stand.
        //
        // RED against one memo per slice: a single row spanning the window, carrying a TTL.
        //
        // Every instant below is a LITERAL. `now` is Thursday 20 August and the cut is Tuesday 18 August at
        // 13:00 Central -- which is two days earlier, and is written out rather than computed for the reason
        // BarCacheServiceTests.SettledNow gives: derived from SettledHistoryAge this expectation would move
        // with the constant, and the case that exists to catch that change would follow it and stay green.
        DateTimeOffset now = Market(2026, 8, 20, 13);
        DateTimeOffset settledBefore = Market(2026, 8, 18, 13);
        BarRange window = new(Market(2026, 8, 18, 9), Market(2026, 8, 18, 16));

        // The tenure anchor, placed so the WHOLE window is history: a non-front bucket on the Monday, then
        // the front's run opening on the Wednesday. T(F) is therefore Wednesday morning, past the window.
        await SeedBucketAsync(Previous, 5, Market(2026, 8, 17, 9), 1_000);
        await SeedAsync(Front, 5, Market(2026, 8, 19, 9), 4);

        // The venue lists the front alone, so August's second candidate (Z26) is dropped and the slice has
        // exactly one surviving candidate -- one contract's memo to count, rather than two identical pairs.
        CountingGateway gateway = Venue(new Dictionary<string, IEnumerable<Bar>>(StringComparer.Ordinal)
        {
            [Front] = [],
        });

        BarCacheService cache = BuildAround(gateway, now);

        await cache.GetBarsAsync(_mes, 5, window, CancellationToken.None);

        List<BarCoverageRecord> memos =
            [.. (await _database.BarCoverage.AsNoTracking().ToListAsync()).OrderBy(m => m.RangeStart)];

        memos.Should().HaveCount(2, "the answer is one fact about two ages of history, not one fact");

        memos[0].ContractId.Should().Be(Front);
        memos[0].RangeStart.Should().Be(window.Start);
        memos[0].RangeEnd.Should().Be(settledBefore);
        memos[0].ExpiresAt.Should().BeNull(
            "everything up to the settled age is history that is not going to fill in");

        memos[1].ContractId.Should().Be(Front);
        memos[1].RangeStart.Should().Be(settledBefore);
        memos[1].RangeEnd.Should().Be(window.End);
        memos[1].ExpiresAt.Should().NotBeNull(
            "a bucket empty only for not having printed yet will print, and a permanent claim would blind "
            + "the cache to it");

        // The behaviour the split exists to keep, and it is the half a column assertion cannot see: two rows
        // that touch are one answer, so nothing is re-asked while both stand.
        gateway.ResetCounters();
        BarReadResult second = await cache.GetBarsAsync(_mes, 5, window, CancellationToken.None);

        second.VenueRequests.Should().Be(0);
        gateway.BarRequests.Should().Be(0, "the union of the two touching memos answers the slice whole");
    }

    [Fact]
    public async Task EveryHandBuiltCache_ServesItsOwnInstrumentThroughTheFetchPath()
    {
        // The behavioural half of the reflection assertion below (gh#505 review). A registry built from
        // different options than the instrument the fixture then reads answers CycleFor with
        // KeyNotFoundException, and the fetch flow now asks it on every read that owes the venue anything --
        // so the mistake arrives as a thrown tool call rather than as a wrong number.
        //
        // ConcurrencyHarness serves TWO symbols: ES for everything and MNQ for the rebuild test, which needs
        // its own instrument because rebuild-indicators filters by instrument rather than by venue. Asserted
        // through a real read on the second one, and on the absence of the degradation warning an unserved
        // instrument earns -- a read that fell back would otherwise pass by fetching exactly as it does now.
        string venue = ConcurrencyHarness.Venue();
        CapturingLogger<BarCacheService> log = new();

        BarCacheService cache = ConcurrencyHarness.Cache(
            _database, venue, ConcurrencyHarness.Bars(0, 4), ConcurrencyHarness.Bucket(8), log);

        BarReadResult result = await cache.GetBarsAsync(
            ConcurrencyHarness.RebuildInstrument,
            ConcurrencyHarness.ResolutionMinutes,
            ConcurrencyHarness.Window(0, 4),
            CancellationToken.None);

        result.Bars.Should().NotBeEmpty("the read has to reach the fetch, or it asks the registry nothing");
        log.Messages.Should().NotContainMatch(
            "*not one this server serves*",
            "the harness configures MNQ, so the cache it hands out must carry the registry that knows it");
    }

    [Fact]
    public void EveryHandBuiltCache_CarriesARegistryServingItsOwnInstrument()
    {
        // A registry is only useful for the instruments it was configured with: CycleFor and
        // CandidateDepthFor throw KeyNotFoundException for anything else. So a fixture that hands the cache a
        // registry built from DIFFERENT options than the instrument it then reads is a KeyNotFoundException
        // waiting for the first historical fetch -- and it would arrive as a thrown tool call, not as a red
        // test, because nothing in the fetch flow consults the registry yet.
        //
        // ConcurrencyHarness is the one fixture where the two disagree. It serves TWO symbols: ES for
        // everything, and MNQ for the rebuild test, which needs its own instrument because rebuild-indicators
        // filters by instrument rather than by venue and would otherwise reconcile every other test's series.
        // Registry() already names both; the cache has to be given THAT one, not a default.
        BarCacheService cache = ConcurrencyHarness.Cache(
            _database, "test", [], SessionStart.AddHours(2));

        InstrumentRegistry registry = RegistryOf(cache);

        registry.CycleFor(ConcurrencyHarness.RebuildInstrument).Code.Should().Be("HMUZ");
        registry.CandidateDepthFor(ConcurrencyHarness.RebuildInstrument).Should().Be(2);

        registry.CycleFor(ConcurrencyHarness.Instrument).Code.Should().Be("HMUZ");
        registry.CandidateDepthFor(ConcurrencyHarness.Instrument).Should().Be(2);
    }

    /// <summary>The registry a service was constructed with.</summary>
    /// <param name="cache">The service.</param>
    /// <returns>The registry.</returns>
    /// <remarks>
    /// Read off the field because the service exposes no registry, and it should not: nothing outside the
    /// fetch flow has a reason to ask it for one. The behavioural assertion that could not be written when
    /// this was added — the fetch did not yet consult the registry — now exists beside it as
    /// <c>EveryHandBuiltCache_ServesItsOwnInstrumentThroughTheFetchPath</c>; this one is kept because it
    /// names the two registry questions directly and fails by <i>name</i> if the field is ever renamed,
    /// which is the right failure direction for a coupling nobody should introduce quietly.
    /// </remarks>
    private static InstrumentRegistry RegistryOf(BarCacheService cache) =>
        (InstrumentRegistry)typeof(BarCacheService)
            .GetField("_registry", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(cache)!;

    /// <summary>A flat bar carrying one volume — the only number these cases decide anything on.</summary>
    /// <param name="bucket">When the bucket opens.</param>
    /// <param name="volume">The volume.</param>
    /// <returns>The bar.</returns>
    private static Bar Flat(DateTimeOffset bucket, long volume) =>
        new(bucket, 100m, 101m, 99m, 100.5m, volume);

    /// <summary>A run of flat bars at one resolution, all carrying the same volume.</summary>
    /// <param name="from">The first bucket.</param>
    /// <param name="count">How many buckets.</param>
    /// <param name="minutes">The bar size in minutes.</param>
    /// <param name="volume">The volume every bar carries.</param>
    /// <returns>The bars, ascending.</returns>
    private static IEnumerable<Bar> Series(DateTimeOffset from, int count, int minutes, long volume) =>
        Enumerable.Range(0, count).Select(i => Flat(from.AddMinutes(minutes * i), volume));

    /// <summary>A venue listing several expiries of MES, with <see cref="Front"/> the active one.</summary>
    /// <param name="byContract">The bars each listed contract holds.</param>
    /// <returns>The double.</returns>
    private static CountingGateway Venue(IReadOnlyDictionary<string, IEnumerable<Bar>> byContract) =>
        Venue(byContract, Front);

    /// <summary>A venue whose active contract the test names.</summary>
    /// <param name="byContract">The bars each listed contract holds.</param>
    /// <param name="front">The contract the venue marks active.</param>
    /// <returns>The double.</returns>
    private static CountingGateway Venue(
        IReadOnlyDictionary<string, IEnumerable<Bar>> byContract, string front) =>
        new(byContract, front);

    /// <summary>The stored rows of a window, read past the tracker.</summary>
    /// <param name="from">The window start.</param>
    /// <param name="to">The window end, exclusive.</param>
    /// <param name="resolutionMinutes">The resolution.</param>
    /// <returns>The rows, ascending.</returns>
    /// <remarks>
    /// <b><c>AsNoTracking</c>, and it is not tidiness.</b> The bars are written by an
    /// <c>ON CONFLICT … DO UPDATE</c> statement the change tracker never sees, so a tracked read is answered
    /// from the identity map with whatever this context seeded rather than with the row the statement wrote.
    /// </remarks>
    private async Task<List<BarRecord>> StoredAsync(
        DateTimeOffset from, DateTimeOffset to, int resolutionMinutes = 5) =>
        await _database.Bars
            .AsNoTracking()
            .Where(b => b.ResolutionMinutes == resolutionMinutes
                && b.BucketStart >= from
                && b.BucketStart < to)
            .OrderBy(b => b.BucketStart)
            .ToListAsync();

    /// <summary>
    /// Seeds the shape that puts the present band at <see cref="SessionStart"/>, so a June or March window
    /// is history by measurement rather than by the seven-day fallback.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two runs, and both are load-bearing. The <see cref="Previous"/> bucket in July is what makes the
    /// front's <b>trailing</b> run start in August: <c>T(F)</c> is the earliest front bucket <i>after</i> the
    /// latest bucket that is not the front's, so without it a front bar seeded anywhere earlier — which
    /// several cases below do seed — would drag the present band back over the range under test and the case
    /// would pass by testing the present path.
    /// </para>
    /// <para>
    /// Every instant here is a literal. Derived from <see cref="BarCacheService.PresentHorizon"/> these would
    /// move with the constant, and the cases that exist to catch a change to it would follow it and stay
    /// green.
    /// </para>
    /// </remarks>
    private async Task SeedTenureAnchorAsync()
    {
        await SeedAsync(Previous, 5, Market(2026, 7, 14, 9), 1);
        await SeedAsync(Front, 5, SessionStart, 12);
    }

    /// <summary>Seeds one bucket under one contract id, at a stated volume.</summary>
    /// <param name="contractId">The contract the row is attributed to.</param>
    /// <param name="resolutionMinutes">The resolution.</param>
    /// <param name="bucket">The bucket start.</param>
    /// <param name="volume">The volume.</param>
    private async Task SeedBucketAsync(
        string contractId, int resolutionMinutes, DateTimeOffset bucket, long volume)
    {
        _database.Bars.Add(new BarRecord
        {
            Venue = "test",
            Instrument = _mes.Symbol,
            ResolutionMinutes = resolutionMinutes,
            BucketStart = bucket,
            Open = 100m,
            High = 101m,
            Low = 99m,
            Close = 100.5m,
            Volume = volume,
            ContractId = contractId,
            RecordedAt = SessionStart,
        });

        await _database.SaveChangesAsync();
        _database.ChangeTracker.Clear();
    }

    /// <summary>Seeds a run of buckets under one contract id, from an instant.</summary>
    /// <param name="contractId">The contract the rows are attributed to.</param>
    /// <param name="resolutionMinutes">The resolution to seed under.</param>
    /// <param name="from">The first bucket.</param>
    /// <param name="count">How many buckets the run holds.</param>
    private async Task SeedAsync(
        string contractId, int resolutionMinutes, DateTimeOffset from, int count)
    {
        for (int i = 0; i < count; i++)
        {
            await SeedBucketAsync(
                contractId, resolutionMinutes, from.AddMinutes(i * resolutionMinutes), 1_000);
        }
    }

    /// <summary>Seeds a run of buckets under one contract id.</summary>
    /// <param name="contractId">The contract the rows are attributed to.</param>
    /// <param name="resolutionMinutes">The resolution to seed under.</param>
    /// <param name="fromMinute">Minutes past <see cref="SessionStart"/> the run opens at.</param>
    /// <param name="count">How many buckets the run holds.</param>
    /// <remarks>
    /// <b>The tracker is cleared afterwards (gh#387).</b> These rows go in through the change tracker while
    /// the reads under test are ordinary queries; left tracked, the identity map would answer them with the
    /// instances this method wrote rather than with the rows the store holds.
    /// </remarks>
    private async Task SeedAsync(string contractId, int resolutionMinutes, int fromMinute, int count)
    {
        for (int i = 0; i < count; i++)
        {
            _database.Bars.Add(new BarRecord
            {
                Venue = "test",
                Instrument = _mes.Symbol,
                ResolutionMinutes = resolutionMinutes,
                BucketStart = SessionStart.AddMinutes(fromMinute + (i * resolutionMinutes)),
                Open = 100m,
                High = 101m,
                Low = 99m,
                Close = 100.5m,
                Volume = 1_000,
                ContractId = contractId,
                RecordedAt = SessionStart,
            });
        }

        await _database.SaveChangesAsync();
        _database.ChangeTracker.Clear();
    }

    /// <summary>The two tool types whose payloads carry the historical selection.</summary>
    /// <param name="Bars">The bar tools.</param>
    /// <param name="Sessions">The session-bar tools.</param>
    /// <remarks>
    /// Named rather than returned loose so a case says at its call site which surface it is pinning:
    /// <c>get_bars</c> and <c>get_latest_bars</c> build the payload themselves, while
    /// <c>get_session_bars</c> receives the fact through <c>SessionBarService</c> — three separate places
    /// the field can be dropped between the cache and the wire (gh#592).
    /// </remarks>
    private sealed record ToolFamily(BarTools Bars, SessionBarTools Sessions);

    /// <summary>Composes the tool surface over the same MES fixture the service cases use.</summary>
    /// <param name="gateway">The venue double.</param>
    /// <param name="now">The instant to read at.</param>
    /// <returns>The tools.</returns>
    /// <remarks>
    /// Around <see cref="BuildAround"/> rather than beside it, so the cache under the tools is the cache
    /// every other case here reasons about — a second composition would be a second fixture free to disagree
    /// about the tenure start, the cycle or the clock.
    /// </remarks>
    /// <param name="resolverInstruments">
    /// What the tool resolver serves. Wider than <paramref name="cacheInstruments"/> when the case is the
    /// registry whole-read arm: the tool must accept the symbol, and the cache must not.
    /// </param>
    /// <param name="cacheInstruments">What the cache's registry serves. Defaults to MES like every other case.</param>
    private ToolFamily Tools(
        CountingGateway gateway,
        DateTimeOffset now,
        string resolverInstruments = "MES",
        string cacheInstruments = "MES")
    {
        FakeTimeProvider clock = new(now);
        BarCacheService cache = BuildAround(gateway, now, clock: clock, instruments: cacheInstruments);

        IOptions<MarketDataOptions> market = Options.Create(new MarketDataOptions
        {
            Instruments = resolverInstruments,
            SessionCloseCentral = "16:00",
            MaxRows = 5_000,
        });

        InstrumentResolver resolver = new(new InstrumentRegistry(market), new StoreAvailabilityHolder());
        ToolGuards guards = new(market);

        SessionBarService sessions = new(
            _database,
            cache,
            gateway,
            Calendar,
            new IndicatorProjector(
                _database,
                new IndicatorCatalog(
                    Options.Create(new IndicatorOptions { AtrPeriod = 3, RsiPeriod = 3 }), Calendar),
                NullLogger<IndicatorProjector>.Instance,
                _telemetry),
            clock,
            NullLogger<SessionBarService>.Instance);

        return new ToolFamily(
            new BarTools(resolver, cache, guards, clock),
            new SessionBarTools(
                resolver, sessions, new SessionCatalog(market, Calendar), Calendar, guards, clock));
    }

    /// <summary>Builds the cache over a venue listing both the front and the contract behind it.</summary>
    /// <param name="now">The instant to read at.</param>
    /// <returns>The service.</returns>
    private BarCacheService Build(DateTimeOffset now) =>
        BuildAround(
            Venue(new Dictionary<string, IEnumerable<Bar>>(StringComparer.Ordinal)
            {
                [Front] = [],
                [Previous] = [],
            }),
            now);

    /// <summary>Builds the cache around a venue double the test already holds.</summary>
    /// <param name="gateway">The venue double.</param>
    /// <param name="now">The instant to read at.</param>
    /// <param name="logger">A logger, when the case needs to read what the fetch said it did.</param>
    /// <param name="clock">
    /// The clock, when the case needs to <b>move</b> it — a <c>ContractDirectory</c> negative lapses on
    /// elapsed time, and a clock this method owns cannot be advanced from the test. It must already read
    /// <paramref name="now"/>; one is built from <paramref name="now"/> when the case does not care.
    /// </param>
    /// <returns>The service.</returns>
    /// <exception cref="ArgumentException">
    /// A supplied clock reads a different instant than <paramref name="now"/>.
    /// </exception>
    /// <param name="instruments">The symbols the cache's registry serves.</param>
    private BarCacheService BuildAround(
        CountingGateway gateway,
        DateTimeOffset now,
        ILogger<BarCacheService>? logger = null,
        FakeTimeProvider? clock = null,
        string instruments = "MES")
    {
        // ONE INSTANT, NOT TWO. `now` exists only to build the clock, so a supplied clock reading something
        // else wins silently and the case is then about a moment it does not name -- and every expectation
        // here is a literal precisely so that the moment is written down. Refused while it is a typo rather
        // than a green test measuring the wrong hour.
        if (clock is not null && clock.GetUtcNow() != now)
        {
            throw new ArgumentException(
                "The clock reads " + clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)
                + " but `now` is " + now.ToString("O", CultureInfo.InvariantCulture)
                + ". The clock is what the read sees, so the two must agree.",
                nameof(clock));
        }

        clock ??= new FakeTimeProvider(now);
        BarSessionCalendar calendar = Calendar;

        IndicatorCatalog catalog = new(
            Options.Create(new IndicatorOptions { AtrPeriod = 3, RsiPeriod = 3 }), calendar);

        IndicatorProjector projector =
            new(_database, catalog, NullLogger<IndicatorProjector>.Instance, _telemetry);

        IOptions<MarketDataOptions> market = Options.Create(new MarketDataOptions
        {
            Instruments = instruments,
            SessionCloseCentral = "16:00",
            MaxRows = 5_000,
        });

        return new BarCacheService(
            _database,
            gateway,
            calendar,
            projector,
            new InstrumentRegistry(market),
            new ContractDirectory(clock),
            clock,
            logger ?? NullLogger<BarCacheService>.Instance,
            _telemetry);
    }
}
