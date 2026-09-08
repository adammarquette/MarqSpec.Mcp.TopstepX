using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using Npgsql;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// The two tables one series is projected between — its bars, and the values standing over them.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the only thing that differs between a resolution series and a session series.</b> A resolution
/// series reads <c>Bars</c> and writes <c>IndicatorValues</c>, keyed by a bar size; a session series reads
/// <c>SessionBars</c> and writes <c>SessionIndicatorValues</c>, keyed by a session name. Everything above —
/// the segmenting, the warm-up, the rounding, the skip-unchanged rule, the reconcile and the whole-series
/// guard — is identical, and identical is the point: two copies of a projection are two projections free to
/// disagree about a number nobody would question (ADR-0006).
/// </para>
/// <para>
/// <b>Built from the key, never injected.</b> <see cref="For"/> is the only way one is made, and it is
/// reached from inside <see cref="IndicatorProjector"/> and <see cref="IndicatorCacheService"/> rather than
/// taken as a constructor parameter. That is deliberate: the constructors of those services are hand-built
/// at fifty-odd sites across the two test projects, and a new constructor parameter would be a fifty-odd-site
/// edit that made "every existing suite still passes, unmodified" impossible to claim.
/// </para>
/// <para>
/// <b>An instance is per pass, and it remembers what it loaded.</b> <see cref="LoadValuesAsync"/> hands back
/// the numbers the pass compares against and keeps the rows they came from, so
/// <see cref="Remove"/> can take a value out through the change tracker without the caller having to hold —
/// or name — a stored entity type. One is therefore used for exactly one projection or one probe, and never
/// shared.
/// </para>
/// </remarks>
internal interface ISeriesTables
{
    /// <summary>
    /// The statement that writes the values, as one <c>ON CONFLICT … DO UPDATE</c> on the composite key.
    /// </summary>
    /// <remarks>
    /// <b>The store decides insert-versus-update, not the pass</b> (gh#133), and the conflict target is the
    /// primary key the pre-read looked the values up by.
    /// </remarks>
    string UpsertSql { get; }

    /// <summary>Builds the tables for one series.</summary>
    /// <param name="key">The series.</param>
    /// <param name="database">The store.</param>
    /// <param name="sessions">
    /// The closed vocabulary of session names. Required when <paramref name="key"/> is a
    /// <see cref="SeriesKey.Session"/> — bar reads restate that definition's provenance (ADR-0022 §4).
    /// Ignored for a resolution series.
    /// </param>
    /// <returns>The tables that series lives in.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="sessions"/> is <see langword="null"/> and <paramref name="key"/> is a session series.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A series kind with no tables behind it. Enumerated rather than defaulted: a kind that fell through to
    /// another kind's tables would project one series' bars into the other's rows.
    /// </exception>
    static ISeriesTables For(SeriesKey key, TopstepXDbContext database, SessionCatalog? sessions = null)
    {
        ArgumentNullException.ThrowIfNull(key);

        return key switch
        {
            SeriesKey.Resolution resolution => new ResolutionSeriesTables(resolution, database),
            SeriesKey.Session session => new SessionSeriesTables(
                session,
                database,
                sessions ?? throw new ArgumentNullException(
                    nameof(sessions),
                    "A session series' bar reads restate the standing definition's provenance (ADR-0022 §4). "
                    + "Pass the SessionCatalog rather than projecting over every row that shares the name.")),
            _ => throw new ArgumentOutOfRangeException(
                nameof(key),
                key.GetType().Name,
                "No tables are known for that series kind. Add its implementation rather than letting it "
                + "fall through: the wrong pair here reads one series' bars and writes another's values."),
        };
    }

    /// <summary>
    /// The whole stored series, ascending, as the bars the indicators compute over.
    /// </summary>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The bars.</returns>
    /// <remarks>
    /// <b>Untracked, and the whole series.</b> Nothing here mutates a bar, and a whole series in the change
    /// tracker is re-examined by every subsequent <c>SaveChanges</c> (gh#73 review). Ascending because
    /// <c>IndicatorGuard</c> refuses anything else — a shuffled series does not fail, it computes a different,
    /// wrong number.
    /// </remarks>
    Task<List<Bar>> LoadBarsAsync(CancellationToken cancellationToken);

    /// <summary>How many bars the store holds for the series.</summary>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The count.</returns>
    /// <remarks>
    /// Uncapped, because this is the reconcile's whole-series claim: the number it is checked against is how
    /// many bars the pass actually read, and a capped count could agree by coincidence.
    /// </remarks>
    Task<int> CountBarsAsync(CancellationToken cancellationToken);

    /// <summary>How many bars the store holds, counted no further than a cap.</summary>
    /// <param name="cap">The largest count worth knowing.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The count, at most <paramref name="cap"/>.</returns>
    /// <remarks>
    /// <b>The cap is what keeps the read-time probe flat.</b> The only thing the number decides is
    /// <c>WarmupBars &lt;= bars</c> for each catalogue member, so any count at or above the largest warm-up
    /// answers every one of those comparisons identically.
    /// </remarks>
    Task<int> CountBarsAsync(int cap, CancellationToken cancellationToken);

    /// <summary>
    /// Every value the store holds for the series, keyed by <c>(Indicator, Period, Bucket)</c>.
    /// </summary>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The values, at the column's own scale.</returns>
    /// <remarks>
    /// <b>Loaded unconditionally, and untracked.</b> Unconditionally because reconciliation has to run even
    /// when no bars remain — values standing over a series whose bars have all gone are exactly the values
    /// nothing can justify. Untracked because these rows are written by SQL the change tracker never sees, so
    /// a tracked copy is a stale entity the identity map would hand back to the next read in the same scope
    /// (gh#103).
    /// </remarks>
    Task<Dictionary<(string Indicator, int Period, DateTimeOffset Bucket), decimal>> LoadValuesAsync(
        CancellationToken cancellationToken);

    /// <summary>
    /// The series' newest buckets, newest first, capped at the largest warm-up.
    /// </summary>
    /// <param name="cap">How many buckets is enough — the catalogue's largest warm-up.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The openings, newest first, at most <paramref name="cap"/> of them.</returns>
    /// <remarks>
    /// <b>The buckets themselves rather than a count of them</b> (gh#531). Still one query, still capped, and
    /// it answers a second question the count could not: <c>tail[w - 1]</c> is the <c>w</c>-th newest bucket —
    /// which is how far a warm-up of <c>w</c> can push a pair's newest value back before the pair is genuinely
    /// short. Counted in bars, not in time: a threshold of <c>newest - (w - 1) × resolution</c> would be wrong
    /// across every weekend and session break, where the stored buckets are not contiguous.
    /// </remarks>
    Task<List<DateTimeOffset>> LoadNewestBucketsAsync(int cap, CancellationToken cancellationToken);

    /// <summary>
    /// The newest bucket the store holds a value at, per <c>(Indicator, Period)</c>.
    /// </summary>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The newest bucket per pair.</returns>
    /// <remarks>
    /// The read-time probe's second aggregate — <i>how far</i> each pair reaches, not merely which ones exist
    /// (gh#531). Existence is satisfied by a pair whose rows stopped halfway down the series; a grouped
    /// <c>max</c> answers the stronger question over the same scan. One grouping rather than one
    /// <c>EXISTS</c> per catalogue member, which was measured slower on gh#246.
    /// </remarks>
    Task<Dictionary<(string Indicator, int Period), DateTimeOffset>> NewestHeldAsync(
        CancellationToken cancellationToken);

    /// <summary>Takes one loaded value out, through the change tracker.</summary>
    /// <param name="key">The value's key, as <see cref="LoadValuesAsync"/> returned it.</param>
    /// <remarks>
    /// <b>The removal waits for the caller's <c>SaveChanges</c>, and the upsert does not.</b> That asymmetry
    /// is why a projection pass refuses to run outside a transaction.
    /// </remarks>
    void Remove((string Indicator, int Period, DateTimeOffset Bucket) key);

    /// <summary>Binds one pass's values to <see cref="UpsertSql"/>.</summary>
    /// <param name="pending">The values to write.</param>
    /// <param name="now">The instant the pass runs at, stamped on the rows it changes.</param>
    /// <returns>The parameters, in no particular order.</returns>
    /// <remarks>
    /// <b>Arrays rather than a row per value.</b> A whole series times every configured <c>(name, period)</c>
    /// is tens of thousands of rows, and four parameters each would exceed the protocol's parameter limit
    /// many times over.
    /// </remarks>
    NpgsqlParameter[] UpsertParameters(List<PendingValue> pending, DateTimeOffset now);
}

/// <summary>One value a pass found the store does not already hold.</summary>
/// <param name="Indicator">The indicator's stable name.</param>
/// <param name="Period">The period, part of the storage key.</param>
/// <param name="BucketStart">The bucket — a bar's opening instant.</param>
/// <param name="Value">The value, already rounded to the stored scale.</param>
internal readonly record struct PendingValue(
    string Indicator,
    int Period,
    DateTimeOffset BucketStart,
    decimal Value);
