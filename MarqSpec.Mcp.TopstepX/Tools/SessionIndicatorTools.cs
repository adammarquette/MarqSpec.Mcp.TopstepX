using System.ComponentModel;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MarqSpec.Mcp.TopstepX.Tools;

/// <summary>
/// <c>get_session_indicators</c> and <c>get_session_indicator_at</c> — the indicator series over a named
/// session, one value per trade date (gh#501, ADR-0022).
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own tool type rather than a <c>session</c> argument on <c>get_indicators</c></b> (ADR-0017). A
/// session series is not a coarse resolution series: it is keyed on the CME trade date rather than on a
/// bucket grid, its period counts trading days rather than bars, and its vocabulary is narrower — a session
/// that IS one bar has no intra-session volume distribution, so session-anchored <c>vwap</c> is refused by
/// name here where <c>get_indicators</c> serves it.
/// </para>
/// <para>
/// <b>This copies <see cref="IndicatorTools"/>, not <see cref="SessionBarTools"/>, and the difference is the
/// venue.</b> <see cref="SessionBarTools"/> holds a <see cref="SessionBarService"/>, which holds a gateway
/// and can therefore fetch base bars — which is why it translates <c>VenueException</c>. Nothing on this
/// route can: the values are read from the store, the projection behind them derives from stored session
/// bars, and <see cref="IndicatorCacheService"/> holds no gateway at all (ADR-0022 §7). So the gateway is
/// read once for its venue id and is not kept, and there is no catch here for an exception this route cannot
/// raise — a catch nobody can test goes stale in silence.
/// </para>
/// <para>
/// <b>Derived values are never derived HERE.</b> A session bar exists only after <c>get_session_bars</c> or
/// <c>get_latest_session_bars</c> covered its trade date, and these tools do not build one — so a window
/// whose sessions were never read answers with no values, and that is a fact about what has been asked for
/// rather than about the market.
/// </para>
/// </remarks>
/// <param name="resolver">Turns a caller's symbol into an instrument, refusing first if the store is absent.</param>
/// <param name="database">The store these series are read from and projected into.</param>
/// <param name="catalog">The indicators this server computes, and the periods it computes them at.</param>
/// <param name="indicators">The cache-aside projection an indicator read triggers.</param>
/// <param name="sessions">The closed vocabulary of session names.</param>
/// <param name="calendar">The session calendar every trade-date list is walked off.</param>
/// <param name="gateway">Read ONCE for its venue id and not kept — every stored row is keyed by it.</param>
/// <param name="guards">The request-shape checks the whole tool surface shares.</param>
[McpServerToolType]
public sealed class SessionIndicatorTools(
    InstrumentResolver resolver,
    TopstepXDbContext database,
    IndicatorCatalog catalog,
    IndicatorCacheService indicators,
    SessionCatalog sessions,
    BarSessionCalendar calendar,
    IMarketDataGateway gateway,
    ToolGuards guards)
{
    private readonly InstrumentResolver _resolver = resolver;
    private readonly TopstepXDbContext _database = database;
    private readonly IndicatorCatalog _catalog = catalog;
    private readonly IndicatorCacheService _indicators = indicators;
    private readonly SessionCatalog _sessions = sessions;
    private readonly BarSessionCalendar _calendar = calendar;
    // THE VENUE ID, NOT THE GATEWAY, for the reason IndicatorTools states at the same line: every row this
    // tool reads is keyed by the venue, that is the only thing it wants from the client, and keeping the
    // client would put a live venue connection in reach of a type that never calls one.
    // VenueFailureReportingTests walks FIELDS, so a SessionBarService here -- which holds a gateway -- would
    // demand a VenueException catch this route can never raise (gh#501).
    private readonly string _venue = gateway.VenueId;
    private readonly ToolGuards _guards = guards;

    /// <summary>Reads a stored indicator series over a named session.</summary>
    /// <param name="symbol">The instrument symbol.</param>
    /// <param name="session">The session name.</param>
    /// <param name="indicator">The indicator name.</param>
    /// <param name="fromUtc">The window start, inclusive.</param>
    /// <param name="toUtc">The window end, exclusive.</param>
    /// <param name="period">
    /// Which configured period to read, or <see langword="null"/> for the indicator's primary one.
    /// </param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The values, one per trade date that has one.</returns>
    [McpServerTool(ReadOnly = true, Idempotent = true, Title = "Get session indicators")]
    [Description(
        "Reads an indicator series computed over WHOLE SESSIONS — one value per trade date, not per bar. The "
        + "VENDOR IS NEVER CALLED and nothing is derived here: every value is a projection of session bars "
        + "this server already stored. A session bar exists ONLY after get_session_bars or "
        + "get_latest_session_bars covered that trade date, so a window nobody has read sessions for answers "
        + "with no values — read the sessions first, then read this. Where the sessions ARE stored and the "
        + "values are not, the first read that asks for them computes and stores them. period counts "
        + "SESSIONS: an sma at 20 over rth is twenty trading days, so a series needs that many stored "
        + "sessions inside ONE contract run before it measures anything, and a trade date where it could not "
        + "yet measure is ABSENT rather than zero. Known indicators on a session series: atr, rsi, sma, ema, "
        + "macd, macd-signal, macd-histogram, bb-upper, bb-middle, bb-lower, vwap-rolling. `vwap` is NOT "
        + "among them and asking for it is an error naming vwap-rolling: session-anchored vwap weights a "
        + "session's own volume distribution, and a session that is one bar has none. Any other unknown name "
        + "is an error listing these, because a typo that returned no data would read as 'no signal'. Values "
        + "are never smoothed across a contract roll, so expect a run of absent trade dates just after one; "
        + "`contracts.span` says whether the window contains a roll — and Unknown there means no session bar "
        + "could be built at all. The session name is a closed vocabulary: an unknown one is an error listing "
        + "the configured names, never an empty series.")]
    public async Task<ToolPayloads.SessionIndicatorSeries> GetSessionIndicators(
        [Description("The instrument symbol, e.g. ES.")] string symbol,
        [Description(
            "The session name: full, rth, asia or europe as shipped, or any name the operator configured.")]
        string session,
        [Description("The indicator name, e.g. rsi.")] string indicator,
        [Description("Window start, ISO-8601 UTC, inclusive.")] DateTimeOffset fromUtc,
        [Description("Window end, ISO-8601 UTC, exclusive.")] DateTimeOffset toUtc,
        [Description("Which configured period to read, e.g. 200. Omit for the indicator's primary configured "
            + "period. Only periods the operator configured "
            + "are readable: any other is an error listing the configured ones, never an empty series. Not "
            + "accepted for vwap, which is anchored to the session. For macd, macd-signal and macd-histogram this "
            + "is the SLOW length.")] int? period = null,
        CancellationToken cancellationToken = default)
    {
        InstrumentId instrument = _resolver.Resolve(symbol);
        SessionDefinition definition = Definition(session);

        // THE TRADE DATES ARE WALKED OFF THE CALENDAR, exactly as get_session_bars walks them, so the two
        // tools agree about which sessions a window contains rather than each deciding for itself.
        SessionWindowPlan plan = _guards.ValidateSessionWindow(fromUtc, toUtc, definition, _calendar);

        SeriesKey series = new SeriesKey.Session(_venue, instrument.Symbol, definition.Name);

        // BEFORE EnsureProjectedAsync, for the reason get_indicators states: a name or a period this series
        // does not carry is refused without the read ever reaching the store, so a mistyped call cannot make
        // a whole series replay to serve something that was always going to be rejected.
        IIndicator resolved = ResolveIndicator(series, indicator, period);

        // Cache-aside, the way the resolution series already is: anything the catalogue computes for this
        // session that the store does not hold is projected from the session bars already stored, before the
        // read runs. No vendor traffic either way -- the session bars are local, and building one is
        // get_session_bars' job rather than this one's.
        await _indicators.EnsureProjectedAsync(series, cancellationToken).ConfigureAwait(false);

        DateOnly[] tradeDates = [.. plan.TradeDates];

        List<ToolPayloads.SessionIndicatorPoint> values = await Values(instrument, definition, resolved)
            .Join(
                Bars(instrument, definition, tradeDates),
                v => new { v.Venue, v.Instrument, v.Session, Opening = v.BucketStart },
                s => new { s.Venue, s.Instrument, s.Session, Opening = s.OpenUtc },
                (v, s) => new { s.TradeDate, s.OpenUtc, v.Value })
            .OrderBy(row => row.TradeDate)
            .Select(row => new ToolPayloads.SessionIndicatorPoint(row.TradeDate, row.OpenUtc, row.Value))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new ToolPayloads.SessionIndicatorSeries(
            instrument.Symbol,
            definition.Name,
            resolved.Name,
            resolved.Period,
            values,
            await CoverageAsync(instrument, definition, tradeDates, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Reads one session indicator value as of a moment.</summary>
    /// <param name="symbol">The instrument symbol.</param>
    /// <param name="session">The session name.</param>
    /// <param name="indicator">The indicator name.</param>
    /// <param name="asOfUtc">The moment.</param>
    /// <param name="period">
    /// Which configured period to read, or <see langword="null"/> for the indicator's primary one.
    /// </param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The reading, or one carrying nothing at all meaning cannot measure.</returns>
    [McpServerTool(ReadOnly = true, Idempotent = true, Title = "Get session indicator as of")]
    [Description(
        "Reads one whole-session indicator value as of a moment, from the same stored series "
        + "get_session_indicators reads and on the same terms: no vendor call, nothing derived here, and "
        + "values that exist only after get_session_bars or get_latest_session_bars covered the trade date. "
        + "Answers from the last session that had CLOSED at or before that moment — a session still in "
        + "progress is never answered from, because its numbers are not final, and a value from after the "
        + "moment is information the market did not have. Cannot-measure DROPS the `value` KEY instead of "
        + "sending null, so the whole reading arrives as `{}`: test whether the key is THERE, never whether "
        + "it equals null. An ABSENT value means CANNOT MEASURE — the sessions were never read, or the "
        + "series is shorter than the period needs — and never zero and never neutral: refuse to conclude "
        + "rather than substitute. `tradeDate` names the session the value belongs to and `contractId` the "
        + "contract behind it; two readings from different contracts are not comparable. period selects "
        + "among the operator's configured periods on the same terms as get_session_indicators, and `vwap` "
        + "is refused on a session series naming vwap-rolling.")]
    public async Task<ToolPayloads.SessionIndicatorReading> GetSessionIndicatorAt(
        [Description("The instrument symbol, e.g. ES.")] string symbol,
        [Description(
            "The session name: full, rth, asia or europe as shipped, or any name the operator configured.")]
        string session,
        [Description("The indicator name, e.g. atr.")] string indicator,
        [Description("The moment, ISO-8601 UTC.")] DateTimeOffset asOfUtc,
        [Description("Which configured period to read, e.g. 200. Omit for the indicator's primary configured "
            + "period. Only periods the operator configured "
            + "are readable: any other is an error listing the configured ones, never an empty series. Not "
            + "accepted for vwap, which is anchored to the session. For macd, macd-signal and macd-histogram this "
            + "is the SLOW length.")] int? period = null,
        CancellationToken cancellationToken = default)
    {
        InstrumentId instrument = _resolver.Resolve(symbol);
        SessionDefinition definition = Definition(session);

        SeriesKey series = new SeriesKey.Session(_venue, instrument.Symbol, definition.Name);

        // Refused before the store is touched, and here the stakes are higher than on the window read: this
        // one answers cannot-measure when it matches no row, so a name or a period that reached the query
        // would come back as an honest-looking absence.
        IIndicator resolved = ResolveIndicator(series, indicator, period);
        DateTimeOffset asOf = asOfUtc.ToUniversalTime();

        await _indicators.EnsureProjectedAsync(series, cancellationToken).ConfigureAwait(false);

        // AS-OF IS THE SESSION'S CLOSE, NOT ITS OPEN, and that is the one line where this differs from
        // get_indicator_at. A session bar opens hours before it closes, so comparing the opening instant
        // would answer a question asked during today's session with today's own, still-forming number. The
        // comparison is only reachable through the join: SessionIndicatorValues carries the opening instant
        // as its key and no close at all, because the close is the session bar's fact rather than the
        // value's.
        ToolPayloads.SessionIndicatorReading? reading = await Values(instrument, definition, resolved)
            .Join(
                ClosedBy(instrument, definition, asOf),
                v => new { v.Venue, v.Instrument, v.Session, Opening = v.BucketStart },
                s => new { s.Venue, s.Instrument, s.Session, Opening = s.OpenUtc },
                (v, s) => new { s.TradeDate, s.OpenUtc, s.ContractId, v.Value })
            .OrderByDescending(row => row.OpenUtc)
            .Select(row => new ToolPayloads.SessionIndicatorReading(
                row.Value, row.TradeDate, row.OpenUtc, row.ContractId))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        // NOTHING AT ALL rather than a zero or a nearest value. Every field is nullable, so this reaches the
        // wire as `{}` -- the shape PayloadNullWireShapeTests pins, and the one a caller must test for by
        // key presence.
        return reading ?? new ToolPayloads.SessionIndicatorReading(null, null, null, null);
    }

    /// <summary>Resolves a session name, or refuses naming the configured ones.</summary>
    /// <param name="session">The name the caller asked for.</param>
    /// <returns>The definition.</returns>
    /// <exception cref="McpException">The name is not in the vocabulary.</exception>
    /// <remarks>
    /// The same translation <see cref="SessionBarTools"/> performs, on the same two exception types and for
    /// the same reason: the catalogue's message already lists the known names, and only the type changes.
    /// </remarks>
    private SessionDefinition Definition(string session) =>
        ExceptionTranslation.Try(
            () => _sessions.Resolve(session),
            static ex => ex is KeyNotFoundException or ArgumentException);

    /// <summary>The catalogue's refusal for THIS series, as this surface's refusal.</summary>
    /// <param name="series">The session series being read.</param>
    /// <param name="name">The indicator name.</param>
    /// <param name="period">The chosen period, or <see langword="null"/> for the primary.</param>
    /// <returns>The instance the read will answer from.</returns>
    /// <remarks>
    /// <b>Three refusals rather than <see cref="IndicatorTools"/>' two.</b> An unknown name and an
    /// unconfigured period are the catalogue's <see cref="KeyNotFoundException"/>s, unchanged; the third is
    /// series-aware and is an <see cref="ArgumentException"/> — a name the catalogue computes but this series
    /// does not carry, which today is <c>vwap</c> alone. ADR-0022 requires it be said rather than omitted
    /// silently, because the alternative answer is an empty series and an empty series is indistinguishable
    /// from a market that produced none.
    /// </remarks>
    private IIndicator ResolveIndicator(SeriesKey series, string name, int? period) =>
        ExceptionTranslation.Try(
            () => _catalog.ResolveFor(series, name, period),
            static ex => ex is KeyNotFoundException or ArgumentException);

    /// <summary>The stored values for one indicator over this instrument's session series.</summary>
    /// <param name="instrument">The instrument.</param>
    /// <param name="definition">The session.</param>
    /// <param name="resolved">The indicator and period the read answers under.</param>
    /// <returns>The query.</returns>
    /// <remarks>
    /// <para>
    /// <b>The whole storage key is named here, not only the half the join does not supply.</b> The instrument
    /// and the session are redundant against today's four-column join and are written anyway: they are free —
    /// the same index answers either way — and without them this query alone reads "every venue row for this
    /// indicator", which is a correct series only for as long as the one call site keeps its join. A copy
    /// made against a differently-keyed bar table, or a join narrowed later, would silently widen it.
    /// </para>
    /// <para>
    /// <b>AsNoTracking</b>, for the reason every read of a projected table here is: the rows are written by
    /// SQL the change tracker never sees, so a tracked copy is a stale entity the identity map hands back to
    /// the next read in the same scope (gh#103).
    /// </para>
    /// </remarks>
    private IQueryable<SessionIndicatorValueRecord> Values(
        InstrumentId instrument, SessionDefinition definition, IIndicator resolved)
    {
        string venue = _venue;
        string symbol = instrument.Symbol;
        string session = definition.Name;
        string name = resolved.Name;
        int period = resolved.Period;

        return _database.SessionIndicatorValues
            .AsNoTracking()
            .Where(v => v.Venue == venue
                && v.Instrument == symbol
                && v.Session == session
                && v.Indicator == name
                && v.Period == period);
    }

    /// <summary>The stored session bars for a set of trade dates.</summary>
    /// <param name="instrument">The instrument.</param>
    /// <param name="definition">The session.</param>
    /// <param name="tradeDates">The trade dates the window names.</param>
    /// <returns>The query.</returns>
    private IQueryable<SessionBarRecord> Bars(
        InstrumentId instrument, SessionDefinition definition, DateOnly[] tradeDates)
    {
        string venue = _venue;
        string symbol = instrument.Symbol;
        string session = definition.Name;

        return _database.SessionBars
            .AsNoTracking()
            .Where(s => s.Venue == venue
                && s.Instrument == symbol
                && s.Session == session
                && tradeDates.Contains(s.TradeDate));
    }

    /// <summary>The stored session bars whose session had already closed at a moment.</summary>
    /// <param name="instrument">The instrument.</param>
    /// <param name="definition">The session.</param>
    /// <param name="asOf">The moment, UTC.</param>
    /// <returns>The query.</returns>
    private IQueryable<SessionBarRecord> ClosedBy(
        InstrumentId instrument, SessionDefinition definition, DateTimeOffset asOf)
    {
        string venue = _venue;
        string symbol = instrument.Symbol;
        string session = definition.Name;

        return _database.SessionBars
            .AsNoTracking()
            .Where(s => s.Venue == venue
                && s.Instrument == symbol
                && s.Session == session
                && s.CloseUtc <= asOf);
    }

    /// <summary>
    /// Reports which contracts produced the sessions underneath a window, without loading their prices.
    /// </summary>
    /// <param name="instrument">The instrument.</param>
    /// <param name="definition">The session.</param>
    /// <param name="tradeDates">The trade dates the window names.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The coverage.</returns>
    /// <remarks>
    /// <para>
    /// <b>Over the sessions in the window, not only the ones that carry a value</b>, on the same terms as
    /// <see cref="IndicatorTools"/>' bar-side coverage. A warm-up date produced no value and still produced
    /// the values after it, so a coverage that started at the first measured trade date would hide the
    /// contract those numbers were actually derived from.
    /// </para>
    /// <para>
    /// The prices are structural zeros and are never read: <see cref="ToolPayloads.ToSessionCoverage"/>
    /// segments on the contract id and reports the session OPENS at both ends, so this projects the three
    /// fields the answer depends on rather than fetching an OHLCV payload to throw away.
    /// </para>
    /// </remarks>
    private async Task<ToolPayloads.ContractCoverage> CoverageAsync(
        InstrumentId instrument,
        SessionDefinition definition,
        DateOnly[] tradeDates,
        CancellationToken cancellationToken)
    {
        List<SessionBar> shape = await Bars(instrument, definition, tradeDates)
            .OrderBy(s => s.OpenUtc)
            .Select(s => new SessionBar(
                s.TradeDate, s.OpenUtc, s.CloseUtc, 0m, 0m, 0m, 0m, 0L, s.ContractId, 0))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return ToolPayloads.ToSessionCoverage(shape);
    }
}
