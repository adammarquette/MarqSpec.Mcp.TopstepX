using System.Globalization;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// Makes a footprint read cache-aside: a stored tape the cells do not yet reflect is projected
/// from the prints on the first read that asks for it (gh#366, gh#377).
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
/// <b>The probe is completeness, not a clock.</b> It asks whether the cells
/// <see cref="FootprintAggregator"/> produces from the stored tape are already stored at the
/// asked bar size — the ADR-0014 missing-pair shape. Trade <c>RecordedAt</c> is receipt time;
/// cell <c>RecordedAt</c> is the projection clock; they are different facts and are not
/// compared (gh#377). No stored prints ⇒ nothing to project. Matching cells ⇒ return,
/// opening no transaction. Otherwise replay. The answer is memoised for the life of the
/// request scope so <c>get_footprint</c> and <c>get_volume_profile</c> in one call cost one
/// probe.
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

        List<TradePrint> prints = await LoadPrintsAsync(venue, instrument, cancellationToken)
            .ConfigureAwait(false);

        if (prints.Count == 0)
        {
            _complete.Add(key);
            return false;
        }

        IReadOnlyList<FootprintCell> expected = FootprintAggregator.Aggregate(prints, resolutionMinutes);

        List<FootprintCellRecord> stored = await _database.FootprintCells
            .AsNoTracking()
            .Where(c => c.Venue == venue
                && c.Instrument == instrument.Symbol
                && c.ResolutionMinutes == resolutionMinutes)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (CellsReflectTape(expected, stored))
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

    private async Task<List<TradePrint>> LoadPrintsAsync(
        string venue,
        InstrumentId instrument,
        CancellationToken cancellationToken)
    {
        var rows = await _database.Trades
            .AsNoTracking()
            .Where(t => t.Venue == venue && t.Instrument == instrument.Symbol)
            .Select(t => new
            {
                t.Instrument,
                t.ContractId,
                t.TradeTimeUtc,
                t.Price,
                t.Size,
                t.Direction,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<TradePrint> prints = new(rows.Count);
        foreach (var row in rows)
        {
            prints.Add(new TradePrint(
                row.Instrument,
                row.ContractId,
                row.TradeTimeUtc,
                row.Price,
                row.Size,
                row.Direction));
        }

        return prints;
    }

    /// <summary>
    /// Whether every cell the tape justifies is already stored, and no stored cell is leftover.
    /// </summary>
    private static bool CellsReflectTape(
        IReadOnlyList<FootprintCell> expected,
        IReadOnlyList<FootprintCellRecord> stored)
    {
        if (expected.Count != stored.Count)
        {
            return false;
        }

        if (expected.Count == 0)
        {
            return true;
        }

        Dictionary<(DateTimeOffset Bucket, decimal Price), (long Buy, long Sell)> byKey = new(expected.Count);

        foreach (FootprintCell cell in expected)
        {
            decimal price = Math.Round(cell.Price, TopstepXDbContext.PriceScale, MidpointRounding.AwayFromZero);
            byKey[(cell.BucketStart, price)] = (cell.BuyVolume, cell.SellVolume);
        }

        foreach (FootprintCellRecord row in stored)
        {
            if (!byKey.TryGetValue((row.BucketStart, row.Price), out (long Buy, long Sell) volumes)
                || volumes.Buy != row.BuyVolume
                || volumes.Sell != row.SellVolume)
            {
                return false;
            }
        }

        return true;
    }
}
