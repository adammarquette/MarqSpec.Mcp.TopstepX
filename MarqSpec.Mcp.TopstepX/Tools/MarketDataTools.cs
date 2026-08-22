using System.ComponentModel;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MarqSpec.Mcp.TopstepX.Tools;

/// <summary>
/// Bars, indicators and levels — the reason this server exists.
/// </summary>
[McpServerToolType]
public sealed class MarketDataTools(
    BarCacheService cache,
    TopstepXDbContext database,
    InstrumentRegistry registry,
    IndicatorCatalog catalog,
    IMarketDataGateway gateway,
    ToolGuards guards,
    StoreAvailabilityHolder store,
    TimeProvider clock)
{
    private readonly BarCacheService _cache = cache;
    private readonly TopstepXDbContext _database = database;
    private readonly InstrumentRegistry _registry = registry;
    private readonly IndicatorCatalog _catalog = catalog;
    private readonly IMarketDataGateway _gateway = gateway;
    private readonly ToolGuards _guards = guards;
    private readonly StoreAvailabilityHolder _store = store;
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
        + "maintenance window and holidays. The response reports fetchedBuckets, so a caller can see whether "
        + "the answer cost a vendor round trip. Never returns a truncated series: an over-cap window is "
        + "refused with the real count.")]
    public async Task<ToolPayloads.BarSeries> GetBars(
        [Description("The instrument symbol, e.g. ES.")] string symbol,
        [Description("The bar size in minutes, e.g. 1, 5, 15, 60.")] int resolutionMinutes,
        [Description("Window start, ISO-8601 UTC, inclusive.")] DateTimeOffset fromUtc,
        [Description("Window end, ISO-8601 UTC, exclusive.")] DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        InstrumentId instrument = Resolve(symbol);
        BarRange window = _guards.ValidateWindow(fromUtc, toUtc, resolutionMinutes);

        BarReadResult result = await ReadAsync(instrument, resolutionMinutes, window, cancellationToken)
            .ConfigureAwait(false);

        return new ToolPayloads.BarSeries(
            instrument.Symbol,
            resolutionMinutes,
            [.. result.Bars.Select(ToolPayloads.ToPoint)],
            result.FetchedBuckets,
            result.VenueRequests);
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
        InstrumentId instrument = Resolve(symbol);
        int wanted = _guards.ValidateCount(count);

        TimeSpan barSize = TimeSpan.FromMinutes(resolutionMinutes);
        DateTimeOffset now = _clock.GetUtcNow();
        DateTimeOffset end = BarGapDetector.AlignDown(now, barSize);

        // Reach back four times the bar span, plus four days. Sessions are shut roughly a quarter of the
        // clock and closed for whole weekends, so a window sized to the bar count alone comes up short.
        TimeSpan reach = TimeSpan.FromTicks(barSize.Ticks * wanted * 4) + TimeSpan.FromDays(4);
        BarRange window = new(end - reach, end);

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
            result.VenueRequests);
    }

    /// <summary>Reads a stored indicator series.</summary>
    /// <param name="symbol">The instrument symbol.</param>
    /// <param name="resolutionMinutes">The bar size in minutes.</param>
    /// <param name="indicator">The indicator name.</param>
    /// <param name="fromUtc">The window start, inclusive.</param>
    /// <param name="toUtc">The window end, exclusive.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The values.</returns>
    [McpServerTool(ReadOnly = true, Idempotent = true, Title = "Get indicators")]
    [Description(
        "Reads a pre-computed indicator series. Known indicators: atr, rsi, sma, ema, macd, macd-signal, "
        + "macd-histogram, vwap, bb-upper, bb-middle, bb-lower. An unknown name is an error listing these, "
        + "because a typo that returned no data would read as 'no signal'. Buckets where the indicator could "
        + "not yet measure are ABSENT rather than zero.")]
    public async Task<ToolPayloads.IndicatorSeries> GetIndicators(
        [Description("The instrument symbol, e.g. ES.")] string symbol,
        [Description("The bar size in minutes.")] int resolutionMinutes,
        [Description("The indicator name, e.g. rsi.")] string indicator,
        [Description("Window start, ISO-8601 UTC, inclusive.")] DateTimeOffset fromUtc,
        [Description("Window end, ISO-8601 UTC, exclusive.")] DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        InstrumentId instrument = Resolve(symbol);
        BarRange window = _guards.ValidateWindow(fromUtc, toUtc, resolutionMinutes);
        IIndicator resolved = ResolveIndicator(indicator);

        List<ToolPayloads.IndicatorPoint> values = await _database.IndicatorValues
            .Where(v => v.Venue == _gateway.VenueId
                && v.Instrument == instrument.Symbol
                && v.ResolutionMinutes == resolutionMinutes
                && v.Indicator == resolved.Name
                && v.Period == resolved.Period
                && v.BucketStart >= window.Start
                && v.BucketStart < window.End)
            .OrderBy(v => v.BucketStart)
            .Select(v => new ToolPayloads.IndicatorPoint(v.BucketStart, v.Value))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new ToolPayloads.IndicatorSeries(
            instrument.Symbol, resolutionMinutes, resolved.Name, resolved.Period, values);
    }

    /// <summary>Reads one indicator value as of a moment.</summary>
    /// <param name="symbol">The instrument symbol.</param>
    /// <param name="resolutionMinutes">The bar size in minutes.</param>
    /// <param name="indicator">The indicator name.</param>
    /// <param name="asOfUtc">The moment.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The value, or a null value meaning cannot measure.</returns>
    [McpServerTool(ReadOnly = true, Idempotent = true, Title = "Get indicator as of")]
    [Description(
        "Reads one indicator value as of a moment. Returns the value at or BEFORE that moment, never after — "
        + "a later value is information the market did not have. A null value means CANNOT MEASURE, not zero "
        + "and not neutral: the right response to it is to refuse to conclude, not to substitute.")]
    public async Task<ToolPayloads.IndicatorReading> GetIndicatorAt(
        [Description("The instrument symbol, e.g. ES.")] string symbol,
        [Description("The bar size in minutes.")] int resolutionMinutes,
        [Description("The indicator name, e.g. atr.")] string indicator,
        [Description("The moment, ISO-8601 UTC.")] DateTimeOffset asOfUtc,
        CancellationToken cancellationToken)
    {
        InstrumentId instrument = Resolve(symbol);
        IIndicator resolved = ResolveIndicator(indicator);
        DateTimeOffset asOf = asOfUtc.ToUniversalTime();

        var row = await _database.IndicatorValues
            .Where(v => v.Venue == _gateway.VenueId
                && v.Instrument == instrument.Symbol
                && v.ResolutionMinutes == resolutionMinutes
                && v.Indicator == resolved.Name
                && v.Period == resolved.Period
                && v.BucketStart <= asOf)
            .OrderByDescending(v => v.BucketStart)
            .Select(v => new { v.Value, v.BucketStart })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return row is null
            ? new ToolPayloads.IndicatorReading(null, null)
            : new ToolPayloads.IndicatorReading(row.Value, row.BucketStart);
    }

    /// <summary>Detects support and resistance zones.</summary>
    /// <param name="symbol">The instrument symbol.</param>
    /// <param name="resolutionMinutes">The timeframe in minutes.</param>
    /// <param name="lookbackBars">How much history to detect over.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The levels, ordered by price.</returns>
    [McpServerTool(ReadOnly = true, Idempotent = true, Title = "Get key levels")]
    [Description(
        "Detects support and resistance as ZONES rather than lines, sized in ATR multiples so a zone is "
        + "comparably wide across instruments. Significance is prominence in ATR multiples, so a 2.0 on ES "
        + "and a 2.0 on NQ mean the same thing. A zone's support/resistance label is assigned relative to the "
        + "CURRENT price, not to how it formed — a broken resistance is today's support.")]
    public async Task<IReadOnlyList<ToolPayloads.LevelInfo>> GetKeyLevels(
        [Description("The instrument symbol, e.g. ES.")] string symbol,
        [Description("The timeframe in minutes.")] int resolutionMinutes,
        [Description("How many bars of history to detect over. 500 is a reasonable default.")]
        int lookbackBars,
        CancellationToken cancellationToken)
    {
        InstrumentId instrument = Resolve(symbol);
        int wanted = _guards.ValidateCount(lookbackBars);

        List<Bar> bars = await _database.Bars
            .Where(b => b.Venue == _gateway.VenueId
                && b.Instrument == instrument.Symbol
                && b.ResolutionMinutes == resolutionMinutes)
            .OrderByDescending(b => b.BucketStart)
            .Take(wanted)
            .Select(b => new Bar(b.BucketStart, b.Open, b.High, b.Low, b.Close, b.Volume))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (bars.Count == 0)
        {
            return [];
        }

        bars.Reverse();

        // Levels are scaled and scored in ATR, so they are computed from the same bars rather than read from
        // the store: an ATR row is keyed to the configured period, and detection needs it aligned one-to-one
        // with exactly these bars.
        IIndicator atr = _catalog.Resolve("atr");
        IReadOnlyList<decimal?> scale = atr.Compute(bars);

        KeyLevelOptions options = new();
        IReadOnlyList<KeyLevelZone> zones = KeyLevels.Detect(bars, scale, options);

        return [.. zones.Select(z => new ToolPayloads.LevelInfo(
            resolutionMinutes, z.Bottom, z.Top, z.Midpoint, z.Kind, z.Significance, z.TouchCount, z.FormedAtBucket))];
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
    }

    private IIndicator ResolveIndicator(string name)
    {
        try
        {
            return _catalog.Resolve(name);
        }
        catch (KeyNotFoundException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    private InstrumentId Resolve(string symbol)
    {
        // Every tool in this class reads the store, so the check sits on the path they all take. A per-tool
        // check is a check a new tool forgets.
        _store.Value.Require();

        try
        {
            return _registry.Resolve(symbol);
        }
        catch (KeyNotFoundException ex)
        {
            throw new McpException(ex.Message);
        }
        catch (ArgumentException ex)
        {
            throw new McpException(ex.Message);
        }
    }
}
