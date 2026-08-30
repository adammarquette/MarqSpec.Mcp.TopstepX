using System.Globalization;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// Makes a footprint read cache-aside: a covered tape with no cells at the asked resolution is
/// projected from the stored prints on the first read that asks for it (gh#366).
/// </summary>
/// <remarks>
/// <para>
/// <b>The trigger is on-read, the ADR-0014 shape.</b> <see cref="FootprintProjector"/> existed and
/// was registered; nothing in the host called it. <see cref="TradeTapeRecorder"/> writes
/// <c>Trades</c> only. Ingest after each print is not taken — the projector is whole-tape, and live
/// <c>TapeCoverage</c> is a sibling claim (gh#365). A read of a window the ledger has covered is
/// the moment cells have to exist.
/// </para>
/// <para>
/// <b>The venue is unreachable from here, and that is stated by the constructor.</b> This takes no
/// <c>IMarketDataGateway</c>: every print a projection needs is already stored.
/// </para>
/// <para>
/// <b>It reuses the whole-series replay unchanged</b> — <see cref="FootprintProjector.ProjectAsync"/>,
/// over the entire stored tape, inside <see cref="SeriesUnitOfWork"/>. A narrowed window would
/// reconcile away every cell outside it, which is the failure the projector's whole-tape guard
/// exists to refuse. A confirming rebuild is still an empty diff (ADR-0006). A bucket whose
/// counted prints span two contracts still produces no cell (ADR-0011).
/// </para>
/// <para>
/// <b>The probe is two aggregates.</b> No trades ⇒ nothing to project. Cells whose
/// <c>RecordedAt</c> is at least as new as the newest stored print ⇒ the tape has not grown since
/// the last write that could have changed a cell. Otherwise replay. The answer is memoised for
/// the life of the request scope so <c>get_footprint</c> and <c>get_volume_profile</c> in one
/// call cost one probe.
/// </para>
/// </remarks>
/// <param name="database">The store.</param>
/// <param name="projector">The whole-tape replay.</param>
/// <param name="clock">The clock, stamped on rows a projection actually changes.</param>
/// <param name="logger">The logger. A read that silently replayed a year of prints would be invisible.</param>
public sealed class FootprintCacheService(
    TopstepXDbContext database,
    FootprintProjector projector,
    TimeProvider clock,
    ILogger<FootprintCacheService> logger)
{
    private readonly TopstepXDbContext _database = database;
    private readonly FootprintProjector _projector = projector;
    private readonly TimeProvider _clock = clock;
    private readonly ILogger<FootprintCacheService> _logger = logger;

    /// <summary>
    /// Series this scope has already found complete.
    /// </summary>
    private readonly HashSet<(string Venue, string Instrument, int ResolutionMinutes)> _complete = [];

    /// <summary>
    /// How many times this scope asked the store whether a series was complete.
    /// </summary>
    public int Probes { get; private set; }

    /// <summary>How many whole-tape replays this scope ran to serve a read.</summary>
    public int Projections { get; private set; }

    /// <summary>
    /// Projects the asked resolution from the stored tape when the tape has prints the cells do
    /// not yet reflect.
    /// </summary>
    /// <param name="venue">The venue.</param>
    /// <param name="instrument">The instrument.</param>
    /// <param name="resolutionMinutes">The bar size in minutes.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns><see langword="true"/> if this call replayed the tape.</returns>
    /// <exception cref="StoreContentionException">Every attempt lost to a concurrent writer.</exception>
    public async Task<bool> EnsureProjectedAsync(
        string venue,
        InstrumentId instrument,
        int resolutionMinutes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(venue);

        if (resolutionMinutes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(resolutionMinutes),
                resolutionMinutes,
                "A bar size must be positive.");
        }

        (string, string, int) key = (venue, instrument.Symbol, resolutionMinutes);
        if (_complete.Contains(key))
        {
            return false;
        }

        Probes++;

        DateTimeOffset? lastTrade = await _database.Trades
            .AsNoTracking()
            .Where(t => t.Venue == venue && t.Instrument == instrument.Symbol)
            .Select(t => (DateTimeOffset?)t.RecordedAt)
            .DefaultIfEmpty()
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false);

        if (lastTrade is null)
        {
            _complete.Add(key);
            return false;
        }

        DateTimeOffset? lastCell = await _database.FootprintCells
            .AsNoTracking()
            .Where(c => c.Venue == venue
                && c.Instrument == instrument.Symbol
                && c.ResolutionMinutes == resolutionMinutes)
            .Select(c => (DateTimeOffset?)c.RecordedAt)
            .DefaultIfEmpty()
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false);

        if (lastCell is { } cell && lastTrade <= cell)
        {
            _complete.Add(key);
            return false;
        }

        string what = instrument.Symbol + " " + resolutionMinutes.ToString(CultureInfo.InvariantCulture) + "m";

        _logger.LogInformation(
            "The stored tape has prints {Instrument} {Resolution}m cells do not yet reflect. "
            + "Replaying the whole tape to serve this read.",
            instrument.Symbol,
            resolutionMinutes);

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
