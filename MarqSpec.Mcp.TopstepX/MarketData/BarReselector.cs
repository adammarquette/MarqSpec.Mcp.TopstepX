using System.Globalization;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// What <see cref="BarReselector"/> did to the window it was pointed at.
/// </summary>
/// <param name="BarsRevised">
/// Rows the winner's upsert actually changed, as the <b>store</b> reports them. A winner the store already
/// agrees with costs nothing and counts as nothing — the skip-unchanged <c>WHERE</c> on the bar statement
/// sees to that — so this is the number of buckets whose numbers or provenance genuinely moved.
/// </param>
/// <param name="BarsRemoved">
/// Rows inside the effective window that <b>another contract</b> held and the winner does not restate, and
/// which were therefore deleted.
/// </param>
/// <param name="UnattributedRemoved">
/// Rows deleted that carried <b>no</b> contract id at all — written before the provenance migration, or
/// backfilled by a gateway that was not yet stamping one (gh#402). Counted apart from
/// <paramref name="BarsRemoved"/> because folding the two together would tell an operator that a contract
/// lost buckets it never held.
/// </param>
/// <param name="TradeDatesChanged">
/// Trade dates whose stored contract before the run differs from the winner after it, compared against the
/// first attributed bucket the store held for the date.
/// </param>
/// <param name="Ties">
/// Trade dates whose top volume was shared by more than one contract. The break is deterministic — the
/// nearer expiry — but an operator reading a rewritten window should be told which dates were decided that
/// way rather than by the volume.
/// </param>
/// <param name="SeriesSkipped">
/// Resolution series the window is too wide to re-decide in one pass. Skipped loudly rather than trimmed:
/// the window is the operator's statement of intent, and silently reselecting part of it would report a
/// number about a smaller question than the one that was asked.
/// </param>
/// <param name="SlicesSkipped">Slices no cycle candidate was listed for, and which were therefore not re-decided.</param>
/// <param name="VenueRequests">History requests this run issued — every candidate's pages, and nothing else.</param>
/// <param name="EffectiveWindow">
/// The window that was actually re-decided: the union of the whole trade dates the operator's window
/// intersects. Reported because it is not the window they typed, and every other number here is about this
/// one.
/// </param>
public sealed record BarReselectResult(
    int BarsRevised,
    int BarsRemoved,
    int UnattributedRemoved,
    int TradeDatesChanged,
    int Ties,
    int SeriesSkipped,
    int SlicesSkipped,
    int VenueRequests,
    BarRange EffectiveWindow);

/// <summary>
/// Re-decides which contract each stored bar in an operator-named window belongs to — the
/// <c>reselect-bars</c> verb (gh#506, ADR-0020 §5).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the one thing in the server that rewrites an attributed bucket.</b> A read never does: keeping
/// a trade date the store already answered for is what makes a warm read byte-identical to the one before it
/// and what stops a seam appearing inside a day. So a window filled before ADR-0020 — every row stamped with
/// the venue's own pick, however thin its series was — stays wrong until someone decides to fix it. Deciding
/// that is an operator's judgement, and this is where it is carried out: bounded by a window they named,
/// counted, and logged.
/// </para>
/// <para>
/// <b>It takes an <c>IMarketDataGateway</c> — through <see cref="BarCacheService"/> — and that is a stated
/// departure from <see cref="IndicatorRebuilder"/>.</b> A rebuild is a replay: every indicator value is
/// reproducible from the bars already stored, so it reaches no venue at all. A reselect cannot be, because
/// the fact it needs — how much volume each candidate contract carried on each trade date — was never
/// stored. Only the winner's bars were.
/// </para>
/// <para>
/// <b>One <see cref="SeriesUnitOfWork"/> per resolution series</b>, as <see cref="IndicatorRebuilder"/> does,
/// and for the same reasons: the projection's two reads have to be one snapshot, and a failure over the last
/// series must not throw away the ones before it. Every venue page is walked <i>before</i> the transaction
/// opens, so a retry costs no vendor requests and no paced page walk sits inside an open snapshot.
/// </para>
/// </remarks>
/// <param name="database">The store.</param>
/// <param name="cache">The cache service, for its re-decision seam. The only thing here that reaches the venue.</param>
/// <param name="projector">The indicator projection, run in the same unit of work as the bar write.</param>
/// <param name="calendar">The session calendar, for grouping buckets into trade dates exactly as the policy does.</param>
/// <param name="clock">The clock. Stamped on rows the run actually changes.</param>
/// <param name="logger">The logger. The verb writes nothing to the console; these lines are the report.</param>
public sealed class BarReselector(
    TopstepXDbContext database,
    BarCacheService cache,
    IndicatorProjector projector,
    BarSessionCalendar calendar,
    TimeProvider clock,
    ILogger<BarReselector> logger)
{
    /// <summary>How many bucket starts one delete statement names.</summary>
    private const int DeleteBatch = 1_000;

    private readonly TopstepXDbContext _database = database;
    private readonly BarCacheService _cache = cache;
    private readonly IndicatorProjector _projector = projector;
    private readonly BarSessionCalendar _calendar = calendar;
    private readonly TimeProvider _clock = clock;
    private readonly ILogger<BarReselector> _logger = logger;

    /// <summary>
    /// Re-decides every resolution series the store holds for an instrument inside a window.
    /// </summary>
    /// <param name="instrument">The instrument.</param>
    /// <param name="window">The window the operator named.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>What the run revised, removed and declined to decide.</returns>
    /// <exception cref="ReselectPlanException">
    /// The plan would degrade for the whole window — see <c>BarCacheService.ReselectWindowAsync</c>. It can
    /// be thrown on the <i>second</i> series of a run, by which point the first has already committed its
    /// own unit of work; the per-series log lines are what say how far the run got.
    /// </exception>
    public async Task<BarReselectResult> ReselectAsync(
        InstrumentId instrument,
        BarRange window,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(window);

        DateTimeOffset now = _clock.GetUtcNow();
        BarRange effective = EffectiveWindowFor(window);

        // Every resolution the store actually holds INSIDE the effective window, rather than every
        // configured one: a resolution nobody has fetched for these dates has no provenance to re-decide,
        // and asking for it would be a venue round trip that looks like a result.
        //
        // A named record rather than an anonymous type because this projection is the run's unit of work --
        // what gets its own transaction, its own counters and its own log line.
        List<StoredSeries> found = await _database.Bars
            .AsNoTracking()
            .Where(b => b.Instrument == instrument.Symbol
                && b.BucketStart >= effective.Start
                && b.BucketStart < effective.End)
            .Select(b => new StoredSeries(b.Venue, b.ResolutionMinutes))
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Ordered here rather than in the query: a DISTINCT over a projected shape is what the store can
        // answer, and an ORDER BY on top of it is not. The order is for the log, so that a run over several
        // resolutions reads the same way twice.
        List<StoredSeries> series =
            [.. found.OrderBy(static s => s.Venue, StringComparer.Ordinal).ThenBy(static s => s.ResolutionMinutes)];

        if (series.Count == 0)
        {
            // LOUDER THAN THE ORDINARY SUMMARY, because the counters cannot tell these two apart. "0 bars
            // revised, 0 removed, 0 trade dates changed" is what a window that was already correct reports,
            // and it is also what a mistyped year reports -- and the second one has looked at nothing. A
            // plausible number standing in for a missing one is the failure this server refuses everywhere
            // else, so the absence is stated rather than counted.
            _logger.LogWarning(
                "Reselect found no stored series for {Instrument} inside {From}..{To} (asked "
                + "{AskedFrom}..{AskedTo}): nothing to re-decide. Check the symbol and the dates -- a window "
                + "nothing was ever fetched for reports the same counters as a window that was already "
                + "correct.",
                instrument.Symbol,
                effective.Start,
                effective.End,
                window.Start,
                window.End);

            return new BarReselectResult(0, 0, 0, 0, 0, 0, 0, 0, effective);
        }

        int revised = 0;
        int removed = 0;
        int unattributed = 0;
        int datesChanged = 0;
        int ties = 0;
        int seriesSkipped = 0;
        int slicesSkipped = 0;
        int venueRequests = 0;

        foreach (StoredSeries s in series)
        {
            // THE SAME CAP THE GAP DETECTOR REFUSES ON, applied before anything is asked of the venue. A
            // window wider than one pass is skipped rather than trimmed: the window is the operator's
            // statement of what they mean to rewrite, and silently reselecting the first part of it would
            // report a number about a smaller question than the one they asked.
            //
            // COUNTED AS WALL-CLOCK SPAN OVER THE BAR SIZE, where BarGapDetector counts the buckets the
            // CALENDAR expects -- so this over-estimates by every maintenance window, weekend and holiday
            // the span covers, roughly a third of it. Deliberately the conservative direction: this bound
            // exists to stop a run that would enumerate too much, and refusing slightly early is a worse
            // error message, while admitting slightly late is the unbounded pass the cap is for. Asking the
            // calendar instead would mean enumerating the very grid the cap is meant to avoid enumerating.
            long buckets =
                (effective.End - effective.Start).Ticks / TimeSpan.FromMinutes(s.ResolutionMinutes).Ticks;

            if (buckets > BarGapDetector.MaxBucketsPerPass)
            {
                // BOTH WINDOWS, because the number is measured over the second one. Naming only the
                // effective range would quote a bucket count against a range the operator never typed --
                // and someone who then narrows their window to just under the cap gets refused again, for
                // a widening the line never showed them.
                _logger.LogWarning(
                    "Skipping {Instrument} {Resolution}m: asked {AskedFrom}..{AskedTo}, whose whole trade "
                    + "dates {From}..{To} span at most {Buckets} buckets at this resolution, over the "
                    + "{Cap} a single pass will enumerate. Narrow the window or reselect this resolution "
                    + "on its own.",
                    instrument.Symbol,
                    s.ResolutionMinutes,
                    window.Start,
                    window.End,
                    effective.Start,
                    effective.End,
                    buckets,
                    BarGapDetector.MaxBucketsPerPass);

                seriesSkipped++;
                continue;
            }

            (IReadOnlyList<TradeDateSelection> selections, int requests, int skipped) = await _cache
                .ReselectWindowAsync(instrument, s.ResolutionMinutes, effective, now, cancellationToken)
                .ConfigureAwait(false);

            venueRequests += requests;
            slicesSkipped += skipped;

            SeriesOutcome outcome = await ReselectSeriesAsync(
                s, instrument, effective, selections, now, cancellationToken).ConfigureAwait(false);

            // The series is committed and this context will never look at it again, so let it go. Safe here
            // and nowhere else in this class: RunAsync has returned, and `series` holds projections.
            _database.ChangeTracker.Clear();

            revised += outcome.BarsRevised;
            removed += outcome.BarsRemoved;
            unattributed += outcome.UnattributedRemoved;
            datesChanged += outcome.TradeDatesChanged;
            ties += outcome.Ties;

            _logger.LogInformation(
                "Reselected {Instrument} {Resolution}m over {From}..{To}: {Revised} bars revised, "
                + "{Removed} removed, {Unattributed} unattributed rows removed, {Dates} trade dates "
                + "changed, {Ties} decided by a tie, {Coverage} coverage claims dropped, {Requests} venue "
                + "requests.",
                instrument.Symbol,
                s.ResolutionMinutes,
                effective.Start,
                effective.End,
                outcome.BarsRevised,
                outcome.BarsRemoved,
                outcome.UnattributedRemoved,
                outcome.TradeDatesChanged,
                outcome.Ties,
                outcome.CoverageRemoved,
                requests);
        }

        // BOTH WINDOWS, and the pair is the point: the operator sees the range they typed beside the range
        // that was actually re-decided, so a widening they did not expect is a line they can read rather
        // than a surprise in the row counts.
        _logger.LogInformation(
            "Reselect complete for {Instrument}: asked {AskedFrom}..{AskedTo}, re-decided whole trade dates "
            + "{From}..{To}. {Revised} bars revised, {Removed} removed, {Unattributed} unattributed rows "
            + "removed, {Dates} trade dates changed, {Ties} decided by a tie, {SeriesSkipped} series "
            + "skipped, {SlicesSkipped} slices skipped, {Requests} venue requests over {Series} series.",
            instrument.Symbol,
            window.Start,
            window.End,
            effective.Start,
            effective.End,
            revised,
            removed,
            unattributed,
            datesChanged,
            ties,
            seriesSkipped,
            slicesSkipped,
            venueRequests,
            series.Count - seriesSkipped);

        return new BarReselectResult(
            revised,
            removed,
            unattributed,
            datesChanged,
            ties,
            seriesSkipped,
            slicesSkipped,
            venueRequests,
            effective);
    }

    /// <summary>
    /// The union of the whole trade dates an operator's window intersects.
    /// </summary>
    /// <param name="window">The window the operator named.</param>
    /// <returns>The window that is actually re-decided.</returns>
    /// <remarks>
    /// <para>
    /// <b>An operator types a window; the policy decides a trade date.</b> Taken literally, a window covering
    /// part of a day would re-decide the whole day from part of its volume and then rewrite only the part —
    /// leaving one contract, then another, then the first again <i>inside a single day</i>, which is the
    /// interleaving <c>HistoricalContractPolicy</c> exists to forbid. <c>ContractRollDetector</c> would cut
    /// that day into three segments and every indicator would warm again at each seam, on a day nothing
    /// rolled. So the window is widened to whole sessions and everything happens over the wider one.
    /// </para>
    /// <para>
    /// <b>It only ever widens.</b> The bounds are taken from the calendar and then clamped against the asked
    /// window, because a window that begins or ends inside a maintenance gap has no session bound of its own
    /// to grow to — the fallback names a date whose close can sit <i>before</i> the instant asked about, and
    /// shrinking an operator's window is the one thing this must never do.
    /// </para>
    /// <para>
    /// A bucket the calendar places outside every session takes its UTC date, exactly as <c>Decide</c> groups
    /// it — so the two agree about which day a maintenance-window bucket belongs to.
    /// </para>
    /// </remarks>
    private BarRange EffectiveWindowFor(BarRange window)
    {
        DateOnly first = TradeDateOf(window.Start);
        DateOnly last = TradeDateOf(window.End.AddTicks(-1));

        // A session opens at the reopen on the PREVIOUS calendar day and closes on the trade date itself.
        DateTimeOffset open = MarketClock
            .FromMarket(first.AddDays(-1), _calendar.SessionOpen)
            .ToUniversalTime();
        DateTimeOffset close = MarketClock.FromMarket(last, _calendar.SessionClose).ToUniversalTime();

        return new BarRange(
            open < window.Start ? open : window.Start,
            close > window.End ? close : window.End);
    }

    /// <summary>Writes one series' re-decision, and re-projects over it.</summary>
    /// <param name="series">The series.</param>
    /// <param name="instrument">The instrument.</param>
    /// <param name="window">The window.</param>
    /// <param name="selections">What the policy decided, ascending by trade date.</param>
    /// <param name="now">The instant the run is happening at.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>What this series contributed to the run's counters.</returns>
    private async Task<SeriesOutcome> ReselectSeriesAsync(
        StoredSeries series,
        InstrumentId instrument,
        BarRange window,
        IReadOnlyList<TradeDateSelection> selections,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        Dictionary<DateOnly, string> winners = selections.ToDictionary(
            static selection => selection.TradeDate,
            static selection => selection.ContractId);

        List<Bar> winnerBars =
            [.. selections.SelectMany(static selection => selection.Bars).OrderBy(static bar => bar.OpenTime)];

        int ties = selections.Count(static selection =>
        {
            if (selection.VolumeByContract.Count == 0)
            {
                return false;
            }

            long top = selection.VolumeByContract.Values.Max();
            return selection.VolumeByContract.Values.Count(volume => volume == top) > 1;
        });

        return await SeriesUnitOfWork.RunAsync(
            _database,
            instrument.Symbol + " " + series.ResolutionMinutes.ToString(CultureInfo.InvariantCulture) + "m",
            async token =>
            {
                // WHAT THE STORE SAID BEFORE, read once and used for both questions the run answers about
                // the past: which trade dates changed hands, and which rows the winner does not restate.
                //
                // AsNoTracking, and that is not tidiness: these rows are written by SQL the change tracker
                // never sees, so a tracked copy would be a stale entity the identity map hands back to the
                // next query over Bars -- including the projector's own read, below.
                List<StoredBucket> before = await _database.Bars
                    .AsNoTracking()
                    .Where(b => b.Venue == series.Venue
                        && b.Instrument == instrument.Symbol
                        && b.ResolutionMinutes == series.ResolutionMinutes
                        && b.BucketStart >= window.Start
                        && b.BucketStart < window.End)
                    .OrderBy(b => b.BucketStart)
                    .Select(b => new StoredBucket(b.BucketStart, b.ContractId))
                    .ToListAsync(token)
                    .ConfigureAwait(false);

                int datesChanged = TradeDatesChanged(before, winners);

                int revised = winnerBars.Count == 0
                    ? 0
                    : await _cache.UpsertAsync(
                        series.Venue, instrument, series.ResolutionMinutes, winnerBars, now, token)
                        .ConfigureAwait(false);

                (int removed, int unattributed) = await RemoveLosersAsync(
                    series, instrument, window, before, winners, winnerBars, token).ConfigureAwait(false);

                // EVERY CLAIM THAT TOUCHES THE WINDOW GOES, for every contract -- on OVERLAP rather than on
                // containment. A coverage row says a contract answered a range with nothing, and a settled
                // one never expires; MemoiseEmpty cuts a claim at the settled age and Union merges touching
                // rows, so a claim reaching into the window from outside it is the ordinary shape. Left
                // standing, that straddling memo would suppress the next read of a window whose decision has
                // just been overturned. Losing a claim outside the window costs one re-ask.
                int coverage = await _database.BarCoverage
                    .Where(c => c.Venue == series.Venue
                        && c.Instrument == instrument.Symbol
                        && c.ResolutionMinutes == series.ResolutionMinutes
                        && c.RangeStart < window.End
                        && c.RangeEnd > window.Start)
                    .ExecuteDeleteAsync(token)
                    .ConfigureAwait(false);

                // NO SAVE IS NEEDED HERE, and saying so is the point of the comment. GetBarsAsync saves
                // before projecting because ApplyAsync can leave tracked work, and the projector reads the
                // series back with a query that would not see it. Every write above is immediate -- one
                // ExecuteSqlRaw for the winners, ExecuteDelete for the losers and the coverage claims -- so
                // the store has already applied all of it inside this transaction and the projector's own
                // queries, including the reconcile's bar count, see exactly what was written. Copying the
                // guarded SaveChanges from the read path would have been a no-op asserting an ordering it
                // does not enforce; the day a tracked write is added here, it goes back with its argument.

                // UNCONDITIONAL, and the read path's `if (written > 0)` guard is deliberately not copied. A
                // pass whose upserts were all no-ops can still have DELETED rows, and indicator values left
                // standing over deleted bars are numbers nobody can reproduce from the series they describe.
                await _projector
                    .ProjectAsync(series.Venue, instrument, series.ResolutionMinutes, now, token)
                    .ConfigureAwait(false);

                await _database.SaveChangesAsync(token).ConfigureAwait(false);

                return new SeriesOutcome(revised, removed, unattributed, datesChanged, ties, coverage);
            },
            _logger,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes the window's rows the winner neither restates nor already owns.
    /// </summary>
    /// <param name="series">The series.</param>
    /// <param name="instrument">The instrument.</param>
    /// <param name="window">The window.</param>
    /// <param name="before">The window's rows as the store held them before the upsert.</param>
    /// <param name="winners">The contract each trade date was decided for.</param>
    /// <param name="winnerBars">Every bar the winners answered, so a restated bucket can be told apart.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>
    /// How many rows another contract held, and how many carried no contract id at all. Reported apart
    /// because they are different facts: the first is a contract losing a bucket it held, the second is a
    /// row from before the provenance migration that nothing can attribute.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Decided in memory, deleted by an explicit list.</b> The trade date a bucket belongs to is a session
    /// calendar's answer and the store cannot compute it, so the window is pre-read and the decision is made
    /// here; the statement then names the buckets it removes. That is also the safer shape:
    /// <c>SessionBarService</c>'s reconcile makes the same argument — sweeping a range would throw away a row
    /// that was never re-derived.
    /// </para>
    /// <para>
    /// <b>Three conditions, and the third is not redundant.</b> A row goes only when its trade date actually
    /// has a winner (a date nothing answered for was not re-decided), when its contract is not that winner's,
    /// and when the winner has no bar for the bucket. Without the last one this would delete the very rows
    /// the upsert above has just rewritten: the pre-read still shows them under the loser's id, because that
    /// is who they belonged to when it ran.
    /// </para>
    /// <para>
    /// <b>Batched</b>, because a nine-month window can name tens of thousands of buckets and one statement
    /// carrying all of them is a parameter list nobody sized.
    /// </para>
    /// </remarks>
    private async Task<(int Removed, int Unattributed)> RemoveLosersAsync(
        StoredSeries series,
        InstrumentId instrument,
        BarRange window,
        IReadOnlyList<StoredBucket> before,
        IReadOnlyDictionary<DateOnly, string> winners,
        IReadOnlyList<Bar> winnerBars,
        CancellationToken cancellationToken)
    {
        HashSet<DateTimeOffset> restated = [.. winnerBars.Select(static bar => bar.OpenTime)];

        List<StoredBucket> going =
        [
            .. before
                .Where(row => winners.TryGetValue(TradeDateOf(row.BucketStart), out string? winner)
                    && !string.Equals(row.ContractId, winner, StringComparison.Ordinal)
                    && !restated.Contains(row.BucketStart)),
        ];

        // Counted from the pre-read rather than from the statement, because the store reports one number for
        // rows the two cases have to be told apart in. The two lists are disjoint and their sum is what the
        // deletes below report, which is asserted by the arithmetic rather than hoped for: every row here is
        // either attributed or it is not.
        int unattributed = going.Count(static row => row.ContractId is null);
        List<DateTimeOffset> losers = [.. going.Select(static row => row.BucketStart)];

        int removed = 0;

        for (int from = 0; from < losers.Count; from += DeleteBatch)
        {
            List<DateTimeOffset> batch = losers.GetRange(from, Math.Min(DeleteBatch, losers.Count - from));

            removed += await _database.Bars
                .Where(b => b.Venue == series.Venue
                    && b.Instrument == instrument.Symbol
                    && b.ResolutionMinutes == series.ResolutionMinutes
                    && b.BucketStart >= window.Start
                    && b.BucketStart < window.End
                    && batch.Contains(b.BucketStart))
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        return (removed - unattributed, unattributed);
    }

    /// <summary>
    /// How many trade dates the run moved from one contract to another.
    /// </summary>
    /// <param name="before">The window's rows as the store held them, ascending.</param>
    /// <param name="winners">The contract each trade date was decided for.</param>
    /// <returns>The count.</returns>
    /// <remarks>
    /// Compared against the <b>first attributed</b> bucket of each trade date, because that is the only
    /// well-defined "what the store said" for a date whose rows disagree with each other — which is exactly
    /// the shape a defective fill leaves behind. A date the store held nothing attributed for counts as
    /// changed: it had no provenance and now has one.
    /// </remarks>
    private int TradeDatesChanged(IReadOnlyList<StoredBucket> before, IReadOnlyDictionary<DateOnly, string> winners)
    {
        Dictionary<DateOnly, string> stored = [];

        foreach (StoredBucket row in before)
        {
            if (row.ContractId is null)
            {
                continue;
            }

            stored.TryAdd(TradeDateOf(row.BucketStart), row.ContractId);
        }

        return winners.Count(pair =>
            !stored.TryGetValue(pair.Key, out string? was)
            || !string.Equals(was, pair.Value, StringComparison.Ordinal));
    }

    /// <summary>
    /// The trade date a bucket belongs to, falling back to its UTC date outside every session — exactly as
    /// <c>HistoricalContractPolicy</c> groups bars.
    /// </summary>
    /// <param name="bucket">The bucket start.</param>
    /// <returns>The trade date.</returns>
    /// <remarks>
    /// The fallback is not a convenience: a bucket the calendar places outside every session has to group
    /// under the same key on both sides, or the store's provenance for a trade date and the policy's
    /// grouping of that date's bars would be about different days.
    /// </remarks>
    private DateOnly TradeDateOf(DateTimeOffset bucket) =>
        _calendar.TradeDateFor(bucket) ?? DateOnly.FromDateTime(bucket.UtcDateTime);

    /// <summary>One (venue, resolution) series the store holds inside the window.</summary>
    /// <param name="Venue">The venue the rows are keyed under.</param>
    /// <param name="ResolutionMinutes">The bar size in minutes.</param>
    private sealed record StoredSeries(string Venue, int ResolutionMinutes);

    /// <summary>One stored bucket, and which contract the row standing in it says produced it.</summary>
    /// <param name="BucketStart">When the bucket opens.</param>
    /// <param name="ContractId">The contract recorded against the bucket, or <see langword="null"/>.</param>
    private sealed record StoredBucket(DateTimeOffset BucketStart, string? ContractId);

    /// <summary>What one series contributed to the run's counters.</summary>
    /// <param name="BarsRevised">Rows the winner's upsert changed.</param>
    /// <param name="BarsRemoved">Rows another contract held that the winner does not restate.</param>
    /// <param name="UnattributedRemoved">Rows carrying no contract id that the winner does not restate.</param>
    /// <param name="TradeDatesChanged">Trade dates that changed hands.</param>
    /// <param name="Ties">Trade dates whose top volume was shared.</param>
    /// <param name="CoverageRemoved">Coverage claims overlapping the window that were dropped.</param>
    private sealed record SeriesOutcome(
        int BarsRevised,
        int BarsRemoved,
        int UnattributedRemoved,
        int TradeDatesChanged,
        int Ties,
        int CoverageRemoved);
}
