using MarqSpec.Mcp.TopstepX.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MarqSpec.Mcp.TopstepX.MarketData;

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
    /// <returns>How many values the rebuild changed — written, updated, or removed.</returns>
    public async Task<int> RebuildAsync(string? onlyInstrument, CancellationToken cancellationToken)
    {
        string? only = onlyInstrument?.Trim().ToUpperInvariant();
        DateTimeOffset now = _clock.GetUtcNow();

        // Every (instrument, resolution) the store actually holds, rather than every configured one: a
        // resolution nobody has fetched has nothing to rebuild, and asking for it would be a no-op that looks
        // like a result.
        var series = await _database.Bars
            .Select(b => new { b.Venue, b.Instrument, b.ResolutionMinutes })
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        int total = 0;
        foreach (var s in series)
        {
            if (only is not null && !string.Equals(s.Instrument, only, StringComparison.Ordinal))
            {
                continue;
            }

            if (!_registry.IsServed(s.Instrument))
            {
                _logger.LogWarning(
                    "Skipping {Instrument}: it is in the store but not in MarketData__Instruments.",
                    s.Instrument);
                continue;
            }

            int changed = await ReplaySeriesAsync(s.Venue, s.Instrument, s.ResolutionMinutes, now, cancellationToken)
                .ConfigureAwait(false);

            total += changed;

            _logger.LogInformation(
                "Rebuilt {Count} values for {Instrument} {Resolution}m.",
                changed,
                s.Instrument,
                s.ResolutionMinutes);
        }

        _logger.LogInformation(
            "Rebuild complete: {Total} values changed across {Series} series.", total, series.Count);

        return total;
    }

    private async Task<int> ReplaySeriesAsync(
        string venue,
        string instrument,
        int resolutionMinutes,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        int changed = await _projector
            .ProjectAsync(venue, _registry.Resolve(instrument), resolutionMinutes, now, cancellationToken)
            .ConfigureAwait(false);

        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }
}
