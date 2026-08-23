using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

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
    /// <remarks>
    /// Does <b>not</b> call <c>SaveChanges</c>. The caller owns the unit of work, so bars and the indicators
    /// derived from them commit together — a partial commit would leave a bar whose indicators silently do
    /// not exist, which reads back as a market that produced no signal.
    /// <para>
    /// A confirming rebuild still produces an <b>empty diff</b>: a value the pass recomputed to the same
    /// number counts as produced, so reconciliation removes nothing.
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

        List<BarRecord> stored = await _database.Bars
            .Where(b => b.Venue == venue
                && b.Instrument == instrument.Symbol
                && b.ResolutionMinutes == resolutionMinutes)
            .OrderBy(b => b.BucketStart)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Loaded BEFORE the empty-bars short circuit, because reconciliation applies there too: values
        // standing over a series whose bars have all been deleted are values nothing can justify.
        Dictionary<(string Indicator, int Period, DateTimeOffset Bucket), IndicatorValueRecord> existing =
            await _database.IndicatorValues
                .Where(v => v.Venue == venue
                    && v.Instrument == instrument.Symbol
                    && v.ResolutionMinutes == resolutionMinutes)
                .ToDictionaryAsync(v => (v.Indicator, v.Period, v.BucketStart), cancellationToken)
                .ConfigureAwait(false);

        List<Bar> bars = [.. stored.Select(ToBar)];

        // Every key this pass accounted for -- written, updated, OR recomputed to the same number. The last
        // case is why a confirming rebuild still reconciles to an empty diff.
        HashSet<(string Indicator, int Period, DateTimeOffset Bucket)> produced = [];

        int written = 0;

        // One run per contract, each projected on its own. A single-contract series -- which is every series
        // that has not yet lived through a roll -- is one segment, so this costs nothing and changes nothing
        // for it.
        IReadOnlyList<ContractSegment> segments = ContractRollDetector.Segment(bars);

        foreach (ContractSegment segment in segments)
        {
            List<Bar> run = bars.GetRange(segment.StartIndex, segment.BarCount);
            written += ProjectSegment(venue, instrument, resolutionMinutes, run, existing, produced, now);
        }

        int removed = Reconcile(existing, produced);

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
    /// <param name="existing">Every stored value for the series.</param>
    /// <param name="produced">The keys this pass accounted for.</param>
    /// <returns>How many rows were removed.</returns>
    /// <remarks>
    /// <para>
    /// <b>Scoped to the <c>(Indicator, Period)</c> pairs this catalogue computes</b>, and that scope is the
    /// whole safety of it. Deleting everything a pass did not write would erase a series the operator merely
    /// configured a period away from — ATR(14) and ATR(3) are different numbers under different keys, and a
    /// projection configured for one has no standing over the other's rows. That would be data loss wearing a
    /// cleanup's clothes.
    /// </para>
    /// <para>
    /// A warm-up bucket that has never had a value costs nothing here: there is no row to remove. What this
    /// reaches is the row that <i>used</i> to be justified — the ATR smoothed across a splice that a later,
    /// better-informed pass correctly declines to compute.
    /// </para>
    /// </remarks>
    private int Reconcile(
        Dictionary<(string Indicator, int Period, DateTimeOffset Bucket), IndicatorValueRecord> existing,
        HashSet<(string Indicator, int Period, DateTimeOffset Bucket)> produced)
    {
        HashSet<(string Indicator, int Period)> owned =
            [.. _catalog.All.Select(i => (i.Name, i.Period))];

        int removed = 0;

        foreach ((var key, IndicatorValueRecord row) in existing)
        {
            if (!owned.Contains((key.Indicator, key.Period)) || produced.Contains(key))
            {
                continue;
            }

            _database.IndicatorValues.Remove(row);
            removed++;
        }

        return removed;
    }

    /// <summary>Projects every configured indicator over one single-contract run of bars.</summary>
    /// <remarks>
    /// The run is what each indicator sees, so its smoothing seeds from the run's own first bar. Handing the
    /// whole series in would let a roll gap -- routinely tens of points between adjacent quarters -- be
    /// smoothed forward as though it were price action, which is exactly the number nobody would question.
    /// </remarks>
    private int ProjectSegment(
        string venue,
        InstrumentId instrument,
        int resolutionMinutes,
        IReadOnlyList<Bar> bars,
        Dictionary<(string Indicator, int Period, DateTimeOffset Bucket), IndicatorValueRecord> existing,
        HashSet<(string Indicator, int Period, DateTimeOffset Bucket)> produced,
        DateTimeOffset now)
    {
        int written = 0;

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

                if (existing.TryGetValue(key, out IndicatorValueRecord? row))
                {
                    if (row.Value == value)
                    {
                        // Unchanged. Leaving RecordedAt alone is what makes a confirming rebuild produce an
                        // empty diff rather than rewriting every timestamp in the series.
                        continue;
                    }

                    row.Value = value;
                    row.RecordedAt = now;
                }
                else
                {
                    _database.IndicatorValues.Add(new IndicatorValueRecord
                    {
                        Venue = venue,
                        Instrument = instrument.Symbol,
                        ResolutionMinutes = resolutionMinutes,
                        Indicator = indicator.Name,
                        Period = indicator.Period,
                        BucketStart = bars[i].OpenTime,
                        Value = value,
                        RecordedAt = now,
                    });
                }

                written++;
            }
        }

        return written;
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
