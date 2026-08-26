using System.Globalization;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using Microsoft.EntityFrameworkCore;
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
/// <b>It reuses the whole-series replay unchanged</b> — <see cref="IndicatorProjector.ProjectAsync"/>, over
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
public sealed class IndicatorCacheService(
    TopstepXDbContext database,
    IndicatorCatalog catalog,
    IndicatorProjector projector,
    TimeProvider clock,
    ILogger<IndicatorCacheService> logger)
{
    private readonly TopstepXDbContext _database = database;
    private readonly IndicatorCatalog _catalog = catalog;
    private readonly IndicatorProjector _projector = projector;
    private readonly TimeProvider _clock = clock;
    private readonly ILogger<IndicatorCacheService> _logger = logger;

    /// <summary>
    /// Series this scope has already found complete.
    /// </summary>
    /// <remarks>
    /// <c>get_market_snapshot</c> asks <c>get_indicator_at</c> once per indicator per resolution — eleven
    /// reads of one series — and without this each would re-ask the store the same question. The scope is one
    /// request, and within it a series found complete stays complete: the only thing that writes a bar
    /// projects over it in the same unit of work, so there is no way for the answer to change underneath a
    /// request that is not itself the fill that changed it.
    /// </remarks>
    private readonly HashSet<(string Venue, string Instrument, int ResolutionMinutes)> _complete = [];

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
    /// <b>The probe is two aggregates, and it decides the whole cost of a warm read.</b> One count of the
    /// series' bars, and one <c>DISTINCT (Indicator, Period)</c> over its values — at most as many rows as
    /// the catalogue has members, both served by the composite indexes those tables are keyed on. A warm
    /// series pays exactly that and opens no transaction.
    /// </para>
    /// <para>
    /// <b>A pair is only <i>missing</i> when the stored bars could have produced it.</b>
    /// <see cref="IIndicator.WarmupBars"/> is the domain's own statement of that boundary, and using it is
    /// what stops a short series replaying itself on every read forever: six bars cannot satisfy MACD's
    /// signal warm-up of thirty-five, so a projection would compute nothing, write nothing, and leave the
    /// probe answering "missing" for as long as the series stays short. An absence the bars genuinely
    /// justify is a fact (`R-2.3`) and is left alone.
    /// </para>
    /// <para>
    /// <b>The residue, stated exactly.</b> The bound counts the whole series, while warm-up restarts at every
    /// contract roll (ADR-0011). A series whose every contract run is shorter than the warm-up but whose
    /// total is not would be re-probed and re-replayed on each read. That needs more rolls than a quarterly
    /// contract can have in the span of a warm-up, and the cost of it is a replay of a series that short —
    /// so it is recorded rather than guarded.
    /// </para>
    /// </remarks>
    public async Task<bool> EnsureProjectedAsync(
        string venue,
        InstrumentId instrument,
        int resolutionMinutes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(venue);

        (string, string, int) key = (venue, instrument.Symbol, resolutionMinutes);
        if (_complete.Contains(key))
        {
            return false;
        }

        Probes++;

        int bars = await _database.Bars
            .CountAsync(
                b => b.Venue == venue
                    && b.Instrument == instrument.Symbol
                    && b.ResolutionMinutes == resolutionMinutes,
                cancellationToken)
            .ConfigureAwait(false);

        // No bars, nothing to project from. Checked before the second query rather than after it: an unknown
        // instrument, or one nothing has ever fetched, is the commonest cold read there is and it must not
        // cost a transaction to compute nothing.
        if (bars == 0)
        {
            _complete.Add(key);
            return false;
        }

        // AsNoTracking for the reason every read of IndicatorValues here is: the rows are written by SQL the
        // change tracker never sees, so a tracked copy is a stale entity the identity map would hand back to
        // the next read in the same scope (gh#103). Projected to two columns because that is the whole
        // question -- WHICH pairs exist, never how many rows or what they hold.
        List<(string Indicator, int Period)> held = await _database.IndicatorValues
            .AsNoTracking()
            .Where(v => v.Venue == venue
                && v.Instrument == instrument.Symbol
                && v.ResolutionMinutes == resolutionMinutes)
            .Select(v => new { v.Indicator, v.Period })
            .Distinct()
            .Select(v => ValueTuple.Create(v.Indicator, v.Period))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        HashSet<(string Indicator, int Period)> stored = [.. held];

        List<IIndicator> missing =
            [.. _catalog.All.Where(i => i.WarmupBars <= bars && !stored.Contains((i.Name, i.Period)))];

        if (missing.Count == 0)
        {
            _complete.Add(key);
            return false;
        }

        string what = instrument.Symbol + " " + resolutionMinutes.ToString(CultureInfo.InvariantCulture) + "m";

        _logger.LogInformation(
            "The catalogue computes {Missing} the store holds no value for on {Series} ({Bars} bars): "
            + "{Names}. Replaying the series to serve this read.",
            missing.Count,
            what,
            bars,
            string.Join(", ", missing.Select(i => i.Name + "(" + i.Period.ToString(CultureInfo.InvariantCulture) + ")")));

        DateTimeOffset now = _clock.GetUtcNow();

        await SeriesUnitOfWork.RunAsync(
            _database,
            what,
            async token =>
            {
                int changed = await _projector
                    .ProjectAsync(venue, instrument, resolutionMinutes, now, token)
                    .ConfigureAwait(false);

                await _database.SaveChangesAsync(token).ConfigureAwait(false);
                return changed;
            },
            _logger,
            cancellationToken).ConfigureAwait(false);

        Projections++;
        _complete.Add(key);
        return true;
    }
}
