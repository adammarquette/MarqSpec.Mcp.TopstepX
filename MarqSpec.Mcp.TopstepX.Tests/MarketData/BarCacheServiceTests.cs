using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
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
    /// Seeds the store with pre-migration rows: the right numbers, and no recorded contract.
    /// </summary>
    /// <param name="bars">The bars to seed, stripped of their provenance.</param>
    /// <remarks>
    /// This is what a row written before migration <c>20260823074908_AddBarContractId</c> looks like — present,
    /// numerically correct, and therefore never "missing" until gh#402 taught the read path otherwise.
    /// </remarks>
    private async Task SeedLegacyRowsAsync(IEnumerable<Bar> bars)
    {
        foreach (Bar bar in bars)
        {
            _database.Bars.Add(new BarRecord
            {
                Venue = "test",
                Instrument = _es.Symbol,
                ResolutionMinutes = 5,
                BucketStart = bar.OpenTime,
                Open = bar.Open,
                High = bar.High,
                Low = bar.Low,
                Close = bar.Close,
                Volume = bar.Volume,
                ContractId = null,
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
        // issue described. A missing run coalesces ACROSS a stretch the calendar excludes: here 15:00 and
        // 17:00 Central either side of the 16:00-17:00 maintenance window are one range, which is the
        // coalescing FindMissing documents as a saving. If the venue publishes so much as one bar INSIDE that
        // excluded stretch, every fetch of the run comes back non-empty, no memo is ever written, and the
        // expected buckets stay missing -- so the run costs one paced page on every read, forever.
        //
        // ACCEPTED, not fixed (ADR-0011, gh#408). A memo over buckets a non-empty page omitted would say "the
        // venue has nothing here" permanently, over a range the venue DID answer for -- turning a bounded,
        // visible traffic cost into a silently absent bar that nothing will ever go and fetch. This test pins
        // the accepted cost; if it goes red because a later change bounded it, that change is the fix landing
        // and this test moves with the ADR.
        DateTimeOffset afternoon =
            MarketClock.FromMarket(new DateOnly(2026, 8, 18), new TimeOnly(15, 0)).ToUniversalTime();
        DateTimeOffset insideMaintenance =
            MarketClock.FromMarket(new DateOnly(2026, 8, 18), new TimeOnly(16, 30)).ToUniversalTime();

        (BarCacheService cache, CountingGateway gateway) = Build(
            [new Bar(insideMaintenance, 100m, 101m, 99m, 100.5m, 1_000)], SettledNow);

        BarRange window = new(afternoon, afternoon.AddHours(3));
        for (int read = 0; read < 3; read++)
        {
            await cache.GetBarsAsync(_es, 5, window, CancellationToken.None);
        }

        gateway.BarRequests.Should().Be(
            3,
            "one bar the calendar never expects keeps the whole coalesced run's answer non-empty, so no memo "
            + "is written and the run is re-asked on every read");
        _database.BarCoverage.Should().BeEmpty();
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
