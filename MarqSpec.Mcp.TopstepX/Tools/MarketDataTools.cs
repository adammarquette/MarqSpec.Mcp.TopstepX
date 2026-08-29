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
    IOptions<KeyLevelDetectionOptions> detection,
    VolumeProfileService volumeProfiles,
    TapeAvailabilityHolder? tape = null,
    TapeVolumeFrontService? volumeFront = null)
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
    private readonly VolumeProfileService _volumeProfiles = volumeProfiles;
    private readonly TapeAvailabilityHolder _tape = tape ?? new();
    private readonly TapeVolumeFrontService _volumeFront = volumeFront
        ?? new TapeVolumeFrontService(database, gateway, levelMethods.Calendar);

    // The detection defaults, not the detection options: three of the seven fields are overridden per call, and
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

    /// <summary>The level method <c>get_key_levels</c> detects with when the caller does not name one.</summary>
    private const string DefaultLevelMethodName = "swing";

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
    /// <param name="pivotLookback">How many bars to its left a pivot must dominate, or null for the configured one.</param>
    /// <param name="pivotRightLookback">How many bars to its right a pivot must dominate, or null for the configured one.</param>
    /// <param name="methods">
    /// The level methods to run, comma-separated, or null for <c>swing</c>. Unknown names are an error
    /// listing the known ones.
    /// </param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The levels, ordered by price, plus the confluence score over the requested methods.</returns>
    [McpServerTool(ReadOnly = true, Idempotent = true, Title = "Get key levels")]
    [Description(
        "Detects support and resistance as ZONES rather than lines, sized in ATR multiples so a zone is "
        + "comparably wide across instruments. Significance is prominence in ATR multiples, so a 2.0 on ES "
        + "and a 2.0 on NQ mean the same thing. A zone's support/resistance label is assigned relative to the "
        + "CURRENT price, not to how it formed — a broken resistance is today's support. Detection is "
        + "confined to the contract in front: if the lookback spans a quarterly roll, `detectedOverBars` is "
        + "smaller than the lookback asked for, because a level from the expiring contract sits at a price "
        + "the current one has never traded. Read `detectedOverBars` — fewer bars behind a level is less "
        + "weight for it. Overlapping zones MERGE whichever side of price they formed on, so one reported "
        + "zone can be a support and a resistance that ran into each other; `touchCount` is how many pivots "
        + "went into it. `pivotSource`, `pivotLookback` and `pivotRightLookback` tune the detection for one "
        + "call; OMIT them and this server's configured defaults apply. They carry no default of their own, "
        + "because the default is an operator setting rather than a constant — omitting one asks for the "
        + "configured value, it does not name a particular one. Zone width, the significance floor and the "
        + "two caps are operator settings only, so every level this server reports is sized, filtered and "
        + "capped alike and two of them can be compared. Each method returns at most `detection.maxLevels` "
        + "levels, the most significant ones; `methods[i].levels.length == detection.maxLevels` is the "
        + "per-method signal that that method was cut, and `capped` is true when any requested method "
        + "stopped there. The top-level `levels` array is the union, ordered by price — its length is "
        + "not a completeness signal. Levels below a method's cap are absent rather than folded into "
        + "the ones you can see. "
        + "The response reports the detection it actually ran under as `detection`, so an empty `levels` can "
        + "be told from a market with no structure — read it with `detectedOverBars`. "
        + "`methods` selects which detectors run — `swing`, `session`, `pivot-classic`, `pivot-fibonacci`, "
        + "`pivot-camarilla`, `pivot-woodie`, `pivot-demark`, `volume-poc`, `volume-vah`, `volume-val`, "
        + "`volume-traded` — comma-separated; Omit for swing. The "
        + "response names each method's zones and a family-aware confluence score, with the tolerance "
        + "it was computed against. Methods that share a family share one budget. A requested method "
        + "that contributed nothing is named, with why.")]
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
            "How many bars to its LEFT a pivot must dominate; larger means fewer, more structural levels. "
            + "Omit to use this server's configured lookback. The window is asymmetric: detection needs "
            + "this + `pivotRightLookback` + 1 bars to find even one pivot — and the window it runs over "
            + "is whatever the store holds, cut back to the contract in front, which can be far less than "
            + "`lookbackBars` asked for. When that happens the answer is an EMPTY level set, not an error: "
            + "compare `detection.pivotLookback` against `detectedOverBars` to tell that from a market with "
            + "no structure.")]
        int? pivotLookback = null,
        [Description(
            "How many bars to its RIGHT a pivot must dominate — the confirmation window. Omit to use this "
            + "server's configured value. It is shorter than the left one by default because the two sides "
            + "answer different questions: the left asks how much history the level stood clear of, the "
            + "right only has to show the extreme held. It is also the lag: the last this-many bars of the "
            + "series can never produce a pivot, so the newest structure is always missing from the answer. "
            +             "There is no zero — a pivot judged only by the bars before it repaints as soon as the next "
            + "one arrives.")]
        int? pivotRightLookback = null,
        [Description(
            "Which level methods to run, comma-separated: swing, session, pivot-classic, pivot-fibonacci, "
            + "pivot-camarilla, pivot-woodie, pivot-demark, volume-poc, volume-vah, volume-val, "
            + "volume-traded. Omit for swing. An unknown name is an error "
            + "listing the known ones — never an empty level set. Session and every pivot-* method refuse "
            + "when a bucket of this resolutionMinutes overhangs a session close. Volume-* methods consume "
            + "the tape-derived profile for the window; they never spread a bar's volume across its range.")]
        string? methods = null,
        CancellationToken cancellationToken = default)
    {
        InstrumentId instrument = Resolve(symbol);
        ToolGuards.ValidateResolution(resolutionMinutes);
        int wanted = _guards.ValidateCount(lookbackBars);

        // Before the read, not after it. Every check below is a fact about the REQUEST, and a store with no
        // bars returns early -- so validating after the read is how an Unknown source arriving from
        // configuration would be answered with an empty level set instead of a refusal.
        KeyLevelOptions detection = ResolveDetection(pivotSource, pivotLookback, pivotRightLookback)
            with
        {
            ResolutionMinutes = resolutionMinutes,
        };
        IReadOnlyList<ILevelMethod> requested = ResolveMethods(methods);

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
            return AssembleLevelSet(
                [],
                new ToolPayloads.ContractCoverage(ToolPayloads.ContractSpan.Unknown, []),
                0,
                detection,
                requested,
                overhang: false,
                scale: []);
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

        bool overhang = SessionBucketGuard.OverhangsClose(
            resolutionMinutes, _levelMethods.Calendar, detectable);

        VolumeProfile? profile = null;
        string? volumeAbsent = null;
        if (detectable.Count > 0 && requested.Any(static m => m.Family == VolumeLevels.FamilyName))
        {
            try
            {
                DateTimeOffset windowStart = detectable[0].OpenTime;
                DateTimeOffset windowEnd = detectable[^1].OpenTime.AddMinutes(resolutionMinutes);
                VolumeProfileRead read = await new VolumeProfileService(_database)
                    .ReadAsync(
                        _gateway.VenueId,
                        instrument,
                        resolutionMinutes,
                        windowStart,
                        windowEnd,
                        cancellationToken)
                    .ConfigureAwait(false);

                // Narrowed is gh#221's confinement signal. Binding the confined profile would
                // report a POC of the listened subset as a POC of the key-levels window —
                // detectedOverBars still names the full bar series.
                if (read.Window.Narrowed)
                {
                    volumeAbsent = VolumeLevels.NarrowedReason;
                }
                else
                {
                    profile = read.Profile;
                }
            }
            catch (InvalidOperationException)
            {
                volumeAbsent = VolumeLevels.NoTapeReason;
            }
            catch (ArgumentException)
            {
                volumeAbsent = VolumeLevels.NoTapeReason;
            }
        }

        using VolumeProfileScope? bind = profile is { } bound ? new VolumeProfileScope(bound) : null;
        return AssembleLevelSet(
            detectable, coverage, detectable.Count, detection, requested, overhang, scale, volumeAbsent);
    }

    /// <summary>
    /// Runs the requested methods, scores their agreement, and builds the payload.
    /// </summary>
    private ToolPayloads.LevelSet AssembleLevelSet(
        IReadOnlyList<Bar> detectable,
        ToolPayloads.ContractCoverage coverage,
        int detectedOverBars,
        KeyLevelOptions detection,
        IReadOnlyList<ILevelMethod> requested,
        bool overhang,
        IReadOnlyList<decimal?> scale,
        string? volumeAbsent = null)
    {
        List<ConfluenceMethodInput> inputs = [];
        List<ToolPayloads.LevelInfo> combined = [];
        List<ToolPayloads.LevelMethodResult> methodResults = [];
        int timeframe = detection.ResolutionMinutes;

        foreach (ILevelMethod method in requested)
        {
            decimal weight = _detection.WeightOf(method.Name);
            bool anchored = method.Name == "session" || method.Family == PivotLevels.FamilyName;

            if (detectable.Count == 0)
            {
                inputs.Add(new ConfluenceMethodInput(method.Name, method.Family, [], "no data"));
                methodResults.Add(new ToolPayloads.LevelMethodResult(method.Name, method.Family, weight, [], "no data"));
                continue;
            }

            if (method.Family == VolumeLevels.FamilyName && volumeAbsent is not null)
            {
                inputs.Add(new ConfluenceMethodInput(method.Name, method.Family, [], volumeAbsent));
                methodResults.Add(new ToolPayloads.LevelMethodResult(
                    method.Name, method.Family, weight, [], volumeAbsent));
                continue;
            }

            if (anchored && overhang)
            {
                inputs.Add(new ConfluenceMethodInput(
                    method.Name, method.Family, [], SessionBucketGuard.RefusalReason));
                methodResults.Add(new ToolPayloads.LevelMethodResult(
                    method.Name, method.Family, weight, [], SessionBucketGuard.RefusalReason));
                continue;
            }

            IReadOnlyList<KeyLevelZone> zones = method.Detect(detectable, scale, detection);
            inputs.Add(new ConfluenceMethodInput(method.Name, method.Family, zones));

            List<ToolPayloads.LevelInfo> infos = [];
            foreach (KeyLevelZone zone in zones)
            {
                ToolPayloads.LevelInfo info = new(
                    timeframe, zone.Bottom, zone.Top, zone.Midpoint, zone.Kind, zone.Significance,
                    zone.TouchCount, zone.FormedAtBucket, method.Name, zone.Period);
                infos.Add(info);
                combined.Add(info);
            }

            methodResults.Add(new ToolPayloads.LevelMethodResult(
                method.Name,
                method.Family,
                weight,
                infos,
                zones.Count == 0 ? ConfluenceScoring.NoLevelsReason : null,
                Capped: zones.Count == detection.MaxLevels));
        }

        combined.Sort(static (left, right) =>
        {
            int byPrice = left.Midpoint.CompareTo(right.Midpoint);
            if (byPrice != 0)
            {
                return byPrice;
            }

            int byBottom = left.Bottom.CompareTo(right.Bottom);
            return byBottom != 0
                ? byBottom
                : string.CompareOrdinal(left.Method, right.Method);
        });

        ConfluenceResult scored = ConfluenceScoring.Score(
            inputs, _detection.Weights, detection.ZoneAtrMultiple);

        return new ToolPayloads.LevelSet(
            combined,
            coverage,
            detectedOverBars,
            Reported(detection),
            methodResults,
            new ToolPayloads.ConfluenceScore(
                scored.Score,
                scored.Tolerance,
                [.. scored.Constituents.Select(c =>
                    new ToolPayloads.ConfluenceConstituentInfo(c.Method, c.Family, c.Weight, c.ZoneCount))],
                [.. scored.Absent.Select(a => new ToolPayloads.ConfluenceAbsenceInfo(a.Method, a.Reason))]),
            Capped: methodResults.Exists(static m => m.Capped));
    }

    /// <summary>Reads stored footprint cells for a covered tape window.</summary>
    /// <param name="symbol">The instrument symbol.</param>
    /// <param name="resolutionMinutes">The bar size the cells were projected at.</param>
    /// <param name="fromUtc">The window start, inclusive.</param>
    /// <param name="toUtc">The window end, exclusive.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The cells under the ledger window that was actually covered.</returns>
    [McpServerTool(ReadOnly = true, Idempotent = true, Title = "Get footprint")]
    [Description(
        "Reads buy/sell volume by price by bar from stored footprint cells. The tape only goes forward: "
        + "there is no historical footprint for a period before recording began — not slow, not expensive, "
        + "ABSENT. A window before recording began is refused and names the earliest covered time; an empty "
        + "answer is not a quiet market. The response reports `covered` from TapeCoverage — not the window "
        + "you asked for — and `contracts` with span SingleContract naming which contract was listened to. "
        + "`contracts.segments` use bar-open times from the cells (`firstBucket` / `lastBucket`), not the "
        + "exclusive coverage end — that range stays on `covered`. A roll or listening hole narrows the "
        + "answer to the newest contiguous run and sets `covered.narrowed`. When the live tape is not "
        + "listening for that instrument the tool refuses with a sentence naming the fix — an empty "
        + "answer and an absent tape must not look the same. Top-level fields are always present. "
        + "`front` names the tape volume-front beside the contract Bars would fetch — `used` is "
        + "`tape-volume` or `none`, never a silent prefer of the gateway. `contracts` stays the "
        + "newest listening run; it is not rewritten from `front`. Keys inside `front` are omitted "
        + "when that answer does not exist. "
        + "TapeCoverage is not per-resolution: a covered window with no cells at the asked bar size is "
        + "refused rather than returned as empty `cells` — that quiet-looking shape would hide an "
        + "unprojected resolution. Never truncates: an over-cap window is refused.")]
    public async Task<ToolPayloads.FootprintSeries> GetFootprint(
        [Description("The instrument symbol, e.g. ES.")] string symbol,
        [Description("The bar size in minutes the cells were projected at.")] int resolutionMinutes,
        [Description("Window start, ISO-8601 UTC, inclusive.")] DateTimeOffset fromUtc,
        [Description("Window end, ISO-8601 UTC, exclusive.")] DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        InstrumentId instrument = Resolve(symbol);
        BarRange window = _guards.ValidateWindow(fromUtc, toUtc, resolutionMinutes);
        _tape.For(instrument.Symbol).Require();

        FootprintRead read;
        try
        {
            read = await _volumeProfiles.ReadCellsAsync(
                    _gateway.VenueId,
                    instrument,
                    resolutionMinutes,
                    window.Start,
                    window.End,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            throw new McpException(ex.Message);
        }

        if (read.Cells.Count == 0)
        {
            throw new McpException(await EmptyFootprintRefusalAsync(
                    instrument, resolutionMinutes, read.Window, cancellationToken)
                .ConfigureAwait(false));
        }

        RefuseIfOverCellCap(read.Cells.Count, "footprint cells");

        List<ToolPayloads.FootprintCellPoint> cells =
        [
            .. read.Cells
                .OrderBy(c => c.BucketStart)
                .ThenBy(c => c.Price)
                .Select(c => new ToolPayloads.FootprintCellPoint(
                    c.BucketStart, c.Price, c.BuyVolume, c.SellVolume)),
        ];

        return new ToolPayloads.FootprintSeries(
            instrument.Symbol,
            resolutionMinutes,
            cells,
            new ToolPayloads.CoveredWindow(read.Window.Start, read.Window.End, read.Window.Narrowed),
            ToolPayloads.ToTapeCoverage(read.Window, read.Cells),
            await FrontAsync(instrument, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Aggregates stored footprint cells into a volume profile for a covered tape window.</summary>
    /// <param name="symbol">The instrument symbol.</param>
    /// <param name="resolutionMinutes">The bar size the cells were projected at.</param>
    /// <param name="fromUtc">The window start, inclusive.</param>
    /// <param name="toUtc">The window end, exclusive.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The profile under the ledger window that was actually covered.</returns>
    [McpServerTool(ReadOnly = true, Idempotent = true, Title = "Get volume profile")]
    [Description(
        "Aggregates stored footprint cells into volume by price, the point of control, and the 70% value "
        + "area. The tape only goes forward: there is no historical footprint for a period before recording "
        + "began — not slow, not expensive, ABSENT. A window before recording began is refused and names "
        + "the earliest covered time; an empty profile is not a quiet market. The response reports "
        + "`covered` from TapeCoverage — not the window you asked for — and `contracts` with span "
        + "SingleContract naming which contract was listened to. `contracts.segments` use bar-open times "
        + "from the cells, not the exclusive coverage end. A roll or listening hole narrows the answer to "
        + "the newest contiguous run and sets `covered.narrowed`. When the live tape is not listening the "
        + "tool refuses with a sentence naming the fix — an empty profile and an absent tape must not look "
        + "the same. Health is that instrument's tape, not another symbol's subscribe. Top-level "
        + "fields are always present. `front` names the tape volume-front beside the contract Bars "
        + "would fetch — `used` is `tape-volume` or `none`, never a silent prefer of the gateway. "
        + "`contracts` stays the newest listening run; it is not rewritten from `front`. Keys inside "
        + "`front` are omitted when that answer does not exist. "
        + "Never truncates: an over-cap window is refused.")]
    public async Task<ToolPayloads.VolumeProfileSeries> GetVolumeProfile(
        [Description("The instrument symbol, e.g. ES.")] string symbol,
        [Description("The bar size in minutes the cells were projected at.")] int resolutionMinutes,
        [Description("Window start, ISO-8601 UTC, inclusive.")] DateTimeOffset fromUtc,
        [Description("Window end, ISO-8601 UTC, exclusive.")] DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        InstrumentId instrument = Resolve(symbol);
        BarRange window = _guards.ValidateWindow(fromUtc, toUtc, resolutionMinutes);
        _tape.For(instrument.Symbol).Require();

        FootprintRead cells;
        try
        {
            cells = await _volumeProfiles.ReadCellsAsync(
                    _gateway.VenueId,
                    instrument,
                    resolutionMinutes,
                    window.Start,
                    window.End,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            throw new McpException(ex.Message);
        }

        RefuseIfOverCellCap(cells.Cells.Count, "footprint cells");

        VolumeProfile profile;
        try
        {
            profile = VolumeProfileAggregator.From(cells.Cells);
        }
        catch (ArgumentException ex)
        {
            throw new McpException(ex.Message);
        }

        return new ToolPayloads.VolumeProfileSeries(
            instrument.Symbol,
            resolutionMinutes,
            [.. profile.ByPrice.Select(level => new ToolPayloads.VolumeAtPricePoint(level.Price, level.Volume))],
            profile.PointOfControl,
            profile.ValueAreaLow,
            profile.ValueAreaHigh,
            profile.ValueAreaVolume,
            profile.TotalVolume,
            new ToolPayloads.CoveredWindow(cells.Window.Start, cells.Window.End, cells.Window.Narrowed),
            ToolPayloads.ToTapeCoverage(cells.Window, cells.Cells),
            await FrontAsync(instrument, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Reports the most recent tape changeover a symbol's stored prints can prove.</summary>
    /// <param name="symbol">The instrument symbol.</param>
    /// <param name="asOfUtc">The instant to evaluate, or null for now.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The changeover, both front-month answers, and the bar-side seam around the flip.</returns>
    [McpServerTool(ReadOnly = true, Idempotent = true, Title = "Get contract roll")]
    [Description(
        "Reports the most recent contract-roll changeover the stored tape can prove for a symbol, "
        + "and the tape front at asOfUtc. There is no historical tape before recording "
        + "began — a changeover from before that is ABSENT, not guessed. `front` is the same object "
        + "get_footprint returns: `used` is `tape-volume` or `none`, never a silent prefer of the "
        + "gateway. Keys inside `front` — including `changeover`, `gatewayContractId` and `agree` — "
        + "are omitted when that answer does not exist. The gateway pick is live only; a historical "
        + "asOfUtc omits `gatewayContractId` and `agree` rather than dating today's pick as if it "
        + "were as-of. `contracts` is the bar-side seam around the changeover (`span` / "
        + "segments) across every stored bar size; it is omitted when there is no "
        + "changeover to place a window around. `SingleContract` means no held series in "
        + "that window crosses the roll — not that the finest one does not. `span` Unknown "
        + "means provenance was never recorded, not that there was no roll. "
        + "asOfUtc is bounded like get_market_session's atUtc.")]
    public async Task<ToolPayloads.ContractRollInfo> GetContractRoll(
        [Description("The instrument symbol, e.g. ES.")] string symbol,
        [Description("The instant to evaluate, ISO-8601 UTC. Defaults to now.")] DateTimeOffset? asOfUtc = null,
        CancellationToken cancellationToken = default)
    {
        InstrumentId instrument = Resolve(symbol);
        DateTimeOffset now = _clock.GetUtcNow().ToUniversalTime();
        DateTimeOffset at =
            ToolGuards.ValidateInstant((asOfUtc ?? now).ToUniversalTime(), "asOfUtc");
        bool resolveGateway = asOfUtc is null || at == now;

        ToolPayloads.VolumeFrontInfo front =
            await FrontAsync(instrument, cancellationToken, at, resolveGateway).ConfigureAwait(false);

        ToolPayloads.ContractCoverage? contracts = front.Changeover is { } flip
            ? await BarSeamAroundAsync(instrument, flip, at, cancellationToken).ConfigureAwait(false)
            : null;

        return new ToolPayloads.ContractRollInfo(instrument.Symbol, at, front, contracts);
    }

    /// <summary>
    /// Reads both answers for the front month. Called only after the tape-derived answer is
    /// already going to be returned — a no-tape refusal is not rescued by this object.
    /// </summary>
    private async Task<ToolPayloads.VolumeFrontInfo> FrontAsync(
        InstrumentId instrument,
        CancellationToken cancellationToken,
        DateTimeOffset? asOfUtc = null,
        bool resolveGateway = true)
    {
        TapeVolumeFrontRead read;
        try
        {
            read = await _volumeFront
                .ReadAsync(instrument, cancellationToken, asOfUtc, resolveGateway)
                .ConfigureAwait(false);
        }
        catch (VenueException ex)
        {
            throw new McpException("The venue could not answer: " + ex.Message);
        }

        VolumeFrontChangeover? flip = read.Tape.Changeover;
        return new ToolPayloads.VolumeFrontInfo(
            read.Used,
            resolveGateway ? read.Agree : null,
            read.Tape.ActiveContractId,
            read.Tape.ActiveSessionDate,
            resolveGateway ? read.GatewaySelectedContractId : null,
            flip is null
                ? null
                : new ToolPayloads.VolumeFrontChangeoverInfo(
                    flip.SessionDate,
                    flip.FlippedAtUtc,
                    flip.FromContractId,
                    flip.ToContractId));
    }

    /// <summary>
    /// Bar provenance in a short window around a tape changeover — stored bars only, never a fetch.
    /// </summary>
    private async Task<ToolPayloads.ContractCoverage> BarSeamAroundAsync(
        InstrumentId instrument,
        ToolPayloads.VolumeFrontChangeoverInfo flip,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken)
    {
        DateTimeOffset anchor = flip.FlippedAtUtc
            ?? MarketClock.FromMarket(flip.SessionDate, _levelMethods.Calendar.SessionClose)
                .ToUniversalTime();

        DateTimeOffset start = anchor - TimeSpan.FromDays(2);
        DateTimeOffset end = anchor + TimeSpan.FromDays(2);
        if (end > asOfUtc)
        {
            end = asOfUtc;
        }

        if (end <= start)
        {
            return new ToolPayloads.ContractCoverage(ToolPayloads.ContractSpan.Unknown, []);
        }

        List<int> resolutions = await _database.Bars
            .AsNoTracking()
            .Where(bar => bar.Venue == _gateway.VenueId
                && bar.Instrument == instrument.Symbol
                && bar.BucketStart >= start
                && bar.BucketStart < end)
            .Select(bar => bar.ResolutionMinutes)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (resolutions.Count == 0)
        {
            return new ToolPayloads.ContractCoverage(ToolPayloads.ContractSpan.Unknown, []);
        }

        // Finest-only under-reports: a 1-minute series that starts after the flip is
        // SingleContract while a coarser series already held across it is SpansRoll.
        // Inspect every stored size. SpansRoll wins — that is the question this tool
        // answers. Unknown beats SingleContract: a missing provenance is not a confident
        // negative. Coarsest first so a spanning series keeps its own segments.
        ToolPayloads.ContractCoverage? spanning = null;
        ToolPayloads.ContractCoverage? unknown = null;
        ToolPayloads.ContractCoverage? single = null;
        BarRange window = new(start, end);

        foreach (int resolution in resolutions.OrderByDescending(static r => r))
        {
            ToolPayloads.ContractCoverage coverage = await CoverageAsync(
                    instrument,
                    resolution,
                    window,
                    cancellationToken)
                .ConfigureAwait(false);

            switch (coverage.Span)
            {
                case ToolPayloads.ContractSpan.SpansRoll:
                    spanning ??= coverage;
                    break;
                case ToolPayloads.ContractSpan.Unknown:
                    unknown ??= coverage;
                    break;
                case ToolPayloads.ContractSpan.SingleContract:
                    single ??= coverage;
                    break;
            }
        }

        return spanning ?? unknown ?? single
            ?? new ToolPayloads.ContractCoverage(ToolPayloads.ContractSpan.Unknown, []);
    }

    /// <summary>
    /// Refuses a covered window with no cells at the asked bar size — TapeCoverage is not per-resolution,
    /// so an empty list would look like a quiet market when the series was simply never projected.
    /// </summary>
    private async Task<string> EmptyFootprintRefusalAsync(
        InstrumentId instrument,
        int resolutionMinutes,
        CoveredTapeWindow covered,
        CancellationToken cancellationToken)
    {
        // Broaden slightly so a cell whose bucket grazes the covered window is still visible — same
        // loadFrom margin ReadCellsAsync uses. Distinct resolutions other than the ask name the bug.
        DateTimeOffset loadFrom = covered.Start.AddMinutes(-Math.Max(resolutionMinutes, 1));

        List<int> otherResolutions = await _database.FootprintCells
            .AsNoTracking()
            .Where(c => c.Venue == _gateway.VenueId
                && c.Instrument == instrument.Symbol
                && c.ResolutionMinutes != resolutionMinutes
                && c.BucketStart < covered.End
                && c.BucketStart > loadFrom)
            .Select(c => c.ResolutionMinutes)
            .Distinct()
            .OrderBy(r => r)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        string resolution = resolutionMinutes.ToString(
            System.Globalization.CultureInfo.InvariantCulture);

        if (otherResolutions.Count > 0)
        {
            string known = string.Join(
                ", ",
                otherResolutions.Select(static r =>
                    r.ToString(System.Globalization.CultureInfo.InvariantCulture) + "m"));

            return "No footprint cells at " + resolution
                + "-minute resolution for the covered window. TapeCoverage is not per-resolution — "
                + "listening succeeded, and cells exist at other bar sizes (" + known
                + "). Ask for a resolution that has been projected. An empty cell list would look like a "
                + "quiet market.";
        }

        return "No footprint cells at " + resolution
            + "-minute resolution for the covered window. An empty cell list would look like a quiet "
            + "market.";
    }

    /// <summary>
    /// Refuses a tape-derived answer that would exceed the row cap rather than truncating it.
    /// </summary>
    private void RefuseIfOverCellCap(int count, string what)
    {
        if (count > _guards.MaxRows)
        {
            throw new McpException(
                "That covered window holds "
                + count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " " + what + ", over this server's cap of "
                + _guards.MaxRows.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ". Narrow the window or ask for a coarser resolution. The read is refused rather than "
                + "truncated, because a shortened answer is indistinguishable from a complete one.");
        }
    }

    /// <summary>
    /// Resolves the requested method names, defaulting to <see cref="DefaultLevelMethodName"/>.
    /// </summary>
    /// <exception cref="McpException">A name is not in the vocabulary.</exception>
    private IReadOnlyList<ILevelMethod> ResolveMethods(string? methods)
    {
        IEnumerable<string> names = string.IsNullOrWhiteSpace(methods)
            ? [DefaultLevelMethodName]
            : methods.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(static name => name.ToLowerInvariant())
                .Distinct(StringComparer.Ordinal);

        List<ILevelMethod> resolved = [];
        foreach (string name in names)
        {
            try
            {
                resolved.Add(_levelMethods.Resolve(name));
            }
            catch (KeyNotFoundException ex)
            {
                throw new McpException(ex.Message);
            }
        }

        return resolved.Count == 0 ? [_levelMethods.Resolve(DefaultLevelMethodName)] : resolved;
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
    /// <param name="pivotLookback">The requested left lookback, or null to take the configured one.</param>
    /// <param name="pivotRightLookback">The requested right lookback, or null to take the configured one.</param>
    /// <returns>The options detection will run under.</returns>
    /// <exception cref="McpException">
    /// The named source is not in the vocabulary, the configured source is not either, or either lookback is
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
    private KeyLevelOptions ResolveDetection(string? pivotSource, int? pivotLookback, int? pivotRightLookback)
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
        int rightLookback = pivotRightLookback ?? defaults.RightLookback;

        if (lookback < 1)
        {
            throw new McpException(
                "pivotLookback must be at least 1; got "
                + lookback.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ". A pivot dominates that many bars to its left, so there is no such thing as a pivot "
                + "that dominates none.");
        }

        return rightLookback < 1
            ? throw new McpException(
                "pivotRightLookback must be at least 1; got "
                + rightLookback.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ". The right window is the confirmation, so a pivot with none is a guess about the bars "
                + "that have not arrived and it repaints as soon as they do.")
            : defaults with { Source = source, Lookback = lookback, RightLookback = rightLookback };
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
        new(
            options.Source,
            options.Lookback,
            options.ZoneAtrMultiple,
            options.MinSignificance,
            options.RightLookback,
            options.MaxZoneWidthPercent,
            options.MaxLevels);

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
