using System.Globalization;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

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

        List<BarRecord> rows = await _database.Bars
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

        List<BarCoverageRecord> covered = await _database.BarCoverage
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

            for (DateTimeOffset from = range.Start; from < range.End; from += page)
            {
                DateTimeOffset to = from + page;
                if (to > range.End)
                {
                    to = range.End;
                }

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
                // derived from it -- this must not depend on a venue behaving.
                slices.Add(new FetchedSlice(slice, [.. bars.Where(b => b.OpenTime + barSize <= now)]));
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

    private async Task<int> UpsertAsync(
        string venue,
        InstrumentId instrument,
        int resolutionMinutes,
        IReadOnlyList<Bar> bars,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        DateTimeOffset first = bars[0].OpenTime;
        DateTimeOffset last = bars[^1].OpenTime;

        // Load the overlap once and merge in memory rather than round-tripping per bar. The composite key is
        // the idempotence guard, so a re-fetch can only ever update the bucket it already wrote.
        Dictionary<DateTimeOffset, BarRecord> existing = await _database.Bars
            .Where(b => b.Venue == venue
                && b.Instrument == instrument.Symbol
                && b.ResolutionMinutes == resolutionMinutes
                && b.BucketStart >= first
                && b.BucketStart <= last)
            .ToDictionaryAsync(b => b.BucketStart, cancellationToken)
            .ConfigureAwait(false);

        int written = 0;

        foreach (Bar bar in bars)
        {
            if (existing.TryGetValue(bar.OpenTime, out BarRecord? row))
            {
                if (row.Open == bar.Open
                    && row.High == bar.High
                    && row.Low == bar.Low
                    && row.Close == bar.Close
                    && row.Volume == bar.Volume
                    && string.Equals(row.ContractId, bar.ContractId, StringComparison.Ordinal))
                {
                    continue;
                }

                // A revision. The venue restates bars after the fact, which is precisely why the write is an
                // upsert keyed on the bucket rather than an append.
                //
                // The contract moves WITH the prices, never on its own. Both come out of the same venue
                // answer, so a row always says which contract produced the numbers standing in it -- writing
                // one without the other would leave a row whose provenance describes a different observation
                // from the one it holds, which is worse than no provenance at all.
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
        bool settled = range.End <= now - SettledHistoryAge;
        DateTimeOffset? expiresAt = settled ? null : now + RecentEmptyTtl;

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

        _logger.LogDebug(
            "The venue returned no bars for {Instrument} {Resolution}m over {From:o}..{To:o}; recorded as "
            + "covered ({Ttl}).",
            instrument.Symbol,
            resolutionMinutes,
            range.Start,
            range.End,
            settled ? "permanently" : "briefly");
    }
}
