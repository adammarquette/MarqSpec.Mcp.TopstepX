using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// Cache-aside: what it costs, and when it costs nothing.
/// </summary>
/// <remarks>
/// These pin the property the whole design exists to provide. They run against an in-memory store rather than
/// a container because the claim is about <i>logic</i> — which buckets are asked for — and a suite that has to
/// wait for Docker to prove it is a suite people skip. The schema-level claims (hypertable, HNSW index, CHECK
/// constraints, real upsert semantics) belong to the integration tier, which an in-memory provider could not
/// prove anything about.
/// </remarks>
public sealed class BarCacheServiceTests : IDisposable
{
    private static readonly InstrumentId _es = new("ES");

    private readonly TopstepXDbContext _database;

    public BarCacheServiceTests() =>
        _database = new TopstepXDbContext(
            new DbContextOptionsBuilder<TopstepXDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics
                    .InMemoryEventId.TransactionIgnoredWarning))
                .Options);

    public void Dispose() => _database.Dispose();

    /// <summary>A Tuesday mid-session, so every bucket in the window is one the venue owes us.</summary>
    private static DateTimeOffset SessionStart =>
        MarketClock.FromMarket(new DateOnly(2026, 8, 18), new TimeOnly(9, 0)).ToUniversalTime();

    /// <summary>
    /// A moment far enough past <see cref="SessionStart"/> that an empty answer over that session is settled
    /// history, and the memo recording it is therefore permanent.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately a literal rather than <c>SessionStart + SettledHistoryAge</c>.</b> Derived from the
    /// constant, this instant would move with it, and the tests below that exist to catch a change to
    /// <see cref="BarCacheService.SettledHistoryAge"/> would follow it and stay green.
    /// </remarks>
    private static DateTimeOffset SettledNow => SessionStart.AddDays(3);

    private static IReadOnlyList<Bar> VenueBars(int count) =>
        [.. Enumerable.Range(0, count).Select(i =>
            new Bar(SessionStart.AddMinutes(5 * i), 100m + i, 101m + i, 99m + i, 100.5m + i, 1_000))];

    /// <summary>
    /// One-minute bars covering a whole span, so a missing run is wider than one venue page.
    /// </summary>
    /// <param name="span">How much clock time to cover from <see cref="SessionStart"/>.</param>
    /// <returns>The bars, ascending.</returns>
    /// <remarks>
    /// <see cref="BarCacheService.VenuePageSizeBars"/> is 1,000, so at this resolution a page is 1,000 minutes
    /// — under seventeen hours. Anything narrower than that cannot observe the paging at all, which is the
    /// whole reason this helper exists beside <see cref="VenueBars"/>.
    /// </remarks>
    private static IReadOnlyList<Bar> MinuteBars(TimeSpan span) =>
        [.. Enumerable.Range(0, (int)span.TotalMinutes).Select(i =>
            new Bar(SessionStart.AddMinutes(i), 100m, 101m, 99m, 100.5m, 1_000))];

    /// <summary>
    /// Seeds the store with pre-migration rows: the right numbers, and no recorded contract.
    /// </summary>
    /// <param name="bars">The bars to seed, stripped of their provenance.</param>
    /// <param name="resolutionMinutes">The resolution to seed them under.</param>
    /// <remarks>
    /// This is what a row written before migration <c>20260823074908_AddBarContractId</c> looks like — present,
    /// numerically correct, and therefore never "missing" until gh#402 taught the read path otherwise.
    /// </remarks>
    private Task SeedLegacyRowsAsync(IEnumerable<Bar> bars, int resolutionMinutes = 5) =>
        SeedRowsAsync(bars, contractId: null, resolutionMinutes);

    /// <summary>
    /// Seeds the store directly, with or without a recorded contract.
    /// </summary>
    /// <param name="bars">The bars to seed.</param>
    /// <param name="contractId">The contract to record, or <see langword="null"/> for a pre-migration row.</param>
    /// <param name="resolutionMinutes">The resolution to seed them under.</param>
    /// <remarks>
    /// A test that needs a store which is <i>almost</i> healed — every bucket attributed except one — cannot be
    /// built out of the legacy seeder alone, and the one unattributed bucket is the whole subject of gh#412.
    /// </remarks>
    private async Task SeedRowsAsync(
        IEnumerable<Bar> bars, string? contractId, int resolutionMinutes = 5)
    {
        foreach (Bar bar in bars)
        {
            _database.Bars.Add(new BarRecord
            {
                Venue = "test",
                Instrument = _es.Symbol,
                ResolutionMinutes = resolutionMinutes,
                BucketStart = bar.OpenTime,
                Open = bar.Open,
                High = bar.High,
                Low = bar.Low,
                Close = bar.Close,
                Volume = bar.Volume,
                ContractId = contractId,
                RecordedAt = SessionStart,
            });
        }

        await _database.SaveChangesAsync();
    }

    private (BarCacheService Cache, CountingGateway Gateway) Build(
        IEnumerable<Bar> venueBars,
        DateTimeOffset now)
    {
        (BarCacheService cache, CountingGateway gateway, _) = BuildWithClock(venueBars, now);
        return (cache, gateway);
    }

    private (BarCacheService Cache, CountingGateway Gateway, FakeTimeProvider Clock) BuildWithClock(
        IEnumerable<Bar> venueBars,
        DateTimeOffset now)
    {
        CountingGateway gateway = new(venueBars);
        BarSessionCalendar calendar = BarSessionCalendar.Parse("16:00", []);
        FakeTimeProvider clock = new(now);

        IndicatorCatalog catalog = new(
            Options.Create(new IndicatorOptions { AtrPeriod = 3, RsiPeriod = 3 }), calendar);

        IndicatorProjector projector = new(_database, catalog, NullLogger<IndicatorProjector>.Instance);

        BarCacheService cache = new(
            _database, gateway, calendar, projector, clock, NullLogger<BarCacheService>.Instance);

        return (cache, gateway, clock);
    }

    [Fact]
    public async Task FirstRead_FetchesFromTheVenue()
    {
        DateTimeOffset now = SessionStart.AddHours(2);
        (BarCacheService cache, CountingGateway gateway) = Build(VenueBars(12), now);

        BarReadResult result = await cache.GetBarsAsync(
            _es, 5, new BarRange(SessionStart, SessionStart.AddHours(1)), CancellationToken.None);

        result.Bars.Should().HaveCount(12);
        result.FetchedBuckets.Should().Be(12);
        gateway.BarRequests.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task SecondIdenticalRead_IssuesZeroVenueRequests()
    {
        // THE test. If this ever goes red, the server is a thin proxy that hits the vendor on every question,
        // which is exactly what it was built not to be.
        DateTimeOffset now = SessionStart.AddHours(2);
        (BarCacheService cache, CountingGateway gateway) = Build(VenueBars(12), now);
        BarRange window = new(SessionStart, SessionStart.AddHours(1));

        await cache.GetBarsAsync(_es, 5, window, CancellationToken.None);
        gateway.ResetCounters();

        BarReadResult second = await cache.GetBarsAsync(_es, 5, window, CancellationToken.None);

        second.Bars.Should().HaveCount(12);
        second.FetchedBuckets.Should().Be(0);
        second.VenueRequests.Should().Be(0);
        gateway.BarRequests.Should().Be(0);
        gateway.ContractRequests.Should().Be(0);
    }

    [Fact]
    public async Task WeekendWindow_IssuesZeroVenueRequests_EvenOnAColdStore()
    {
        // The other half of termination. A closed market is not a gap, so an empty store over a weekend must
        // not produce a single request -- let alone one on every subsequent call.
        DateTimeOffset saturday =
            MarketClock.FromMarket(new DateOnly(2026, 8, 22), new TimeOnly(0, 0)).ToUniversalTime();
        (BarCacheService cache, CountingGateway gateway) = Build([], saturday.AddDays(2));

        BarReadResult result = await cache.GetBarsAsync(
            _es, 5, new BarRange(saturday, saturday.AddHours(12)), CancellationToken.None);

        result.Bars.Should().BeEmpty();
        result.VenueRequests.Should().Be(0);
        gateway.BarRequests.Should().Be(0);
    }

    [Fact]
    public async Task RangeTheVenueAnswersEmpty_IsNotRequestedAgain()
    {
        // The negative-result ledger. Without it, a genuine data hole -- before the contract listed, a
        // cancelled session -- is expected by the calendar and absent from the store, so it is indistinguishable
        // from "not fetched yet" and is re-requested on every single call.
        DateTimeOffset now = SessionStart.AddHours(2);
        (BarCacheService cache, CountingGateway gateway) = Build([], now);
        BarRange window = new(SessionStart, SessionStart.AddHours(1));

        await cache.GetBarsAsync(_es, 5, window, CancellationToken.None);
        gateway.BarRequests.Should().BeGreaterThan(0);

        gateway.ResetCounters();
        BarReadResult second = await cache.GetBarsAsync(_es, 5, window, CancellationToken.None);

        second.VenueRequests.Should().Be(0);
        gateway.BarRequests.Should().Be(0);
    }

    [Fact]
    public async Task StillFormingBars_AreNeverStored()
    {
        // The request already asks the venue for closed bars only. This must not depend on the venue
        // behaving: a half-formed bar stored as final is indistinguishable from data, and every value
        // derived from it is wrong in a way that looks entirely ordinary.
        DateTimeOffset now = SessionStart.AddMinutes(12); // mid-way through the third bucket
        (BarCacheService cache, _) = Build(VenueBars(4), now);

        BarReadResult result = await cache.GetBarsAsync(
            _es, 5, new BarRange(SessionStart, SessionStart.AddMinutes(20)), CancellationToken.None);

        // 09:00 and 09:05 have closed; 09:10 is still forming at 09:12.
        result.Bars.Should().HaveCount(2);
        result.Bars[^1].OpenTime.Should().Be(SessionStart.AddMinutes(5));
    }

    [Fact]
    public async Task ARevisedBar_UpdatesInPlaceRatherThanDuplicating()
    {
        // The venue restates bars after the fact, which is why the write is an upsert keyed on the bucket.
        // The composite primary key is the idempotence guard.
        DateTimeOffset now = SessionStart.AddHours(2);
        (BarCacheService cache, _) = Build(VenueBars(3), now);
        BarRange window = new(SessionStart, SessionStart.AddMinutes(15));

        await cache.GetBarsAsync(_es, 5, window, CancellationToken.None);

        _database.Bars.Should().HaveCount(3);
        _database.Bars.Count(b => b.BucketStart == SessionStart).Should().Be(1);
    }

    [Fact]
    public async Task IndicatorsAreProjected_InTheSameCallThatWritesTheBars()
    {
        // An indicator must exist the moment its bar does. A bar whose indicators are silently absent reads
        // back as a market that produced no signal.
        DateTimeOffset now = SessionStart.AddHours(2);
        (BarCacheService cache, _) = Build(VenueBars(12), now);

        await cache.GetBarsAsync(
            _es, 5, new BarRange(SessionStart, SessionStart.AddHours(1)), CancellationToken.None);

        _database.IndicatorValues.Should().Contain(v => v.Indicator == "atr");
        _database.IndicatorValues.Should().Contain(v => v.Indicator == "rsi");
    }

    [Fact]
    public async Task AWiderWindow_FetchesOnlyTheBucketsTheStoreLacks()
    {
        DateTimeOffset now = SessionStart.AddHours(3);
        (BarCacheService cache, CountingGateway gateway) = Build(VenueBars(24), now);

        await cache.GetBarsAsync(
            _es, 5, new BarRange(SessionStart, SessionStart.AddHours(1)), CancellationToken.None);

        gateway.ResetCounters();
        BarReadResult wider = await cache.GetBarsAsync(
            _es, 5, new BarRange(SessionStart, SessionStart.AddHours(2)), CancellationToken.None);

        wider.Bars.Should().HaveCount(24);
        wider.FetchedBuckets.Should().Be(12); // only the second hour
    }

    [Fact]
    public async Task AStoredBar_RecordsTheContractThatProducedIt()
    {
        // gh#42. The series is keyed by the symbol and the fetch is made against one contract; without the
        // contract on the row, the quarter a bar came from is unrecoverable the moment the front month rolls.
        DateTimeOffset now = SessionStart.AddHours(2);
        (BarCacheService cache, _) = Build(VenueBars(12), now);

        await cache.GetBarsAsync(
            _es, 5, new BarRange(SessionStart, SessionStart.AddHours(1)), CancellationToken.None);

        _database.Bars.Should().NotBeEmpty();
        _database.Bars.Should().AllSatisfy(b => b.ContractId.Should().Be("CON.F.US.TEST.Z26"));
    }

    [Fact]
    public async Task AReadCarriesTheContractBackToTheCaller()
    {
        // The provenance has to survive the trip out of the store, or nothing above it can report the seam.
        DateTimeOffset now = SessionStart.AddHours(2);
        (BarCacheService cache, _) = Build(VenueBars(12), now);

        BarReadResult result = await cache.GetBarsAsync(
            _es, 5, new BarRange(SessionStart, SessionStart.AddHours(1)), CancellationToken.None);

        result.Bars.Should().AllSatisfy(b => b.ContractId.Should().Be("CON.F.US.TEST.Z26"));
    }

    [Fact]
    public async Task StoredBarsWithNoRecordedContract_AreRefetched_SoTheUpsertCanHealThem()
    {
        // gh#402. A bucket the store already holds is not "missing" by FindMissing's definition, so a bucket
        // written before migration 20260823074908_AddBarContractId -- ContractId == null -- was never asked
        // for again and kept that null forever. This is RED against the pre-fix query: it selected every
        // stored bucket start regardless of ContractId, so FindMissing saw the window as fully covered and
        // the venue was never asked.
        DateTimeOffset now = SessionStart.AddHours(2);
        IReadOnlyList<Bar> venueBars = VenueBars(12);
        (BarCacheService cache, CountingGateway gateway) = Build(venueBars, now);

        // Seed the store directly with the SAME numbers the venue would answer with, but no contract -- this
        // is exactly what a pre-migration row looks like: present, with numbers already correct, and never
        // "missing".
        await SeedLegacyRowsAsync(venueBars);

        BarReadResult result = await cache.GetBarsAsync(
            _es, 5, new BarRange(SessionStart, SessionStart.AddHours(1)), CancellationToken.None);

        gateway.BarRequests.Should().BeGreaterThan(
            0, "a bucket with no recorded contract must be re-asked for, not treated as already answered");
        result.Bars.Should().OnlyContain(b => b.ContractId != null);
        _database.Bars.Should().AllSatisfy(b => b.ContractId.Should().Be("CON.F.US.TEST.Z26"));
    }

    [Fact]
    public async Task ALegacyRangeTheVenueCannotAttribute_CostsExactlyOneVenueRequest_HoweverOftenItIsRead()
    {
        // gh#408, and this is the whole safety argument for gh#402 -- which put a VENDOR CALL ON A READ PATH.
        // A bucket with no recorded contract is deliberately not "stored", so every read re-derives it as
        // missing and would re-ask for it. The only thing standing between that and one venue request per read
        // forever is the memo RecordEmptyAsync writes when the venue answers the range empty. Nothing pinned
        // it, so a change to who writes that memo would have turned an unbounded per-read fetch back on with
        // every test still green.
        //
        // RED against a build with the RecordEmptyAsync call dropped from ApplyAsync: three reads, three
        // requests.
        IReadOnlyList<Bar> legacy = VenueBars(12);

        // The venue holds NOTHING for this range any more -- past its retention, or a hole it will not
        // restate. It cannot attribute what it will not serve, so the null is not going to heal; the question
        // is only what re-asking costs.
        (BarCacheService cache, CountingGateway gateway) = Build([], SettledNow);
        await SeedLegacyRowsAsync(legacy);

        BarRange window = new(SessionStart, SessionStart.AddHours(1));
        for (int read = 0; read < 3; read++)
        {
            await cache.GetBarsAsync(_es, 5, window, CancellationToken.None);
        }

        gateway.BarRequests.Should().Be(
            1,
            "the empty answer to the first read is memoised permanently, so the heal is bounded to one "
            + "request per range rather than one per read");
        gateway.ContractRequests.Should().Be(1);
        _database.Bars.Should().AllSatisfy(
            b => b.ContractId.Should().BeNull("the venue answered nothing, so nothing was guessed"));
    }

    [Fact]
    public async Task ALegacyRangeWiderThanOneVenuePage_IsStillBoundedToOneRequestPerPage()
    {
        // gh#408, second review round -- and the finding that the one-hour fixture above CANNOT see. A range
        // is fetched in pages of VenuePageSizeBars, the memo is written PER PAGE SLICE, and ExcludeCoveredAsync
        // drops a range only when a SINGLE coverage row contains it whole. N page-memos never contain the
        // N-page range they came from; their union does, and nothing computed the union.
        //
        // That is not an exotic shape for this population: legacy rows are by construction everything written
        // before migration 20260823074908, so a multi-page missing run is the ORDINARY case the heal serves.
        // Two days of one-minute bars is three pages, and the cost was three paced pages on every read,
        // forever -- flat, not converging.
        //
        // RED against the pre-fix ExcludeCoveredAsync: 12 requests across these four reads, 3 per read.
        TimeSpan span = TimeSpan.FromDays(2);
        (BarCacheService cache, CountingGateway gateway) = Build([], SessionStart.AddDays(5));
        await SeedLegacyRowsAsync(MinuteBars(span), resolutionMinutes: 1);

        BarRange window = new(SessionStart, SessionStart + span);
        for (int read = 0; read < 4; read++)
        {
            await cache.GetBarsAsync(_es, 1, window, CancellationToken.None);
        }

        gateway.BarRequests.Should().Be(
            3,
            "the range is three pages wide, so it costs three requests ONCE -- the union of the three "
            + "page-memos answers it thereafter, and re-reading adds nothing");
        _database.BarCoverage.Should().HaveCount(3, "a memo is written per page slice, not per range");
    }

    [Fact]
    public async Task TwoCoverageRowsWithARealGapBetweenThem_DoNotAnswerTheGap()
    {
        // The guard on the union above, and the reason it is a union rather than a min-and-max. Merging every
        // coverage row into one span from the earliest start to the latest end would make the multi-page test
        // pass just as well -- and would then drop a range sitting in a genuine gap between two memos, which
        // is a bar the venue HAS and the store never fetches. Traffic is the failure this path is allowed;
        // a silently absent bar is not.
        //
        // The venue holds 09:15..09:25 and nothing either side, so the two reads below memoise two ranges
        // with a real, unanswered gap between them.
        (BarCacheService cache, CountingGateway gateway) = Build(
            VenueBars(12).Skip(3).Take(3), SessionStart.AddDays(5));

        await cache.GetBarsAsync(
            _es, 5, new BarRange(SessionStart, SessionStart.AddMinutes(15)), CancellationToken.None);
        await cache.GetBarsAsync(
            _es, 5, new BarRange(SessionStart.AddMinutes(30), SessionStart.AddMinutes(45)),
            CancellationToken.None);

        _database.BarCoverage.Should().HaveCount(2, "two separated ranges were answered empty");
        gateway.ResetCounters();

        BarReadResult gap = await cache.GetBarsAsync(
            _es, 5, new BarRange(SessionStart.AddMinutes(15), SessionStart.AddMinutes(30)),
            CancellationToken.None);

        gateway.BarRequests.Should().Be(
            1, "the gap between two coverage rows is not covered by either of them, nor by their union");
        gap.Bars.Should().HaveCount(3);
    }

    [Fact]
    public async Task TheMemoThatBoundsTheHeal_IsPermanent_SoTheBoundSurvivesTheClockMoving()
    {
        // The other half of gh#408's bound, and the half a same-instant test cannot see. The memo bounds the
        // re-ask only because the range is settled history and is therefore written with NO EXPIRY AT ALL.
        // Given RecentEmptyTtl instead, the fifteen minutes run out and the heal is an unbounded per-read
        // vendor fetch again -- on a schedule, so a test that reads twice at one instant stays green through it.
        //
        // RED against a build with SettledHistoryAge lengthened to 30 days: the range stops being settled, the
        // memo is written with an expiry, and the second read below -- an hour later -- re-fetches.
        (BarCacheService cache, CountingGateway gateway, FakeTimeProvider clock) =
            BuildWithClock([], SettledNow);
        await SeedLegacyRowsAsync(VenueBars(12));

        BarRange window = new(SessionStart, SessionStart.AddHours(1));
        await cache.GetBarsAsync(_es, 5, window, CancellationToken.None);

        // Well past RecentEmptyTtl. A memo carrying that TTL is now expired and invisible to ExcludeCoveredAsync.
        clock.Advance(TimeSpan.FromHours(1));
        await cache.GetBarsAsync(_es, 5, window, CancellationToken.None);

        // The behaviour first, and the column that produces it second: a bound that is only ever asserted as a
        // stored value can be satisfied by a row nothing reads.
        gateway.BarRequests.Should().Be(
            1, "a permanent memo does not lapse, so the clock moving does not restart the re-ask");
        _database.BarCoverage.Should().ContainSingle().Which.ExpiresAt.Should().BeNull(
            "a range two days behind the present is settled history, and an empty answer over it is believed "
            + "permanently");
    }

    [Fact]
    public async Task APageThatOmitsSomeOfItsOwnNullBuckets_LeavesThemNull_AndSettlesAfterOneFurtherRequest()
    {
        // gh#408 part 2, as the issue frames it: a retention edge INSIDE one page. The venue answers the range
        // non-empty -- so no memo is written for the whole slice -- while omitting six of the buckets the store
        // holds as legacy nulls. Those six stay null and stay missing.
        //
        // It does NOT cost a page on every read forever, which is what this fixture was written to find out.
        // FindMissing coalesces only across EXPECTED buckets, so the second read asks for the narrowed run --
        // which contains nothing the venue will serve, is answered empty, and earns exactly the permanent memo
        // the first read could not. Two requests, then nothing. That correction is why this test exists rather
        // than an assertion of unboundedness; the shape that genuinely does not converge is the test below.
        IReadOnlyList<Bar> legacy = VenueBars(12);
        (BarCacheService cache, CountingGateway gateway) = Build(legacy.Skip(6), SettledNow);
        await SeedLegacyRowsAsync(legacy);

        BarRange window = new(SessionStart, SessionStart.AddHours(1));
        for (int read = 0; read < 4; read++)
        {
            await cache.GetBarsAsync(_es, 5, window, CancellationToken.None);
        }

        gateway.BarRequests.Should().Be(2, "the first page is answered non-empty; the narrowed second is not");

        // The provenance the venue would not restate is left absent, not invented from the neighbours.
        _database.Bars
            .Where(b => b.BucketStart < SessionStart.AddMinutes(30))
            .Should().AllSatisfy(b => b.ContractId.Should().BeNull());
        _database.Bars
            .Where(b => b.BucketStart >= SessionStart.AddMinutes(30))
            .Should().AllSatisfy(b => b.ContractId.Should().Be("CON.F.US.TEST.Z26"));
    }

    [Fact]
    public async Task AVenueBarTheCalendarDoesNotExpect_SuppressesTheMemoForTheRunAroundIt_OnEveryRead()
    {
        // gh#408 part 2, the shape that genuinely does not converge -- and it is not the retention edge the
        // issue described. A missing run coalesces ACROSS a stretch the calendar excludes, which is the saving
        // FindMissing documents. If the venue publishes so much as one bar INSIDE that excluded stretch, the
        // page holding it comes back non-empty, no memo is written for it, the expected buckets around it stay
        // missing, and the run is re-derived whole on the next read.
        //
        // MEASURED AT ITS REAL COST, not a one-page fixture's (gh#408, second review round). A range is asked
        // WHOLE -- ExcludeCoveredAsync drops it only on total containment and deliberately never splits it --
        // so the cost is the width of the coalesced run in pages, on every read, even though two of the three
        // pages here are memoised empty and only the first is not. Two days of one-minute bars is three pages,
        // so this is nine requests across three reads, not three.
        //
        // ACCEPTED, not fixed (ADR-0011, gh#408). A memo over buckets a non-empty page omitted would say "the
        // venue has nothing here" permanently, over a range the venue DID answer for -- turning a paced,
        // VenueRequests-visible traffic cost into a silently absent bar that nothing will ever go and fetch.
        // Unlike the multi-page bound above, this one needs a CALENDAR that disagrees with the venue, which is
        // a misconfiguration rather than a steady state, and the remedy is the calendar. This test pins the
        // accepted cost; if it goes red because a later change bounded it, that change is the fix landing and
        // this test moves with the ADR.
        DateTimeOffset insideMaintenance =
            MarketClock.FromMarket(new DateOnly(2026, 8, 18), new TimeOnly(16, 30)).ToUniversalTime();

        (BarCacheService cache, CountingGateway gateway) = Build(
            [new Bar(insideMaintenance, 100m, 101m, 99m, 100.5m, 1_000)], SessionStart.AddDays(5));

        BarRange window = new(SessionStart, SessionStart.AddDays(2));
        for (int read = 0; read < 3; read++)
        {
            await cache.GetBarsAsync(_es, 1, window, CancellationToken.None);
        }

        gateway.BarRequests.Should().Be(
            9,
            "one bar the calendar never expects keeps its page's answer non-empty, so the run is never wholly "
            + "covered and all three of its pages are re-asked on every read");
        _database.Bars.Should().ContainSingle(
            b => b.BucketStart == insideMaintenance, "the only bar the venue served is the unexpected one");
    }

    [Fact]
    public async Task ALegacyNullTheCalendarDoesNotExpect_IsHealed_SoTheWindowStopsReportingUnknown()
    {
        // gh#412. THE SHARP ONE: this shape fails toward a DEGRADED ANSWER, not toward vendor traffic.
        //
        // gh#402 made a bucket with no recorded contract count as not-stored, so FindMissing re-asks for it
        // and the upsert heals it. But FindMissing walks only the buckets the CALENDAR EXPECTS, so a legacy
        // null sitting off that grid is never enumerated, never asked for, and never heals.
        //
        // A row CAN sit off that grid, and it is the CONFIGURATION that makes it so, not the venue: the
        // session close and the holiday list are settings (MarketDataOptions.SessionCloseCentral, .Holidays)
        // and the write path stores what the venue answers without consulting them, so correcting a close or
        // declaring a holiday late strands rows already written. 16:30 Central is used below because this
        // calendar plainly does not expect it -- it is a CONSTRUCTED demonstration, the same bucket gh#408's
        // fixture uses, and no live store has been observed holding one.
        //
        // The cost is not a request. ToCoverage reads an unattributed run beside a single recorded one as
        // Unknown -- correctly, per ADR-0011 -- so ONE unhealable off-grid null downgrades get_key_levels and
        // get_market_snapshot to "cannot tell whether this window spans a roll" for every read, forever, on a
        // window whose bars are in fact all one contract.
        //
        // The fixture is deliberately ALMOST healed: every calendar-expected bucket on both sides of the
        // maintenance window already carries the contract, so the ONLY thing the store lacks is the off-grid
        // null. That makes the request count exact (one bucket, one request) and makes Unknown attributable
        // to that bucket alone rather than to a store that is generally cold.
        //
        // RED against the pre-fix detector: zero venue requests, the 16:30 row keeps its null, and the span
        // is Unknown.
        DateTimeOffset offGrid =
            MarketClock.FromMarket(new DateOnly(2026, 8, 18), new TimeOnly(16, 30)).ToUniversalTime();
        BarRange window = new(SessionStart, SessionStart.AddDays(1));

        // The venue holds the off-grid bar -- it published it, which is how the null got there.
        (BarCacheService cache, CountingGateway gateway) = Build(
            [new Bar(offGrid, 100m, 101m, 99m, 100.5m, 1_000)], SessionStart.AddDays(5));

        // Everything the calendar DOES expect in the window, already attributed: 09:00-15:55 on the Tuesday,
        // then 17:00 through 09:00 on the Wednesday. Nothing here is missing.
        IReadOnlyList<DateTimeOffset> expected =
            BarGapDetector.ExpectedBuckets(window, TimeSpan.FromMinutes(5), BarSessionCalendar.Parse("16:00", []));
        await SeedRowsAsync(
            expected.Select(b => new Bar(b, 100m, 101m, 99m, 100.5m, 1_000)), "CON.F.US.TEST.Z26");

        // ... and the one pre-migration row the calendar does not expect.
        await SeedRowsAsync([new Bar(offGrid, 100m, 101m, 99m, 100.5m, 1_000)], contractId: null);

        BarReadResult result = await cache.GetBarsAsync(_es, 5, window, CancellationToken.None);

        gateway.BarRequests.Should().Be(
            1,
            "the one bucket the store cannot attribute must be re-asked for, and it is the only thing the "
            + "store lacks -- so the heal costs exactly one request, not a re-fetch of the window");
        _database.Bars.Single(b => b.BucketStart == offGrid).ContractId.Should().Be(
            "CON.F.US.TEST.Z26",
            "the venue restated the bar it published, and the ordinary upsert writes the contract with the "
            + "prices -- nothing here is guessed from the neighbouring rows");
        ToolPayloads.ToCoverage(result.Bars).Span.Should().Be(
            ToolPayloads.ContractSpan.SingleContract,
            "with the null healed the window is one recorded contract end to end, which is the span the bars "
            + "actually justify -- an unhealable off-grid null pins it at Unknown forever");
    }

    [Fact]
    public async Task ANonPositiveResolution_IsRefused()
    {
        (BarCacheService cache, _) = Build([], SessionStart);

        Func<Task> read = () => cache.GetBarsAsync(
            _es, 0, new BarRange(SessionStart, SessionStart.AddHours(1)), CancellationToken.None);

        await read.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }
}
