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

    /// <summary>How much history key-level detection covers when the caller does not say.</summary>
    /// <remarks>
    /// Enough for a level to have been touched more than once at most intraday resolutions. The description
    /// on the parameter advertised this number while the schema required the argument, so an agent following
    /// the description was rejected before its call reached any code (gh#70).
    /// </remarks>
    public const int DefaultLookbackBars = 500;

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
        + "refused with the real count. The response also carries `contracts`: bars are keyed by the symbol, "
        + "so a window spanning a quarterly roll contains TWO contracts. `contracts.span` is SingleContract, "
        + "SpansRoll, or Unknown — Unknown means the provenance was never recorded, NOT that there was no "
        + "roll. Adjacent quarters do not trade at the same price; do not read a series across a roll as one.")]
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
        InstrumentId instrument = Resolve(symbol);
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
        + "not yet measure are ABSENT rather than zero. Values are never smoothed across a contract roll, so "
        + "expect a run of absent values just after one; `contracts.span` says whether the window contains a "
        + "roll — and Unknown there means the provenance was never recorded, not that there was none.")]
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
            instrument.Symbol,
            resolutionMinutes,
            resolved.Name,
            resolved.Period,
            values,
            await CoverageAsync(instrument, resolutionMinutes, window, cancellationToken)
                .ConfigureAwait(false));
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
        + "and not neutral: the right response to it is to refuse to conclude, not to substitute. The result "
        + "names the contract the value belongs to; two readings from different contracts are not comparable.")]
    public async Task<ToolPayloads.IndicatorReading> GetIndicatorAt(
        [Description("The instrument symbol, e.g. ES.")] string symbol,
        [Description("The bar size in minutes.")] int resolutionMinutes,
        [Description("The indicator name, e.g. atr.")] string indicator,
        [Description("The moment, ISO-8601 UTC.")] DateTimeOffset asOfUtc,
        CancellationToken cancellationToken)
    {
        InstrumentId instrument = Resolve(symbol);
        ToolGuards.ValidateResolution(resolutionMinutes);
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

        if (row is null)
        {
            return new ToolPayloads.IndicatorReading(null, null);
        }

        // Which contract this reading belongs to. A value is only ever computed inside one contract, so the
        // bar at its bucket is the answer -- and without it, two readings either side of a roll are two
        // numbers with nothing saying they measure different instruments.
        string? contractId = await _database.Bars
            .Where(b => b.Venue == _gateway.VenueId
                && b.Instrument == instrument.Symbol
                && b.ResolutionMinutes == resolutionMinutes
                && b.BucketStart == row.BucketStart)
            .Select(b => b.ContractId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return new ToolPayloads.IndicatorReading(row.Value, row.BucketStart, contractId);
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
        + "CURRENT price, not to how it formed — a broken resistance is today's support. Detection is "
        + "confined to the contract in front: if the lookback spans a quarterly roll, `detectedOverBars` is "
        + "smaller than the lookback asked for, because a level from the expiring contract sits at a price "
        + "the current one has never traded. Read `detectedOverBars` — fewer bars behind a level is less "
        + "weight for it.")]
    public async Task<ToolPayloads.LevelSet> GetKeyLevels(
        [Description("The instrument symbol, e.g. ES.")] string symbol,
        [Description("The timeframe in minutes.")] int resolutionMinutes,
        [Description("How many bars of history to detect over. Omit for 500.")]
        int lookbackBars = DefaultLookbackBars,
        CancellationToken cancellationToken = default)
    {
        InstrumentId instrument = Resolve(symbol);
        ToolGuards.ValidateResolution(resolutionMinutes);
        int wanted = _guards.ValidateCount(lookbackBars);

        List<Bar> bars = await _database.Bars
            .Where(b => b.Venue == _gateway.VenueId
                && b.Instrument == instrument.Symbol
                && b.ResolutionMinutes == resolutionMinutes)
            .OrderByDescending(b => b.BucketStart)
            .Take(wanted)
            .Select(b => new Bar(b.BucketStart, b.Open, b.High, b.Low, b.Close, b.Volume, b.ContractId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (bars.Count == 0)
        {
            return new ToolPayloads.LevelSet(
                [], new ToolPayloads.ContractCoverage(ToolPayloads.ContractSpan.Unknown, []), 0);
        }

        // Reversed FIRST, then described. Coverage over a descending series would give every segment a
        // FirstBucket later than its LastBucket -- harmless while nothing reads it, and a shape not worth
        // leaving available to the next edit.
        bars.Reverse();
        ToolPayloads.ContractCoverage coverage = ToolPayloads.ToCoverage(bars);

        // Only the contract in front. A zone detected across a roll is built partly from a quarter that no
        // longer trades, and it lands at a price the current contract has never been near -- which reads
        // exactly like a level price is about to touch. The lookback is reported alongside, because silently
        // halving the history behind a level changes how much weight it deserves (ADR-0011).
        IReadOnlyList<Bar> detectable = ContractRollDetector.Newest(bars);

        // Levels are scaled and scored in ATR, so they are computed from the same bars rather than read from
        // the store: an ATR row is keyed to the configured period, and detection needs it aligned one-to-one
        // with exactly these bars.
        IIndicator atr = _catalog.Resolve("atr");
        IReadOnlyList<decimal?> scale = atr.Compute(detectable);

        KeyLevelOptions options = new();
        IReadOnlyList<KeyLevelZone> zones = KeyLevels.Detect(detectable, scale, options);

        return new ToolPayloads.LevelSet(
            [.. zones.Select(z => new ToolPayloads.LevelInfo(
                resolutionMinutes, z.Bottom, z.Top, z.Midpoint, z.Kind, z.Significance, z.TouchCount,
                z.FormedAtBucket))],
            coverage,
            detectable.Count);
    }

    /// <summary>
    /// Reports which contracts produced the bars underneath a window, without loading the bars themselves.
    /// </summary>
    /// <remarks>
    /// Two columns rather than whole rows: an indicator read is not a bar read, and it should not pay for one.
    /// The segmentation still happens in memory rather than as a <c>GROUP BY</c>, so that every tool answers
    /// this question with the same code and the same contiguity semantics — a grouped query would report an
    /// interleaved series as two contracts where the shared detector reports three runs, and two tools
    /// disagreeing about whether a roll happened is worse than either answer.
    /// </remarks>
    private async Task<ToolPayloads.ContractCoverage> CoverageAsync(
        InstrumentId instrument,
        int resolutionMinutes,
        BarRange window,
        CancellationToken cancellationToken)
    {
        List<Bar> shape = await _database.Bars
            .Where(b => b.Venue == _gateway.VenueId
                && b.Instrument == instrument.Symbol
                && b.ResolutionMinutes == resolutionMinutes
                && b.BucketStart >= window.Start
                && b.BucketStart < window.End)
            .OrderBy(b => b.BucketStart)
            // Prices are structural zeros, never read. Coverage is a question about PROVENANCE, and
            // ContractRollDetector.Segment looks only at the bucket and the contract id -- so this projects
            // the two fields the answer depends on and leaves the rest unpopulated rather than fetching an
            // OHLCV payload to throw away. If anything downstream ever reads a price off one of these, it is
            // reading a zero that was never a price.
            .Select(b => new Bar(b.BucketStart, 0m, 0m, 0m, 0m, 0L, b.ContractId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return ToolPayloads.ToCoverage(shape);
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
        catch (StoreContentionException ex)
        {
            // A THIRD fact: nothing upstream failed and nothing was refused -- the store would not serialise
            // this write against another one, and a retry has already been spent. Raw, it reaches a caller as
            // a nested PostgresException about "concurrent delete", which says nothing about what to do; this
            // is the gh#69 shape on the path gh#73 reshaped. Caught NARROWLY on purpose: IndicatorProjector's
            // whole-series guard also surfaces as an InvalidOperationException, and a blanket catch would
            // swallow an invariant violation as though it were contention.
            throw new McpException(ex.Message);
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
