using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// Deriving a session bar and storing it: complete, or absent with a reason, and never both.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every test here is integration-tier, and there is nowhere else for them (gh#387).</b> This is a write
/// path — one <c>ON CONFLICT … DO UPDATE</c> statement, two <c>ExecuteDelete</c> reconciles and a
/// <c>RepeatableRead</c> transaction with a single retry — and none of that is representable against the
/// in-memory provider.
/// </para>
/// <para>
/// The numbers below are <b>hand-checked</b> rather than recomputed through the aggregator: a test that
/// asserts the code does what the code does passes forever and proves nothing.
/// </para>
/// </remarks>
[Collection(SeriesStoreCollection.Name)]
public sealed class SessionBarServiceTests : IAsyncLifetime
{
    private static readonly InstrumentId _es = new("ES");

    /// <summary>A Tuesday that trades, and whose whole <c>rth</c> session is closed at <c>SettledNow</c>.</summary>
    private static readonly DateOnly _tuesday = new(2026, 8, 18);

    /// <summary>The Wednesday after it.</summary>
    private static readonly DateOnly _wednesday = new(2026, 8, 19);

    /// <summary>
    /// The regular session: 08:30–15:00 Central off 30-minute bars.
    /// </summary>
    /// <remarks>
    /// In August, Central is CDT (UTC−5), so both trade dates' windows are <c>[13:30Z, 20:00Z)</c> — six and a
    /// half hours, which is <b>13</b> half-hour buckets opening at 13:30, 14:00 … 19:30Z.
    /// </remarks>
    private static readonly SessionDefinition _rth = new("rth", new TimeOnly(8, 30), new TimeOnly(15, 0), 30);

    private readonly SeriesStoreFixture _fixture;

    private readonly TopstepXDbContext _database;

    /// <param name="fixture">The shared container.</param>
    public SessionBarServiceTests(SeriesStoreFixture fixture)
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
        return Task.CompletedTask;
    }

    /// <summary>
    /// A week after both sessions closed.
    /// </summary>
    /// <remarks>
    /// <b>More than <see cref="BarCacheService.SettledHistoryAge"/> past the window, and that is the point of
    /// the literal.</b> A range the venue answers empty is memoised permanently only once it is settled
    /// history; at a <c>now</c> inside two days those ledger rows carry a fifteen-minute TTL and the overnight
    /// gap between the two sessions is re-asked for on every single read.
    /// </remarks>
    private static DateTimeOffset SettledNow => new(2026, 8, 25, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Stores one complete session and reports the incomplete one as absent, with no row for it.
    /// </summary>
    /// <returns>The running test.</returns>
    [Fact]
    public async Task GetAsync_StoresOneBarPerCompleteTradeDate_AndListsIncompleteOnes()
    {
        string venue = ConcurrencyHarness.Venue();
        SessionBarService service = Service(
            _database, venue, [.. RthBars(_tuesday), .. RthBars(_wednesday, skip: 5)], SettledNow);

        SessionBarReadResult result =
            await service.GetAsync(_es, _rth, [_tuesday, _wednesday], CancellationToken.None);

        // Hand-checked from the fixture: bar i is O 100+i, H 105+i, L 95+i, C 101+i, V 10, thirteen of them.
        SessionBar bar = result.Bars.Should().ContainSingle().Subject;
        bar.TradeDate.Should().Be(_tuesday);
        bar.Open.Should().Be(100m, "the session opens on the 13:30Z bucket's open");
        bar.High.Should().Be(117m, "the highest of 105..117 is the last bucket's");
        bar.Low.Should().Be(95m, "the lowest of 95..107 is the first bucket's");
        bar.Close.Should().Be(113m, "the session closes on the 19:30Z bucket's close");
        bar.Volume.Should().Be(130, "thirteen buckets of ten");
        bar.BaseBucketCount.Should().Be(13);
        bar.ContractId.Should().Be(ConcurrencyHarness.ContractId);
        bar.OpenUtc.Should().Be(new DateTimeOffset(2026, 8, 18, 13, 30, 0, TimeSpan.Zero));
        bar.CloseUtc.Should().Be(new DateTimeOffset(2026, 8, 18, 20, 0, 0, TimeSpan.Zero));

        SessionBarOutcome absent = result.Absent.Should().ContainSingle().Subject;
        absent.TradeDate.Should().Be(_wednesday);
        absent.Reason.Should().Be(
            SessionBarAbsence.Incomplete, "the 16:00Z bucket is missing, so the session is not whole");
        absent.ExpectedBuckets.Should().Be(13);
        absent.MissingBuckets.Should().Be(1);

        IReadOnlyList<SessionBarRecord> rows = await StoredAsync(venue);
        SessionBarRecord row = rows.Should().ContainSingle(
            "an incomplete session is never recorded — no row, no ledger, no marker").Subject;
        row.TradeDate.Should().Be(_tuesday);
        row.Session.Should().Be("rth");
        row.WindowCentral.Should().Be("08:30-15:00");
        row.BaseResolutionMinutes.Should().Be(30);
        row.BaseBucketCount.Should().Be(13);
        row.ContractId.Should().Be(ConcurrencyHarness.ContractId);
        row.Open.Should().Be(100m);
        row.High.Should().Be(117m);
        row.Low.Should().Be(95m);
        row.Close.Should().Be(113m);
        row.Volume.Should().Be(130);
    }

    /// <summary>
    /// Costs the venue nothing once the base coverage ledger has settled, and does not rewrite the row.
    /// </summary>
    /// <returns>The running test.</returns>
    /// <remarks>
    /// <b>It takes three reads rather than two, and the middle one is why.</b> A cold store makes every
    /// expected bucket in the covering window one contiguous missing range, so the first read is a single
    /// request the venue answers <i>with bars</i> — non-empty, therefore nothing is memoised. Only the second
    /// read sees the two holes the venue has none for (the overnight between the sessions, and Wednesday's
    /// dropped 16:00Z bucket), asks for them, and records both as covered. Because <c>SettledNow</c> is past
    /// <see cref="BarCacheService.SettledHistoryAge"/> those rows never expire, so the third read is the one
    /// that genuinely costs nothing.
    /// </remarks>
    [Fact]
    public async Task GetAsync_IssuesZeroVenueRequests_OnceTheBaseLedgerHasSettled()
    {
        string venue = ConcurrencyHarness.Venue();
        SessionBarService service = Service(
            _database, venue, [.. RthBars(_tuesday), .. RthBars(_wednesday, skip: 5)], SettledNow);

        SessionBarReadResult first =
            await service.GetAsync(_es, _rth, [_tuesday, _wednesday], CancellationToken.None);
        first.FetchedBuckets.Should().Be(25, "thirteen Tuesday buckets and twelve Wednesday ones");

        DateTimeOffset recordedAt = (await StoredAsync(venue)).Should().ContainSingle().Subject.RecordedAt;

        SessionBarReadResult second =
            await service.GetAsync(_es, _rth, [_tuesday, _wednesday], CancellationToken.None);
        second.FetchedBuckets.Should().Be(0, "the venue has no bar for either hole, so nothing was written");

        SessionBarReadResult third =
            await service.GetAsync(_es, _rth, [_tuesday, _wednesday], CancellationToken.None);
        third.VenueRequests.Should().Be(
            0, "every hole is memoised permanently, so a settled read reaches the venue not at all");
        third.FetchedBuckets.Should().Be(0);
        third.Bars.Should().ContainSingle().Which.TradeDate.Should().Be(_tuesday);

        SessionBarRecord row = (await StoredAsync(venue)).Should().ContainSingle().Subject;
        row.RecordedAt.Should().Be(
            recordedAt,
            "the session bar has not changed, so the skip-unchanged clause must leave the row alone rather "
            + "than rewrite it with the same numbers under a new timestamp");
    }

    /// <summary>
    /// Removes a stored session bar once a base revision makes the session span a roll.
    /// </summary>
    /// <returns>The running test.</returns>
    [Fact]
    public async Task GetAsync_RemovesASessionBar_WhenABaseRevisionMakesItSpanARoll()
    {
        string venue = ConcurrencyHarness.Venue();
        SessionBarService service = Service(_database, venue, RthBars(_tuesday), SettledNow);

        await service.GetAsync(_es, _rth, [_tuesday], CancellationToken.None);
        (await StoredAsync(venue)).Should().ContainSingle();

        // WRITTEN, not fetched, and that is not a shortcut. The cache never re-asks for a bucket it already
        // holds -- FindMissing breaks its runs at every stored bucket -- so a roll landing on bars the store
        // already has is unreachable through a second fill. Writing the store is what a later fill against a
        // rolled contract, or a rebuild, actually does to those rows.
        await using (TopstepXDbContext other = _fixture.CreateContext())
        {
            await other.Database.ExecuteSqlRawAsync(
                @"UPDATE ""Bars"" SET ""ContractId"" = @next
                  WHERE ""Venue"" = @venue AND ""Instrument"" = 'ES' AND ""ResolutionMinutes"" = 30
                    AND ""BucketStart"" >= @from",
                new NpgsqlParameter("next", ConcurrencyHarness.NextContractId),
                new NpgsqlParameter("venue", venue),
                new NpgsqlParameter("from", new DateTimeOffset(2026, 8, 18, 17, 0, 0, TimeSpan.Zero)));
        }

        SessionBarReadResult result = await service.GetAsync(_es, _rth, [_tuesday], CancellationToken.None);

        result.Bars.Should().BeEmpty();
        SessionBarOutcome absent = result.Absent.Should().ContainSingle().Subject;
        absent.Reason.Should().Be(SessionBarAbsence.SpansRoll);
        absent.ExpectedBuckets.Should().Be(13);
        absent.MissingBuckets.Should().Be(0, "a spliced session is missing nothing; it is unusable");

        (await StoredAsync(venue)).Should().BeEmpty(
            "the row was derived from bars that no longer support it, and a stale session bar spanning a roll "
            + "is a bookkeeping event wearing an ordinary face");
    }

    /// <summary>
    /// Discards a row built under a definition that no longer holds, on either half of the provenance pair.
    /// </summary>
    /// <returns>The running test.</returns>
    [Fact]
    public async Task GetAsync_DiscardsRowsBuiltUnderADifferentDefinition()
    {
        // THE BASE RESOLUTION. The same window off hourly bars and then off half-hourly ones: a Bar carries
        // no size, so nothing but this row could tell the two apart.
        string baseVenue = ConcurrencyHarness.Venue();
        SessionDefinition hourly = new("full", new TimeOnly(17, 0), new TimeOnly(16, 0), 60);
        SessionDefinition halfHourly = hourly with { BaseResolutionMinutes = 30 };

        await Service(_database, baseVenue, FullSessionBars(60), SettledNow)
            .GetAsync(_es, hourly, [_tuesday], CancellationToken.None);
        (await StoredAsync(baseVenue)).Should().ContainSingle()
            .Which.BaseBucketCount.Should().Be(23, "Mon 22:00Z to Tue 21:00Z is twenty-three hourly buckets");

        await Service(_database, baseVenue, FullSessionBars(30), SettledNow)
            .GetAsync(_es, halfHourly, [_tuesday], CancellationToken.None);

        SessionBarRecord rebased = (await StoredAsync(baseVenue)).Should().ContainSingle(
            "the row built at sixty minutes was discarded, not left beside the new one under a key that "
            + "cannot tell them apart").Subject;
        rebased.BaseResolutionMinutes.Should().Be(30);
        rebased.BaseBucketCount.Should().Be(46);

        // THE WINDOW. The same base resolution, an earlier close.
        string windowVenue = ConcurrencyHarness.Venue();
        SessionDefinition shorter = _rth with { EndCentral = new TimeOnly(14, 30) };

        await Service(_database, windowVenue, RthBars(_tuesday), SettledNow)
            .GetAsync(_es, _rth, [_tuesday], CancellationToken.None);
        (await StoredAsync(windowVenue)).Should().ContainSingle()
            .Which.WindowCentral.Should().Be("08:30-15:00");

        await Service(_database, windowVenue, RthBars(_tuesday), SettledNow)
            .GetAsync(_es, shorter, [_tuesday], CancellationToken.None);

        SessionBarRecord rewindowed = (await StoredAsync(windowVenue)).Should().ContainSingle().Subject;
        rewindowed.WindowCentral.Should().Be("08:30-14:30");
        rewindowed.BaseBucketCount.Should().Be(12);
        rewindowed.CloseUtc.Should().Be(new DateTimeOffset(2026, 8, 18, 19, 30, 0, TimeSpan.Zero));
    }

    /// <summary>
    /// Never records a session that has not closed, however complete the bars standing in it look.
    /// </summary>
    /// <returns>The running test.</returns>
    [Fact]
    public async Task GetAsync_NeverStoresASessionThatHasNotClosed()
    {
        // Wednesday 12:00 Central, which is the middle of Wednesday's rth session.
        DateTimeOffset midSession = new(2026, 8, 19, 17, 0, 0, TimeSpan.Zero);

        string venue = ConcurrencyHarness.Venue();
        SessionBarService service = Service(
            _database, venue, [.. RthBars(_tuesday), .. RthBars(_wednesday)], midSession);

        SessionBarReadResult result =
            await service.GetAsync(_es, _rth, [_tuesday, _wednesday], CancellationToken.None);

        result.Bars.Should().ContainSingle().Which.TradeDate.Should().Be(_tuesday);

        SessionBarOutcome absent = result.Absent.Should().ContainSingle().Subject;
        absent.TradeDate.Should().Be(_wednesday);
        absent.Reason.Should().Be(SessionBarAbsence.NotClosed);
        absent.ExpectedBuckets.Should().Be(0, "nothing was expected of a session that has not happened yet");
        absent.MissingBuckets.Should().Be(0);

        IReadOnlyList<SessionBarRecord> rows = await StoredAsync(venue);
        rows.Should().ContainSingle().Which.TradeDate.Should().Be(
            _tuesday, "an open session is never stored, not even as a partial row nobody promised to revise");
    }

    /// <summary>
    /// Two reads deriving the same session converge on one row, through the single serialization retry.
    /// </summary>
    /// <returns>The running test.</returns>
    /// <remarks>
    /// The base series is warmed first so neither read reaches the venue, and the stored session row is
    /// deleted through a second context so both reads genuinely have to write it.
    /// </remarks>
    [Fact]
    public async Task TwoConcurrentReads_ConvergeWithOneRetry()
    {
        string venue = ConcurrencyHarness.Venue();

        await Service(_database, venue, RthBars(_tuesday), SettledNow)
            .GetAsync(_es, _rth, [_tuesday], CancellationToken.None);

        await using (TopstepXDbContext seed = _fixture.CreateContext())
        {
            await seed.Database.ExecuteSqlRawAsync(
                @"DELETE FROM ""SessionBars"" WHERE ""Venue"" = @venue",
                new NpgsqlParameter("venue", venue));
        }

        await using TopstepXDbContext otherStore = _fixture.CreateContext();

        async Task DeriveTheSameSessionAndCommit() =>
            await Service(otherStore, venue, RthBars(_tuesday), SettledNow)
                .GetAsync(_es, _rth, [_tuesday], CancellationToken.None);

        // AFTER the pre-read, and matched on a column only it selects. The transaction's snapshot is taken by
        // the discard at (5a), which is a DELETE and so cannot be matched -- the interceptor watches SELECT
        // only. The pre-read is the first SELECT naming "WindowCentral", and it sits exactly between the
        // snapshot and the upsert, which is where the other read has to commit for the two to collide.
        InterleavingInterceptor straddle = InterleavingInterceptor.After(
            "\"WindowCentral\"", venue, DeriveTheSameSessionAndCommit);
        CapturingLogger<SessionBarService> log = new();
        await using TopstepXDbContext store = _fixture.CreateContext(straddle);

        SessionBarService read = Service(store, venue, RthBars(_tuesday), SettledNow, log);

        Func<Task> derive = () => read.GetAsync(_es, _rth, [_tuesday], CancellationToken.None);

        await derive.Should().NotThrowAsync();

        straddle.Fired.Should().BeTrue(
            "the interleaving is the test -- if the other read never ran between the snapshot and the upsert, "
            + "this passed by not exercising anything");
        log.Messages.Should().ContainMatch(
            "*serialization failure*",
            "the upsert has to have met the other read's row, not merely followed it -- a retry leaves no "
            + "other trace, so the outcome alone would pass against a run where nothing collided");

        SessionBarRecord row = (await StoredAsync(venue)).Should().ContainSingle().Subject;
        row.TradeDate.Should().Be(_tuesday);
        row.Open.Should().Be(100m);
        row.High.Should().Be(117m);
        row.Low.Should().Be(95m);
        row.Close.Should().Be(113m);
        row.Volume.Should().Be(130);
        row.BaseBucketCount.Should().Be(13);
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
    /// Bar <c>i</c> is <c>O 100+i, H 105+i, L 95+i, C 101+i, V 10</c> — a plain ramp, because the assertions
    /// on the aggregate are hand-computed and a reader has to be able to check them.
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

    /// <summary>
    /// Tuesday's whole <c>full</c> session — Mon 22:00Z to Tue 21:00Z — at a chosen base resolution.
    /// </summary>
    /// <param name="resolutionMinutes">The base bar size, which must divide the 23-hour session.</param>
    /// <returns>The bars, ascending.</returns>
    private static IReadOnlyList<Bar> FullSessionBars(int resolutionMinutes) =>
    [
        .. Enumerable.Range(0, 23 * 60 / resolutionMinutes)
            .Select(i => new Bar(
                new DateTimeOffset(2026, 8, 17, 22, 0, 0, TimeSpan.Zero).AddMinutes(resolutionMinutes * i),
                100m + i,
                105m + i,
                95m + i,
                101m + i,
                10,
                ConcurrencyHarness.ContractId)),
    ];

    /// <summary>The service under test, over one context and one private venue.</summary>
    /// <param name="database">The store.</param>
    /// <param name="venue">The private venue id this test owns.</param>
    /// <param name="available">The base bars the venue is willing to serve.</param>
    /// <param name="now">The instant the read runs at.</param>
    /// <param name="logger">A logger, when the test needs to read what the read said it did.</param>
    /// <returns>The service.</returns>
    private static SessionBarService Service(
        TopstepXDbContext database,
        string venue,
        IEnumerable<Bar> available,
        DateTimeOffset now,
        ILogger<SessionBarService>? logger = null)
    {
        SeriesGateway gateway = new(venue, available);
        FakeTimeProvider clock = new(now);
        BarCacheService bars = new(
            database,
            gateway,
            ConcurrencyHarness.Calendar(),
            ConcurrencyHarness.Projector(database),
            clock,
            NullLogger<BarCacheService>.Instance);

        return new SessionBarService(
            database,
            bars,
            gateway,
            ConcurrencyHarness.Calendar(),
            clock,
            logger ?? NullLogger<SessionBarService>.Instance);
    }

    /// <summary>
    /// The session bars one venue holds, read through a second context.
    /// </summary>
    /// <param name="venue">The private venue id.</param>
    /// <returns>The rows, ascending by trade date.</returns>
    /// <remarks>
    /// A second context, because the write is raw SQL the change tracker never sees: re-reading through the
    /// context that ran it would hand back whatever the identity map is holding.
    /// </remarks>
    private async Task<IReadOnlyList<SessionBarRecord>> StoredAsync(string venue)
    {
        await using TopstepXDbContext verify = _fixture.CreateContext();
        return await verify.SessionBars
            .AsNoTracking()
            .Where(s => s.Venue == venue)
            .OrderBy(s => s.TradeDate)
            .ToListAsync();
    }
}
