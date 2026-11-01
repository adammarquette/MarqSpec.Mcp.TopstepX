using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>What one backfill page cost the cache.</summary>
/// <param name="Bars">How many bars the read returned.</param>
/// <param name="BarsWritten">How many buckets the store wrote from the venue.</param>
/// <param name="VenueRequests">
/// How many history requests the read issued. Zero means the store already covered it.
/// </param>
public readonly record struct BarBackfillPageResult(int Bars, int BarsWritten, int VenueRequests);

/// <summary>
/// The cache seam the backfill driver calls. <see cref="BarCacheService"/> is the production implementation.
/// </summary>
public interface IBarBackfillCache
{
    /// <summary>Reads one page, fetching only what the store does not already cover.</summary>
    /// <param name="instrument">The instrument.</param>
    /// <param name="resolutionMinutes">The bar size in minutes.</param>
    /// <param name="window">The page.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The page cost.</returns>
    Task<BarBackfillPageResult> ReadAsync(
        InstrumentId instrument,
        int resolutionMinutes,
        BarRange window,
        CancellationToken cancellationToken);
}

/// <summary>Adapts <see cref="BarCacheService"/> to <see cref="IBarBackfillCache"/>.</summary>
public sealed class BarCacheBackfillAdapter(BarCacheService cache) : IBarBackfillCache
{
    private readonly BarCacheService _cache = cache;

    /// <inheritdoc />
    public async Task<BarBackfillPageResult> ReadAsync(
        InstrumentId instrument,
        int resolutionMinutes,
        BarRange window,
        CancellationToken cancellationToken)
    {
        BarReadResult result = await _cache
            .GetBarsAsync(instrument, resolutionMinutes, window, cancellationToken)
            .ConfigureAwait(false);
        return new BarBackfillPageResult(result.Bars.Count, result.FetchedBuckets, result.VenueRequests);
    }
}
