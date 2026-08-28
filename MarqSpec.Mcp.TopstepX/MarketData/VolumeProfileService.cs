using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// Reads stored footprint cells and <c>TapeCoverage</c>, then asks Domain for the profile (gh#221).
/// </summary>
/// <remarks>
/// <para>
/// The host owns the store; Domain references nothing. Coverage rows are seeded by tests the
/// way gh#220 proved cells — the recorder is not built and nothing here subscribes to the hub.
/// </para>
/// <para>
/// A window that spans a roll or a listening hole is confined to the newest contiguous
/// run of the contract in front and the narrowing is reported. The reported window is that
/// run, not the ask. A window with no tape refuses rather than returning an empty profile
/// (ADR-0011).
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
        ArgumentException.ThrowIfNullOrWhiteSpace(venue);

        if (resolutionMinutes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(resolutionMinutes),
                resolutionMinutes,
                "A bar size must be positive.");
        }

        List<TapeCoverageRecord> rows = await _database.TapeCoverage
            .AsNoTracking()
            .Where(c => c.Venue == venue
                && c.Instrument == instrument.Symbol
                && c.RangeStart < end
                && c.RangeEnd > start)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        CoveredTapeWindow window = VolumeProfileAggregator.Confine(
            start,
            end,
            [.. rows.Select(row => new ListeningRange(row.ContractId, row.RangeStart, row.RangeEnd))]);

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

        return new VolumeProfileRead(VolumeProfileAggregator.From(cells), window);
    }
}
