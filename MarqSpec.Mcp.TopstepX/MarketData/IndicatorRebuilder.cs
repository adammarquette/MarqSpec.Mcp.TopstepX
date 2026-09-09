using MarqSpec.Mcp.TopstepX.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// What <see cref="IndicatorRebuilder"/> observed after replaying the store.
/// </summary>
/// <param name="ValuesChanged">Rows written, updated, or removed.</param>
/// <param name="SeriesRewritten">
/// Series in which at least one value actually changed. A confirming rebuild is not one of these.
/// </param>
/// <remarks>
/// A heal count, not a skew count (ADR-0012, gh#348). The pass that suffers adjacent-fill write-skew cannot
/// see that it did, so this is counted on the rebuild that repairs it — never inside a fill.
/// </remarks>
public sealed record IndicatorRebuildResult(int ValuesChanged, int SeriesRewritten);

/// <summary>
/// Replays the indicator projection over every series the store holds — the <c>rebuild-indicators</c> verb.
/// </summary>
/// <remarks>
/// <para>
/// A rebuild is a <b>replay, never a re-ingest</b> (ADR-0006): every stored value is reproducible from the
/// bars, so adding an indicator or correcting one costs no vendor traffic. It takes no
/// <c>IMarketDataGateway</c> at all, which is the strongest available statement that the venue is never
/// reached from here.
/// </para>
/// <para>
/// It lives here rather than inline in the composition root so that the verb can be <i>run by a test</i>. A
/// CLI verb with no test and no run is not delivered, and this one shipped in Phase 2 having never been
/// executed anywhere — which is how the rounding defect of gh#37 survived a whole phase.
/// </para>
/// </remarks>
/// <param name="database">The store.</param>
/// <param name="projector">The projection.</param>
/// <param name="registry">The instruments this server serves.</param>
/// <param name="clock">The clock, stamped on rows the rebuild actually changes.</param>
/// <param name="logger">The logger.</param>
public sealed class IndicatorRebuilder(
    TopstepXDbContext database,
    IndicatorProjector projector,
    InstrumentRegistry registry,
    TimeProvider clock,
    ILogger<IndicatorRebuilder> logger)
{
    private readonly TopstepXDbContext _database = database;
    private readonly IndicatorProjector _projector = projector;
    private readonly InstrumentRegistry _registry = registry;
    private readonly TimeProvider _clock = clock;
    private readonly ILogger<IndicatorRebuilder> _logger = logger;

    /// <summary>
    /// Replays every stored series, or just one instrument's.
    /// </summary>
    /// <param name="onlyInstrument">One symbol to restrict to, or <see langword="null"/> for all of them.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>
    /// How many values changed, and how many series those changes belonged to. A confirming rebuild is
    /// <c>(0, 0)</c> — the heal count does not move when nothing was rewritten.
    /// </returns>
    /// <remarks>
    /// <b>One <see cref="SeriesUnitOfWork"/> per series</b>, which is where the isolation level and the
    /// retry are decided. A projection reads the bars and then the values standing over them and reconciles
    /// the second against the first; the two have to be one snapshot, or a fill committing between them
    /// leaves the pass holding values it never saw the bars for and it deletes them (gh#73). This verb ran
    /// with <b>no transaction at all</b>, so its two reads were two autocommitted statements — the same
    /// defect over every series in the store, in the one command an operator reaches for when they are
    /// trying to repair it.
    /// <para>
    /// Per series rather than one transaction over the whole run. A rebuild is idempotent per series, so the
    /// series is the natural unit of work; one snapshot held across every series would be pinned for the
    /// length of the run, and a failure at the end would throw away everything before it for no gain.
    /// </para>
    /// <para>
    /// <b>"Every stored series" means the union of <c>Bars</c> and <c>IndicatorValues</c></b> (gh#571). Read
    /// off <c>Bars</c> alone — which is what it did — a series whose every bar has been deleted is not in the
    /// list, so its values are never visited and the verb reports an empty diff over the one set of rows that
    /// most needs it.
    /// </para>
    /// </remarks>
    public async Task<IndicatorRebuildResult> RebuildAsync(string? onlyInstrument, CancellationToken cancellationToken)
    {
        string? only = onlyInstrument?.Trim().ToUpperInvariant();
        DateTimeOffset now = _clock.GetUtcNow();

        // Every series the store actually holds, rather than every configured one: a resolution nobody has
        // fetched has nothing to rebuild, and asking for it would be a no-op that looks like a result.
        //
        // An ANONYMOUS TYPE, not a ValueTuple: a tuple inside the Select translates to a Postgres row
        // constructor Npgsql then refuses to materialise, and the in-memory provider reads one happily
        // (gh#282). The named record per row is built below, after materialisation — and it is SeriesKey
        // itself, which is what the walk and the log both want anyway.
        var resolutionFromBars = await _database.Bars
            .Select(b => new { b.Venue, b.Instrument, b.ResolutionMinutes })
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // AND EVERY SERIES THE VALUES TABLE HOLDS, WHICH IS NOT THE SAME LIST (gh#571). There is no foreign
        // key from IndicatorValues to Bars (ADR-0011 §2), so deleting a series' last bar leaves its values
        // standing -- and a series with no bars is not in the list above, so the verb an operator runs to
        // repair the store walked straight past it and reported an empty diff. Every one of those rows is a
        // number nothing can reproduce, which is what ADR-0006 forbids the store to hold.
        //
        // On a store with no orphans this adds nothing: a series with values has bars, so the second list is
        // a subset of the first and the union is the first. The cost is one DISTINCT over the values table,
        // once per run of a verb that then replays every series in the store.
        var resolutionFromValues = await _database.IndicatorValues
            .Select(v => new { v.Venue, v.Instrument, v.ResolutionMinutes })
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // AND THE SESSION SERIES, on exactly the same terms (gh#501). A session series the rebuild could not
        // see is a series nothing can repair, and `rebuild-indicators` is the command an operator reaches for
        // when they are trying to. The values-table half is the same orphan-catch as above.
        var sessionFromBars = await _database.SessionBars
            .Select(s => new { s.Venue, s.Instrument, s.Session })
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var sessionFromValues = await _database.SessionIndicatorValues
            .Select(v => new { v.Venue, v.Instrument, v.Session })
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Ordered, so a run walks the store the same way twice. Anonymous types of the same shape in one
        // assembly are one type with structural equality, so Union deduplicates on the three fields.
        List<SeriesKey> series =
        [
            .. resolutionFromBars
                .Union(resolutionFromValues)
                .OrderBy(s => s.Venue, StringComparer.Ordinal)
                .ThenBy(s => s.Instrument, StringComparer.Ordinal)
                .ThenBy(s => s.ResolutionMinutes)
                .Select(SeriesKey (s) => new SeriesKey.Resolution(s.Venue, s.Instrument, s.ResolutionMinutes)),
            .. sessionFromBars
                .Union(sessionFromValues)
                .OrderBy(s => s.Venue, StringComparer.Ordinal)
                .ThenBy(s => s.Instrument, StringComparer.Ordinal)
                .ThenBy(s => s.Session, StringComparer.Ordinal)
                .Select(SeriesKey (s) => new SeriesKey.Session(s.Venue, s.Instrument, s.Session)),
        ];

        int total = 0;
        int rewritten = 0;
        int walked = 0;
        foreach (SeriesKey stored in series)
        {
            if (only is not null && !string.Equals(stored.Instrument, only, StringComparison.Ordinal))
            {
                continue;
            }

            if (!_registry.IsServed(stored.Instrument))
            {
                _logger.LogWarning(
                    "Skipping {Instrument}: it is in the store but not in MarketData__Instruments.",
                    stored.Instrument);
                continue;
            }

            // Through the registry, exactly as before: the projection has always queried under the symbol
            // InstrumentRegistry.Resolve hands back rather than under the raw stored string. They agree for
            // every row the store holds, because a row is written under a normalised symbol -- so this
            // changes nothing and says which of the two the query uses.
            SeriesKey key = stored with { Instrument = _registry.Resolve(stored.Instrument).Symbol };

            int changed = await ReplaySeriesAsync(key, now, cancellationToken)
                .ConfigureAwait(false);
            walked++;
            if (changed > 0)
            {
                rewritten++;
            }

            // The series is committed and this context will never look at it again, so let it go.
            //
            // THE COST THIS USED TO NAME IS GONE, and saying so is the point of keeping the comment. It used
            // to be that the run accumulated every series' IndicatorValues for its whole length and each
            // later series paid for the earlier ones on every SaveChanges (gh#73 review) -- the REPAIR verb
            // over the WHOLE store, degrading worst exactly where the store is largest. Since gh#133 the
            // projection reads its values untracked and writes them with SQL, so after a series the tracker
            // holds nothing but the reconcile's deletions, which SaveChanges has already detached.
            //
            // It stays because it is a cheap statement of the invariant rather than a cure for a measured
            // cost: nothing below reads a tracked object, and a later change that starts tracking again would
            // otherwise re-acquire the old behaviour silently. Safe here and nowhere else in this class:
            // RunAsync has committed, and `series` holds projections rather than entities.
            _database.ChangeTracker.Clear();

            total += changed;

            // NAMED THE WAY SeriesUnitOfWork NAMES IT. The verb discards the result, so this line is the
            // operator-visible output — and a session series has no resolution to report.
            _logger.LogInformation(
                "Rebuilt {Count} values for {Series}.",
                changed,
                key.Describe());
        }

        _logger.LogInformation(
            "Rebuild complete: {Total} values changed; {Rewritten} of {Walked} series rewritten.",
            total,
            rewritten,
            walked);

        return new IndicatorRebuildResult(total, rewritten);
    }

    private Task<int> ReplaySeriesAsync(
        SeriesKey key,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        SeriesUnitOfWork.RunAsync(
            _database,
            key.Describe(),
            async token =>
            {
                int changed = await _projector
                    .ProjectAsync(key, now, token)
                    .ConfigureAwait(false);

                await _database.SaveChangesAsync(token).ConfigureAwait(false);
                return changed;
            },
            _logger,
            cancellationToken);
}
