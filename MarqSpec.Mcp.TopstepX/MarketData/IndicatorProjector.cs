using System.Globalization;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
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
/// <see cref="ProjectAsync"/>.
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
public sealed class IndicatorProjector(
    TopstepXDbContext database,
    IndicatorCatalog catalog,
    ILogger<IndicatorProjector> logger)
{
    private readonly TopstepXDbContext _database = database;
    private readonly IndicatorCatalog _catalog = catalog;
    private readonly ILogger<IndicatorProjector> _logger = logger;

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
    public async Task<int> ProjectAsync(
        string venue,
        InstrumentId instrument,
        int resolutionMinutes,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(venue);

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

        // AsNoTracking because NOTHING HERE MUTATES A BAR -- the projection reads them and writes
        // IndicatorValues. Tracked, a whole series' history sits in the change tracker being re-examined by
        // every subsequent SaveChanges, and EF's change detection is superlinear in the tracked count. That
        // is invisible on one series and is the whole cost on a store-wide rebuild (gh#73 review).
        //
        // Safe against the cache-aside path too: BarCacheService saves its bars BEFORE projecting -- a query
        // cannot see tracked-only rows (gh#31) -- so this reads exactly what tracking would have returned.
        List<BarRecord> stored = await _database.Bars
            .AsNoTracking()
            .Where(b => b.Venue == venue
                && b.Instrument == instrument.Symbol
                && b.ResolutionMinutes == resolutionMinutes)
            .OrderBy(b => b.BucketStart)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Loaded unconditionally, and the early return for an empty series is gone with it: reconciliation
        // has to run even when no bars remain, because values standing over a series whose bars have all been
        // deleted are exactly values nothing can justify.
        IQueryable<IndicatorValueRecord> values = _database.IndicatorValues
            .Where(v => v.Venue == venue
                && v.Instrument == instrument.Symbol
                && v.ResolutionMinutes == resolutionMinutes);

        // AsNoTracking, and it is not tidiness (gh#103's identity-map finding). These rows are written by SQL
        // the change tracker never sees, so a tracked copy is a stale entity the identity map would hand back
        // to the next read of IndicatorValues in the same scope in preference to the row it just read. It is
        // also what the perf note on the bar read above says: a whole series in the tracker is re-examined by
        // every subsequent SaveChanges.
        Dictionary<(string Indicator, int Period, DateTimeOffset Bucket), IndicatorValueRecord> existing =
            await values.AsNoTracking()
                .ToDictionaryAsync(v => (v.Indicator, v.Period, v.BucketStart), cancellationToken)
                .ConfigureAwait(false);

        List<Bar> bars = [.. stored.Select(ToBar)];

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
            ProjectSegment(run, existing, produced, pending);
        }

        int written = pending.Count == 0
            ? 0
            : await WriteAsync(venue, instrument, resolutionMinutes, pending, now, cancellationToken)
                .ConfigureAwait(false);

        int removed = await ReconcileAsync(
            venue, instrument, resolutionMinutes, stored.Count, existing, produced, cancellationToken)
            .ConfigureAwait(false);

        if (written + removed > 0)
        {
            _logger.LogDebug(
                "Projected {Count} indicator values for {Instrument} {Resolution}m over {Bars} bars in "
                + "{Segments} contract segment(s); removed {Removed} the bars no longer justify.",
                written,
                instrument.Symbol,
                resolutionMinutes,
                bars.Count,
                segments.Count,
                removed);
        }

        return written + removed;
    }

    /// <summary>
    /// Removes stored values this pass was configured to produce but did not.
    /// </summary>
    /// <param name="venue">The venue.</param>
    /// <param name="instrument">The instrument.</param>
    /// <param name="resolutionMinutes">The bar size in minutes.</param>
    /// <param name="barsRead">How many bars this pass loaded — the claim the guard below checks.</param>
    /// <param name="existing">
    /// Every stored value for the series. Untracked, which changes nothing here:
    /// <c>Remove</c> attaches an untracked row as <c>Deleted</c> and the statement it produces is the same
    /// <c>DELETE</c> by key.
    /// </param>
    /// <param name="produced">The keys this pass accounted for.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>How many rows were removed.</returns>
    /// <exception cref="InvalidOperationException">
    /// This pass read a different number of bars from what the store holds for the series, so its unscoped
    /// removal would reach values it never read the bars for.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>Scoped to the <c>(Indicator, Period)</c> pairs this catalogue computes</b>, and that scope is half
    /// the safety of it. Deleting everything a pass did not write would erase a series the operator merely
    /// configured a period away from — ATR(14) and ATR(3) are different numbers under different keys, and a
    /// projection configured for one has no standing over the other's rows. That would be data loss wearing a
    /// cleanup's clothes.
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
    /// </remarks>
    private async Task<int> ReconcileAsync(
        string venue,
        InstrumentId instrument,
        int resolutionMinutes,
        int barsRead,
        Dictionary<(string Indicator, int Period, DateTimeOffset Bucket), IndicatorValueRecord> existing,
        HashSet<(string Indicator, int Period, DateTimeOffset Bucket)> produced,
        CancellationToken cancellationToken)
    {
        HashSet<(string Indicator, int Period)> owned =
            [.. _catalog.All.Select(i => (i.Name, i.Period))];

        List<IndicatorValueRecord> unjustified = [];

        foreach ((var key, IndicatorValueRecord row) in existing)
        {
            if (!owned.Contains((key.Indicator, key.Period)) || produced.Contains(key))
            {
                continue;
            }

            unjustified.Add(row);
        }

        if (unjustified.Count == 0)
        {
            return 0;
        }

        // Checked BEFORE anything is removed, so a refusal costs nothing and leaves nothing half-done.
        int storedBars = await _database.Bars
            .CountAsync(
                b => b.Venue == venue
                    && b.Instrument == instrument.Symbol
                    && b.ResolutionMinutes == resolutionMinutes,
                cancellationToken)
            .ConfigureAwait(false);

        if (storedBars != barsRead)
        {
            throw new InvalidOperationException(
                "This projection pass read " + barsRead.ToString(CultureInfo.InvariantCulture) + " bars for "
                + instrument.Symbol + " " + resolutionMinutes.ToString(CultureInfo.InvariantCulture)
                + "m on '" + venue + "', but the store holds "
                + storedBars.ToString(CultureInfo.InvariantCulture)
                + " — so it did not read the whole series. Reconciliation removes every value the pass did not "
                + "produce and is not scoped by bucket range, so completing it would delete values whose bars "
                + "this pass never read. Either read the whole series in one snapshot, or scope the removal to "
                + "the bucket range that was actually read.");
        }

        foreach (IndicatorValueRecord row in unjustified)
        {
            _database.IndicatorValues.Remove(row);
        }

        return unjustified.Count;
    }

    /// <summary>Projects every configured indicator over one single-contract run of bars.</summary>
    /// <param name="bars">The run.</param>
    /// <param name="existing">Every stored value for the series — the pre-filter, not the decision.</param>
    /// <param name="produced">The keys this pass accounted for. Appended to.</param>
    /// <param name="pending">The values that are not what the store already holds. Appended to.</param>
    /// <remarks>
    /// The run is what each indicator sees, so its smoothing seeds from the run's own first bar. Handing the
    /// whole series in would let a roll gap -- routinely tens of points between adjacent quarters -- be
    /// smoothed forward as though it were price action, which is exactly the number nobody would question.
    /// </remarks>
    private void ProjectSegment(
        IReadOnlyList<Bar> bars,
        Dictionary<(string Indicator, int Period, DateTimeOffset Bucket), IndicatorValueRecord> existing,
        HashSet<(string Indicator, int Period, DateTimeOffset Bucket)> produced,
        List<PendingValue> pending)
    {
        foreach (IIndicator indicator in _catalog.All)
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
                if (existing.TryGetValue(key, out IndicatorValueRecord? row) && row.Value == value)
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

    /// <summary>
    /// The value write, as one statement the store resolves against the rows it has committed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The conflict target is the composite primary key</b> — the same key the pre-read looked the values
    /// up by, reached directly instead of being inferred from a read of it. Under
    /// <see cref="SeriesUnitOfWork.Isolation"/> a conflict against a row committed <i>after</i> this
    /// transaction's snapshot is refused with <c>40001</c> rather than <c>23505</c>, which is what
    /// <c>R-2.10</c> already retries once — and the retry runs over the store the winner committed, where the
    /// pre-filter above simply recognises those values as already produced.
    /// </para>
    /// <para>
    /// <b>There is no skip-unchanged <c>WHERE</c>, and its absence is deliberate</b> — see the comment at the
    /// pre-filter. Nothing reaches this statement that the C# comparison did not already find different, and
    /// that comparison is made at the column's own scale.
    /// </para>
    /// <para>
    /// <b>Arrays rather than a row per value</b>: a whole series times eleven indicators is tens of thousands
    /// of rows, and four parameters each would exceed the protocol's parameter limit many times over.
    /// </para>
    /// </remarks>
    private const string UpsertValuesSql = """
        INSERT INTO "IndicatorValues" (
            "Venue", "Instrument", "ResolutionMinutes", "Indicator", "Period", "BucketStart",
            "Value", "RecordedAt")
        SELECT @venue, @instrument, @resolution, a.indicator, a.period, a.bucket, a.value, @recorded
        FROM unnest(@indicators, @periods, @buckets, @values)
             AS a(indicator, period, bucket, value)
        ON CONFLICT ("Venue", "Instrument", "ResolutionMinutes", "Indicator", "Period", "BucketStart")
        DO UPDATE SET
            "Value" = excluded."Value",
            "RecordedAt" = excluded."RecordedAt"
        """;

    /// <summary>One value this pass found the store does not already hold.</summary>
    /// <param name="Indicator">The indicator's stable name.</param>
    /// <param name="Period">The period, part of the storage key.</param>
    /// <param name="BucketStart">The bucket.</param>
    /// <param name="Value">The value, already rounded to the stored scale.</param>
    private readonly record struct PendingValue(
        string Indicator,
        int Period,
        DateTimeOffset BucketStart,
        decimal Value);

    /// <summary>Writes the values this pass found the store does not already hold.</summary>
    /// <param name="venue">The venue.</param>
    /// <param name="instrument">The instrument.</param>
    /// <param name="resolutionMinutes">The bar size in minutes.</param>
    /// <param name="pending">The values to write.</param>
    /// <param name="now">The instant this pass runs at.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>
    /// <b>How many rows the store reports it wrote or revised</b> — the statement's own row count, never
    /// <c>pending.Count</c>. There is no skip-unchanged <c>WHERE</c> on this statement, so the two agree
    /// today; the contract is the store's number, so they still agree if one is ever added (gh#387).
    /// </returns>
    private async Task<int> WriteAsync(
        string venue,
        InstrumentId instrument,
        int resolutionMinutes,
        List<PendingValue> pending,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        NpgsqlParameter[] parameters =
        [
            new("venue", NpgsqlDbType.Varchar) { Value = venue },
            new("instrument", NpgsqlDbType.Varchar) { Value = instrument.Symbol },
            new("resolution", NpgsqlDbType.Integer) { Value = resolutionMinutes },
            new("recorded", NpgsqlDbType.TimestampTz) { Value = now },
            new("indicators", NpgsqlDbType.Array | NpgsqlDbType.Varchar)
            {
                Value = pending.Select(v => v.Indicator).ToArray(),
            },
            new("periods", NpgsqlDbType.Array | NpgsqlDbType.Integer)
            {
                Value = pending.Select(v => v.Period).ToArray(),
            },
            new("buckets", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz)
            {
                Value = pending.Select(v => v.BucketStart).ToArray(),
            },
            new("values", NpgsqlDbType.Array | NpgsqlDbType.Numeric)
            {
                Value = pending.Select(v => v.Value).ToArray(),
            },
        ];

        return await _database.Database
            .ExecuteSqlRawAsync(UpsertValuesSql, parameters, cancellationToken)
            .ConfigureAwait(false);
    }

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
