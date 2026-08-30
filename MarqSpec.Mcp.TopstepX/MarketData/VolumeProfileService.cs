using System.Globalization;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// Reads stored footprint cells and <c>TapeCoverage</c> — the host face of order-flow projections
/// (gh#221, gh#222).
/// </summary>
/// <remarks>
/// <para>
/// The host owns the store; Domain references nothing. Coverage rows are seeded by tests the
/// way gh#220 proved cells — the recorder is not built and nothing here subscribes to the hub.
/// </para>
/// <para>
/// A window that spans a roll or a listening hole is confined to the newest contiguous
/// run of the contract in front and the narrowing is reported. The reported window is that
/// run, not the ask. A window with no tape refuses rather than returning an empty answer
/// (ADR-0011). A window entirely before recording began names the earliest covered time so a
/// caller cannot read absence as a quiet market (gh#222).
/// </para>
/// </remarks>
/// <param name="database">The store.</param>
public sealed class VolumeProfileService(TopstepXDbContext database)
{
    private readonly TopstepXDbContext _database = database;

    /// <summary>
    /// Builds a volume profile over the cells that were actually covered in
    /// <paramref name="start"/>–<paramref name="end"/>.
    /// </summary>
    /// <param name="venue">The venue.</param>
    /// <param name="instrument">The instrument.</param>
    /// <param name="resolutionMinutes">The bar size the cells were projected at.</param>
    /// <param name="start">The start of the ask, inclusive.</param>
    /// <param name="end">The end of the ask, exclusive.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The profile and the window the ledger actually covered.</returns>
    /// <exception cref="InvalidOperationException">No tape overlaps the ask.</exception>
    /// <exception cref="ArgumentException">The covered cells carry no volume.</exception>
    public async Task<VolumeProfileRead> ReadAsync(
        string venue,
        InstrumentId instrument,
        int resolutionMinutes,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        FootprintRead cells = await ReadCellsAsync(
                venue, instrument, resolutionMinutes, start, end, cancellationToken)
            .ConfigureAwait(false);

        return new VolumeProfileRead(VolumeProfileAggregator.From(cells.Cells), cells.Window);
    }

    /// <summary>
    /// Reads the footprint cells that were actually covered in
    /// <paramref name="start"/>–<paramref name="end"/>.
    /// </summary>
    /// <param name="venue">The venue.</param>
    /// <param name="instrument">The instrument.</param>
    /// <param name="resolutionMinutes">The bar size the cells were projected at.</param>
    /// <param name="start">The start of the ask, inclusive.</param>
    /// <param name="end">The end of the ask, exclusive.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The cells and the window the ledger actually covered.</returns>
    /// <exception cref="InvalidOperationException">No tape overlaps the ask.</exception>
    public async Task<FootprintRead> ReadCellsAsync(
        string venue,
        InstrumentId instrument,
        int resolutionMinutes,
        DateTimeOffset start,
        DateTimeOffset end,
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

        CoveredTapeWindow window = await ConfineAsync(
                venue, instrument, start, end, cancellationToken)
            .ConfigureAwait(false);

        DateTimeOffset loadFrom = window.Start.AddMinutes(-resolutionMinutes);

        List<FootprintCellRecord> stored = await _database.FootprintCells
            .AsNoTracking()
            .Where(c => c.Venue == venue
                && c.Instrument == instrument.Symbol
                && c.ResolutionMinutes == resolutionMinutes
                && c.BucketStart < window.End
                && c.BucketStart > loadFrom)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<FootprintCell> cells = [];

        foreach (FootprintCellRecord row in stored)
        {
            if (!VolumeProfileAggregator.BarOverlapsWindow(
                row.BucketStart, resolutionMinutes, window.Start, window.End))
            {
                continue;
            }

            cells.Add(new FootprintCell(
                row.Instrument,
                row.ResolutionMinutes,
                row.BucketStart,
                row.Price,
                row.BuyVolume,
                row.SellVolume));
        }

        return new FootprintRead(cells, window);
    }

    /// <summary>
    /// Confines the ask to the newest contiguous listening run, or refuses when no tape overlaps.
    /// </summary>
    private async Task<CoveredTapeWindow> ConfineAsync(
        string venue,
        InstrumentId instrument,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        List<TapeCoverageRecord> rows = await _database.TapeCoverage
            .AsNoTracking()
            .Where(c => c.Venue == venue
                && c.Instrument == instrument.Symbol
                && c.RangeStart < end
                && c.RangeEnd > start)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (rows.Count == 0)
        {
            throw await EmptyTapeRefusalAsync(venue, instrument, cancellationToken)
                .ConfigureAwait(false);
        }

        return VolumeProfileAggregator.Confine(
            start,
            end,
            [.. rows.Select(row => new ListeningRange(row.ContractId, row.RangeStart, row.RangeEnd))]);
    }

    /// <summary>
    /// Builds the refusal for a window with no overlapping tape. When any coverage exists later (or
    /// elsewhere), the message names the earliest covered instant so a pre-recording ask cannot read as quiet.
    /// </summary>
    private async Task<InvalidOperationException> EmptyTapeRefusalAsync(
        string venue,
        InstrumentId instrument,
        CancellationToken cancellationToken)
    {
        DateTimeOffset? earliest = await _database.TapeCoverage
            .AsNoTracking()
            .Where(c => c.Venue == venue && c.Instrument == instrument.Symbol)
            .OrderBy(c => c.RangeStart)
            .Select(c => (DateTimeOffset?)c.RangeStart)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (earliest is { } start)
        {
            return new InvalidOperationException(
                "A window before recording began cannot produce a footprint or volume profile. "
                + "The tape only goes forward — there is no historical backfill — and the earliest "
                + "covered time for this instrument is "
                + start.ToString("O", CultureInfo.InvariantCulture)
                + ". An empty answer here would look like a quiet market.");
        }

        return new InvalidOperationException(
            "A window with no tape cannot produce a footprint or volume profile. There is no "
            + "market-tape backfill, so an empty answer would look like a quiet market.");
    }
}
