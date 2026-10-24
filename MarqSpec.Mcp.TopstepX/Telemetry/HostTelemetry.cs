using System.Diagnostics;
using System.Diagnostics.Metrics;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.Extensions.Logging;

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

    /// <summary>One recorder-health report (gh#756, R-5.14).</summary>
    public const string RecorderHealthReadsInstrument = "mcp.recorder.health.reads";

    /// <summary>A session conflict that stopped the recorder (gh#757).</summary>
    public const string TapeSessionConflictsInstrument = "mcp.tape.session_conflicts";

    /// <summary>A renewal or login that was refused (gh#757).</summary>
    public const string SessionRenewalRefusalsInstrument = "mcp.session.renewal_refusals";

    /// <summary>Indicator values a projection pass wrote.</summary>
    public const string IndicatorProjectionsInstrument = "mcp.indicator.projections";

    /// <summary>One gated execution write, refused or transmitted (ADR-0024, gh#710).</summary>
    public const string ExecutionWritesInstrument = "mcp.execution.writes";

    /// <summary>
    /// A bookkeeping failure after a trading attempt: the daily-cap unlock or the execution-write telemetry
    /// itself. Untagged. It must not decide the trading outcome (gh#750).
    /// </summary>
    public const string BookkeepingFaultsInstrument = "mcp.execution.bookkeeping.faults";

    /// <summary>Logged when bookkeeping fails. Constant, so a connection string cannot land in the line.</summary>
    private const string BookkeepingFaultMessage = "Trading bookkeeping failed.";

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

    /// <summary>Why an execution write stopped before or at the venue. Values: <see cref="ExecutionWriteRefusal"/>.</summary>
    public const string RefusalTag = "refusal";

    /// <summary>
    /// Whether this execution-write measurement is a replay of a stored transmit. Values:
    /// <see cref="ExecutionWriteReplay"/>. A sum of new outcomes filters <see cref="ExecutionWriteReplay.False"/>.
    /// </summary>
    public const string ReplayTag = "replay";

    /// <summary>Why a range was outstanding. Values: <see cref="GapReason"/>.</summary>
    public const string ReasonTag = "reason";

    /// <summary>Which way the hub moved. Values: <see cref="TapeTransition"/>.</summary>
    public const string TransitionTag = "transition";

    /// <summary>What happened to a tape claim. Values: <see cref="TapeLeaseChange"/>.</summary>
    public const string ChangeTag = "change";

    /// <summary>Where a session conflict was seen. Values: <see cref="SessionConflictSource"/>.</summary>
    public const string SourceTag = "source";

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
    private readonly Counter<long> _recorderHealthReads;
    private readonly Counter<long> _tapeSessionConflicts;
    private readonly Counter<long> _sessionRenewalRefusals;
    private readonly Counter<long> _indicatorProjections;
    private readonly Counter<long> _executionWrites;
    private readonly Counter<long> _bookkeepingFaults;
    private readonly ILogger<HostTelemetry>? _logger;

    /// <summary>Creates the meter and the activity source.</summary>
    /// <param name="logger">Optional. A bookkeeping failure logs one constant line through it.</param>
    /// <remarks>
    /// <b>The meter carries this instance as its <c>Scope</c>.</b> That is what lets a test collect from the
    /// instance it built rather than from every meter in the process that happens to share the name — the
    /// unit tier constructs one of these per test, and without a scope <c>MetricCollector</c> would match
    /// them all and read a number another test produced.
    /// </remarks>
    public HostTelemetry(ILogger<HostTelemetry>? logger = null)
    {
        _logger = logger;
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

        _recorderHealthReads = _meter.CreateCounter<long>(
            RecorderHealthReadsInstrument,
            unit: "{read}",
            description: "Recorder-health reports, by whether the recorder was ok, degraded or unavailable.");

        _tapeSessionConflicts = _meter.CreateCounter<long>(
            TapeSessionConflictsInstrument,
            unit: "{conflict}",
            description: "Session conflicts that stopped the tape recorder, by where the signal was seen.");

        _sessionRenewalRefusals = _meter.CreateCounter<long>(
            SessionRenewalRefusalsInstrument,
            unit: "{refusal}",
            description: "Held-session renewals and logins that were refused, by why.");

        _indicatorProjections = _meter.CreateCounter<long>(
            IndicatorProjectionsInstrument,
            unit: "{value}",
            description: "Indicator values a projection pass wrote, by indicator.");

        _executionWrites = _meter.CreateCounter<long>(
            ExecutionWritesInstrument,
            unit: "{write}",
            description: "Gated execution writes, by operation and by refusal reason.");

        _bookkeepingFaults = _meter.CreateCounter<long>(
            BookkeepingFaultsInstrument,
            unit: "{fault}",
            description: "Daily-cap unlock or execution-write telemetry failures. Untagged.");
    }

    /// <summary>
    /// Counts one bookkeeping failure, then logs a constant line. The counter is incremented first so a
    /// throwing logger still leaves the measurement. Never throws: a listener on this counter must not
    /// replace the trading outcome.
    /// </summary>
    public void RecordBookkeepingFault()
    {
        try
        {
            _bookkeepingFaults.Add(1);
            _logger?.LogError(BookkeepingFaultMessage);
        }
        catch (Exception)
        {
            // The count is the signal. A listener that throws while it is recorded must not escape.
        }
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

    /// <summary>Opens a span for one gated execution write and counts it when the scope closes.</summary>
    /// <param name="operation">Which write, from <see cref="ExecutionOperation"/>.</param>
    /// <returns>A scope the caller disposes when the write has settled, however it settled.</returns>
    public ExecutionWriteScope ExecutionWrite(string operation) => new(this, operation);

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

    /// <summary>
    /// Records one session conflict that stopped the recorder, as a counter and a span.
    /// </summary>
    /// <param name="source">Where it was seen, from <see cref="SessionConflictSource"/>.</param>
    /// <returns>The span. The caller disposes it. Null when nothing is listening.</returns>
    /// <remarks>
    /// <b>The source only, never the vendor's sentence.</b> A hub close message is free text. Putting it on
    /// the span would be a new series per distinct sentence, and a place a credential can land. The log line
    /// in the recorder names the reason in this server's own words.
    /// </remarks>
    public Activity? SessionConflict(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        Activity? span = Activities.StartActivity("tape.session_conflict", ActivityKind.Internal);
        span?.SetTag(SourceTag, source);

        _tapeSessionConflicts.Add(1, new KeyValuePair<string, object?>(SourceTag, source));
        return span;
    }

    /// <summary>
    /// Records one renewal or login refusal, as a counter and a span.
    /// </summary>
    /// <param name="reason">Why it was refused, from <see cref="SessionRenewalRefusal"/>.</param>
    /// <returns>The span. The caller disposes it. Null when nothing is listening.</returns>
    /// <remarks>
    /// <b>The reason only.</b> A validate body, a bearer, and a vendor sentence are not tags. The log line
    /// names the outcome in this server's own words.
    /// </remarks>
    public Activity? SessionRenewalRefusal(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        Activity? span = Activities.StartActivity("session.renewal_refusal", ActivityKind.Internal);
        span?.SetTag(ReasonTag, reason);

        _sessionRenewalRefusals.Add(1, new KeyValuePair<string, object?>(ReasonTag, reason));
        return span;
    }

    /// <summary>Counts one tape-claim hand-off.</summary>
    /// <param name="symbol">The venue-neutral instrument symbol the claim is over.</param>
    /// <param name="change">What happened, from <see cref="TapeLeaseChange"/>.</param>
    public void TapeLeaseChanged(string symbol, string change) =>
        _tapeLeaseChanges.Add(
            1,
            new KeyValuePair<string, object?>(SymbolTag, symbol),
            new KeyValuePair<string, object?>(ChangeTag, change));

    /// <summary>Counts one recorder-health report.</summary>
    /// <param name="outcome">What the report found, from <see cref="RecorderHealthOutcome"/>.</param>
    /// <remarks>
    /// Once per report that was actually built. A cached reply is not a second report, and a build the caller
    /// cancelled is not a report. Cancellation records
    /// nothing: a poll that gave up is not a statement about the recorder, and it is not a
    /// <see cref="RecorderHealthOutcome.Timeout"/>. Not tagged by symbol or contract — one hub, one number,
    /// or the series splits by however many instruments the deployment happens to list.
    /// </remarks>
    public void RecorderHealthRead(string outcome) =>
        _recorderHealthReads.Add(1, new KeyValuePair<string, object?>(OutcomeTag, outcome));

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

    /// <summary>One gated execution write, from entry to refusal or transmit.</summary>
    public sealed class ExecutionWriteScope : IDisposable
    {
        private readonly HostTelemetry _telemetry;
        private readonly string _operation;
        private readonly Activity? _span;
        private string? _refusal;
        private string _replay = ExecutionWriteReplay.False;
        private bool _closed;

        internal ExecutionWriteScope(HostTelemetry telemetry, string operation)
        {
            _telemetry = telemetry;
            _operation = operation;

            _span = telemetry.Activities.StartActivity("execution." + operation, ActivityKind.Internal);
            _span?.SetTag(OperationTag, operation);
        }

        /// <summary>Sets the outcome tag recorded when the scope closes.</summary>
        /// <param name="outcomeTag">A stable code from <see cref="ExecutionWriteRefusal"/>.</param>
        public void Outcome(string outcomeTag) => _refusal = outcomeTag;

        /// <summary>
        /// Marks this measurement as a replay of a stored transmit. A dashboard that counts new outcomes
        /// excludes <see cref="ExecutionWriteReplay.True"/>.
        /// </summary>
        public void MarkReplay() => _replay = ExecutionWriteReplay.True;

        /// <summary>
        /// True when closing this scope threw. The gate reads it after dispose. Disposal itself does not
        /// throw and does not replace the trading result.
        /// </summary>
        public bool BookkeepingFailed { get; private set; }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_closed)
            {
                return;
            }

            _closed = true;

            try
            {
                string refusal = _refusal ?? ExecutionWriteRefusal.Error;
                _span?.SetTag(RefusalTag, refusal);
                _span?.SetTag(ReplayTag, _replay);

                _telemetry._executionWrites.Add(
                    1,
                    new KeyValuePair<string, object?>(OperationTag, _operation),
                    new KeyValuePair<string, object?>(RefusalTag, refusal),
                    new KeyValuePair<string, object?>(ReplayTag, _replay));

                _span?.Dispose();
            }
            catch (Exception)
            {
                // A synchronous MeterListener or ActivityListener runs on this thread. It must not escape:
                // the trading result is already decided, and a throw here used to become SafeToRetry.
                BookkeepingFailed = true;
                _telemetry.RecordBookkeepingFault();
            }
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

/// <summary>Which venue write a gated execution call was — separate from read vocabulary (gh#710).</summary>
/// <remarks>
/// The five names are defined once, on <see cref="TradingMcpTools"/>. These constants alias that definition
/// so a telemetry tag and a published tool name cannot drift.
/// </remarks>
public static class ExecutionOperation
{
    /// <summary>Placing an order.</summary>
    public const string PlaceOrderOperation = TradingMcpTools.PlaceOrderOperation;

    /// <summary>Modifying an open order.</summary>
    public const string ModifyOrderOperation = TradingMcpTools.ModifyOrderOperation;

    /// <summary>Canceling an order.</summary>
    public const string CancelOrderOperation = TradingMcpTools.CancelOrderOperation;

    /// <summary>Flattening a position.</summary>
    public const string ClosePositionOperation = TradingMcpTools.ClosePositionOperation;

    /// <summary>Partially closing a position.</summary>
    public const string PartialClosePositionOperation = TradingMcpTools.PartialClosePositionOperation;

    /// <summary>Every value.</summary>
    public static IReadOnlyList<string> All { get; } =
        [
            PlaceOrderOperation,
            ModifyOrderOperation,
            CancelOrderOperation,
            ClosePositionOperation,
            PartialClosePositionOperation,
        ];
}

/// <summary>Why an execution write stopped — the <c>refusal</c> tag's closed vocabulary (gh#710).</summary>
public static class ExecutionWriteRefusal
{
    /// <summary>The gate permitted the write and the adapter reached the venue.</summary>
    public const string Transmitted = "transmitted";

    /// <summary>Master enable is off (<c>Trading__Enabled=false</c>).</summary>
    public const string TradingDisabled = "trading_disabled";

    /// <summary>The kill switch is engaged.</summary>
    public const string KillSwitch = "kill_switch";

    /// <summary>The account id is not on the optional allowlist.</summary>
    public const string AccountNotAllowlisted = "account_not_allowlisted";

    /// <summary>The symbol is on the denylist.</summary>
    public const string SymbolDenied = "symbol_denied";

    /// <summary>The requested size exceeds the per-order cap.</summary>
    public const string OrderSizeExceedsLimit = "order_size_exceeds_limit";

    /// <summary>The day total would exceed the daily cap.</summary>
    public const string DailyContractLimitExceeded = "daily_contract_limit_exceeded";

    /// <summary>The account id was not returned by a fresh account fetch.</summary>
    public const string AccountNotFound = "account_not_found";

    /// <summary>The parsed stage is not practice.</summary>
    public const string NonPracticeAccount = "non_practice_account";

    /// <summary>The account name matched no known stage family.</summary>
    public const string UnknownAccountStage = "unknown_account_stage";

    /// <summary>The venue marks the account inactive or not tradable.</summary>
    public const string AccountNotActive = "account_not_active";

    /// <summary>The contract id is not a usable venue contract id.</summary>
    public const string MalformedContractId = "malformed_contract_id";

    /// <summary>The contract product is not mapped to a served instrument.</summary>
    public const string ContractProductNotMapped = "contract_product_not_mapped";

    /// <summary>Trading configuration failed validation.</summary>
    public const string TradingConfigurationInvalid = "trading_configuration_invalid";

    /// <summary>The modify target order was not found on a fresh fetch.</summary>
    public const string OrderNotFound = "order_not_found";

    /// <summary>Risk checks passed in dry-run; nothing was transmitted.</summary>
    public const string Preview = "preview";

    /// <summary>The call failed with an unhandled exception before a stable outcome was recorded.</summary>
    public const string Error = "error";

    /// <summary>The venue rejected the write with a definitive client error.</summary>
    public const string VenueRejected = "venue_rejected";

    /// <summary>The transmit outcome could not be classified safely for retry.</summary>
    public const string OutcomeUnknown = "outcome_unknown";

    /// <summary>The caller did not supply a usable idempotency key.</summary>
    public const string IdempotencyKeyRequired = "idempotency_key_required";

    /// <summary>The idempotency key is longer than the audit column. Refused before any audit row.</summary>
    public const string IdempotencyKeyTooLong = "idempotency_key_too_long";

    /// <summary>The idempotency key is already stored for a different request.</summary>
    public const string IdempotencyConflict = "idempotency_conflict";

    /// <summary>Size was not a positive contract count, so no audit row was written.</summary>
    public const string SizeNotPositive = "size_not_positive";

    /// <summary>A limit or stop order was missing the price that type requires.</summary>
    public const string PriceRequired = "price_required";

    /// <summary>A supplied price was zero or negative.</summary>
    public const string PriceNotPositive = "price_not_positive";

    /// <summary>A partial close asked for more contracts than the open position holds.</summary>
    public const string PartialCloseExceedsPosition = "partial_close_exceeds_position";

    /// <summary>Every value.</summary>
    public static IReadOnlyList<string> All { get; } =
        [
            Transmitted,
            TradingDisabled,
            KillSwitch,
            AccountNotAllowlisted,
            SymbolDenied,
            OrderSizeExceedsLimit,
            DailyContractLimitExceeded,
            AccountNotFound,
            NonPracticeAccount,
            UnknownAccountStage,
            AccountNotActive,
            MalformedContractId,
            ContractProductNotMapped,
            TradingConfigurationInvalid,
            OrderNotFound,
            Preview,
            Error,
            VenueRejected,
            OutcomeUnknown,
            IdempotencyKeyRequired,
            IdempotencyKeyTooLong,
            IdempotencyConflict,
            SizeNotPositive,
            PriceRequired,
            PriceNotPositive,
            PartialCloseExceedsPosition,
        ];
}

/// <summary>Whether an execution-write measurement replays a stored transmit — the <c>replay</c> tag.</summary>
public static class ExecutionWriteReplay
{
    /// <summary>This measurement is a replay. It is not a new outcome.</summary>
    public const string True = "true";

    /// <summary>This measurement is the attempt itself.</summary>
    public const string False = "false";

    /// <summary>Every value.</summary>
    public static IReadOnlyList<string> All { get; } = [True, False];
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

/// <summary>
/// What a recorder-health report found — the <c>outcome</c> tag on <c>mcp.recorder.health.reads</c>.
/// </summary>
/// <remarks>
/// The tag key is the one <see cref="HostTelemetry.OutcomeTag"/> already declares. These values are this
/// instrument's vocabulary, not the cache hit/miss/partial set (gh#756).
/// </remarks>
public static class RecorderHealthOutcome
{
    /// <summary>The hub is up, every reported contract is listening, and the elapsed session has no hole.</summary>
    public const string Ok = "ok";

    /// <summary>The report was readable and the recorder is not fully healthy, or the session could not be named.</summary>
    public const string Degraded = "degraded";

    /// <summary>The database did not answer, so the ledger is unknown.</summary>
    public const string Unavailable = "unavailable";

    /// <summary>
    /// The database check exceeded its deadline. The wire still says the store is unavailable. A caller who
    /// cancelled is not this value — that records nothing.
    /// </summary>
    public const string Timeout = "timeout";

    /// <summary>Every value.</summary>
    public static IReadOnlyList<string> All { get; } = [Ok, Degraded, Unavailable, Timeout];
}

/// <summary>Where a session conflict was seen — the <c>source</c> tag's closed vocabulary (gh#757).</summary>
public static class SessionConflictSource
{
    /// <summary>The market hub's close message named another session.</summary>
    public const string Hub = "hub";

    /// <summary>Session validate returned <c>InvalidSession</c> or <c>SessionNotFound</c>.</summary>
    public const string Api = "api";

    /// <summary>Every value.</summary>
    public static IReadOnlyList<string> All { get; } = [Hub, Api];
}

/// <summary>
/// Why a held-session renewal or a <c>loginKey</c> was refused — the <c>reason</c> tag's closed vocabulary
/// for <see cref="HostTelemetry.SessionRenewalRefusalsInstrument"/> (gh#757).
/// </summary>
public static class SessionRenewalRefusal
{
    /// <summary>The stop was already latched, so validate was not posted.</summary>
    public const string Latched = "latched";

    /// <summary>Validate reported <c>ExpiredToken</c>. The bearer can never become valid.</summary>
    public const string Expired = "expired";

    /// <summary>Validate failed while the held token was still fresh. Login stays closed.</summary>
    public const string Outage = "outage";

    /// <summary>Validate failed and the held token is inside its refresh window. One login may run.</summary>
    public const string Exhausted = "exhausted";

    /// <summary>Validate reported another session. Login stays closed.</summary>
    public const string Conflict = "conflict";

    /// <summary>A <c>loginKey</c> was refused because the login gate was closed.</summary>
    public const string Closed = "closed";

    /// <summary>Every value.</summary>
    public static IReadOnlyList<string> All { get; } = [Latched, Expired, Outage, Exhausted, Conflict, Closed];
}
