using System.Diagnostics;
using System.Diagnostics.Metrics;
using MarqSpec.Mcp.TopstepX.MarketData;

namespace MarqSpec.Mcp.TopstepX.Telemetry;

/// <summary>
/// The one <see cref="Meter"/> and the one <see cref="ActivitySource"/> this repository owns — the numbers
/// that say what this server is <i>for</i>, rather than what a framework underneath it happened to see
/// (<see href="../../documentation/adr/0019-otlp-as-the-telemetry-boundary.md">ADR-0019</see>, gh#536).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the stable surface, and that is the whole reason it exists.</b> The MCP SDK's source and meter
/// are named <c>Experimental.ModelContextProtocol</c> and the name says what it is: instrument names and tag
/// keys there may move on a bump, and a dashboard built on them may break. Everything below is named by this
/// repository and changes only when this repository changes it, so a panel that must not break is built here
/// (ADR-0019 decision 6). Renaming an instrument or a tag value is therefore the same class of change as
/// renaming an <c>IIndicator</c>: it orphans every stored series in every backend already scraping it.
/// </para>
/// <para>
/// <b>It lives in the host, and it may never move down.</b> <c>Domain</c> references nothing and may read no
/// clock, store or config singleton, because a projection whose output depends on when — or on whether anyone
/// was listening — stops being replayable (ADR-0006). A <see cref="Meter"/> is a process-wide singleton and
/// would be all three problems at once.
/// </para>
/// <para>
/// <b>Every tag value below comes from a closed vocabulary, or is an instrument symbol or a resolution.</b>
/// A timestamp, a venue contract id or a vendor's free-text message would each turn one time series into an
/// unbounded family of them — which is a cost problem in a backend, and a memory problem in this process,
/// because a <see cref="Counter{T}"/> keeps one accumulator per distinct tag set forever.
/// </para>
/// <para>
/// <b>The gate over that is <c>HostTelemetryTests</c>, and it enumerates nothing.</b> It discovers the
/// instruments off this meter through a <see cref="MeterListener"/> — which is blind to measurement type, so
/// a <see cref="Histogram{T}"/> of <see cref="double"/> is covered the same as a <see cref="Counter{T}"/> of
/// <see cref="long"/> — drives them through the public methods below by reflection, and reads the allowed tag
/// keys off the <c>…Tag</c> constants and the allowed values off the vocabularies themselves. Adding an
/// instrument here therefore cannot skip the gate: either a public method records to it and its tags are
/// checked, or nothing does and the gate fails naming it (gh#559). What the gate decides is which instruments
/// exist, and then — <b>on the paths its fixed inputs reach</b> — which tag keys each writes and any value
/// this class <i>manufactures</i>. A tag written only behind a condition those inputs do not satisfy is
/// outside it, so that qualifier is a limit rather than a formality. What it cannot decide at all is that a
/// value merely <i>forwarded</i> through a parameter below comes from a closed vocabulary, because that is a
/// property of the call sites: for the one tag where those are decidable, <c>VenueCallGuardTests</c> reads
/// <c>ProjectXMarketDataGateway</c>'s compiled body.
/// </para>
/// <para>
/// <b>With no <c>Otel__Endpoint</c> configured nothing subscribes, and these instruments cost nothing.</b>
/// An unlistened <see cref="Counter{T}"/> is a predicate and a return; an unlistened
/// <see cref="ActivitySource"/> returns <see langword="null"/> from <c>StartActivity</c> without allocating.
/// So the instrumentation is unconditional at every call site: there is no "is telemetry on" branch to get
/// wrong, and no configuration under which a counted path and an uncounted path can diverge.
/// </para>
/// </remarks>
public sealed class HostTelemetry : IDisposable
{
    /// <summary>The meter and activity source name — <b>a storage key in every backend</b>.</summary>
    public const string Name = "MarqSpec.Mcp.TopstepX";

    /// <summary>A cache-aside read, tagged with what it cost.</summary>
    public const string CacheReadsInstrument = "mcp.cache.reads";

    /// <summary>One request issued to the venue.</summary>
    public const string VenueCallsInstrument = "mcp.venue.calls";

    /// <summary>How long a venue request took, in seconds.</summary>
    public const string VenueCallDurationInstrument = "mcp.venue.call.duration";

    /// <summary>A range the gap detector found missing and this read asked the venue for.</summary>
    public const string GapFillsInstrument = "mcp.gap.fills";

    /// <summary>A print stored from the market hub.</summary>
    public const string TapeTicksInstrument = "mcp.tape.ticks";

    /// <summary>A market-hub connection transition.</summary>
    public const string TapeReconnectsInstrument = "mcp.tape.reconnects";

    /// <summary>A tape-claim hand-off (ADR-0016).</summary>
    public const string TapeLeaseChangesInstrument = "mcp.tape.lease.changes";

    /// <summary>Indicator values a projection pass wrote.</summary>
    public const string IndicatorProjectionsInstrument = "mcp.indicator.projections";

    /// <summary>Which cache-aside series a read was of. Values: <see cref="CacheSeries"/>.</summary>
    public const string SeriesTag = "series";

    /// <summary>What a cache-aside read cost. Values: <see cref="CacheOutcome"/>.</summary>
    public const string OutcomeTag = "outcome";

    /// <summary>The venue-neutral instrument symbol, already normalised through <c>InstrumentId</c>.</summary>
    public const string SymbolTag = "symbol";

    /// <summary>The bar size in minutes, as an integer.</summary>
    public const string ResolutionTag = "resolution";

    /// <summary>
    /// The configured session name a series is over, e.g. <c>rth</c> — the session flavour's answer to
    /// <see cref="ResolutionTag"/>, and never emitted beside it.
    /// </summary>
    /// <remarks>
    /// <b>A session series has no resolution, and it is not given a sentinel one.</b> Its window is
    /// wall-clock and its minutes move with daylight saving, so any integer put in <see cref="ResolutionTag"/>
    /// would be a lie — and a sentinel that collided with a real resolution would silently merge two series
    /// in the backend. So the two tags are alternatives: a measurement carries exactly one of them, and which
    /// one it carries says which kind of series it was over.
    /// </remarks>
    public const string SessionTag = "session";

    /// <summary>Which venue read this was. Values: <see cref="VenueOperation"/>.</summary>
    public const string OperationTag = "operation";

    /// <summary>Why a range was outstanding. Values: <see cref="GapReason"/>.</summary>
    public const string ReasonTag = "reason";

    /// <summary>Which way the hub moved. Values: <see cref="TapeTransition"/>.</summary>
    public const string TransitionTag = "transition";

    /// <summary>What happened to a tape claim. Values: <see cref="TapeLeaseChange"/>.</summary>
    public const string ChangeTag = "change";

    /// <summary>The indicator's stable lowercase name, from the catalogue.</summary>
    public const string IndicatorTag = "indicator";

    private readonly Meter _meter;
    private readonly Counter<long> _cacheReads;
    private readonly Counter<long> _venueCalls;
    private readonly Histogram<double> _venueCallDuration;
    private readonly Counter<long> _gapFills;
    private readonly Counter<long> _tapeTicks;
    private readonly Counter<long> _tapeReconnects;
    private readonly Counter<long> _tapeLeaseChanges;
    private readonly Counter<long> _indicatorProjections;

    /// <summary>Creates the meter and the activity source.</summary>
    /// <remarks>
    /// <b>The meter carries this instance as its <c>Scope</c>.</b> That is what lets a test collect from the
    /// instance it built rather than from every meter in the process that happens to share the name — the
    /// unit tier constructs one of these per test, and without a scope <c>MetricCollector</c> would match
    /// them all and read a number another test produced.
    /// </remarks>
    public HostTelemetry()
    {
        _meter = new Meter(new MeterOptions(Name) { Scope = this });
        Activities = new ActivitySource(Name);

        _cacheReads = _meter.CreateCounter<long>(
            CacheReadsInstrument,
            unit: "{read}",
            description: "Cache-aside reads, by series and by whether the store already held the answer.");

        _venueCalls = _meter.CreateCounter<long>(
            VenueCallsInstrument,
            unit: "{call}",
            description: "Requests issued to the venue, by operation.");

        _venueCallDuration = _meter.CreateHistogram<double>(
            VenueCallDurationInstrument,
            unit: "s",
            description: "How long a venue request took, by operation.");

        _gapFills = _meter.CreateCounter<long>(
            GapFillsInstrument,
            unit: "{range}",
            description: "Bar ranges the gap detector found outstanding and a read asked the venue for, by reason.");

        _tapeTicks = _meter.CreateCounter<long>(
            TapeTicksInstrument,
            unit: "{print}",
            description: "Prints stored from the market hub, by instrument.");

        _tapeReconnects = _meter.CreateCounter<long>(
            TapeReconnectsInstrument,
            unit: "{transition}",
            description: "Market-hub connection transitions the recorder acted on.");

        _tapeLeaseChanges = _meter.CreateCounter<long>(
            TapeLeaseChangesInstrument,
            unit: "{change}",
            description: "Tape-claim hand-offs, by instrument and by what happened to the claim.");

        _indicatorProjections = _meter.CreateCounter<long>(
            IndicatorProjectionsInstrument,
            unit: "{value}",
            description: "Indicator values a projection pass wrote, by indicator.");
    }

    /// <summary>The spans this repository names. Children of the SDK's <c>tools/call</c> span.</summary>
    public ActivitySource Activities { get; }

    /// <summary>Counts one cache-aside read.</summary>
    /// <param name="series">Which series, from <see cref="CacheSeries"/>.</param>
    /// <param name="symbol">The venue-neutral instrument symbol.</param>
    /// <param name="resolutionMinutes">The bar size in minutes.</param>
    /// <param name="outcome">What it cost, from <see cref="CacheOutcome"/>.</param>
    /// <remarks>
    /// <b>This is the number the cache-aside design is judged on</b> (`R-1.1`, `R-1.3`). Whether a read was
    /// served from the store or turned into a venue round trip is invisible from outside the process, and it
    /// is the single number that says the design is working.
    /// </remarks>
    public void CacheRead(string series, string symbol, int resolutionMinutes, string outcome) =>
        _cacheReads.Add(
            1,
            new KeyValuePair<string, object?>(SeriesTag, series),
            new KeyValuePair<string, object?>(SymbolTag, symbol),
            new KeyValuePair<string, object?>(ResolutionTag, resolutionMinutes),
            new KeyValuePair<string, object?>(OutcomeTag, outcome));

    /// <summary>Counts one cache-aside read of a series named by its key.</summary>
    /// <param name="series">Which cache, from <see cref="CacheSeries"/>.</param>
    /// <param name="symbol">The venue-neutral instrument symbol.</param>
    /// <param name="key">Which series the read was of.</param>
    /// <param name="outcome">What it cost, from <see cref="CacheOutcome"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="key"/> is a kind nothing tags yet.</exception>
    /// <remarks>
    /// <b>A resolution key delegates to the integer overload, and that is the whole point of the shape.</b>
    /// Every panel already built on <c>mcp.cache.reads</c> reads the tags that overload emits, so the
    /// key-taking form must produce them byte for byte rather than merely equivalently — one added or renamed
    /// tag retires every stored series in every backend scraping it. A session key emits
    /// <see cref="SessionTag"/> in <see cref="ResolutionTag"/>'s place; see that field for why not both.
    /// </remarks>
    public void CacheRead(string series, string symbol, SeriesKey key, string outcome)
    {
        ArgumentNullException.ThrowIfNull(key);

        switch (key)
        {
            case SeriesKey.Resolution resolution:
                CacheRead(series, symbol, resolution.Minutes, outcome);
                return;

            case SeriesKey.Session session:
                _cacheReads.Add(
                    1,
                    new KeyValuePair<string, object?>(SeriesTag, series),
                    new KeyValuePair<string, object?>(SymbolTag, symbol),
                    new KeyValuePair<string, object?>(SessionTag, session.Name),
                    new KeyValuePair<string, object?>(OutcomeTag, outcome));
                return;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(key),
                    key.GetType().Name,
                    "A series kind this meter has no dimension for. Decide its tags deliberately: a kind "
                    + "that fell through to another's tags would merge two series in every backend.");
        }
    }

    /// <summary>Opens a span for one venue request and counts it when the scope closes.</summary>
    /// <param name="operation">Which read, from <see cref="VenueOperation"/>.</param>
    /// <returns>A scope the caller disposes when the request has finished, however it finished.</returns>
    /// <remarks>
    /// The count and the duration are recorded on <b>dispose</b> rather than on success, so a call that
    /// throws is still a call that was issued and still took time. A vendor allowance is spent by the
    /// request, not by the answer.
    /// </remarks>
    public VenueCallScope VenueCall(string operation) => new(this, operation);

    /// <summary>Opens a span for one cache-aside read.</summary>
    /// <param name="series">Which series, from <see cref="CacheSeries"/>.</param>
    /// <param name="symbol">The venue-neutral instrument symbol.</param>
    /// <param name="resolutionMinutes">The bar size in minutes.</param>
    /// <returns>
    /// The span, or <see langword="null"/> when nothing is listening — which is the ordinary state under
    /// stdio with no endpoint configured, and costs no allocation.
    /// </returns>
    public Activity? StartCacheRead(string series, string symbol, int resolutionMinutes)
    {
        Activity? span = Activities.StartActivity("cache." + series, ActivityKind.Internal);

        span?.SetTag(SeriesTag, series);
        span?.SetTag(SymbolTag, symbol);
        span?.SetTag(ResolutionTag, resolutionMinutes);

        return span;
    }

    /// <summary>Opens a span for one cache-aside read of a series named by its key.</summary>
    /// <param name="series">Which cache, from <see cref="CacheSeries"/>.</param>
    /// <param name="symbol">The venue-neutral instrument symbol.</param>
    /// <param name="key">Which series the read is of.</param>
    /// <returns>The span, or <see langword="null"/> when nothing is listening.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="key"/> is a kind nothing tags yet.</exception>
    /// <remarks>A resolution key delegates, byte for byte; see <see cref="CacheRead(string, string, SeriesKey, string)"/>.</remarks>
    public Activity? StartCacheRead(string series, string symbol, SeriesKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        switch (key)
        {
            case SeriesKey.Resolution resolution:
                return StartCacheRead(series, symbol, resolution.Minutes);

            case SeriesKey.Session session:
                Activity? span = Activities.StartActivity("cache." + series, ActivityKind.Internal);

                span?.SetTag(SeriesTag, series);
                span?.SetTag(SymbolTag, symbol);
                span?.SetTag(SessionTag, session.Name);

                return span;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(key),
                    key.GetType().Name,
                    "A series kind this activity source has no dimension for. Decide its tags deliberately: "
                    + "a kind that fell through to another's tags would merge two series in every backend.");
        }
    }

    /// <summary>Counts the ranges one read asked the venue for to close a gap.</summary>
    /// <param name="symbol">The venue-neutral instrument symbol.</param>
    /// <param name="resolutionMinutes">The bar size in minutes.</param>
    /// <param name="reason">Why they were outstanding, from <see cref="GapReason"/>.</param>
    /// <param name="ranges">How many ranges. Zero is not recorded.</param>
    /// <remarks>
    /// <b>Asked for, not fetched, and the wording is the fix rather than a hedge.</b> The call site records
    /// this <i>before</i> the venue is reached, so a venue that throws leaves gap fills counted for ranges
    /// nothing came back for — and no <c>mcp.cache.reads</c> at all, because that one is recorded after. An
    /// outage therefore shows gap fills with no matching reads, which is a legible shape rather than a
    /// contradiction, and it is why this counts demand rather than delivery. Moving the record past the
    /// fetch was the alternative and is worse: it would report zero demand during exactly the outage an
    /// operator is trying to size (gh#536 review, finding 5).
    /// </remarks>
    public void GapFilled(string symbol, int resolutionMinutes, string reason, int ranges)
    {
        if (ranges <= 0)
        {
            return;
        }

        _gapFills.Add(
            ranges,
            new KeyValuePair<string, object?>(SymbolTag, symbol),
            new KeyValuePair<string, object?>(ResolutionTag, resolutionMinutes),
            new KeyValuePair<string, object?>(ReasonTag, reason));
    }

    /// <summary>Counts one print stored from the hub.</summary>
    /// <param name="symbol">The venue-neutral instrument symbol the print was attributed to.</param>
    /// <remarks>
    /// <b>The instrument symbol, never the contract id.</b> A contract id rolls quarterly, so tagging with it
    /// would retire one time series and start another every quarter — and the series a chart is drawn from is
    /// the instrument's.
    /// </remarks>
    public void TapeTick(string symbol) =>
        _tapeTicks.Add(1, new KeyValuePair<string, object?>(SymbolTag, symbol));

    /// <summary>Counts one market-hub connection transition.</summary>
    /// <param name="transition">Which way it moved, from <see cref="TapeTransition"/>.</param>
    /// <remarks>
    /// <b>Not tagged by symbol, because one hub carries every instrument.</b> A reconnect is a fact about the
    /// connection, and attributing it to each subscribed symbol would multiply one event into as many as the
    /// deployment happens to be configured for — the same event, counted differently on two deployments.
    /// </remarks>
    public void TapeReconnect(string transition) =>
        _tapeReconnects.Add(1, new KeyValuePair<string, object?>(TransitionTag, transition));

    /// <summary>Counts one tape-claim hand-off.</summary>
    /// <param name="symbol">The venue-neutral instrument symbol the claim is over.</param>
    /// <param name="change">What happened, from <see cref="TapeLeaseChange"/>.</param>
    public void TapeLeaseChanged(string symbol, string change) =>
        _tapeLeaseChanges.Add(
            1,
            new KeyValuePair<string, object?>(SymbolTag, symbol),
            new KeyValuePair<string, object?>(ChangeTag, change));

    /// <summary>Counts the values a projection pass wrote for one indicator.</summary>
    /// <param name="indicator">The indicator's stable lowercase name.</param>
    /// <param name="symbol">The venue-neutral instrument symbol.</param>
    /// <param name="resolutionMinutes">The bar size in minutes.</param>
    /// <param name="values">How many values. Zero is not recorded.</param>
    /// <remarks>
    /// <b>The name only, never the period.</b> An operator can configure additional periods
    /// (<see href="../../documentation/adr/0018-period-selection-among-configured-periods.md">ADR-0018</see>),
    /// so a period tag is a cardinality an operator can grow without touching this repository.
    /// </remarks>
    public void IndicatorProjected(string indicator, string symbol, int resolutionMinutes, int values)
    {
        if (values <= 0)
        {
            return;
        }

        _indicatorProjections.Add(
            values,
            new KeyValuePair<string, object?>(IndicatorTag, indicator),
            new KeyValuePair<string, object?>(SymbolTag, symbol),
            new KeyValuePair<string, object?>(ResolutionTag, resolutionMinutes));
    }

    /// <summary>Counts the values a projection pass wrote for one indicator over a series named by its key.</summary>
    /// <param name="indicator">The indicator's stable lowercase name.</param>
    /// <param name="symbol">The venue-neutral instrument symbol.</param>
    /// <param name="key">Which series the pass was over.</param>
    /// <param name="values">How many values. Zero is not recorded.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="key"/> is a kind nothing tags yet.</exception>
    /// <remarks>A resolution key delegates, byte for byte; see <see cref="CacheRead(string, string, SeriesKey, string)"/>.</remarks>
    public void IndicatorProjected(string indicator, string symbol, SeriesKey key, int values)
    {
        ArgumentNullException.ThrowIfNull(key);

        switch (key)
        {
            case SeriesKey.Resolution resolution:
                IndicatorProjected(indicator, symbol, resolution.Minutes, values);
                return;

            case SeriesKey.Session session:
                if (values <= 0)
                {
                    return;
                }

                _indicatorProjections.Add(
                    values,
                    new KeyValuePair<string, object?>(IndicatorTag, indicator),
                    new KeyValuePair<string, object?>(SymbolTag, symbol),
                    new KeyValuePair<string, object?>(SessionTag, session.Name));
                return;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(key),
                    key.GetType().Name,
                    "A series kind this meter has no dimension for. Decide its tags deliberately: a kind "
                    + "that fell through to another's tags would merge two series in every backend.");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _meter.Dispose();
        Activities.Dispose();
    }

    /// <summary>One venue request, from the moment it is issued to the moment it settles.</summary>
    /// <remarks>
    /// A <c>class</c> rather than a <c>ref struct</c> because the call it wraps is awaited, and a scope that
    /// cannot cross an <c>await</c> would have to be started and stopped by two statements with the vendor
    /// call between them — which is the shape where an early <c>return</c> loses the measurement.
    /// </remarks>
    public sealed class VenueCallScope : IDisposable
    {
        private readonly HostTelemetry _telemetry;
        private readonly string _operation;
        private readonly long _startedAt;
        private readonly Activity? _span;
        private bool _closed;

        internal VenueCallScope(HostTelemetry telemetry, string operation)
        {
            _telemetry = telemetry;
            _operation = operation;
            _startedAt = Stopwatch.GetTimestamp();

            _span = telemetry.Activities.StartActivity("venue." + operation, ActivityKind.Client);
            _span?.SetTag(OperationTag, operation);
        }

        /// <summary>Marks the request failed, without naming why.</summary>
        /// <remarks>
        /// <b>No vendor message reaches the span.</b> It is free text on a channel a language model reads
        /// (ADR-0008) and a place a credential has already come to rest once in the sibling client. The status
        /// says the call failed; <c>VenueException</c> carries the vendor's numeric code to the caller.
        /// </remarks>
        public void Failed() => _span?.SetStatus(ActivityStatusCode.Error);

        /// <inheritdoc />
        public void Dispose()
        {
            if (_closed)
            {
                return;
            }

            _closed = true;

            KeyValuePair<string, object?> operation = new(OperationTag, _operation);

            _telemetry._venueCalls.Add(1, operation);
            _telemetry._venueCallDuration.Record(
                Stopwatch.GetElapsedTime(_startedAt).TotalSeconds,
                operation);

            _span?.Dispose();
        }
    }
}

/// <summary>Which cache-aside series a read was of — the <c>series</c> tag's closed vocabulary.</summary>
public static class CacheSeries
{
    /// <summary>Bars, read through <c>BarCacheService</c>.</summary>
    public const string Bars = "bars";

    /// <summary>Indicator values, read through <c>IndicatorCacheService</c>.</summary>
    public const string Indicators = "indicators";

    /// <summary>Footprint cells, read through <c>FootprintCacheService</c>.</summary>
    public const string Footprint = "footprint";

    /// <summary>Every value, for the vocabulary test and for a dashboard's legend.</summary>
    public static IReadOnlyList<string> All { get; } = [Bars, Indicators, Footprint];
}

/// <summary>What a cache-aside read cost — the <c>outcome</c> tag's closed vocabulary.</summary>
public static class CacheOutcome
{
    /// <summary>Served entirely from the store. Nothing was fetched and nothing was projected.</summary>
    public const string Hit = "hit";

    /// <summary>The store held nothing for this series, so the whole answer had to be produced.</summary>
    public const string Miss = "miss";

    /// <summary>The store held part of the answer and the rest had to be produced.</summary>
    public const string Partial = "partial";

    /// <summary>Every value.</summary>
    public static IReadOnlyList<string> All { get; } = [Hit, Miss, Partial];
}

/// <summary>Which venue read a call was — the <c>operation</c> tag's closed vocabulary.</summary>
/// <remarks>
/// <b>Named here rather than taken from the vendor's method names.</b> A vendor rename would otherwise
/// silently retire a time series, and the string is a storage key in every backend already scraping it.
/// </remarks>
public static class VenueOperation
{
    /// <summary>The fuzzy contract search behind <c>ResolveContractsAsync</c>.</summary>
    public const string ResolveContracts = "resolve_contracts";

    /// <summary>The exact-id lookup behind <c>FindContractAsync</c> (ADR-0020).</summary>
    public const string FindContract = "find_contract";

    /// <summary>One page of historical bars. The paged loop issues one of these per page.</summary>
    public const string GetBars = "get_bars";

    /// <summary>Listing the login's accounts.</summary>
    public const string GetAccounts = "get_accounts";

    /// <summary>Reading open positions.</summary>
    public const string GetPositions = "get_positions";

    /// <summary>Reading working orders, or searching them over a window.</summary>
    public const string GetOrders = "get_orders";

    /// <summary>Searching fills over a window.</summary>
    public const string GetTrades = "get_trades";

    /// <summary>Every value.</summary>
    public static IReadOnlyList<string> All { get; } =
        [ResolveContracts, FindContract, GetBars, GetAccounts, GetPositions, GetOrders, GetTrades];
}

/// <summary>Why a bar range was outstanding — the <c>reason</c> tag's closed vocabulary.</summary>
public static class GapReason
{
    /// <summary>The store held nothing at all in the window. The ordinary cold read.</summary>
    public const string Absent = "absent";

    /// <summary>The store held part of the window and the session calendar expected more.</summary>
    public const string Gap = "gap";

    /// <summary>
    /// The window contains buckets carrying no contract provenance, which are re-asked so the row heals
    /// (gh#402, gh#412).
    /// </summary>
    public const string Unattributed = "unattributed";

    /// <summary>Every value.</summary>
    public static IReadOnlyList<string> All { get; } = [Absent, Gap, Unattributed];
}

/// <summary>Which way the market hub moved — the <c>transition</c> tag's closed vocabulary.</summary>
public static class TapeTransition
{
    /// <summary>The hub came up, and the recorder is about to restore its subscriptions.</summary>
    public const string Connected = "connected";

    /// <summary>The hub went away, and the recorder closed its open coverage ranges.</summary>
    public const string Disconnected = "disconnected";

    /// <summary>Every value.</summary>
    public static IReadOnlyList<string> All { get; } = [Connected, Disconnected];
}

/// <summary>What happened to a tape claim — the <c>change</c> tag's closed vocabulary (ADR-0016).</summary>
public static class TapeLeaseChange
{
    /// <summary>This process took the claim and is recording the instrument.</summary>
    public const string Acquired = "acquired";

    /// <summary>Another recorder holds it, or the store would not say — either way, not recorded here.</summary>
    public const string Refused = "refused";

    /// <summary>This process held it and no longer does: taken over, or lapsed under a store outage.</summary>
    public const string Lost = "lost";

    /// <summary>Every value.</summary>
    public static IReadOnlyList<string> All { get; } = [Acquired, Refused, Lost];
}
