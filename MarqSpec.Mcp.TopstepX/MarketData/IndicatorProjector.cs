using System.Globalization;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// Recomputes the stored indicator values for a series (ADR-0006).
/// </summary>
/// <remarks>
/// <para>
/// Runs inside the bar-write unit of work, so an indicator exists the moment its bar does and no read ever
/// pays for a computation.
/// </para>
/// <para>
/// <b>It always recomputes from the start of the stored series</b>, never from a window around the changed
/// bars. Wilder smoothing is recursive: every value depends on the entire history before it, so seeding from
/// a window makes the answer depend on how much history happened to be loaded. Two runs over identical data
/// would then disagree, and neither would be wrong in a way anyone could point at.
/// </para>
/// <para>
/// <b>The one thing it will not smooth across is a contract roll</b> (ADR-0011). A symbol-keyed series holds
/// the expiring quarter and the new one side by side, and the gap between them is a bookkeeping event rather
/// than market movement. The series is therefore split into contiguous single-contract runs and each is
/// projected on its own, seeded from <i>that run's</i> first bar. The warm-up restarts at the roll, so the
/// first values after it are <b>absent</b> — which is the honest answer: the new contract has not traded
/// enough bars to measure yet. That is a refinement of the paragraph above, not a contradiction of it: the
/// seams are a function of the stored bars, so a rebuild still replays to the same numbers.
/// </para>
/// <para>
/// <b>It reconciles rather than only upserting.</b> A pass removes every value it is configured to produce
/// that the current bars no longer justify. Before segmenting, that could not happen: the warm-up boundary
/// was the start of the stored series, so a bucket could only ever move from <i>not computable</i> to
/// <i>computable</i>, and an upsert-only projection was safe. A contract seam moves the boundary in the other
/// direction — a bucket that had a value can correctly have none — and a row nothing rewrites is a row that
/// stays. That row is a stored number the bars cannot account for, which is the failure gh#42 is about.
/// </para>
/// <para>
/// <b>The store decides insert-versus-update, not this process</b> (gh#133). The values a pass produces are
/// written with one <c>ON CONFLICT … DO UPDATE</c> on the composite key, so a pass whose snapshot missed a
/// concurrent pass's rows updates them rather than colliding on the key. Deciding it from the pre-read — which
/// is what this did until gh#133 — decides it against <i>this</i> transaction's snapshot, and the loser took
/// a <c>23505</c> out of <c>get_bars</c> for an ordinary question. The removal half stays with the change
/// tracker, so the two halves need a transaction around them rather than merely one snapshot; see
/// <see cref="ProjectAsync(SeriesKey, DateTimeOffset, CancellationToken)"/>.
/// </para>
/// <para>
/// <b>The cost, stated honestly.</b> That makes a projection O(series), not O(changed). A year of 5-minute
/// bars is on the order of 70,000 rows per instrument — comfortably fast, and bounded by how much history the
/// operator keeps rather than by how often bars arrive. If it ever stops being comfortable, the answer is an
/// incremental form that carries the smoothing state forward explicitly, not a moving seed window.
/// </para>
/// </remarks>
/// <param name="database">The store.</param>
/// <param name="catalog">The indicators to project.</param>
/// <param name="logger">The logger.</param>
/// <param name="telemetry">The app-owned meter.</param>
public sealed class IndicatorProjector(
    TopstepXDbContext database,
    IndicatorCatalog catalog,
    ILogger<IndicatorProjector> logger,
    HostTelemetry telemetry)
{
    private readonly TopstepXDbContext _database = database;
    private readonly IndicatorCatalog _catalog = catalog;
    private readonly ILogger<IndicatorProjector> _logger = logger;
    private readonly HostTelemetry _telemetry = telemetry;

    /// <summary>
    /// Recomputes every configured indicator for one series and writes the values that changed.
    /// </summary>
    /// <param name="venue">The venue.</param>
    /// <param name="instrument">The instrument.</param>
    /// <param name="resolutionMinutes">The bar size in minutes.</param>
    /// <param name="now">The instant this pass runs at, stamped on changed rows.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>How many rows this pass changed — written, updated, or <b>removed</b>.</returns>
    /// <exception cref="InvalidOperationException">
    /// The caller opened no transaction, so the two halves of this pass could not commit together.
    /// </exception>
    /// <remarks>
    /// Does <b>not</b> call <c>SaveChanges</c>. The caller owns the unit of work, so bars and the indicators
    /// derived from them commit together — a partial commit would leave a bar whose indicators silently do
    /// not exist, which reads back as a market that produced no signal.
    /// <para>
    /// A confirming rebuild still produces an <b>empty diff</b>: a value the pass recomputed to the same
    /// number counts as produced, so reconciliation removes nothing.
    /// </para>
    /// <para>
    /// <b>The caller must give this pass ONE SNAPSHOT of the store.</b> Two reads happen here — the bars, then
    /// the values standing over them — and the second is reconciled against the first. Under
    /// <c>READ COMMITTED</c> those are two snapshots, so a concurrent fill committing between them leaves this
    /// pass holding values it never saw the bars for, and reconciliation deletes them (gh#73). Both call sites
    /// therefore wrap it in a <see cref="System.Data.IsolationLevel.RepeatableRead"/> transaction, and
    /// <see cref="ReconcileAsync"/> refuses rather than deleting if that ever stops being true.
    /// </para>
    /// <para>
    /// <b>And it must be a transaction, not merely one snapshot</b> (gh#133). The values are written by a
    /// statement the store executes when it is sent, while the removals still go through the change tracker
    /// and wait for the caller's <c>SaveChanges</c>. Inside a transaction that difference is invisible; outside
    /// one the write autocommits and the removals do not, so this refuses rather than half-committing.
    /// </para>
    /// </remarks>
    public Task<int> ProjectAsync(
        string venue,
        InstrumentId instrument,
        int resolutionMinutes,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(venue);

        return ProjectAsync(
            new SeriesKey.Resolution(venue, instrument.Symbol, resolutionMinutes), now, cancellationToken);
    }

    /// <summary>
    /// Recomputes every configured indicator for one series — resolution or session — and writes what changed.
    /// </summary>
    /// <param name="key">Which series.</param>
    /// <param name="now">The instant this pass runs at, stamped on changed rows.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>How many rows this pass changed — written, updated, or <b>removed</b>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The caller opened no transaction, so the two halves of this pass could not commit together.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>This is the body; the overload above forwards to it.</b> A session series and a resolution series
    /// differ in exactly two places — which tables they live in (<see cref="ISeriesTables"/>) and which
    /// indicators the catalogue computes over them (<see cref="IndicatorCatalog.ForSeries"/>) — and in
    /// nothing else. Everything the resolution series relies on, from the contract segmenting to the rounding
    /// to the reconcile's whole-series guard, is the same code running over the other pair of tables, so the
    /// two kinds cannot drift apart on a number.
    /// </para>
    /// <para>
    /// Every rule the overload's remarks state applies here unchanged: no <c>SaveChanges</c>, one
    /// <see cref="System.Data.IsolationLevel.RepeatableRead"/> snapshot, and a transaction rather than merely
    /// a snapshot.
    /// </para>
    /// </remarks>
    public async Task<int> ProjectAsync(
        SeriesKey key,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(key.Venue);

        // CHECKED FIRST, so a refusal costs nothing and leaves nothing half-done -- the same shape as the
        // whole-series guard below, and it cannot fire as shipped for the same reason: both call sites go
        // through SeriesUnitOfWork. It fires when a third one is added, which is the only way to get this
        // wrong.
        if (_database.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "A projection pass writes its values with one statement the store runs as it is sent, and "
                + "removes the values the bars no longer justify through the change tracker, which waits for "
                + "the caller's SaveChanges. Outside a transaction the first commits on its own and the "
                + "second does not, leaving values standing that this very pass decided to remove — and bars "
                + "committed without the indicators derived from them. Wrap this call in a transaction; "
                + "SeriesUnitOfWork is the one shape every series write uses.");
        }

        // WHICH TABLES, decided from the key and nowhere else. The pair is built here rather than injected:
        // these constructors are hand-built at fifty-odd sites across the two test projects, and one more
        // parameter would be an edit to every one of them (gh#501).
        ISeriesTables tables = ISeriesTables.For(key, _database);

        // WHAT THIS SERIES' VOCABULARY IS, and it has to be the same list for the compute below and the
        // reconcile at the end. Read once for exactly that reason: two calls could not disagree today, and a
        // compute walking one list while the reconcile walked another would delete rows on every pass.
        IReadOnlyList<IIndicator> catalogue = _catalog.ForSeries(key);

        List<Bar> bars = await tables.LoadBarsAsync(cancellationToken).ConfigureAwait(false);

        // Loaded unconditionally, and the early return for an empty series is gone with it: reconciliation
        // has to run even when no bars remain, because values standing over a series whose bars have all been
        // deleted are exactly values nothing can justify.
        Dictionary<(string Indicator, int Period, DateTimeOffset Bucket), decimal> existing =
            await tables.LoadValuesAsync(cancellationToken).ConfigureAwait(false);

        // Every key this pass accounted for -- written, updated, OR recomputed to the same number. The last
        // case is why a confirming rebuild still reconciles to an empty diff.
        HashSet<(string Indicator, int Period, DateTimeOffset Bucket)> produced = [];

        // Every value this pass decided is not what the store already holds. Collected across all segments
        // and written once at the end, so the whole series costs one statement rather than one per contract
        // run -- and so no key can appear twice in it, which ON CONFLICT cannot resolve within one command.
        List<PendingValue> pending = [];

        // One run per contract, each projected on its own. A single-contract series -- which is every series
        // that has not yet lived through a roll -- is one segment, so this costs nothing and changes nothing
        // for it.
        IReadOnlyList<ContractSegment> segments = ContractRollDetector.Segment(bars);

        foreach (ContractSegment segment in segments)
        {
            List<Bar> run = bars.GetRange(segment.StartIndex, segment.BarCount);
            ProjectSegment(catalogue, run, existing, produced, pending);
        }

        int written = pending.Count == 0
            ? 0
            : await WriteAsync(tables, pending, now, cancellationToken).ConfigureAwait(false);

        // COUNTED PER INDICATOR, from what this pass decided to write rather than from the statement's row
        // count. The two differ only where the store resolved a conflict against a value a concurrent pass
        // had already committed, and `pending` is the honest answer to "what did this projection produce" --
        // which is the question an operator watching a catalogue addition roll through is asking.
        //
        // A CONFIRMING PASS RECORDS NOTHING, because `pending` is empty for it. That is the point: an empty
        // diff is what proves indicators are reproducible projections (ADR-0006), and a rebuild that showed
        // up here as a spike would say the opposite.
        foreach (IGrouping<string, PendingValue> byIndicator in pending.GroupBy(
            value => value.Indicator, StringComparer.Ordinal))
        {
            _telemetry.IndicatorProjected(
                byIndicator.Key, key.Instrument, key, byIndicator.Count());
        }

        // THE BUCKETS THIS SERIES ACTUALLY HAS BARS AT. Built from the same list the projection computed
        // over, so it is the same snapshot the whole-series guard below checks -- never a second read.
        HashSet<DateTimeOffset> barBuckets = [.. bars.Select(b => b.OpenTime)];

        ReconcileTally tally = await ReconcileAsync(
            key, tables, catalogue, bars.Count, barBuckets, existing, produced, cancellationToken)
            .ConfigureAwait(false);

        int removed = tally.Total;

        if (written + removed > 0)
        {
            _logger.LogDebug(
                "Projected {Count} indicator values for {Series} over {Bars} bars in "
                + "{Segments} contract segment(s); removed {Removed} the bars no longer justify.",
                written,
                key.Describe(),
                bars.Count,
                segments.Count,
                removed);
        }

        // THE TWO ORPHAN KINDS ARE REPORTED AT INFORMATION, SEPARATELY, AND NEVER FOLDED INTO THE LINE ABOVE
        // (gh#571). The Debug line is the ordinary bookkeeping of a pass; these two are the store admitting it
        // held numbers nothing could reproduce, and they call for different follow-ups -- a period the
        // operator retired is a configuration question, a bucket with no bar is a bars question. A single
        // "removed 2" cannot tell an operator which of their own actions caused it, and a Debug-only line is
        // close enough to silent that nobody would ever learn either.
        if (tally.Retired > 0 || tally.Orphaned > 0)
        {
            _logger.LogInformation(
                "Swept {Retired} stored indicator value(s) for {Series} under "
                + "an (indicator, period) pair the catalogue no longer computes, and {Orphaned} standing at a "
                + "bucket with no bar. Neither could be reproduced from the stored bars (ADR-0006); both are "
                + "recoverable by restoring the configuration or the bars and replaying.",
                tally.Retired,
                key.Describe(),
                tally.Orphaned);
        }

        return written + removed;
    }

    /// <summary>What one reconcile pass removed, split by why it removed it.</summary>
    /// <param name="Unjustified">
    /// Values under a pair the catalogue computes, at a bucket that still has a bar, that this pass declined
    /// to produce — the warm-up that restarts at a contract seam (ADR-0011).
    /// </param>
    /// <param name="Retired">
    /// Values under an <c>(Indicator, Period)</c> pair the catalogue no longer computes.
    /// </param>
    /// <param name="Orphaned">Values standing at a bucket the series holds no bar at.</param>
    private readonly record struct ReconcileTally(int Unjustified, int Retired, int Orphaned)
    {
        /// <summary>How many rows were removed in all.</summary>
        public int Total => Unjustified + Retired + Orphaned;
    }

    /// <summary>
    /// Removes every stored value for this series that the current bars and catalogue cannot account for.
    /// </summary>
    /// <param name="key">The series.</param>
    /// <param name="tables">The series' tables — where the count is read and the removal is made.</param>
    /// <param name="catalogue">
    /// What this series' projection is configured to produce, and therefore the only thing it may delete.
    /// The <b>same list</b> the compute walked: two reads of it could not disagree today, and a compute and a
    /// reconcile over different lists would delete rows on every pass.
    /// </param>
    /// <param name="barsRead">How many bars this pass loaded — the claim the guard below checks.</param>
    /// <param name="barBuckets">
    /// The buckets this pass read a bar at, from the same list it projected over — never a second read.
    /// </param>
    /// <param name="existing">
    /// Every stored value for the series. Untracked, which changes nothing here:
    /// <c>Remove</c> attaches an untracked row as <c>Deleted</c> and the statement it produces is the same
    /// <c>DELETE</c> by key.
    /// </param>
    /// <param name="produced">The keys this pass accounted for.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>How many rows were removed, split by why.</returns>
    /// <exception cref="InvalidOperationException">
    /// This pass read a different number of bars from what the store holds for the series, so its unscoped
    /// removal would reach values it never read the bars for.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>Three kinds of row are removed, counted apart because they mean different things</b> (gh#571):
    /// </para>
    /// <list type="number">
    /// <item>
    /// <b>Unjustified</b> — the pair is computed and the bucket has a bar, but this pass declined to produce
    /// a value there. That is the warm-up restarting at a contract seam (ADR-0011), and it is the case this
    /// method was written for.
    /// </item>
    /// <item>
    /// <b>Retired</b> — the <c>(Indicator, Period)</c> pair is one the catalogue no longer computes.
    /// </item>
    /// <item>
    /// <b>Orphaned</b> — the value stands at a bucket this series holds no bar at, because the bars under it
    /// were deleted (a base revision, a session-bar discard, <c>reselect-bars</c>) and nothing links the two
    /// tables (ADR-0011 §2 rejected the foreign key).
    /// </item>
    /// </list>
    /// <para>
    /// <b>The second kind used to be skipped, deliberately, and gh#571 reversed that.</b> The old argument was
    /// that ATR(14) and ATR(3) are different numbers under different keys, so a projection configured for one
    /// has no standing over the other's rows — sweeping them would be data loss wearing a cleanup's clothes.
    /// The half that is right is kept and is enforced below: this reaches only the series it projected. The
    /// half that is wrong is that a retired pair's rows are not *another series* — they are this one, under a
    /// window nothing computes any more. No pass recomputes them, so no replay can confirm them and none can
    /// correct them: <c>rebuild-indicators</c> reports an empty diff over exactly the rows that need it, and
    /// they read back as ordinary numbers. ADR-0006 forbids the store to hold a value it cannot reproduce
    /// from its bars, and that is what these are. They are also cheap to get back — one configuration line
    /// and one replay — because reproducibility runs both ways.
    /// </para>
    /// <para>
    /// <b>It is NOT scoped by bucket range, and that is the other half.</b> A pass sweeps the whole series,
    /// which is sound only because a pass <i>reads</i> the whole series — true at both call sites, and until
    /// now guaranteed by nothing. A <c>ProjectAsync</c> that took a range and narrowed the bar query while
    /// leaving this sweep alone would delete every value outside that range, silently, and the loss would look
    /// exactly like a warm-up. So the claim is checked rather than trusted: if the bars this pass read and the
    /// bars the store holds disagree, it refuses. Either widen the read, or narrow the removal by the same
    /// amount.
    /// </para>
    /// <para>
    /// <b>It cannot fire as shipped, and that is the point rather than a weakness.</b> Both counts come from
    /// one snapshot under the same predicate, so under <see cref="SeriesUnitOfWork.Isolation"/> they cannot
    /// disagree. It fires when someone narrows the bar query — its real purpose — or when a call site opens no
    /// transaction, or weakens the isolation level. A regression guard, then, not a second line of defence
    /// standing behind gh#73 in production. The check costs one count, and only when there is something to
    /// remove, which is neither the confirming rebuild nor the ordinary fill.
    /// </para>
    /// <para>
    /// A warm-up bucket that has never had a value costs nothing here: there is no row to remove. What this
    /// reaches is the row that <i>used</i> to be justified — the ATR smoothed across a splice that a later,
    /// better-informed pass correctly declines to compute.
    /// </para>
    /// <para>
    /// <b>The sweep is bounded by the series this pass already read</b>, and by nothing else. It walks
    /// <paramref name="existing"/> and <paramref name="barBuckets"/>, both of which are in hand — no extra
    /// query, no store-wide scan, and no reach outside <c>(venue, instrument, resolution)</c>.
    /// </para>
    /// </remarks>
    private async Task<ReconcileTally> ReconcileAsync(
        SeriesKey key,
        ISeriesTables tables,
        IReadOnlyList<IIndicator> catalogue,
        int barsRead,
        HashSet<DateTimeOffset> barBuckets,
        Dictionary<(string Indicator, int Period, DateTimeOffset Bucket), decimal> existing,
        HashSet<(string Indicator, int Period, DateTimeOffset Bucket)> produced,
        CancellationToken cancellationToken)
    {
        HashSet<(string Indicator, int Period)> owned =
            [.. catalogue.Select(i => (i.Name, i.Period))];

        List<(string Indicator, int Period, DateTimeOffset Bucket)> doomed = [];
        int unjustified = 0;
        int retired = 0;
        int orphaned = 0;

        foreach ((var stored, _) in existing)
        {
            // Produced implies both that the pair is computed and that the bucket has a bar, so this one
            // check is the whole of "the current bars account for this row".
            if (produced.Contains(stored))
            {
                continue;
            }

            // CLASSIFIED IN THIS ORDER SO THE THREE COUNTS CANNOT DOUBLE-COUNT. A row can be both retired and
            // orphaned; the bar is the more basic fact, so it wins, and Total is always the row count.
            if (!barBuckets.Contains(stored.Bucket))
            {
                orphaned++;
            }
            else if (!owned.Contains((stored.Indicator, stored.Period)))
            {
                retired++;
            }
            else
            {
                unjustified++;
            }

            doomed.Add(stored);
        }

        if (doomed.Count == 0)
        {
            return default;
        }

        // Checked BEFORE anything is removed, so a refusal costs nothing and leaves nothing half-done.
        int storedBars = await tables.CountBarsAsync(cancellationToken).ConfigureAwait(false);

        if (storedBars != barsRead)
        {
            throw new InvalidOperationException(
                "This projection pass read " + barsRead.ToString(CultureInfo.InvariantCulture) + " bars for "
                + key.Describe() + " on '" + key.Venue + "', but the store holds "
                + storedBars.ToString(CultureInfo.InvariantCulture)
                + " — so it did not read the whole series. Reconciliation removes every value the pass did not "
                + "produce and is not scoped by bucket range, so completing it would delete values whose bars "
                + "this pass never read. Either read the whole series in one snapshot, or scope the removal to "
                + "the bucket range that was actually read.");
        }

        foreach ((string Indicator, int Period, DateTimeOffset Bucket) row in doomed)
        {
            tables.Remove(row);
        }

        return new ReconcileTally(unjustified, retired, orphaned);
    }

    /// <summary>Projects every configured indicator over one single-contract run of bars.</summary>
    /// <param name="catalogue">What this series' projection computes.</param>
    /// <param name="bars">The run.</param>
    /// <param name="existing">Every stored value for the series — the pre-filter, not the decision.</param>
    /// <param name="produced">The keys this pass accounted for. Appended to.</param>
    /// <param name="pending">The values that are not what the store already holds. Appended to.</param>
    /// <remarks>
    /// The run is what each indicator sees, so its smoothing seeds from the run's own first bar. Handing the
    /// whole series in would let a roll gap -- routinely tens of points between adjacent quarters -- be
    /// smoothed forward as though it were price action, which is exactly the number nobody would question.
    /// </remarks>
    private static void ProjectSegment(
        IReadOnlyList<IIndicator> catalogue,
        IReadOnlyList<Bar> bars,
        Dictionary<(string Indicator, int Period, DateTimeOffset Bucket), decimal> existing,
        HashSet<(string Indicator, int Period, DateTimeOffset Bucket)> produced,
        List<PendingValue> pending)
    {
        foreach (IIndicator indicator in catalogue)
        {
            IReadOnlyList<decimal?> values = indicator.Compute(bars);

            for (int i = 0; i < values.Count; i++)
            {
                // A null is the indicator saying it cannot measure yet. It is not written: an absent row and a
                // row holding a stand-in value read back identically, and only one of them is honest.
                if (values[i] is not { } computed)
                {
                    continue;
                }

                // ROUNDED TO THE STORED SCALE BEFORE ANYTHING ELSE.
                //
                // The column is numeric(18,8), so Postgres keeps 8 places; the computation carries the full
                // decimal precision -- 38.95895082 read back against 38.958950821743... computed. Comparing
                // those is always unequal, which made the "skip unchanged" guard below dead code: every
                // rebuild rewrote every row and moved every RecordedAt, so the field recorded when a rebuild
                // last ran rather than when a value last changed.
                //
                // AwayFromZero matches Postgres numeric rounding. Banker's rounding here would store a value
                // the database would have rounded differently, and the comparison would fail again -- for a
                // different reason, on a subset of rows, which is harder to notice than failing always.
                decimal value = Math.Round(computed, TopstepXDbContext.PriceScale, MidpointRounding.AwayFromZero);

                (string Name, int Period, DateTimeOffset OpenTime) key =
                    (indicator.Name, indicator.Period, bars[i].OpenTime);

                // Recorded before the unchanged check, not after: a value the pass recomputed to the same
                // number is still a value the bars justify, and reconciliation must not mistake "confirmed"
                // for "not produced" and delete the whole series on every rebuild.
                produced.Add(key);

                // THE SKIP-UNCHANGED RULE, AND IT IS STATED ONCE -- here, in C#, rather than restated in the
                // statement's own WHERE the way the bar write states its (gh#103). The difference is the
                // rounding directly above: `value` has already been coerced to the column's own scale, and
                // `row.Value` came out of the column, so both sides of this comparison are numeric(18,8) and
                // it cannot answer "changed" for a difference numeric(18,8) cannot hold. That is the gh#37
                // shape, and rounding is where this projection closed it. The bar write cannot do the same --
                // it compares six prices straight off the venue answer, at full decimal precision -- which is
                // why the rule lives in SQL there and here does not.
                //
                // A second copy in SQL would therefore be a clause nothing could ever make fail: unreachable
                // by any input, and so unverifiable by any test.
                if (existing.TryGetValue(key, out decimal stored) && stored == value)
                {
                    // Unchanged. Leaving RecordedAt alone is what makes a confirming rebuild produce an
                    // empty diff rather than rewriting every timestamp in the series.
                    continue;
                }

                // WHICH OF INSERT AND UPDATE THIS IS, IS NOT DECIDED HERE. `existing` is read from this
                // transaction's snapshot, so a concurrent pass that has committed a value this one still
                // believes absent makes both INSERT it -- and the loser took 23505 out of `get_bars` for an
                // ordinary question (gh#133). The store decides, against the row it has committed.
                pending.Add(new PendingValue(indicator.Name, indicator.Period, bars[i].OpenTime, value));
            }
        }
    }

    /// <summary>Writes the values this pass found the store does not already hold.</summary>
    /// <param name="tables">The series' tables — whose statement and parameter binding this runs.</param>
    /// <param name="pending">The values to write.</param>
    /// <param name="now">The instant this pass runs at.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>
    /// <b>How many rows the store reports it wrote or revised</b> — the statement's own row count, never
    /// <c>pending.Count</c>. There is no skip-unchanged <c>WHERE</c> on either statement, so the two agree
    /// today; the contract is the store's number, so they still agree if one is ever added (gh#387).
    /// </returns>
    private async Task<int> WriteAsync(
        ISeriesTables tables,
        List<PendingValue> pending,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        await _database.Database
            .ExecuteSqlRawAsync(
                tables.UpsertSql, tables.UpsertParameters(pending, now), cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Maps a stored row to the domain bar the indicators compute over.</summary>
    /// <param name="record">The stored row.</param>
    /// <returns>The bar.</returns>
    public static Bar ToBar(BarRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new Bar(
            record.BucketStart,
            record.Open,
            record.High,
            record.Low,
            record.Close,
            record.Volume,
            record.ContractId);
    }
}
