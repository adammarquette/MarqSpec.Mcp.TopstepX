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
        + "only the first is evidence of a bar fetch: `venueRequests` counts HISTORY requests alone, so "
        + "`venueRequests == 0` is the exact test for no bar fetch — NOT for no vendor call at all, "
        + "because a read whose gaps the empty-range memo covers still makes one contract search per "
        + "request. It undercounts vendor traffic and never overcounts it. `fetchedBuckets` counts how "
        + "much the answer changed the store and can read zero even after a genuine fetch. Never returns "
        + "a truncated series: an over-cap window is refused with the real count. The response also "
        + "carries `contracts`: bars are keyed by the symbol, so a window spanning a quarterly roll "
        + "contains TWO contracts. `contracts.span` is SingleContract, SpansRoll, or Unknown — Unknown "
        + "means the provenance was never recorded, NOT that there was no roll. Adjacent quarters do "
        + "not trade at the same price; do not read a series across a roll as one. "
        + "`history.selection` is a SEPARATE question from `contracts.span` and must not be read as one of "
        + "its values: span asks whether these bars cross a roll, history asks whether the contracts they "
        + "were CHOSEN FROM were the ones this product's contract-month cycle names. A window can be "
        + "SingleContract and still have been decided among survivors. AsTheCycleNames means every expiry "
        + "the cycle named was listed by the vendor and the volume decision ran over all of them. "
        + "NarrowedByTheVenue means the vendor did not list some of them, so the decision ran over what was "
        + "left — the bars are a real series from a real contract, and when the only survivor was the "
        + "vendor's own active contract that stretch is the thin, complete-looking series this selection "
        + "exists to prevent you acting on. FellBackToTheFront is worse still: NONE of the cycle's expiries "
        + "was listed, so no volume decision ran for that stretch at all. `history.unresolved` names the "
        + "expiries that fell away, e.g. M26. NotDecidedHere means THIS call decided no history, and it "
        + "covers three different situations: every bucket was already stored, or the window sits in the "
        + "present band, or there was no contract-month cycle to decide against at all — this server does "
        + "not serve that instrument, or the vendor's active contract has an expiry that does not read "
        + "against the cycle. In that third case a deep window is fetched entirely from the vendor's active "
        + "contract with no volume decision made, which is the same thin-series risk as FellBackToTheFront "
        + "and is reported only in this server's log; `venueRequests` above zero on a window reaching well "
        + "before the last few days is what distinguishes it from the first two. NotDecidedHere is in no "
        + "case a statement that the stored history is whole: bars fetched by an earlier degraded read read "
        + "back exactly like any other, and nothing recorded about them says otherwise.")]
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
            ToolPayloads.ToCoverage(result.Bars),
            result.History);
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
        + "forming one. This is usually the tool to reach for over get_bars, which needs explicit dates. "
        + "The response shape is get_bars', `contracts` and `history` included, and those two mean exactly "
        + "what they mean there — read `history.selection` before treating a deep lookback as ordinary.")]
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
            ToolPayloads.ToCoverage(tail),
            result.History);
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
