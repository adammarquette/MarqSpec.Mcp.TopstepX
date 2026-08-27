using System.ComponentModel;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
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
    IndicatorCacheService indicators,
    LevelMethodCatalog levelMethods,
    IMarketDataGateway gateway,
    ToolGuards guards,
    StoreAvailabilityHolder store,
    TimeProvider clock,
    IOptions<KeyLevelDetectionOptions> detection)
{
    private readonly BarCacheService _cache = cache;
    private readonly TopstepXDbContext _database = database;
    private readonly InstrumentRegistry _registry = registry;
    private readonly IndicatorCatalog _catalog = catalog;
    private readonly IndicatorCacheService _indicators = indicators;
    private readonly LevelMethodCatalog _levelMethods = levelMethods;
    private readonly IMarketDataGateway _gateway = gateway;
    private readonly ToolGuards _guards = guards;
    private readonly StoreAvailabilityHolder _store = store;
    private readonly TimeProvider _clock = clock;

    // The detection defaults, not the detection options: two of the four fields are overridden per call, and
    // the merge happens in ResolveDetection. The catalogue holds no copy of these -- ILevelMethod.Detect
    // takes them per call precisely because levels are computed on read and nothing stores them (ADR-0013),
    // so the tool boundary is where "the caller did not say" becomes "the operator's configured value".
    private readonly KeyLevelDetectionOptions _detection = detection.Value;

    /// <summary>How much history key-level detection covers when the caller does not say.</summary>
    /// <remarks>
    /// Enough for a level to have been touched more than once at most intraday resolutions. The description
    /// on the parameter advertised this number while the schema required the argument, so an agent following
    /// the description was rejected before its call reached any code (gh#70).
    /// </remarks>
    public const int DefaultLookbackBars = 500;

    /// <summary>The level method <c>get_key_levels</c> detects with.</summary>
    /// <remarks>
    /// Resolved through <see cref="LevelMethodCatalog"/> rather than called directly, so the path that serves
    /// levels today is the path every later method arrives on (gh#243). The tool surface carries no method
    /// argument yet — selecting one per call is a later card on gh#232 — so this is the whole vocabulary in
    /// use, and what the tool returns is unchanged either way.
    /// </remarks>
    private const string SwingLevelMethodName = "swing";

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
        + "changed the store and reads zero after a genuine fetch. Never returns a truncated series: an "
        + "over-cap window is refused with the real count. The response also carries `contracts`: bars are "
        + "keyed by the symbol, so a window spanning a quarterly roll contains TWO contracts. "
        + "`contracts.span` is SingleContract, SpansRoll, or Unknown — Unknown means the provenance was "
        + "never recorded, NOT that there was no roll. Adjacent quarters do not trade at the same price; "
        + "do not read a series across a roll as one.")]
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
        "Reads an indicator series from a local cache. The VENDOR IS NEVER CALLED: every value is computed "
        + "from bars this server already holds. A series the cache has no values for — after an indicator is "
        + "added or a period is changed — is computed and stored by the FIRST read that asks for it, which "
        + "for a year of 5-minute bars costs about eight seconds once and nothing on any read after. Known "
        + "indicators: atr, rsi, sma, ema, macd, macd-signal, "
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

        // Cache-aside, the way bars already are: a value the catalogue computes and the store does not hold
        // is projected from the bars already cached before the read runs. No vendor traffic either way --
        // the bars are local (gh#246). A warm series pays two aggregates and nothing else.
        await EnsureProjectedAsync(instrument, resolutionMinutes, cancellationToken).ConfigureAwait(false);

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
        "Reads one indicator value as of a moment, from the same local cache get_indicators reads, and on "
        + "the same terms: no vendor call, and a series with no stored values is computed by the first read "
        + "that needs it. Returns the value at or BEFORE that moment, never after — "
        + "a later value is information the market did not have. Cannot-measure DROPS the `value` KEY instead "
        + "of sending null, so the whole reading arrives as `{}`: test whether the key is THERE, never "
        + "whether it equals null. An ABSENT value means CANNOT MEASURE, not zero and not neutral — refuse "
        + "to conclude rather than substitute. `contractId` names the contract the value belongs to when it "
        + "is known; two readings from different contracts are not comparable.")]
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

        // The same cache-aside trigger get_indicators is on (gh#246). This is the read get_market_snapshot
        // composes, eleven times per resolution, so it is the one that would have gone on reporting
        // cannot-measure over bars that measure perfectly well.
        await EnsureProjectedAsync(instrument, resolutionMinutes, cancellationToken).ConfigureAwait(false);

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
    /// <param name="pivotSource">Which price on a bar a pivot is measured from, or null for the configured one.</param>
    /// <param name="pivotLookback">How many bars either side a pivot must dominate, or null for the configured one.</param>
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
        + "weight for it. `pivotSource` and `pivotLookback` tune the detection for one call; OMIT them and "
        + "this server's configured defaults apply. They carry no default of their own, because the default "
        + "is an operator setting rather than a constant — omitting one asks for the configured value, it "
        + "does not name a particular one. Zone width and the significance floor are operator settings only, "
        + "so every level this server reports is sized and filtered alike and two of them can be compared. "
        + "The response reports the detection it actually ran under as `detection`, so an empty `levels` can "
        + "be told from a market with no structure — read it with `detectedOverBars`.")]
    public async Task<ToolPayloads.LevelSet> GetKeyLevels(
        [Description("The instrument symbol, e.g. ES.")] string symbol,
        [Description("The timeframe in minutes.")] int resolutionMinutes,
        [Description("How many bars of history to detect over. Omit for 500.")]
        int lookbackBars = DefaultLookbackBars,
        [Description(
            "Which price on a bar a pivot is measured from: HeikinAshiBody, Body or HighLow. Omit to use "
            + "this server's configured source. HeikinAshiBody smooths single-bar noise into structure and "
            + "is the shipped default. Body reads open and close only, HighLow reads the raw wicks. NOTE: on "
            + "a continuous intraday series, where a bar opens at the previous close, a body high ties with "
            + "its neighbour's on every bar and Body can find NO pivots at all — an empty level set there is "
            + "a property of the source, not a market without structure. An unknown name is an error listing "
            + "the three.")]
        string? pivotSource = null,
        [Description(
            "How many bars either side a pivot must dominate; larger means fewer, more structural levels. "
            + "Omit to use this server's configured lookback. A pivot dominates this many bars on BOTH "
            + "sides, so detection needs 2 × this + 1 bars to find even one — and the window it runs over "
            + "is whatever the store holds, cut back to the contract in front, which can be far less than "
            + "`lookbackBars` asked for. When that happens the answer is an EMPTY level set, not an error: "
            + "compare `detection.pivotLookback` against `detectedOverBars` to tell that from a market with "
            + "no structure.")]
        int? pivotLookback = null,
        CancellationToken cancellationToken = default)
    {
        InstrumentId instrument = Resolve(symbol);
        ToolGuards.ValidateResolution(resolutionMinutes);
        int wanted = _guards.ValidateCount(lookbackBars);

        // Before the read, not after it. Every check below is a fact about the REQUEST, and a store with no
        // bars returns early -- so validating after the read is how an Unknown source arriving from
        // configuration would be answered with an empty level set instead of a refusal.
        KeyLevelOptions detection = ResolveDetection(pivotSource, pivotLookback);

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
                [], new ToolPayloads.ContractCoverage(ToolPayloads.ContractSpan.Unknown, []), 0,
                Reported(detection));
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

        IReadOnlyList<KeyLevelZone> zones =
            _levelMethods.Resolve(SwingLevelMethodName).Detect(detectable, scale, detection);

        return new ToolPayloads.LevelSet(
            [.. zones.Select(z => new ToolPayloads.LevelInfo(
                resolutionMinutes, z.Bottom, z.Top, z.Midpoint, z.Kind, z.Significance, z.TouchCount,
                z.FormedAtBucket))],
            coverage,
            detectable.Count,
            Reported(detection));
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

        // NO CATCH FOR THE STORE HERE, DELIBERATELY. A StoreContentionException, a 23505 from a lost race and
        // a connection dropped mid-save are all facts about this server's database, and they are translated
        // once for the whole tool surface by StoreFaultGuard at the call-tool boundary (gh#89). A second copy
        // here would cover the two tools that reach this method and leave get_indicators, get_key_levels and
        // record_observation exactly as exposed as they were -- which is the shape this repository has now
        // been bitten by three times.
    }

    /// <summary>
    /// Brings the stored indicators for a series up to what the catalogue computes, before reading them.
    /// </summary>
    /// <param name="instrument">The instrument.</param>
    /// <param name="resolutionMinutes">The bar size in minutes.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <remarks>
    /// <b>No catch here, deliberately</b>, for the reason <see cref="ReadAsync"/> states: a
    /// <c>StoreContentionException</c> is a fact about this server's database and is translated once for the
    /// whole tool surface by <see cref="StoreFaultGuard"/>. It cannot raise a <c>VenueException</c> at all —
    /// <see cref="IndicatorCacheService"/> holds no gateway.
    /// </remarks>
    private Task EnsureProjectedAsync(
        InstrumentId instrument,
        int resolutionMinutes,
        CancellationToken cancellationToken) =>
        _indicators.EnsureProjectedAsync(
            _gateway.VenueId, instrument, resolutionMinutes, cancellationToken);

    /// <summary>
    /// Merges what the caller asked for over what the operator configured, and refuses what neither can mean.
    /// </summary>
    /// <param name="pivotSource">The requested source name, or null to take the configured one.</param>
    /// <param name="pivotLookback">The requested lookback, or null to take the configured one.</param>
    /// <returns>The options detection will run under.</returns>
    /// <exception cref="McpException">
    /// The named source is not in the vocabulary, the configured source is not either, or the lookback is
    /// below one.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>The configured source is checked on the same terms as the caller's.</b> Startup validation already
    /// refuses an unservable one (<see cref="KeyLevelDetectionOptions.Validate"/>), so this second door is
    /// closed on a room that should be locked — which is the point: the two doors are opened by different
    /// keys. A value that never went through <c>ValidateOnStart</c> otherwise reaches
    /// <c>KeyLevels.PivotPrices</c>, which reads anything it does not recognise as Heikin-Ashi and returns
    /// an ordinary-looking level set measured from a source nobody chose.
    /// </para>
    /// <para>
    /// <b>Everything refused here is a fact about the REQUEST, decidable before a single bar is read, and
    /// that is now the rule rather than an accident.</b> A lookback below one is not a lookback under any
    /// data. Whether a lookback is <i>satisfiable</i> is a different kind of question and this is the wrong
    /// place for it: it depends on <c>detectable.Count</c> — what the store actually holds, cut back to the
    /// contract in front — which is not known until after the read, and is not something every caller can
    /// change. An earlier revision bounded the lookback against <c>lookbackBars</c> instead, and bounding
    /// the requested window rather than the detected one is wrong twice over. It refused calls that would
    /// have succeeded, because a caller may ask for 500 bars over a store holding 40; and it refused calls
    /// nobody could fix, because <c>get_market_snapshot</c> passes a fixed <c>max(barCount, 200)</c> and
    /// exposes neither knob — a configured <c>PivotLookback</c> of 100, legal on its own range, made every
    /// snapshot call fail with advice to change two arguments that tool does not have.
    /// </para>
    /// <para>
    /// <b>An unsatisfiable lookback is therefore answered, not refused — and the answer says so.</b>
    /// <see cref="ToolPayloads.LevelSet.Detection"/> reports all four parameters beside
    /// <c>detectedOverBars</c>, so an empty level set carries its own explanation. That covers strictly more
    /// than the refusal did: too few bars, a roll that cut the window down, a source whose candidates all
    /// tie, and a significance floor that filtered every zone all arrive explicable, where the bound reached
    /// only the first and only when the caller had asked for exactly what was stored.
    /// </para>
    /// </remarks>
    private KeyLevelOptions ResolveDetection(string? pivotSource, int? pivotLookback)
    {
        KeyLevelOptions defaults = _detection.Defaults();

        PivotSource source;
        if (pivotSource is null)
        {
            source = PivotSources.IsServable(defaults.Source)
                ? defaults.Source
                : throw new McpException(
                    "This server's configured pivot source, '" + defaults.Source
                    + "', is not one it can detect through. Known sources: " + PivotSources.KnownNames
                    + ". Set " + KeyLevelDetectionOptions.SectionName + "__Source to one of them, or name a "
                    + "source on the call.");
        }
        else
        {
            try
            {
                source = PivotSources.Resolve(pivotSource);
            }
            catch (KeyNotFoundException ex)
            {
                throw new McpException(ex.Message);
            }
        }

        int lookback = pivotLookback ?? defaults.Lookback;

        return lookback < 1
            ? throw new McpException(
                "pivotLookback must be at least 1; got "
                + lookback.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ". A pivot dominates that many bars on each side, so there is no such thing as a pivot "
                + "confirmed by none.")
            : defaults with { Source = source, Lookback = lookback };
    }

    /// <summary>
    /// The detection options as the payload reports them.
    /// </summary>
    /// <param name="options">The options detection ran under.</param>
    /// <returns>The reported detection.</returns>
    /// <remarks>
    /// Projected from the same record detection was handed, never rebuilt from configuration. Read back from
    /// <see cref="_detection"/> instead, this would report the operator's defaults on a call that overrode
    /// them — a payload describing a detection that did not happen, which is worse than reporting nothing.
    /// </remarks>
    private static ToolPayloads.LevelDetection Reported(KeyLevelOptions options) =>
        new(options.Source, options.Lookback, options.ZoneAtrMultiple, options.MinSignificance);

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
