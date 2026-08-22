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
    /// <returns>How many values were written or updated.</returns>
    /// <remarks>
    /// Does <b>not</b> call <c>SaveChanges</c>. The caller owns the unit of work, so bars and the indicators
    /// derived from them commit together — a partial commit would leave a bar whose indicators silently do
    /// not exist, which reads back as a market that produced no signal.
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

        if (stored.Count == 0)
        {
            return 0;
        }

        List<Bar> bars = [.. stored.Select(ToBar)];

        Dictionary<(string Indicator, int Period, DateTimeOffset Bucket), IndicatorValueRecord> existing =
            await _database.IndicatorValues
                .Where(v => v.Venue == venue
                    && v.Instrument == instrument.Symbol
                    && v.ResolutionMinutes == resolutionMinutes)
                .ToDictionaryAsync(v => (v.Indicator, v.Period, v.BucketStart), cancellationToken)
                .ConfigureAwait(false);

        int written = 0;

        foreach (IIndicator indicator in _catalog.All)
        {
            IReadOnlyList<decimal?> values = indicator.Compute(bars);

            for (int i = 0; i < values.Count; i++)
            {
                // A null is the indicator saying it cannot measure yet. It is not written: an absent row and a
                // row holding a stand-in value read back identically, and only one of them is honest.
                if (values[i] is not { } value)
                {
                    continue;
                }

                (string Name, int Period, DateTimeOffset OpenTime) key =
                    (indicator.Name, indicator.Period, bars[i].OpenTime);

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

        if (written > 0)
        {
            _logger.LogDebug(
                "Projected {Count} indicator values for {Instrument} {Resolution}m over {Bars} bars.",
                written,
                instrument.Symbol,
                resolutionMinutes,
                bars.Count);
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
            record.Volume);
    }
}
