using System.Diagnostics;
using System.Globalization;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.Telemetry;
using Microsoft.Extensions.Logging;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// Makes an indicator read cache-aside: a value the catalogue computes but the store does not hold is
/// projected from the bars already cached, on the first read that asks for it (gh#246).
/// </summary>
/// <remarks>
/// <para>
/// <b>The gap this closes.</b> Until now the only projection in the serving process sat inside the venue
/// fetch, gated on the venue having owed us bars <i>and</i> on bars actually being written. A window the
/// cache already covered projected nothing, so an indicator added to <see cref="IndicatorCatalog"/> — or a
/// period moved in configuration — had no values for any bar already stored, and the only remedy was an
/// operator running <c>rebuild-indicators</c> against the container. <c>get_indicators</c> reported the
/// absence correctly, and `R-2.3` makes every caller read an absence as <i>cannot measure</i> — but the
/// absence was an artefact of <i>when</i> computation happened rather than a fact about the market.
/// </para>
/// <para>
/// <b>The venue is unreachable from here, and that is stated by the constructor.</b> This takes no
/// <c>IMarketDataGateway</c>, exactly as <see cref="IndicatorRebuilder"/> takes none: every bar a projection
/// needs is already stored, so a read that self-heals costs zero vendor requests by construction rather than
/// by a promise in a comment.
/// </para>
/// <para>
/// <b>It reuses the whole-series replay unchanged</b> —
/// <see cref="IndicatorProjector.ProjectAsync(SeriesKey, DateTimeOffset, CancellationToken)"/>, over
/// the entire stored series, inside <see cref="SeriesUnitOfWork"/>. A read-triggered projection narrowed to
/// the requested window would be a different operation with different concurrency properties:
/// <see cref="IndicatorProjector"/>'s reconciliation is unscoped by bucket range and would delete every value
/// outside the narrowed range, which is the failure its whole-series guard exists to refuse. And a moving
/// seed window is refused outright by <see cref="IIndicator"/>'s contract, because Wilder smoothing is
/// recursive and a value seeded from a window depends on how much history happened to be loaded
/// (ADR-0006, ADR-0012). Reusing the replay means this trigger adds no new concurrency shape at all: it is
/// the same unit of work the fill path and the rebuild verb already run.
/// </para>
/// <para>
/// <b>Two concurrent cold reads produce one set of writes, and no lock is involved.</b> Nothing serialises
/// work on a series — ADR-0012 measured both advisory-lock shapes and rejected both. What makes the pair
/// safe is the store: both passes write with one <c>ON CONFLICT … DO UPDATE</c> under
/// <see cref="SeriesUnitOfWork.Isolation"/>, so the second meets a <c>40001</c>, and
/// <see cref="SeriesUnitOfWork"/>'s single retry re-reads a store that now holds the winner's values,
/// recomputes them to the same numbers, and writes nothing. The retry's empty diff is the property doing the
/// work, and it is the same property a confirming rebuild rests on.
/// </para>
/// </remarks>
/// <param name="database">The store.</param>
/// <param name="catalog">The indicators the store is expected to hold values for.</param>
/// <param name="projector">The whole-series replay.</param>
/// <param name="clock">The clock, stamped on rows a projection actually changes.</param>
/// <param name="logger">The logger. A read that silently replayed a year of bars would be invisible.</param>
/// <param name="telemetry">The app-owned meter and activity source, always supplied by the composition root.</param>
/// <param name="readTriggeredReplays">
/// The process-lifetime count of read-opened replays. Optional only so hand-built tests that do not
/// care about it keep compiling; the composition root always supplies the singleton.
/// </param>
/// <param name="sessions">
/// The closed vocabulary of session names. Optional only so hand-built resolution-only tests keep compiling;
/// the composition root always supplies the singleton, and a session series refuses to project without it.
/// </param>
public sealed class IndicatorCacheService(
    TopstepXDbContext database,
    IndicatorCatalog catalog,
    IndicatorProjector projector,
    TimeProvider clock,
    ILogger<IndicatorCacheService> logger,
    HostTelemetry telemetry,
    IndicatorReadProjectionCounter? readTriggeredReplays = null,
    SessionCatalog? sessions = null)
{
    private readonly TopstepXDbContext _database = database;
    private readonly IndicatorCatalog _catalog = catalog;
    private readonly IndicatorProjector _projector = projector;
    private readonly TimeProvider _clock = clock;
    private readonly ILogger<IndicatorCacheService> _logger = logger;
    private readonly IndicatorReadProjectionCounter _readTriggeredReplays =
        readTriggeredReplays ?? new IndicatorReadProjectionCounter();
    private readonly HostTelemetry _telemetry = telemetry;
    private readonly SessionCatalog? _sessions = sessions;

    /// <summary>
    /// Series this scope has already found complete.
    /// </summary>
    /// <remarks>
    /// <c>get_market_snapshot</c> asks <c>get_indicator_at</c> once per indicator per resolution — one
    /// read of one series per catalogue name — and without this each would re-ask the store the same
    /// question. The scope is one request, and within it a series found complete stays complete: the only
    /// thing that writes a bar projects over it in the same unit of work, so there is no way for the
    /// answer to change underneath a request that is not itself the fill that changed it.
    /// <para>
    /// <b>Keyed by the series key, whose equality is by value AND by runtime type.</b> A key comparing by
    /// reference would memoise nothing and replay on every read; a key whose type were not part of its
    /// equality would let a session named <c>5</c> collide with the five-minute series.
    /// </para>
    /// </remarks>
    private readonly HashSet<SeriesKey> _complete = [];

    /// <summary>
    /// How many times this scope asked the store whether a series was complete.
    /// </summary>
    /// <remarks>
    /// Counted rather than logged, for the reason <see cref="BarReadResult.FetchedBuckets"/> is reported
    /// rather than logged: what a question cost is something a test — and an operator — should be able to
    /// observe, and the probe is the tax every warm read pays forever.
    /// </remarks>
    public int Probes { get; private set; }

    /// <summary>How many whole-series replays this scope ran to serve a read.</summary>
    /// <remarks>
    /// Per-request. The process-lifetime count is <see cref="IndicatorReadProjectionCounter.Replays"/>.
    /// </remarks>
    public int Projections { get; private set; }

    /// <summary>
    /// Projects anything the catalogue computes that the stored bars justify and the store does not hold.
    /// </summary>
    /// <param name="venue">The venue.</param>
    /// <param name="instrument">The instrument.</param>
    /// <param name="resolutionMinutes">The bar size in minutes.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns><see langword="true"/> if this call replayed the series.</returns>
    /// <exception cref="StoreContentionException">Every attempt lost to a concurrent writer.</exception>
    /// <remarks>
    /// <para>
    /// <b>The probe is two aggregates, and it decides the whole cost of a warm read.</b> The newest buckets
    /// of the series, capped at the largest warm-up, and one <c>GROUP BY (Indicator, Period)</c> over the
    /// series' values carrying <c>max(BucketStart)</c> — which returns at most as many rows as the catalogue
    /// has members. A warm series pays exactly that and opens no transaction. Measured on gh#246 at
    /// <b>4.1 ms</b> over 2,000 bars and <b>18 ms</b> over 70,000 before the cap; the cap makes the first
    /// half flat and the remaining growth is the grouping, which scans the whole key range because Postgres
    /// 17 has no index skip scan.
    /// </para>
    /// <para>
    /// <b>It asks whether each pair is COMPLETE, not whether it exists</b> (gh#531). Existence was the
    /// weaker question, and a pair whose rows stop halfway down the series answered it: the read then served
    /// every value written up to wherever the last projection reached and nothing after, which is a series
    /// that ends early wearing the clothes of a market that stopped moving. Both aggregates changed shape to
    /// close that — the bar count became the newest buckets in descending order, and the <c>DISTINCT</c>
    /// became a grouped <c>max</c> — and neither added a query.
    /// </para>
    /// <para>
    /// <b>The completeness boundary is the indicator's own warm-up, counted in BARS rather than in time.</b>
    /// A pair is short when its newest value sits further back than the
    /// <see cref="IIndicator.WarmupBars"/>-th newest bucket — which is <i>one fewer</i> bars behind the
    /// newest than the warm-up itself, because a warm-up of <c>w</c> may leave exactly <c>w - 1</c> trailing
    /// bars without a value and no more. Anything nearer than that is an absence a warm-up can account for, and
    /// after a contract roll it routinely is (ADR-0011): the new run's first bars carry no value at all, and
    /// a probe that read those as a gap would replay the series on every read and write nothing each time.
    /// Counted in bars because a threshold in <i>time</i> — the newest bucket less
    /// <c>warm-up × resolution</c> — is wrong across every weekend and session break, where the stored
    /// buckets are not contiguous.
    /// </para>
    /// <para>
    /// <b>Eleven <c>EXISTS</c> seeks were the obvious alternative and are measurably worse</b> — 21.00,
    /// 19.72, 21.43, 26.04 and 26.99 ms across the five series sizes, so about 20–27 ms and not falling with
    /// size, because eleven round trips cost more than one scan. Measured rather than assumed, in the same
    /// run.
    /// </para>
    /// <para>
    /// <b>A pair is only <i>missing</i> when the stored bars could have produced it.</b>
    /// <see cref="IIndicator.WarmupBars"/> is the domain's own statement of that boundary, and using it is
    /// what stops a short series replaying itself on every read forever: six bars cannot satisfy MACD's
    /// signal warm-up of thirty-four, so a projection would compute nothing, write nothing, and leave the
    /// probe answering "missing" for as long as the series stays short. An absence the bars genuinely
    /// justify is a fact (`R-2.3`) and is left alone.
    /// </para>
    /// <para>
    /// <b>The residue, stated exactly, and it takes ONE roll.</b> The bound counts the whole stored series
    /// while warm-up restarts at every contract seam (ADR-0011), so a series whose every contract run is
    /// shorter than the warm-up but whose total is not is re-probed and re-replayed on every read. That is
    /// not rare arithmetic: the stored series is whatever was fetched rather than a complete contract run —
    /// <see cref="BarCacheService"/> writes only the outstanding buckets and
    /// <c>ContractRollDetector.Segment</c> splits purely on a <c>ContractId</c> change — so two ordinary
    /// <c>get_bars</c> windows either side of
    /// one quarterly roll produce it. Nothing is wrong when it happens: the pass writes nothing and the
    /// absences are the honest answer. The cost is a replay of a series that short, which is why it is
    /// recorded rather than guarded — and pinned by
    /// <c>ASeriesWhoseEveryContractRunIsShorterThanTheWarmUp_ReplaysOnEveryRead</c> rather than asserted.
    /// </para>
    /// <para>
    /// <b>The completeness test widens that residue by exactly one shape, and no more</b> (gh#531). A pair's
    /// legitimate trailing absence is bounded by its warm-up only while <i>one</i> contract run sits at the
    /// tail. Where several consecutive runs are each shorter than the warm-up, the trailing absence is their
    /// sum, the newest value falls behind the threshold, and the series is replayed on every read —
    /// producing nothing, exactly as the residue above does. It is the same series in the same state: a
    /// stored history so fragmentary that no run measures. Closing it exactly would mean reading the whole
    /// series' contract ids in the probe, which is the read a replay already is, so there would be nothing
    /// left to decide.
    /// </para>
    /// </remarks>
    public Task<bool> EnsureProjectedAsync(
        string venue,
        InstrumentId instrument,
        int resolutionMinutes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(venue);

        return EnsureProjectedAsync(
            new SeriesKey.Resolution(venue, instrument.Symbol, resolutionMinutes), cancellationToken);
    }

    /// <summary>
    /// Projects anything the catalogue computes for one series that the stored bars justify and the store
    /// does not hold.
    /// </summary>
    /// <param name="key">Which series — resolution or session.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns><see langword="true"/> if this call replayed the series.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    /// <exception cref="StoreContentionException">Every attempt lost to a concurrent writer.</exception>
    /// <remarks>
    /// <b>This is the body; the overload above forwards to it.</b> A session series is the same probe over
    /// the other pair of tables, against the vocabulary <see cref="IndicatorCatalog.ForSeries"/> gives that
    /// series — every cost and every rule the overload's remarks state applies here unchanged.
    /// </remarks>
    public async Task<bool> EnsureProjectedAsync(SeriesKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);

        // ONE SPAN AND ONE MEASUREMENT PER READ, including the memoised ones. The scope memo below is a store
        // optimisation; a caller that asked twice was served twice, and a rate that fell because a memo was
        // added would read as traffic that stopped (gh#536).
        using Activity? span = _telemetry.StartCacheRead(
            CacheSeries.Indicators, key.Instrument, key);

        if (_complete.Contains(key))
        {
            Hit(key);
            return false;
        }

        Probes++;

        // WHICH TABLES AND WHICH VOCABULARY, both decided from the key. The tables are built here rather than
        // injected: this constructor is hand-built at sixteen sites across the two test projects, and one
        // more *required* parameter would be an edit to every one of them (gh#501). SessionCatalog is
        // optional and trailing for that reason; a session series needs it for ADR-0022 §4 provenance.
        ISeriesTables tables = ISeriesTables.For(key, _database, _sessions);

        // The SAME list the projection will walk and the reconcile will scope itself to. A probe that read a
        // wider vocabulary than the projection computes would replay this series on every read forever.
        IReadOnlyList<IIndicator> catalogue = _catalog.ForSeries(key);

        // CAPPED AT THE LARGEST WARM-UP, and the cap is what keeps this query flat. The count this yields
        // decides `WarmupBars <= bars` for each catalogue member, so any number at or above the largest
        // warm-up answers every one of those comparisons identically -- min(actual, cap) preserves each of
        // them exactly, because a warm-up that fails the comparison is below the cap by definition. Uncapped
        // it is a count of the whole series and grows with it: measured on gh#246 at 2.24 ms over 500 bars
        // and 7.85 ms over 70,000, against 1.98 ms and 2.99 ms for the LIMIT form, on the same store in the
        // same run.
        int cap = catalogue.Max(i => i.WarmupBars);

        // THE BUCKETS THEMSELVES, NEWEST FIRST, RATHER THAN A COUNT OF THEM (gh#531). Still one query, still
        // capped, and it answers a second question the count could not: `tail[w - 1]` is the `w`-th NEWEST
        // bucket -- `w - 1` bars back from the newest, not `w` -- which is how far a warm-up of `w` can push
        // a pair's newest value back before the pair is genuinely short. Counted in BARS, not in time -- a
        // threshold of `newest - (w - 1) * resolution`
        // would be wrong across every weekend and session break, where the stored buckets are not
        // contiguous. The cap is safe for this second use too: no `WarmupBars` this loop reaches exceeds it,
        // so no index it takes is past the end.
        List<DateTimeOffset> tail = await tables
            .LoadNewestBucketsAsync(cap, cancellationToken)
            .ConfigureAwait(false);

        int bars = tail.Count;

        // No bars, nothing to project from. Checked before the second query rather than after it: an unknown
        // instrument, or one nothing has ever fetched, is the commonest cold read there is and it must not
        // cost a transaction to compute nothing.
        if (bars == 0)
        {
            _complete.Add(key);

            // NOT a miss. There is nothing to project FROM, so the store already holds every value the bars
            // justify -- which is none of them -- and `R-2.3` makes that absence a fact rather than a gap.
            // Counting it as a miss would report a permanent stream of misses for every symbol nobody has
            // ever fetched.
            Hit(key);
            return false;
        }

        // AsNoTracking for the reason every read of IndicatorValues here is: the rows are written by SQL the
        // change tracker never sees, so a tracked copy is a stale entity the identity map would hand back to
        // the next read in the same scope (gh#103). Grouped rather than DISTINCT because the question is not
        // WHICH pairs exist but HOW FAR each one reaches (gh#531): existence is satisfied by a pair whose
        // rows stopped halfway down the series, and a read of one of those served a window that ended early
        // and looked ordinary. The grouping returns the same at-most-one-row-per-catalogue-member it always
        // did, over the same scan, carrying one more column.
        Dictionary<(string Indicator, int Period), DateTimeOffset> stored = await tables
            .NewestHeldAsync(cancellationToken)
            .ConfigureAwait(false);

        List<IIndicator> missing =
            [.. catalogue.Where(i => i.WarmupBars <= bars && !IsComplete(i, stored, tail))];

        if (missing.Count == 0)
        {
            _complete.Add(key);
            Hit(key);
            return false;
        }

        // A series the store holds NO values for is a miss; one it holds some of is a partial. The second is
        // the ordinary shape after a catalogue addition, a period move, or a pair the bars have outrun, and
        // reading it as a cold miss would say the cache had lost a series it still has.
        string outcome = stored.Count == 0 ? CacheOutcome.Miss : CacheOutcome.Partial;

        string what = key.Describe();

        _readTriggeredReplays.RecordReplay();

        _logger.LogInformation(
            "The catalogue computes {Missing} the store holds no value for, or holds values that stop short "
            + "of the bars, on {Series}: "
            + "{Names}. Replaying the whole series to serve this read. "
            + "Read-triggered replays this process: {Replays}.",
            missing.Count,
            what,
            string.Join(", ", missing.Select(i => i.Name + "(" + i.Period.ToString(CultureInfo.InvariantCulture) + ")")),
            _readTriggeredReplays.Replays);

        DateTimeOffset now = _clock.GetUtcNow();

        await SeriesUnitOfWork.RunAsync(
            _database,
            what,
            async token =>
            {
                int changed = await _projector
                    .ProjectAsync(key, now, token)
                    .ConfigureAwait(false);

                await _database.SaveChangesAsync(token).ConfigureAwait(false);
                return changed;
            },
            _logger,
            cancellationToken).ConfigureAwait(false);

        Projections++;
        _complete.Add(key);
        _telemetry.CacheRead(CacheSeries.Indicators, key.Instrument, key, outcome);
        return true;
    }

    /// <summary>
    /// Whether the store holds this indicator's values as far down the series as the bars justify.
    /// </summary>
    /// <param name="indicator">The catalogue member.</param>
    /// <param name="stored">The newest bucket the store holds a value at, per <c>(Indicator, Period)</c>.</param>
    /// <param name="tail">The series' newest buckets, newest first, capped at the largest warm-up.</param>
    /// <returns><see langword="true"/> when nothing needs replaying for this pair.</returns>
    /// <remarks>
    /// <para>
    /// <b>Two ways to be incomplete, and only one of them used to count</b> (gh#531). A pair with no rows at
    /// all is the case gh#246 was written for — a catalogue addition, or a period the operator has just
    /// configured. A pair whose rows stop short of the bars is the same absence arriving from the other end,
    /// and the probe could not see it, because <c>DISTINCT (Indicator, Period)</c> answers "present" for
    /// both a full series and a truncated one.
    /// </para>
    /// <para>
    /// <b><c>tail[w - 1]</c> is the whole of the boundary.</b> It is the <c>w</c>-th newest bucket, so it
    /// sits <c>w - 1</c> bars back from the newest and a newest value at or after it is one a warm-up of
    /// <c>w</c> can account for — <c>w</c> bars back would be <c>tail[w]</c>, one bar looser, and one bar
    /// looser serves a series that should be replayed. The
    /// index cannot run past the end: this is only reached for an indicator whose <c>WarmupBars</c> is at
    /// most <paramref name="tail"/>'s length, since the caller has already filtered on
    /// <c>WarmupBars &lt;= bars</c> and <c>bars</c> IS that length. It cannot go below zero either, and the
    /// floor is a statement rather than a guard: <c>w - 1</c> is how many trailing bars a warm-up may leave
    /// without a value, so a warm-up of nought and a warm-up of one both mean <i>every bar has one</i> and
    /// both put the boundary on the newest bar.
    /// </para>
    /// <para>
    /// <b>It is deliberately not exact, and errs towards replaying.</b> A run of absences longer than the
    /// warm-up is read as a gap even where several short contract runs make it honest; the pass then writes
    /// nothing, which is the residue <see cref="EnsureProjectedAsync(SeriesKey, CancellationToken)"/> states. Erring the other way — a
    /// threshold generous enough to never replay a fragmentary series — would put a truncated series back
    /// on the wire as an ordinary answer, and a wrong number costs more than a wasted pass.
    /// </para>
    /// </remarks>
    private static bool IsComplete(
        IIndicator indicator,
        Dictionary<(string Indicator, int Period), DateTimeOffset> stored,
        List<DateTimeOffset> tail) =>
        stored.TryGetValue((indicator.Name, indicator.Period), out DateTimeOffset newest)
            && newest >= tail[Math.Max(indicator.WarmupBars - 1, 0)];

    private void Hit(SeriesKey key) =>
        _telemetry.CacheRead(CacheSeries.Indicators, key.Instrument, key, CacheOutcome.Hit);
}
