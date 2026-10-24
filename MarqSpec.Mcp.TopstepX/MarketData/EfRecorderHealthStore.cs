using MarqSpec.Mcp.TopstepX.Data;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// <see cref="IRecorderHealthStore"/> over <see cref="TopstepXDbContext"/>.
/// </summary>
/// <remarks>
/// Reads only. A probe that fails returns <see langword="false"/> and logs nothing: the driver's
/// exception text can name the host, and a health poll that logged it would repeat that target
/// on every check (gh#551).
/// </remarks>
public sealed class EfRecorderHealthStore(TopstepXDbContext database) : IRecorderHealthStore
{
    private readonly TopstepXDbContext _database = database;

    /// <inheritdoc />
    public async Task<bool> DatabaseConnectedAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _database.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ContractLastTick>> LastTicksAsync(CancellationToken cancellationToken)
    {
        var grouped = await _database.Trades
            .AsNoTracking()
            .GroupBy(trade => new { trade.Instrument, trade.ContractId })
            .Select(group => new
            {
                group.Key.Instrument,
                group.Key.ContractId,
                TradeTimeUtc = group.Max(trade => trade.TradeTimeUtc),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return grouped
            .Select(row => new ContractLastTick(row.Instrument, row.ContractId, row.TradeTimeUtc))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CoverageSlice>> CoverageAsync(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken)
    {
        List<CoverageSlice> slices = await _database.TapeCoverage
            .AsNoTracking()
            .Where(row => row.RangeStart < windowEnd && row.RangeEnd > windowStart)
            .Select(row => new CoverageSlice(row.Instrument, row.ContractId, row.RangeStart, row.RangeEnd))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return slices;
    }
}
