using System.ComponentModel;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Venue;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MarqSpec.Mcp.TopstepX.Tools;

/// <summary>
/// <c>get_bars</c> and <c>get_latest_bars</c> — the cache-aside bar read every other market-data concern is
/// built on top of, but does not itself depend on.
/// </summary>
/// <remarks>
/// One of the five tool types gh#414 split <c>MarketDataTools</c> into, and the narrowest of them: four
/// dependencies where the one type had fifteen. A footprint cache, an indicator catalogue and the level
/// methods are not merely unused here — they are <b>unreachable</b>, which is the difference between the
/// file split gh#391 made and this one.
/// </remarks>
/// <param name="resolver">Turns a caller's symbol into an instrument, refusing first if the store is absent.</param>
/// <param name="cache">The cache-aside bar reader.</param>
/// <param name="guards">The request-shape checks the whole tool surface shares.</param>
/// <param name="clock">The clock <c>get_latest_bars</c> anchors on.</param>
[McpServerToolType]
public sealed class BarTools(
    InstrumentResolver resolver,
    BarCacheService cache,
    ToolGuards guards,
    TimeProvider clock)
{
    private readonly InstrumentResolver _resolver = resolver;
    private readonly BarCacheService _cache = cache;
    private readonly ToolGuards _guards = guards;
    private readonly TimeProvider _clock = clock;

    /// <summary>Reads OHLCV bars for a window.</summary>
    /// <param name="symbol">The instrument symbol.</param>
    /// <param name="resolutionMinutes">The bar size in minutes.</param>
    /// <param name="fromUtc">The window start, inclusive.</param>
    /// <param name="toUtc">The window end, exclusive.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The bars, and what the read cost.</returns>
    [McpServerTool(ReadOnly = true, Idempotent = true, Title = "Get bars")]
    [Description(
        "Reads OHLCV bars for an instrument over a time window. Served from a local cache; the vendor is "
        + "called only for buckets genuinely missing, where 'genuinely' excludes weekends, the daily "
        + "maintenance window and holidays. The response reports `venueRequests` and `fetchedBuckets`, and "
        + "only the first is evidence of a vendor round trip: `venueRequests == 0` is the exact test for "
        + "an answer served entirely from the store, while `fetchedBuckets` counts how much the answer "
        + "changed the store and can read zero even after a genuine fetch. Never returns a truncated "
        + "series: an over-cap window is refused with the real count. The response also carries "
        + "`contracts`: bars are keyed by the symbol, so a window spanning a quarterly roll contains TWO "
        + "contracts. `contracts.span` is SingleContract, SpansRoll, or Unknown — Unknown means the "
        + "provenance was never recorded, NOT that there was no roll. Adjacent quarters do not trade at "
        + "the same price; do not read a series across a roll as one.")]
    public async Task<ToolPayloads.BarSeries> GetBars(
        [Description("The instrument symbol, e.g. ES.")] string symbol,
        [Description("The bar size in minutes, e.g. 1, 5, 15, 60.")] int resolutionMinutes,
        [Description("Window start, ISO-8601 UTC, inclusive.")] DateTimeOffset fromUtc,
        [Description("Window end, ISO-8601 UTC, exclusive.")] DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        InstrumentId instrument = _resolver.Resolve(symbol);
        BarRange window = _guards.ValidateWindow(fromUtc, toUtc, resolutionMinutes);

        BarReadResult result = await ReadAsync(instrument, resolutionMinutes, window, cancellationToken)
            .ConfigureAwait(false);

        return new ToolPayloads.BarSeries(
            instrument.Symbol,
            resolutionMinutes,
            [.. result.Bars.Select(ToolPayloads.ToPoint)],
            result.FetchedBuckets,
            result.VenueRequests,
            ToolPayloads.ToCoverage(result.Bars));
    }

    /// <summary>Reads the most recent closed bars.</summary>
    /// <param name="symbol">The instrument symbol.</param>
    /// <param name="resolutionMinutes">The bar size in minutes.</param>
    /// <param name="count">How many bars.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The bars, ascending.</returns>
    /// <remarks>
    /// The window reaches back <c>count</c> buckets of <i>clock</i> time plus a generous margin, because
    /// closed sessions consume clock time without producing bars. Asking for exactly <c>count × barSize</c>
    /// over a Monday morning would reach back into Sunday and return a handful of bars.
    /// </remarks>
    [McpServerTool(ReadOnly = true, Idempotent = true, Title = "Get latest bars")]
    [Description(
        "Reads the most recent closed bars for an instrument. Anchored on the last CLOSED bucket, never a "
        + "forming one. This is usually the tool to reach for over get_bars, which needs explicit dates.")]
    public async Task<ToolPayloads.BarSeries> GetLatestBars(
        [Description("The instrument symbol, e.g. ES.")] string symbol,
        [Description("The bar size in minutes.")] int resolutionMinutes,
        [Description("How many bars to return.")] int count,
        CancellationToken cancellationToken)
    {
        InstrumentId instrument = _resolver.Resolve(symbol);
        ToolGuards.ValidateResolution(resolutionMinutes);
        int wanted = _guards.ValidateCount(count);

        TimeSpan barSize = TimeSpan.FromMinutes(resolutionMinutes);
        DateTimeOffset now = _clock.GetUtcNow();
        DateTimeOffset end = BarGapDetector.AlignDown(now, barSize);

        // Sized in ToolGuards rather than here. Reaching back is a rule about the resolution and the count
        // together -- and it was the one arithmetic on this surface that could still fault (gh#81).
        BarRange window = ToolGuards.LookbackWindow(end, resolutionMinutes, wanted);

        BarReadResult result = await ReadAsync(instrument, resolutionMinutes, window, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<Bar> tail = result.Bars.Count <= wanted
            ? result.Bars
            : [.. result.Bars.Skip(result.Bars.Count - wanted)];

        return new ToolPayloads.BarSeries(
            instrument.Symbol,
            resolutionMinutes,
            [.. tail.Select(ToolPayloads.ToPoint)],
            result.FetchedBuckets,
            result.VenueRequests,
            ToolPayloads.ToCoverage(tail));
    }

    private async Task<BarReadResult> ReadAsync(
        InstrumentId instrument,
        int resolutionMinutes,
        BarRange window,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _cache.GetBarsAsync(instrument, resolutionMinutes, window, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (VenueException ex)
        {
            // The venue failing is a different fact from this server refusing, and conflating them tells an
            // operator the vendor is down when they made a typo.
            throw new McpException("The venue could not answer: " + ex.Message);
        }

        // NO CATCH FOR THE STORE HERE, DELIBERATELY. A StoreContentionException, a 23505 from a lost race and
        // a connection dropped mid-save are all facts about this server's database, and they are translated
        // once for the whole tool surface by StoreFaultGuard at the call-tool boundary (gh#89). A second copy
        // here would cover the two tools that reach this method and leave get_indicators, get_key_levels and
        // record_observation exactly as exposed as they were -- which is the shape this repository has now
        // been bitten by three times.
    }
}
