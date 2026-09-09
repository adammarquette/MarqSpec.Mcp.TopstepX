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

/// <summary>The outcome of a session-bar read.</summary>
/// <param name="Bars">
/// The session bars the store holds for the requested dates, ascending by trade date. Read back from the
/// store rather than handed straight out of the aggregator, so what a caller receives is what was committed.
/// </param>
/// <param name="Absent">
/// Why each of the other requested dates carries no bar, in the order the caller asked for them — the
/// aggregator's reasons and the host's <see cref="SessionBarAbsence.NotClosed"/> in one list, because a
/// caller sorting them into two would be reconstructing the distinction this type exists to erase.
/// </param>
/// <param name="FetchedBuckets">
/// How many <b>base</b> buckets the one base read wrote or revised. It is the base series' number, not this
/// one's: nothing here fetches, and a session bar is derived rather than fetched at all.
/// </param>
/// <param name="VenueRequests">
/// How many requests that base read issued. Zero is the precise statement that this call reached no venue.
/// </param>
/// <param name="History">
/// How the one base read's historical half was decided (gh#592). The base series' answer, like the two
/// counters above: a session bar is derived from those bars, so a base read that fell back to the venue's own
/// pick produces sessions aggregated over exactly that stretch — and nothing else on this payload says so.
/// </param>
public sealed record SessionBarReadResult(
    IReadOnlyList<SessionBar> Bars,
    IReadOnlyList<SessionBarOutcome> Absent,
    int FetchedBuckets,
    int VenueRequests,
    HistoryCandidates History);

/// <summary>
/// Derives one session bar per closed trade date from the stored base series, and stores the complete ones
/// (ADR-0022).
/// </summary>
/// <remarks>
/// <para>
/// <b>Complete or absent, never partial.</b> The aggregation itself is
/// <see cref="SessionBarAggregator"/>'s, and it refuses a session whose base buckets are not all there, whose
/// bars splice two contracts, or whose bars cannot say which contract they came from. This type adds the two
/// judgements <c>Domain</c> may not make — <i>has the session closed</i>, which needs a clock, and <i>does
/// the stored row still describe the definition standing today</i>, which needs the store — and then writes
/// what is left.
/// </para>
/// <para>
/// <b>An incomplete session is never recorded: no row, no ledger, no marker.</b> Re-derivation is a store
/// read, the base coverage ledger already bounds what re-deriving can cost at the venue, and a session that
/// heals simply appears on the next read. A per-session absence ledger would go stale the moment the missing
/// base buckets arrived.
/// </para>
/// <para>
/// <b>This service never calls the venue.</b> The one call that can is
/// <see cref="BarCacheService.GetBarsAsync"/>, and it is made once, before and outside this service's
/// transaction (ADR-0022 §7). <see cref="IMarketDataGateway"/> is taken here for
/// <see cref="IMarketDataGateway.VenueId"/> alone — the first column of the storage key, read off the gateway
/// exactly as <see cref="BarCacheService"/> reads it, because the same product on two venues is two series.
/// </para>
/// </remarks>
public sealed class SessionBarService
{
    private readonly TopstepXDbContext _database;
    private readonly BarCacheService _bars;
    private readonly IMarketDataGateway _gateway;
    private readonly BarSessionCalendar _calendar;
    private readonly IndicatorProjector _projector;
    private readonly TimeProvider _clock;
    private readonly ILogger<SessionBarService> _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="database">The store.</param>
    /// <param name="bars">The base series, cache-aside. The only thing here that can reach the venue.</param>
    /// <param name="gateway">
    /// The venue — for <see cref="IMarketDataGateway.VenueId"/> and nothing else. See the type's remarks.
    /// </param>
    /// <param name="calendar">The session calendar deciding which buckets a window expects.</param>
    /// <param name="projector">
    /// The indicator projection over the session series this fill writes, run inside the same unit of work.
    /// </param>
    /// <param name="clock">The clock. Injected so a test can place "now" precisely against a session.</param>
    /// <param name="logger">The logger. A serialization retry is announced through it.</param>
    public SessionBarService(
        TopstepXDbContext database,
        BarCacheService bars,
        IMarketDataGateway gateway,
        BarSessionCalendar calendar,
        IndicatorProjector projector,
        TimeProvider clock,
        ILogger<SessionBarService> logger)
    {
        _database = database;
        _bars = bars;
        _gateway = gateway;
        _calendar = calendar;
        _projector = projector;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Derives and stores one session bar per closed trade date, and says why the others have none.
    /// </summary>
    /// <param name="instrument">The instrument.</param>
    /// <param name="definition">The session being derived.</param>
    /// <param name="tradeDates">The trade dates to answer for.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The bars, the absences, and what the base read cost.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="tradeDates"/> names a date more than once; or — from
    /// <see cref="SessionBarAggregator.Aggregate"/> — the base bars are not strictly ascending, or the
    /// calendar's bucket grid does not cover <paramref name="definition"/>'s window end to end. The last is a
    /// definition <see cref="SessionWindows.Validate"/> refuses, so it reaches here only from a definition
    /// that never went through startup validation.
    /// </exception>
    /// <exception cref="VenueException">The base read could not resolve or reach the venue.</exception>
    /// <exception cref="StoreContentionException">Every attempt at the write lost to a concurrent one.</exception>
    /// <exception cref="InvalidOperationException">
    /// A trade date the caller asked about ended up in neither list, or in both. It is thrown rather than
    /// returned because the result type cannot express it: a caller reading a date that is in neither list
    /// has no way to tell <i>no bar, and here is why</i> from <i>not a trading day</i>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>A retry replays this call's own reconcile decision, and that decision is a DELETE.</b> Which dates
    /// are stale is derived at step 5, outside the transaction, from the base bars this call read — so the
    /// second attempt re-runs the reconcile of step 6(e) against the <i>same</i> base view rather than
    /// re-deriving from the store the winner has just committed. That is deliberate: re-deriving inside the
    /// transaction would mean aggregating over bars read under the retry's snapshot, and the base read is
    /// the one step that may not happen in there.
    /// </para>
    /// <para>
    /// <b>The consequence is a lost update, and it is bounded.</b> If a concurrent call derived a complete
    /// bar for a date this call found <see cref="SessionBarAbsence.Incomplete"/> — the missing base bucket
    /// arrived between the two base reads — the replayed reconcile removes that row from the STORE. Nothing
    /// served is wrong: each call's answer stays truthful to the base view it derived from, this one reports
    /// the date absent because that is what its bars supported, and a session bar is re-derived on every
    /// read, so the next read re-derives and re-upserts it. No row, no ledger and no marker records the
    /// absence (ADR-0022), which is exactly why the store healing itself costs nothing.
    /// </para>
    /// <para>
    /// <b>This is the first <see cref="SeriesUnitOfWork"/> body in this repository whose retry replays a
    /// deletion.</b> Every other one replays a fill or a projection, where the second attempt runs over a
    /// strictly better-informed store; here the second attempt can undo work the winner committed. The bound
    /// above is what makes that acceptable, and it is stated here because the unit of work itself cannot
    /// know it.
    /// </para>
    /// <para>
    /// <b>The indicator projection runs when the pass changed the series, and replays with the retry.</b>
    /// This body projects the session series it wrote (step 6(f2), gh#501) whenever it upserted a bar,
    /// reconciled a stale one away, or discarded one built under a definition that no longer holds — and not
    /// otherwise, because every read with one closed date enters this unit of work and recomputing the whole
    /// series on each of them is a cost paid per call for an empty diff. A serialization failure replays the
    /// projection along with the upsert and the reconcile, and unlike the deletion above that costs nothing
    /// to get wrong: a projection derives entirely from the session bars visible on the attempt's own
    /// snapshot and rounds to the stored column's scale, so running it twice over the same bars produces the
    /// same numbers and the second run is an empty diff (ADR-0006). It is the one step here whose replay
    /// needs no bound at all.
    /// </para>
    /// </remarks>
    public async Task<SessionBarReadResult> GetAsync(
        InstrumentId instrument,
        SessionDefinition definition,
        IReadOnlyList<DateOnly> tradeDates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(tradeDates);

        // 1. ONE TRADE DATE IS ONE SESSION BAR, AND IT IS REFUSED HERE RATHER THAN IN THE STORE. A repeated date
        // produces a repeated array entry in the upsert, and Postgres rejects an ON CONFLICT DO UPDATE that
        // would touch one row twice with a 21000 -- a cardinality violation naming a constraint, from inside a
        // transaction, which says nothing about the caller that asked for the same day twice.
        //
        // REFUSED rather than quietly de-duplicated. A caller asking twice has a bug, and Distinct() would
        // answer it as though it had not.
        HashSet<DateOnly> asked = [];
        foreach (DateOnly repeated in tradeDates)
        {
            if (!asked.Add(repeated))
            {
                throw new ArgumentException(
                    "The trade date "
                    + repeated.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    + " was asked for more than once. One trade date is one session bar, and one write cannot "
                    + "affect one row twice; ask for each date once.",
                    nameof(tradeDates));
            }
        }

        DateTimeOffset now = _clock.GetUtcNow();
        string venue = _gateway.VenueId;

        // 2. WHICH SESSIONS HAVE CLOSED, WHICH IS THE ONE JUDGEMENT Domain CANNOT MAKE. A session bar for a
        // window still running would be the partial the whole design refuses -- and it would be a partial
        // that looks entirely ordinary, since every bucket printed so far is present and the count of them is
        // simply lower than the calendar expects. So the clock decides here, in the host that owns one, and
        // SessionBarAbsence.NotClosed is merely DECLARED in Domain (SessionBar.cs) so that both ends share one
        // vocabulary.
        //
        // A window of null joins it: the calendar carries no such session on that date -- a Saturday, a
        // holiday, a definition its close disowns -- and there is nothing to wait for and nothing to store.
        // Reporting it as NotClosed rather than inventing a fifth reason keeps the vocabulary closed, and
        // both mean the same thing to a caller: ask again later, there is no bar and nothing to fetch.
        List<DateOnly> closed = [];
        Dictionary<DateOnly, BarRange> windows = [];
        Dictionary<DateOnly, SessionBarOutcome> absences = [];

        foreach (DateOnly tradeDate in tradeDates)
        {
            if (SessionWindows.WindowFor(_calendar, definition, tradeDate) is { } window && window.End <= now)
            {
                windows[tradeDate] = window;
                closed.Add(tradeDate);
                continue;
            }

            absences[tradeDate] = SessionBarOutcome.Absent(tradeDate, SessionBarAbsence.NotClosed, 0, 0);
        }

        // 3. Nothing has closed, so there is nothing to derive from and nothing to reconcile against. The
        // store is not opened and the venue is not reached: a read that can only answer "not yet" must not
        // cost either of them.
        if (closed.Count == 0)
        {
            return Verified(
                tradeDates,
                [],
                InAskedOrder(tradeDates, absences),
                0,
                0,
                HistoryCandidates.NotDecidedHere);
        }

        DateOnly firstClosed = closed.Min();
        DateOnly lastClosed = closed.Max();

        // 4. ONE BASE READ, AT definition.BaseResolutionMinutes, BEFORE AND OUTSIDE THE TRANSACTION.
        //
        // The resolution is the aggregator's contract and this is the only place it is kept: a Bar carries
        // its open time and not its size, so a series read finer than the base passes the completeness check
        // and yields a session bar that looks whole and is wrong -- its extremes are only the sub-buckets that
        // happened to start on the boundary, and its volume is a fraction of the session's. Aggregate cannot
        // check this and says so; the number lives on the definition, and it is read with it here.
        //
        // ONE CALL RATHER THAN ONE PER DATE, and it is not free: the covering window spans the overnight
        // between the sessions, and the calendar expects buckets right around the clock apart from the one
        // maintenance hour -- so for a daytime session like `rth` the seventeen-odd overnight hours ARE
        // fetched and stored, and FetchedBuckets reports the larger number. That is the right trade anyway.
        // The base series is shared, so those buckets are the same rows every other reader of this instrument
        // wants; BarGapDetector coalesces a run of missing buckets into one paged range whether or not a
        // session boundary sits inside it; and the ranges the venue answers empty are memoised by the base
        // coverage ledger, so the overnight is asked for once rather than on every read. One call per date
        // would buy a narrower first fetch and pay for it with a round trip per date, forever.
        //
        // OUTSIDE the transaction below, for the reason BarCacheService states at its own call site: the page
        // walk is paced, and holding a RepeatableRead snapshot across a minute of deliberate sleeping pins
        // xmin and widens every serialization window on this path. It also makes the retry free -- a second
        // attempt re-derives from the store and re-fetches nothing.
        //
        // NORMALISED TO UTC, and that is not cosmetic. WindowFor hands back bounds carrying the MARKET's
        // offset -- the same instants, written in the coordinate the session was stated in -- and Npgsql
        // refuses to write a DateTimeOffset with a non-zero offset to a `timestamp with time zone`. The
        // aggregator normalises for the same reason (SessionWindows.WindowFor's remark says so), and
        // DateTimeOffset equality compares instants, so the difference is invisible to a test and fatal at
        // the parameter.
        BarReadResult read = await _bars.GetBarsAsync(
            instrument,
            definition.BaseResolutionMinutes,
            new BarRange(
                windows[firstClosed].Start.ToUniversalTime(), windows[lastClosed].End.ToUniversalTime()),
            cancellationToken).ConfigureAwait(false);

        // 5. Pure, and reproducible from the bars alone (ADR-0006).
        IReadOnlyList<SessionBarOutcome> outcomes =
            SessionBarAggregator.Aggregate(read.Bars, _calendar, definition, closed);

        List<SessionBar> derived = [];
        foreach (SessionBarOutcome outcome in outcomes)
        {
            if (outcome.Bar is { } bar)
            {
                derived.Add(bar);
            }
            else
            {
                absences[outcome.TradeDate] = outcome;
            }
        }

        string windowCentral = definition.WindowCentral;
        List<DateOnly> stale = [.. closed.Where(d => !derived.Exists(b => b.TradeDate == d))];

        // 6. ONE UNIT OF WORK, at RepeatableRead with the single retry every series write shares. Everything
        // the caller is answered with is decided in here, the read-back included: a statement run after the
        // commit is a fresh look at whatever the store holds by then, and a concurrent deletion landing in
        // that gap would leave a trade date in NEITHER list.
        SeriesKey series = new SeriesKey.Session(venue, instrument.Symbol, definition.Name);

        (int Written, List<SessionBarRecord> Committed) stored = await SeriesUnitOfWork.RunAsync(
            _database,
            series.Describe(),
            async token =>
            {
                // (a) DISCARD ROWS BUILT UNDER A DIFFERENT DEFINITION, and do it before reading anything.
                //
                // The pair (WindowCentral, BaseResolutionMinutes) is the row's provenance, and a row whose
                // pair disagrees with the definition standing today describes a session nobody asked about:
                // 08:30-15:00 off hourly bars is not 08:30-15:00 off half-hourly ones, and the key cannot
                // tell them apart. Discarded rather than served (ADR-0022 §4).
                //
                // UNSCOPED BY DATE on purpose. A changed definition invalidates the whole series, not the
                // window this call happens to ask about -- leaving the rest would keep serving them from
                // every other window forever.
                // The count is kept because it is one of the three ways this pass can CHANGE the series, and
                // the projection at (f2) turns on that. A discard leaves indicator values standing over bars
                // that no longer exist, which is a change even when nothing was upserted and nothing was
                // reconciled.
                int discarded = await _database.SessionBars
                    .Where(s => s.Venue == venue
                        && s.Instrument == instrument.Symbol
                        && s.Session == definition.Name
                        && (s.WindowCentral != windowCentral
                            || s.BaseResolutionMinutes != definition.BaseResolutionMinutes))
                    .ExecuteDeleteAsync(token)
                    .ConfigureAwait(false);

                // (b) THE PRE-READ. AsNoTracking, because the write below is raw SQL the change tracker never
                // sees (gh#103): a tracked row here is a stale copy the identity map would hand back to the
                // next query in this scope, and both the context and this service are scoped.
                //
                // The whole entity, and no ValueTuple anywhere near the Select (gh#282) -- a tuple there
                // translates to a Postgres row constructor Npgsql refuses to materialise.
                Dictionary<DateOnly, SessionBarRecord> existing = await _database.SessionBars
                    .AsNoTracking()
                    .Where(s => s.Venue == venue
                        && s.Instrument == instrument.Symbol
                        && s.Session == definition.Name
                        && closed.Contains(s.TradeDate))
                    .ToDictionaryAsync(s => s.TradeDate, token)
                    .ConfigureAwait(false);

                // (c) SKIP UNCHANGED, IN C#, AS A PRE-FILTER AND NOT AS A GUARD. It saves a write -- a row
                // filtered out here is never sent, never index-probed and never row-locked -- and it decides
                // nothing: insert-versus-update is a fact about the store, settled by the ON CONFLICT below
                // against the row the store has actually committed.
                List<SessionBar> pending =
                [
                    .. derived.Where(bar =>
                        !existing.TryGetValue(bar.TradeDate, out SessionBarRecord? row) || !Unchanged(row, bar)),
                ];

                // (d) ONE ON CONFLICT ... DO UPDATE, aimed at the primary key. The statement itself, and
                // why it is not aimed at the unique (Venue, Instrument, Session, OpenUtc) index instead,
                // are UpsertSessionBarsSql's.
                int rows = pending.Count == 0
                    ? 0
                    : await UpsertAsync(venue, instrument, definition, windowCentral, pending, now, token)
                        .ConfigureAwait(false);

                // (e) RECONCILE, SCOPED TO WHAT WAS ACTUALLY RE-DERIVED. A date this call asked about and the
                // aggregator refused no longer has a session bar the store is entitled to serve -- a stored
                // row for it was built from base bars that no longer support it, which is exactly what a roll
                // landing on stored buckets does.
                //
                // Only those dates. A date OUTSIDE the ask was not re-derived, so deleting it would throw away
                // a bar on the strength of not having looked -- which is why this deletes an explicit list
                // rather than sweeping the window between the first and last date asked for.
                if (stale.Count > 0)
                {
                    await _database.SessionBars
                        .Where(s => s.Venue == venue
                            && s.Instrument == instrument.Symbol
                            && s.Session == definition.Name
                            && stale.Contains(s.TradeDate))
                        .ExecuteDeleteAsync(token)
                        .ConfigureAwait(false);
                }

                // (f) SAVED BEFORE ANYTHING READS BACK. Nothing here tracks an entity today, so this is
                // normally a no-op -- it stays because the read-back below is a QUERY, and a query does not
                // see rows that are only tracked. The day something in this body starts going through the
                // tracker, the failure without this is silent (AGENT-MEMORY.md).
                if (_database.ChangeTracker.HasChanges())
                {
                    await _database.SaveChangesAsync(token).ConfigureAwait(false);
                }

                // (f2) PROJECT THE SESSION SERIES THIS BODY WROTE, in this transaction, WHEN THIS PASS
                // CHANGED IT (gh#501). A session bar is a bar like any other once it is stored, and the
                // indicators over it are a projection of it (ADR-0006) -- so bars committed without the
                // values they justify would be exactly the state a read then has to replay a whole series to
                // repair. Inside this transaction they commit together or not at all, the way a base fill has
                // always projected in its own unit of work.
                //
                // GATED, ON ALL THREE WAYS THIS BODY CAN CHANGE THE SERIES, and BarCacheService's `written >
                // 0` is the precedent. Unconditional was correct and too expensive: every get_session_bars
                // with one closed date enters this unit of work, so a warm read for a single trade date
                // recomputed the whole series -- an empty diff, but one paid per call over a series that
                // grows with the store.
                //
                // ALL THREE, because `rows` alone is not "something changed". A pass that only reconciled
                // stale dates away has rows == 0 and has left values standing over bars it just deleted; so
                // has a pass that only DISCARDED rows built under a definition that no longer holds, and that
                // one is unscoped by date, so it can fire on a call whose own dates were all unchanged.
                // Removing those values is the projection's reconcile, and a gate missing either term leaves
                // them behind until an operator runs rebuild-indicators.
                if (discarded > 0 || rows > 0 || stale.Count > 0)
                {
                    await _projector.ProjectAsync(series, now, token).ConfigureAwait(false);

                    // (f3) AND SAVED AGAIN, because the projection is deliberately half-tracked: it writes
                    // its values with one statement the store runs as it is sent and removes what the bars no
                    // longer justify through the change tracker, which waits for this. The projector refuses
                    // outright outside a transaction for the same reason (AGENT-MEMORY.md, "Save before you
                    // project").
                    if (_database.ChangeTracker.HasChanges())
                    {
                        await _database.SaveChangesAsync(token).ConfigureAwait(false);
                    }
                }

                // (g) READ BACK WHAT THIS TRANSACTION COMMITTED, rather than handing out what was
                // derived. AsNoTracking for the reason the pre-read is: this table is written by SQL the
                // change tracker never sees.
                //
                // INSIDE the transaction, and that is the whole point. Under RepeatableRead this statement
                // sees this transaction's own writes against this transaction's own snapshot, so what comes
                // back is what THIS call committed -- a concurrent reader deleting the row a moment later
                // cannot turn this answer into a trade date reported in neither list.
                //
                // The provenance pair is restated here rather than left to the discard at (a). This is the
                // statement whose result a caller acts on, so naming the definition it will serve makes "a
                // row built under a definition that no longer holds is never served" a property of the read
                // itself rather than of the sequence that preceded it.
                List<SessionBarRecord> committed = await _database.SessionBars
                    .AsNoTracking()
                    .Where(s => s.Venue == venue
                        && s.Instrument == instrument.Symbol
                        && s.Session == definition.Name
                        && s.WindowCentral == windowCentral
                        && s.BaseResolutionMinutes == definition.BaseResolutionMinutes
                        && closed.Contains(s.TradeDate))
                    .OrderBy(s => s.TradeDate)
                    .ToListAsync(token)
                    .ConfigureAwait(false);

                return (rows, committed);
            },
            _logger,
            cancellationToken).ConfigureAwait(false);

        _logger.LogDebug(
            "Derived {Derived} '{Session}' bars for {Instrument} over {From} to {To}; the store wrote or "
            + "revised {Written}.",
            derived.Count,
            definition.Name,
            instrument.Symbol,
            firstClosed,
            lastClosed,
            stored.Written);

        // 7. EVERY TRADE DATE ASKED FOR IN EXACTLY ONE LIST, CHECKED RATHER THAN ASSUMED.
        return Verified(
            tradeDates,
            [.. stored.Committed.Select(ToSessionBar)],
            InAskedOrder(tradeDates, absences),
            read.FetchedBuckets,
            read.VenueRequests,
            read.History);
    }

    /// <summary>
    /// The result, once every trade date asked for has been accounted for exactly once.
    /// </summary>
    /// <param name="asked">The trade dates the caller asked about.</param>
    /// <param name="bars">The session bars to serve.</param>
    /// <param name="absent">The absences to serve.</param>
    /// <param name="fetchedBuckets">What the base read wrote or revised.</param>
    /// <param name="venueRequests">What the base read cost the venue.</param>
    /// <param name="history">How the base read's historical half was decided.</param>
    /// <returns>The result.</returns>
    /// <exception cref="InvalidOperationException">A date is in neither list, or in both.</exception>
    /// <remarks>
    /// <b>Loud, never a silent gap.</b> The two lists partition the ask, and a caller has no way to see that
    /// broken: a date missing from both looks exactly like a date nobody asked about, and reads as "not a
    /// trading day". So the invariant is enforced at the boundary that states it, naming the date, rather
    /// than left as a property of the sequence above happening to hold.
    /// </remarks>
    private static SessionBarReadResult Verified(
        IReadOnlyList<DateOnly> asked,
        IReadOnlyList<SessionBar> bars,
        IReadOnlyList<SessionBarOutcome> absent,
        int fetchedBuckets,
        int venueRequests,
        HistoryCandidates history)
    {
        HashSet<DateOnly> reported = [.. bars.Select(b => b.TradeDate)];

        foreach (SessionBarOutcome outcome in absent)
        {
            if (!reported.Add(outcome.TradeDate))
            {
                throw new InvalidOperationException(
                    "The trade date "
                    + outcome.TradeDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    + " is reported both as a session bar and as an absence. A trade date has one answer, "
                    + "and a caller reading both would have to choose between them.");
            }
        }

        foreach (DateOnly tradeDate in asked)
        {
            if (!reported.Contains(tradeDate))
            {
                throw new InvalidOperationException(
                    "The trade date "
                    + tradeDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    + " was asked about and is in neither the bars nor the absences. Every date this read "
                    + "answers for is one or the other; a date in neither is indistinguishable from a date "
                    + "nobody asked about, and reads as 'not a trading day'.");
            }
        }

        return new SessionBarReadResult(bars, absent, fetchedBuckets, venueRequests, history);
    }

    /// <summary>
    /// The session write, as one statement the store resolves against the row it has committed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The conflict target is the composite primary key</b> — <c>(Venue, Instrument, Session, TradeDate)</c>,
    /// the idempotence guard the data dictionary names, reached directly instead of inferred from a read of
    /// it. It is deliberately <b>not</b> the unique <c>(Venue, Instrument, Session, OpenUtc)</c> index: that
    /// one exists to make a calendar bug fail the write rather than become two overlapping sessions, and a
    /// <c>DO UPDATE</c> aimed at it would turn the failure it is there to raise into a silent revision.
    /// </para>
    /// <para>
    /// <b>The <c>WHERE</c> is the skip-unchanged rule</b>, and it is stated here rather than in C# because
    /// this is the only place both sides are the column's own type: <c>excluded</c>'s prices have already been
    /// coerced to <c>numeric(18,8)</c>, and a value at full <see cref="decimal"/> precision compared against a
    /// stored one is the shape that made an identical guard dead code for a whole phase (gh#37).
    /// </para>
    /// <para>
    /// <b>Arrays rather than a row per session</b>: a year of one session is 250-odd rows, and fifteen
    /// parameters each would approach the protocol's parameter limit for no benefit. <c>WindowCentral</c> and
    /// <c>BaseResolutionMinutes</c> are scalars because the definition is one per call, and they are not in
    /// the <c>SET</c> or the comparison because the discard above has already removed every row that could
    /// disagree about them.
    /// </para>
    /// </remarks>
    private const string UpsertSessionBarsSql = """
        INSERT INTO "SessionBars" (
            "Venue", "Instrument", "Session", "TradeDate", "OpenUtc", "CloseUtc",
            "Open", "High", "Low", "Close", "Volume", "ContractId",
            "BaseResolutionMinutes", "BaseBucketCount", "WindowCentral", "RecordedAt")
        SELECT @venue, @instrument, @session, a.trade_date, a.open_utc, a.close_utc,
               a.open_price, a.high_price, a.low_price, a.close_price, a.volume, a.contract,
               @resolution, a.bucket_count, @window, @recorded
        FROM unnest(@tradeDates, @opensUtc, @closesUtc, @opens, @highs, @lows, @closes, @volumes,
                    @contracts, @bucketCounts)
             AS a(trade_date, open_utc, close_utc, open_price, high_price, low_price, close_price,
                  volume, contract, bucket_count)
        ON CONFLICT ("Venue", "Instrument", "Session", "TradeDate") DO UPDATE SET
            "OpenUtc" = excluded."OpenUtc",
            "CloseUtc" = excluded."CloseUtc",
            "Open" = excluded."Open",
            "High" = excluded."High",
            "Low" = excluded."Low",
            "Close" = excluded."Close",
            "Volume" = excluded."Volume",
            "ContractId" = excluded."ContractId",
            "BaseBucketCount" = excluded."BaseBucketCount",
            "RecordedAt" = excluded."RecordedAt"
        WHERE ("SessionBars"."OpenUtc", "SessionBars"."CloseUtc", "SessionBars"."Open", "SessionBars"."High",
               "SessionBars"."Low", "SessionBars"."Close", "SessionBars"."Volume",
               "SessionBars"."ContractId", "SessionBars"."BaseBucketCount")
              IS DISTINCT FROM
              (excluded."OpenUtc", excluded."CloseUtc", excluded."Open", excluded."High", excluded."Low",
               excluded."Close", excluded."Volume", excluded."ContractId", excluded."BaseBucketCount")
        """;

    /// <summary>Whether a stored row already holds exactly what has just been derived.</summary>
    /// <param name="row">The stored row.</param>
    /// <param name="bar">The session bar the aggregator produced.</param>
    /// <returns><see langword="true"/> when writing it again would change nothing.</returns>
    /// <remarks>
    /// The bounds are compared as well as the numbers: the same trade date's session moves in UTC across a
    /// daylight-saving change, and a row whose stored bounds no longer match the ones the calendar resolves
    /// today is stale however identical its prices are.
    /// </remarks>
    private static bool Unchanged(SessionBarRecord row, SessionBar bar) =>
        row.OpenUtc == bar.OpenUtc
        && row.CloseUtc == bar.CloseUtc
        && row.Open == bar.Open
        && row.High == bar.High
        && row.Low == bar.Low
        && row.Close == bar.Close
        && row.Volume == bar.Volume
        && string.Equals(row.ContractId, bar.ContractId, StringComparison.Ordinal)
        && row.BaseBucketCount == bar.BaseBucketCount;

    /// <summary>Maps a stored row back to the domain shape.</summary>
    /// <param name="row">The row.</param>
    /// <returns>The session bar.</returns>
    private static SessionBar ToSessionBar(SessionBarRecord row) =>
        new(
            row.TradeDate,
            row.OpenUtc,
            row.CloseUtc,
            row.Open,
            row.High,
            row.Low,
            row.Close,
            row.Volume,
            row.ContractId,
            row.BaseBucketCount);

    /// <summary>The absences, in the order the caller asked for their dates.</summary>
    /// <param name="tradeDates">The dates as asked for.</param>
    /// <param name="absences">The absence for each date that has one.</param>
    /// <returns>The absences, in the caller's order.</returns>
    private static IReadOnlyList<SessionBarOutcome> InAskedOrder(
        IReadOnlyList<DateOnly> tradeDates,
        Dictionary<DateOnly, SessionBarOutcome> absences) =>
        [.. tradeDates.Where(absences.ContainsKey).Select(d => absences[d])];

    /// <summary>
    /// Writes the derived session bars, revising the rows already stored.
    /// </summary>
    /// <param name="venue">The venue.</param>
    /// <param name="instrument">The instrument.</param>
    /// <param name="definition">The definition that produced them.</param>
    /// <param name="windowCentral">The definition's window, as the provenance column records it.</param>
    /// <param name="bars">The bars to write. Never empty.</param>
    /// <param name="now">The instant this write runs at.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>
    /// <b>How many rows the store reports it wrote or revised</b> — the statement's own row count, never this
    /// process's prediction of it. The two differ exactly where the skip-unchanged <c>WHERE</c> bites.
    /// </returns>
    private async Task<int> UpsertAsync(
        string venue,
        InstrumentId instrument,
        SessionDefinition definition,
        string windowCentral,
        IReadOnlyList<SessionBar> bars,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        NpgsqlParameter[] parameters =
        [
            new("venue", NpgsqlDbType.Varchar) { Value = venue },
            new("instrument", NpgsqlDbType.Varchar) { Value = instrument.Symbol },
            new("session", NpgsqlDbType.Varchar) { Value = definition.Name },
            new("resolution", NpgsqlDbType.Integer) { Value = definition.BaseResolutionMinutes },
            new("window", NpgsqlDbType.Varchar) { Value = windowCentral },
            new("recorded", NpgsqlDbType.TimestampTz) { Value = now },
            new("tradeDates", NpgsqlDbType.Array | NpgsqlDbType.Date)
            {
                Value = bars.Select(b => b.TradeDate).ToArray(),
            },
            new("opensUtc", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz)
            {
                Value = bars.Select(b => b.OpenUtc).ToArray(),
            },
            new("closesUtc", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz)
            {
                Value = bars.Select(b => b.CloseUtc).ToArray(),
            },
            new("opens", NpgsqlDbType.Array | NpgsqlDbType.Numeric)
            {
                Value = bars.Select(b => b.Open).ToArray(),
            },
            new("highs", NpgsqlDbType.Array | NpgsqlDbType.Numeric)
            {
                Value = bars.Select(b => b.High).ToArray(),
            },
            new("lows", NpgsqlDbType.Array | NpgsqlDbType.Numeric)
            {
                Value = bars.Select(b => b.Low).ToArray(),
            },
            new("closes", NpgsqlDbType.Array | NpgsqlDbType.Numeric)
            {
                Value = bars.Select(b => b.Close).ToArray(),
            },
            new("volumes", NpgsqlDbType.Array | NpgsqlDbType.Bigint)
            {
                Value = bars.Select(b => b.Volume).ToArray(),
            },
            new("contracts", NpgsqlDbType.Array | NpgsqlDbType.Varchar)
            {
                Value = bars.Select(b => b.ContractId).ToArray(),
            },
            new("bucketCounts", NpgsqlDbType.Array | NpgsqlDbType.Integer)
            {
                Value = bars.Select(b => b.BaseBucketCount).ToArray(),
            },
        ];

        return await _database.Database
            .ExecuteSqlRawAsync(UpsertSessionBarsSql, parameters, cancellationToken)
            .ConfigureAwait(false);
    }
}
