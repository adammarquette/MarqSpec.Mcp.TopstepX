using System.Globalization;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>The outcome of a cache-aside read.</summary>
/// <param name="Bars">The bars, ascending.</param>
/// <param name="FetchedBuckets">
/// How many buckets this call wrote or revised from the venue's answer. Reported rather than logged so that
/// a test, and a caller, can actually observe what a question cost.
/// <para>
/// <b>Zero no longer proves the read touched no venue.</b> It did before the serialization retry: a second
/// attempt re-derives against the winner's committed state, so the buckets it would have written are already
/// there and it writes none — after a real fetch. <see cref="VenueRequests"/> is the exact test for "served
/// entirely from the store", and it stays truthful on that path.
/// </para>
/// </param>
/// <param name="VenueRequests">
/// How many requests were issued to the venue. Zero is the precise statement that nothing was fetched.
/// </param>
public sealed record BarReadResult(
    IReadOnlyList<Bar> Bars,
    int FetchedBuckets,
    int VenueRequests);

/// <summary>
/// Serves bars from the store, reaching the venue only for what is genuinely missing (ADR-0005).
/// </summary>
/// <remarks>
/// <para>
/// "Genuinely missing" is the entire difficulty. A dense clock grid reports every weekend, every overnight
/// maintenance window and every holiday as a gap — for a 24×5 product, roughly a quarter of all clock time —
/// so a cache built on that difference asks the vendor for the weekend on every call, gets an empty answer,
/// concludes nothing, and asks again.
/// </para>
/// <para>
/// Two mechanisms make it terminate. <see cref="BarSessionCalendar"/> decides which buckets the venue was
/// <i>expected</i> to publish. <see cref="BarCoverageRecord"/> records ranges the venue answered <b>empty</b>,
/// which is the third state between "expected and present" and "expected and not yet fetched".
/// </para>
/// </remarks>
public sealed class BarCacheService
{
    /// <summary>
    /// The largest number of bars one venue request may ask for.
    /// </summary>
    /// <remarks>
    /// The gateway caps a history call here and <b>truncates beyond it silently</b> — a caller receiving
    /// exactly this many bars for a wider window cannot tell a complete answer from a clipped one. So the
    /// paging is done here rather than trusted to the venue.
    /// </remarks>
    public const int VenuePageSizeBars = 1_000;

    /// <summary>
    /// How long a range answered empty is believed when it sits near the present.
    /// </summary>
    /// <remarks>
    /// Short, because a bucket empty only for not having printed yet <i>will</i> print, and a permanent claim
    /// would blind the cache to it.
    /// </remarks>
    public static readonly TimeSpan RecentEmptyTtl = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How far back a range must sit before an empty answer is believed permanently.
    /// </summary>
    /// <remarks>
    /// Beyond this, a hole is not going to fill in, and re-asking costs a venue request per call forever.
    /// </remarks>
    public static readonly TimeSpan SettledHistoryAge = TimeSpan.FromDays(2);

    private readonly TopstepXDbContext _database;
    private readonly IMarketDataGateway _gateway;
    private readonly BarSessionCalendar _calendar;
    private readonly IndicatorProjector _projector;
    private readonly TimeProvider _clock;
    private readonly ILogger<BarCacheService> _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="database">The store.</param>
    /// <param name="gateway">The venue.</param>
    /// <param name="calendar">The session calendar deciding which buckets are expected.</param>
    /// <param name="projector">The indicator projection, run in the same unit of work as a bar write.</param>
    /// <param name="clock">The clock. Injected so a test can place "now" precisely against a session.</param>
    /// <param name="logger">The logger.</param>
    public BarCacheService(
        TopstepXDbContext database,
        IMarketDataGateway gateway,
        BarSessionCalendar calendar,
        IndicatorProjector projector,
        TimeProvider clock,
        ILogger<BarCacheService> logger)
    {
        _database = database;
        _gateway = gateway;
        _calendar = calendar;
        _projector = projector;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Reads bars for a window, fetching only what the store genuinely lacks.
    /// </summary>
    /// <param name="instrument">The instrument.</param>
    /// <param name="resolutionMinutes">The bar size in minutes.</param>
    /// <param name="window">The window.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The bars, and what the read cost.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The resolution is not positive.</exception>
    /// <exception cref="VenueException">The venue could not be resolved or could not answer.</exception>
    public async Task<BarReadResult> GetBarsAsync(
        InstrumentId instrument,
        int resolutionMinutes,
        BarRange window,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (resolutionMinutes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(resolutionMinutes), resolutionMinutes, "A resolution must be positive.");
        }

        TimeSpan barSize = TimeSpan.FromMinutes(resolutionMinutes);
        DateTimeOffset now = _clock.GetUtcNow();
        string venue = _gateway.VenueId;

        // 1. What the store already holds.
        List<DateTimeOffset> storedBuckets = await _database.Bars
            .Where(b => b.Venue == venue
                && b.Instrument == instrument.Symbol
                && b.ResolutionMinutes == resolutionMinutes
                && b.BucketStart >= window.Start
                && b.BucketStart < window.End)
            .Select(b => b.BucketStart)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // 2 & 3. Which buckets the venue owed us, minus what we have.
        IReadOnlyList<BarRange> missing =
            BarGapDetector.FindMissing(storedBuckets, window, barSize, _calendar);

        // 4. Ranges the venue has already told us are empty are not missing, they are answered.
        IReadOnlyList<BarRange> outstanding = await ExcludeCoveredAsync(
            venue, instrument, resolutionMinutes, missing, now, cancellationToken).ConfigureAwait(false);

        int fetched = 0;
        int requests = 0;

        if (outstanding.Count > 0)
        {
            // THE VENUE IS CALLED FIRST, AND OUTSIDE THE TRANSACTION.
            //
            // The pacer sits inside the gateway page loop, so a cold year of five-minute bars is 106 pages at
            // 50-per-30-seconds -- roughly a minute of deliberate sleeping. Holding a RepeatableRead snapshot
            // across that would pin the transaction xmin, and therefore vacuum's horizon, for the whole
            // minute, and would widen every serialization window on this path from milliseconds to a minute.
            // It also makes the retry below free: a second attempt re-reads the store and re-derives
            // everything, but re-fetches nothing.
            //
            // The whole answer is held in memory before any of it is written. A cold year is on the order of
            // 70,000 small records per instrument, which is comfortable; if that ever stops being true the
            // answer is to fetch and apply in bounded chunks, not to put the network back inside the snapshot.
            (IReadOnlyList<FetchedSlice> slices, requests) = await FetchAsync(
                instrument, barSize, outstanding, now, cancellationToken).ConfigureAwait(false);

            fetched = await SeriesUnitOfWork.RunAsync(
                _database,
                instrument.Symbol + " " + resolutionMinutes.ToString(CultureInfo.InvariantCulture) + "m",
                async token =>
                {
                    // One transaction spanning the bar write AND the projection over it. They must land
                    // together: a committed bar whose indicators are missing reads back as a market that
                    // produced no signal, which is indistinguishable from a real absence of one.
                    int written = await ApplyAsync(
                        venue, instrument, resolutionMinutes, slices, now, token).ConfigureAwait(false);

                    // Saved BEFORE projecting, and this ordering is load-bearing. The projector reads the
                    // series back with a query, and a query does not see rows that are only tracked -- so
                    // projecting first silently produced no indicators at all, with no error anywhere.
                    if (_database.ChangeTracker.HasChanges())
                    {
                        await _database.SaveChangesAsync(token).ConfigureAwait(false);
                    }

                    if (written > 0)
                    {
                        await _projector
                            .ProjectAsync(venue, instrument, resolutionMinutes, now, token)
                            .ConfigureAwait(false);

                        if (_database.ChangeTracker.HasChanges())
                        {
                            await _database.SaveChangesAsync(token).ConfigureAwait(false);
                        }
                    }

                    return written;
                },
                _logger,
                cancellationToken).ConfigureAwait(false);
        }

        // AsNoTracking for the same reason the overlap pre-read is, and this is the read that comment names.
        // The bars are written by SQL the change tracker never sees, so a tracked row here is a copy the
        // identity map will hand back to the NEXT call in this scope in preference to the row it just read --
        // and both this service and the context are scoped, with get_market_snapshot deliberately making two
        // overlapping bar reads per resolution. The write went through the tracker under the in-memory merge,
        // which is why this was safe before and is not now.
        //
        // Safe to drop tracking: nothing mutates a BarRecord downstream -- ToBar is a pure mapping -- and
        // IndicatorProjector's read of the same table already does exactly this.
        List<BarRecord> rows = await _database.Bars
            .AsNoTracking()
            .Where(b => b.Venue == venue
                && b.Instrument == instrument.Symbol
                && b.ResolutionMinutes == resolutionMinutes
                && b.BucketStart >= window.Start
                && b.BucketStart < window.End)
            .OrderBy(b => b.BucketStart)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new BarReadResult([.. rows.Select(IndicatorProjector.ToBar)], fetched, requests);
    }

    private async Task<IReadOnlyList<BarRange>> ExcludeCoveredAsync(
        string venue,
        InstrumentId instrument,
        int resolutionMinutes,
        IReadOnlyList<BarRange> missing,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (missing.Count == 0)
        {
            return missing;
        }

        // AsNoTracking, for the reason the reads of Bars are (gh#103): on a relational store the ledger is
        // written by SQL the change tracker never sees, so a tracked row here is a copy the identity map
        // would hand back to the NEXT call in this scope in preference to the row that call just read. The
        // context and the service are both scoped, and a refresh moves RecordedAt and ExpiresAt -- the two
        // columns this read exists to judge.
        List<BarCoverageRecord> covered = await _database.BarCoverage
            .AsNoTracking()
            .Where(c => c.Venue == venue
                && c.Instrument == instrument.Symbol
                && c.ResolutionMinutes == resolutionMinutes
                && (c.ExpiresAt == null || c.ExpiresAt > now))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (covered.Count == 0)
        {
            return missing;
        }

        // A range is dropped only when a single coverage row contains it whole. Partial containment is left
        // alone deliberately: splitting a range around a covered sub-range would produce a swarm of tiny
        // fetches, and re-asking for a slightly wider window is the cheaper error.
        List<BarRange> outstanding = [];
        foreach (BarRange range in missing)
        {
            bool answered = covered.Any(c => c.RangeStart <= range.Start && c.RangeEnd >= range.End);
            if (!answered)
            {
                outstanding.Add(range);
            }
        }

        return outstanding;
    }

    /// <summary>One venue answer, held until the transaction that will store it opens.</summary>
    /// <param name="Slice">The range that was asked for.</param>
    /// <param name="Closed">The closed bars it answered with. Empty means the venue has none for the range.</param>
    private sealed record FetchedSlice(BarRange Slice, IReadOnlyList<Bar> Closed);

    /// <summary>
    /// Asks the venue for every outstanding range, and touches no database at all.
    /// </summary>
    /// <remarks>
    /// Separated from the write so the paced page-walk happens outside the transaction, and so a retry of the
    /// write costs no vendor requests. A venue failure here also leaves nothing half-written, because nothing
    /// has been written yet.
    /// </remarks>
    private async Task<(IReadOnlyList<FetchedSlice> Slices, int Requests)> FetchAsync(
        InstrumentId instrument,
        TimeSpan barSize,
        IReadOnlyList<BarRange> ranges,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<VenueContract> contracts =
            await _gateway.ResolveContractsAsync(instrument, cancellationToken).ConfigureAwait(false);

        if (contracts.Count == 0)
        {
            // An empty contract list is also exactly what the WRONG DATA TIER looks like on this gateway --
            // it returns an empty universe rather than an error. Saying so here is the difference between a
            // five-minute fix and an afternoon.
            throw new VenueException(
                "The venue returned no contracts for '" + instrument.Symbol
                + "'. If this instrument is definitely listed, check ProjectX__DataTier: the wrong "
                + "market-data tier returns an empty universe rather than an error.");
        }

        VenueContract contract = contracts[0];
        List<FetchedSlice> slices = [];
        int requests = 0;

        foreach (BarRange range in ranges)
        {
            // The venue caps a call at VenuePageSizeBars and truncates past it silently, so walk the range in
            // pages rather than asking for the whole thing and trusting the answer.
            TimeSpan page = TimeSpan.FromTicks(VenuePageSizeBars * barSize.Ticks);

            // CLAMPED BEFORE THE ADD, and stepped by the clamped end rather than by a whole page. Computing
            // `from + page` first and trimming it afterwards overflows for a range that ends within one page
            // of the end of the calendar -- a page is 1,000 bar spans, so at 60-minute bars it is forty-two
            // days -- and left the tool boundary as a raw ArgumentOutOfRangeException for a range four hours
            // long (gh#110). `from = to` closes the same overflow on the increment. The subtraction is total:
            // the difference between two DateTimeOffsets always fits a TimeSpan.
            for (DateTimeOffset from = range.Start; from < range.End;)
            {
                DateTimeOffset to = range.End - from <= page ? range.End : from + page;
                BarRange slice = new(from, to);
                IReadOnlyList<Bar> bars = await _gateway
                    .GetBarsAsync(contract.ContractId, slice, barSize, cancellationToken)
                    .ConfigureAwait(false);
                requests++;

                // The gateway stamps the provenance at its mapping, because a history call is made against
                // exactly one contract and that is where the fact is structurally in hand (ADR-0011). This
                // does NOT re-stamp it -- silently overwriting would make a gateway that forgot look
                // identical to one that did not, and a bar with no provenance PASSES the roll guard. So the
                // omission is made loud here instead, at the last point before it reaches the store.
                if (bars.Any(b => string.IsNullOrWhiteSpace(b.ContractId)))
                {
                    throw new VenueException(
                        "The venue returned bars with no contract id for '" + instrument.Symbol
                        + "'. A history call is made against one contract, so every bar it answers with must "
                        + "carry that contract: without it a quarterly roll splices two contracts into one "
                        + "series with nothing marking the seam. This is a defect in the gateway "
                        + "implementation, not a venue condition.");
                }

                // Drop still-forming bars even though the request already asks the venue not to send them.
                // A half-formed bar stored as final is indistinguishable from data and corrupts everything
                // derived from it -- this must not depend on a venue behaving. Written as a subtraction
                // rather than `b.OpenTime + barSize <= now` for the same reason the page walk above is:
                // exactly equivalent, and total for a bar the venue placed at the end of the calendar.
                slices.Add(new FetchedSlice(slice, [.. bars.Where(b => now - b.OpenTime >= barSize)]));
                from = to;
            }
        }

        return (slices, requests);
    }

    /// <summary>
    /// Writes what the venue answered. Runs inside the transaction, and in full again if it is retried.
    /// </summary>
    /// <returns>How many buckets were written or revised.</returns>
    private async Task<int> ApplyAsync(
        string venue,
        InstrumentId instrument,
        int resolutionMinutes,
        IReadOnlyList<FetchedSlice> slices,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        int fetched = 0;

        foreach (FetchedSlice slice in slices)
        {
            if (slice.Closed.Count == 0)
            {
                await RecordEmptyAsync(
                    venue, instrument, resolutionMinutes, slice.Slice, now, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            fetched += await UpsertAsync(
                venue, instrument, resolutionMinutes, slice.Closed, now, cancellationToken)
                .ConfigureAwait(false);
        }

        return fetched;
    }

    /// <summary>
    /// Writes one venue answer, revising the buckets already stored.
    /// </summary>
    /// <returns>How many buckets were written or revised.</returns>
    /// <remarks>
    /// Two implementations because the choice between an insert and an update is a fact about the
    /// <b>store</b>, not about this process (gh#103) — and the in-memory provider the unit tier runs on has
    /// no <c>ON CONFLICT</c> to leave it to.
    /// </remarks>
    private Task<int> UpsertAsync(
        string venue,
        InstrumentId instrument,
        int resolutionMinutes,
        IReadOnlyList<Bar> bars,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        _database.Database.IsRelational()
            ? UpsertInStoreAsync(venue, instrument, resolutionMinutes, bars, now, cancellationToken)
            : UpsertInMemoryAsync(venue, instrument, resolutionMinutes, bars, now, cancellationToken);

    /// <summary>
    /// The bar write, as one statement the store resolves against the row it has committed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The conflict target is the composite primary key</b>, which is the idempotence guard the data
    /// dictionary names — reached here directly instead of being inferred from a read of it.
    /// </para>
    /// <para>
    /// <b>The <c>WHERE</c> is the skip-unchanged rule</b>, and it is stated here rather than in C# because
    /// this is the only place both sides of the comparison are the column's own type. <c>excluded</c> is the
    /// row proposed for insertion, so its prices have already been coerced to <c>numeric(18,8)</c> — a
    /// value at full <see cref="decimal"/> precision compared against a stored one is the shape that made
    /// the projection's identical guard dead code for a whole phase (gh#37).
    /// </para>
    /// <para>
    /// <b>Arrays rather than a row per bar</b>: a page is up to <see cref="VenuePageSizeBars"/> bars, and
    /// eleven parameters each would approach the protocol's parameter limit for no benefit.
    /// </para>
    /// </remarks>
    private const string UpsertBarsSql = """
        INSERT INTO "Bars" (
            "Venue", "Instrument", "ResolutionMinutes", "BucketStart",
            "Open", "High", "Low", "Close", "Volume", "ContractId", "RecordedAt")
        SELECT @venue, @instrument, @resolution, a.bucket,
               a.open_price, a.high_price, a.low_price, a.close_price, a.volume, a.contract, @recorded
        FROM unnest(@buckets, @opens, @highs, @lows, @closes, @volumes, @contracts)
             AS a(bucket, open_price, high_price, low_price, close_price, volume, contract)
        ON CONFLICT ("Venue", "Instrument", "ResolutionMinutes", "BucketStart") DO UPDATE SET
            "Open" = excluded."Open",
            "High" = excluded."High",
            "Low" = excluded."Low",
            "Close" = excluded."Close",
            "Volume" = excluded."Volume",
            "ContractId" = excluded."ContractId",
            "RecordedAt" = excluded."RecordedAt"
        WHERE ("Bars"."Open", "Bars"."High", "Bars"."Low", "Bars"."Close", "Bars"."Volume", "Bars"."ContractId")
              IS DISTINCT FROM
              (excluded."Open", excluded."High", excluded."Low", excluded."Close", excluded."Volume",
               excluded."ContractId")
        """;

    /// <summary>Whether a stored row already holds exactly what the venue has just answered with.</summary>
    /// <param name="row">The stored row.</param>
    /// <param name="bar">The bar the venue answered with.</param>
    /// <returns><see langword="true"/> when writing it again would change nothing.</returns>
    private static bool Unchanged(BarRecord row, Bar bar) =>
        row.Open == bar.Open
        && row.High == bar.High
        && row.Low == bar.Low
        && row.Close == bar.Close
        && row.Volume == bar.Volume
        && string.Equals(row.ContractId, bar.ContractId, StringComparison.Ordinal);

    /// <summary>The stored rows a venue answer overlaps.</summary>
    /// <param name="venue">The venue.</param>
    /// <param name="instrument">The instrument.</param>
    /// <param name="resolutionMinutes">The bar size in minutes.</param>
    /// <param name="first">The first bucket the answer covers.</param>
    /// <param name="last">The last bucket the answer covers, inclusive.</param>
    /// <returns>The query.</returns>
    private IQueryable<BarRecord> Overlap(
        string venue,
        InstrumentId instrument,
        int resolutionMinutes,
        DateTimeOffset first,
        DateTimeOffset last) =>
        _database.Bars
            .Where(b => b.Venue == venue
                && b.Instrument == instrument.Symbol
                && b.ResolutionMinutes == resolutionMinutes
                && b.BucketStart >= first
                && b.BucketStart <= last);

    private async Task<int> UpsertInStoreAsync(
        string venue,
        InstrumentId instrument,
        int resolutionMinutes,
        IReadOnlyList<Bar> bars,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // THE PRE-READ SURVIVES, DEMOTED FROM GUARD TO PRE-FILTER (gh#103).
        //
        // It used to decide insert-versus-update, and that is the one decision it cannot make: it is read
        // from this transaction's snapshot, so a concurrent fill of an overlapping range commits a bucket
        // this one still believes absent, both INSERT it, and the loser aborts with 23505 -- taking the
        // coverage ledger and the projection over the same series down with it, and reaching `get_bars` as a
        // store fault. The decision now belongs to the statement below, which makes it against the row the
        // store has actually committed.
        //
        // It is kept because it still SAVES A WRITE, which is the test that decides such a read. The venue
        // restates bars after the fact, so an answer overlapping settled history is mostly buckets that have
        // not moved -- and one filtered out here is never sent, never index-probed and never row-locked.
        //
        // AsNoTracking, and that is not tidiness: these rows are written by SQL the change tracker never
        // sees, so a tracked copy would be a stale entity the identity map hands back to the next tracking
        // query over Bars -- which is the read that answers this very call.
        Dictionary<DateTimeOffset, BarRecord> existing =
            await Overlap(venue, instrument, resolutionMinutes, bars[0].OpenTime, bars[^1].OpenTime)
                .AsNoTracking()
                .ToDictionaryAsync(b => b.BucketStart, cancellationToken)
                .ConfigureAwait(false);

        List<Bar> pending =
        [
            .. bars.Where(bar =>
                !existing.TryGetValue(bar.OpenTime, out BarRecord? row) || !Unchanged(row, bar)),
        ];

        if (pending.Count == 0)
        {
            return 0;
        }

        // The contract moves WITH the prices, never on its own. Both come out of the same venue answer, so a
        // row always says which contract produced the numbers standing in it -- writing one without the other
        // would leave a row whose provenance describes a different observation from the one it holds, which
        // is worse than no provenance at all.
        NpgsqlParameter[] parameters =
        [
            new("venue", NpgsqlDbType.Varchar) { Value = venue },
            new("instrument", NpgsqlDbType.Varchar) { Value = instrument.Symbol },
            new("resolution", NpgsqlDbType.Integer) { Value = resolutionMinutes },
            new("recorded", NpgsqlDbType.TimestampTz) { Value = now },
            new("buckets", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz)
            {
                Value = pending.Select(b => b.OpenTime).ToArray(),
            },
            new("opens", NpgsqlDbType.Array | NpgsqlDbType.Numeric)
            {
                Value = pending.Select(b => b.Open).ToArray(),
            },
            new("highs", NpgsqlDbType.Array | NpgsqlDbType.Numeric)
            {
                Value = pending.Select(b => b.High).ToArray(),
            },
            new("lows", NpgsqlDbType.Array | NpgsqlDbType.Numeric)
            {
                Value = pending.Select(b => b.Low).ToArray(),
            },
            new("closes", NpgsqlDbType.Array | NpgsqlDbType.Numeric)
            {
                Value = pending.Select(b => b.Close).ToArray(),
            },
            new("volumes", NpgsqlDbType.Array | NpgsqlDbType.Bigint)
            {
                Value = pending.Select(b => b.Volume).ToArray(),
            },
            new("contracts", NpgsqlDbType.Array | NpgsqlDbType.Varchar)
            {
                Value = pending.Select(b => b.ContractId).ToArray(),
            },
        ];

        // The count the store reports, not the count this process predicted. They differ in exactly the case
        // this change is about: a bucket the pre-filter believed absent, which a concurrent fill had already
        // committed with the same numbers, is skipped by the WHERE above and is not a write.
        return await _database.Database
            .ExecuteSqlRawAsync(UpsertBarsSql, parameters, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The same write against a provider with no <c>ON CONFLICT</c> — the unit tier's in-memory store.
    /// </summary>
    /// <remarks>
    /// It has no transactions and no snapshots either, so the race the relational path exists to survive is
    /// not merely absent here, it is unrepresentable. This is the merge that was the only implementation
    /// before gh#103, kept as it was.
    /// </remarks>
    private async Task<int> UpsertInMemoryAsync(
        string venue,
        InstrumentId instrument,
        int resolutionMinutes,
        IReadOnlyList<Bar> bars,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        Dictionary<DateTimeOffset, BarRecord> existing =
            await Overlap(venue, instrument, resolutionMinutes, bars[0].OpenTime, bars[^1].OpenTime)
                .ToDictionaryAsync(b => b.BucketStart, cancellationToken)
                .ConfigureAwait(false);

        int written = 0;

        foreach (Bar bar in bars)
        {
            if (existing.TryGetValue(bar.OpenTime, out BarRecord? row))
            {
                if (Unchanged(row, bar))
                {
                    continue;
                }

                // A revision. The venue restates bars after the fact, which is precisely why the write is an
                // upsert keyed on the bucket rather than an append.
                row.Open = bar.Open;
                row.High = bar.High;
                row.Low = bar.Low;
                row.Close = bar.Close;
                row.Volume = bar.Volume;
                row.ContractId = bar.ContractId;
                row.RecordedAt = now;
            }
            else
            {
                _database.Bars.Add(new BarRecord
                {
                    Venue = venue,
                    Instrument = instrument.Symbol,
                    ResolutionMinutes = resolutionMinutes,
                    BucketStart = bar.OpenTime,
                    Open = bar.Open,
                    High = bar.High,
                    Low = bar.Low,
                    Close = bar.Close,
                    Volume = bar.Volume,
                    ContractId = bar.ContractId,
                    RecordedAt = now,
                });
            }

            written++;
        }

        return written;
    }

    /// <summary>
    /// The coverage write, as one statement the store resolves against the row it has committed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The conflict target is the composite primary key</b> — the same key the read this replaced looked
    /// the row up by, reached directly instead of being inferred from a read of it.
    /// </para>
    /// <para>
    /// <b>There is no skip-unchanged <c>WHERE</c>, and its absence is the point.</b> The bar write has one
    /// because rewriting a bucket with the numbers it already held moves <c>RecordedAt</c> for nothing and
    /// sends the whole series back through the projection. Here <c>RecordedAt</c> is <i>the answer this row
    /// exists to give</i> — when the venue was last asked — and <c>ExpiresAt</c> is derived from it, so every
    /// ask is a change by construction and there is nothing to skip. Nothing compares a stored value against
    /// an incoming one, which is also what keeps the gh#37 shape out: the only comparison is the key's, made
    /// by the index, on both sides in the column's own type.
    /// </para>
    /// <para>
    /// <b><c>ExpiresAt</c> is assigned unconditionally, <see langword="null"/> included.</b> Null means
    /// <i>never</i> here, not <i>not recorded</i>, so a write that preserved a stored expiry — omitting the
    /// column, or coalescing over it — would leave a permanent claim wearing the TTL it was given back when
    /// the range was still near the present, and the range would be re-fetched on every call forever.
    /// </para>
    /// </remarks>
    private const string RecordCoverageSql = """
        INSERT INTO "BarCoverage" (
            "Venue", "Instrument", "ResolutionMinutes", "RangeStart", "RangeEnd", "RecordedAt", "ExpiresAt")
        VALUES (@venue, @instrument, @resolution, @rangeStart, @rangeEnd, @recorded, @expires)
        ON CONFLICT ("Venue", "Instrument", "ResolutionMinutes", "RangeStart", "RangeEnd") DO UPDATE SET
            "RecordedAt" = excluded."RecordedAt",
            "ExpiresAt" = excluded."ExpiresAt"
        """;

    /// <summary>
    /// Records that the venue answered a range <b>empty</b>, with the TTL its age earns it.
    /// </summary>
    /// <remarks>
    /// Two implementations for the same reason the bar write has two (gh#122, gh#103): whether this is an
    /// insert or an update is a fact about the <b>store</b> rather than about this process, and the in-memory
    /// provider the unit tier runs on has no <c>ON CONFLICT</c> to leave it to.
    /// </remarks>
    private async Task RecordEmptyAsync(
        string venue,
        InstrumentId instrument,
        int resolutionMinutes,
        BarRange range,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Asymmetric TTL. Near the present an empty answer means "not yet", and believing it permanently
        // would blind the cache to the bar that is about to print. For settled history it means "never", and
        // re-asking costs a venue request on every single call.
        //
        // Decided HERE, once, and handed to both writes: the classification is a question about the clock and
        // the range, which is this process's business, while insert-versus-update is the store's.
        bool settled = range.End <= now - SettledHistoryAge;
        DateTimeOffset? expiresAt = settled ? null : now + RecentEmptyTtl;

        if (_database.Database.IsRelational())
        {
            await RecordEmptyInStoreAsync(
                venue, instrument, resolutionMinutes, range, now, expiresAt, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await RecordEmptyInMemoryAsync(
                venue, instrument, resolutionMinutes, range, now, expiresAt, cancellationToken)
                .ConfigureAwait(false);
        }

        _logger.LogDebug(
            "The venue returned no bars for {Instrument} {Resolution}m over {From:o}..{To:o}; recorded as "
            + "covered ({Ttl}).",
            instrument.Symbol,
            resolutionMinutes,
            range.Start,
            range.End,
            settled ? "permanently" : "briefly");
    }

    private async Task RecordEmptyInStoreAsync(
        string venue,
        InstrumentId instrument,
        int resolutionMinutes,
        BarRange range,
        DateTimeOffset now,
        DateTimeOffset? expiresAt,
        CancellationToken cancellationToken)
    {
        // THE READ THAT USED TO DECIDE THIS IS GONE, RATHER THAN DEMOTED (gh#122).
        //
        // It read the row from THIS transaction's snapshot, so under the RepeatableRead of gh#73 two callers
        // asking about one range the venue answers empty both found no row, both INSERTed one, and the loser
        // took a 23505 out of `get_bars` -- with no bars involved at all, on the ordinary polling case the
        // ledger exists to make cheap. The bar write kept its pre-read because it still SAVES A WRITE; this
        // one had nothing left to save, because the ledger holds the latest answer for a range rather than a
        // history of asking, so every ask is a write.
        //
        // The 23505 becomes a 40001 rather than disappearing: Postgres refuses a conflict against a row
        // committed AFTER this snapshot, which is exactly what `R-2.10` already retries once -- and the
        // retry runs over the store the winner committed, so it converges.
        NpgsqlParameter[] parameters =
        [
            new("venue", NpgsqlDbType.Varchar) { Value = venue },
            new("instrument", NpgsqlDbType.Varchar) { Value = instrument.Symbol },
            new("resolution", NpgsqlDbType.Integer) { Value = resolutionMinutes },
            new("rangeStart", NpgsqlDbType.TimestampTz) { Value = range.Start },
            new("rangeEnd", NpgsqlDbType.TimestampTz) { Value = range.End },
            new("recorded", NpgsqlDbType.TimestampTz) { Value = now },
            new("expires", NpgsqlDbType.TimestampTz) { Value = (object?)expiresAt ?? DBNull.Value },
        ];

        await _database.Database
            .ExecuteSqlRawAsync(RecordCoverageSql, parameters, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The same write against a provider with no <c>ON CONFLICT</c> — the unit tier's in-memory store.
    /// </summary>
    /// <remarks>
    /// It has no transactions and no snapshots either, so the race the relational path exists to survive is
    /// not merely absent here, it is unrepresentable. This is the read-then-write that was the only
    /// implementation before gh#122, kept as it was.
    /// </remarks>
    private async Task RecordEmptyInMemoryAsync(
        string venue,
        InstrumentId instrument,
        int resolutionMinutes,
        BarRange range,
        DateTimeOffset now,
        DateTimeOffset? expiresAt,
        CancellationToken cancellationToken)
    {
        // An EXPIRED row for this exact range is filtered out of the covered set, so it is invisible to the
        // caller -- but it is still in the table, and inserting over it is a primary-key violation. Refresh
        // rather than insert: the ledger tracks the latest answer for a range, not a history of asking.
        BarCoverageRecord? existing = await _database.BarCoverage
            .FirstOrDefaultAsync(
                c => c.Venue == venue
                    && c.Instrument == instrument.Symbol
                    && c.ResolutionMinutes == resolutionMinutes
                    && c.RangeStart == range.Start
                    && c.RangeEnd == range.End,
                cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            existing.RecordedAt = now;
            existing.ExpiresAt = expiresAt;
        }
        else
        {
            _database.BarCoverage.Add(new BarCoverageRecord
            {
                Venue = venue,
                Instrument = instrument.Symbol,
                ResolutionMinutes = resolutionMinutes,
                RangeStart = range.Start,
                RangeEnd = range.End,
                RecordedAt = now,
                ExpiresAt = expiresAt,
            });
        }
    }
}
