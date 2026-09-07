using System.ComponentModel;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Venue;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MarqSpec.Mcp.TopstepX.Tools;

/// <summary>
/// <c>get_session_bars</c> and <c>get_latest_session_bars</c> — one OHLCV bar per whole trading session,
/// derived from the cached base series rather than fetched (gh#496, ADR-0022).
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own tool type rather than a <c>session</c> argument on <c>get_bars</c></b> (ADR-0017). A session
/// bar is not a coarse bar: it is defined on the CME trade date rather than on the bucket grid, it is
/// complete or it is <i>absent with a reason</i>, and it answers with a second list <c>get_bars</c> has no
/// place for. Folding the two together would have given one tool two return shapes and one description
/// trying to describe both.
/// </para>
/// <para>
/// <b>No gateway here, deliberately.</b> The venue is reached only through
/// <see cref="SessionBarService"/> — which reads the base series through <c>BarCacheService</c> and holds a
/// gateway solely for its venue id — and this payload carries no venue field, so taking one would be a
/// dependency this concern never reads (<c>MarketDataToolBoundaryTests</c> pins the set).
/// </para>
/// </remarks>
/// <param name="resolver">Turns a caller's symbol into an instrument, refusing first if the store is absent.</param>
/// <param name="sessions">The session-bar reader: derives, stores and reads back.</param>
/// <param name="catalog">The closed vocabulary of session names.</param>
/// <param name="calendar">The session calendar every trade-date list is walked off.</param>
/// <param name="guards">The request-shape checks the whole tool surface shares.</param>
/// <param name="clock">The clock <c>get_latest_session_bars</c> anchors on.</param>
[McpServerToolType]
public sealed class SessionBarTools(
    InstrumentResolver resolver,
    SessionBarService sessions,
    SessionCatalog catalog,
    BarSessionCalendar calendar,
    ToolGuards guards,
    TimeProvider clock)
{
    private readonly InstrumentResolver _resolver = resolver;
    private readonly SessionBarService _sessions = sessions;
    private readonly SessionCatalog _catalog = catalog;
    private readonly BarSessionCalendar _calendar = calendar;
    private readonly ToolGuards _guards = guards;
    private readonly TimeProvider _clock = clock;

    /// <summary>Reads one session bar per whole session inside a window.</summary>
    /// <param name="symbol">The instrument symbol.</param>
    /// <param name="session">The session name.</param>
    /// <param name="fromUtc">The window start, inclusive.</param>
    /// <param name="toUtc">The window end, exclusive.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The session bars, the absences, and what the read cost.</returns>
    [McpServerTool(ReadOnly = true, Idempotent = true, Title = "Get session bars")]
    [Description(
        "Reads one OHLCV bar per whole trading session over a time window — the day, the overnight, or any "
        + "named slice of it. A session bar is DERIVED from cached finer bars at the session's own base "
        + "resolution rather than fetched as a bar that size, and it exists ONLY when every base bucket the "
        + "calendar expects inside the session is stored and all of them came from one contract. Otherwise "
        + "the trade date is listed under `absent` with a reason — Incomplete, SpansRoll, ProvenanceUnknown "
        + "or NotClosed — and never as a partial bar. NotClosed appears only for a session that has not "
        + "finished yet, so a window entirely in the past never carries one. Only sessions lying WHOLLY "
        + "inside the window are returned. Of the trade dates whose WHOLE session lies inside the window, one "
        + "in NEITHER list is not a trading day; a session the window clips is left out, not reported — so "
        + "read the edges of a window as inclusive of whole sessions only. "
        + "The response reports `venueRequests` and `fetchedBuckets`, and only the first is evidence of a "
        + "bar fetch: `venueRequests` of 0 is the exact test that no bars were fetched, NOT that the vendor "
        + "went untouched — a read the coverage ledger covers still resolves the contract list, so it is "
        + "venue-dependent — while `fetchedBuckets` counts the BASE buckets this call wrote or revised and "
        + "can read zero even after a genuine fetch. Never returns a truncated series: a window over either "
        + "cap — the row cap on trade dates, or the detection cap on the base buckets underneath them — is "
        + "refused with the real count. `contracts` says which contracts produced these sessions; each "
        + "session bar comes from exactly one, so a roll falls BETWEEN two trade dates and `contracts.span` is "
        + "SingleContract, SpansRoll or Unknown. Unknown here means NO session bar could be built at all, "
        + "which is not what it means on get_contract_roll: a session whose provenance was never recorded is "
        + "an `absent` entry reading ProvenanceUnknown, not an Unknown span. The session name is a closed "
        + "vocabulary: an unknown one is an error listing the configured names, never an empty series.")]
    public async Task<ToolPayloads.SessionBarSeries> GetSessionBars(
        [Description("The instrument symbol, e.g. ES.")] string symbol,
        [Description(
            "The session name: full, rth, asia or europe as shipped, or any name the operator configured.")]
        string session,
        [Description("Window start, ISO-8601 UTC, inclusive.")] DateTimeOffset fromUtc,
        [Description("Window end, ISO-8601 UTC, exclusive.")] DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        InstrumentId instrument = _resolver.Resolve(symbol);
        SessionDefinition definition = Definition(session);

        // THE TRADE DATES ARE WALKED OFF THE CALENDAR, and two things follow from that. A calendar walk cannot
        // produce the same date twice, so the duplicate-date ArgumentException SessionBarService.GetAsync
        // throws is unreachable from here -- and it is deliberately not caught, because a catch for an
        // impossible fault is a catch nobody can test and would hide the day this list stops coming from the
        // calendar. And every date in it is one the calendar carries a session on, so the service's
        // NotClosed can only mean the session named has not finished yet -- never a date the calendar
        // disowns, which is the other thing that reason covers one layer down.
        SessionWindowPlan plan = _guards.ValidateSessionWindow(fromUtc, toUtc, definition, _calendar);

        SessionBarReadResult result = await ReadAsync(
            instrument, definition, plan.TradeDates, cancellationToken).ConfigureAwait(false);

        return Payload(instrument, definition, result);
    }

    /// <summary>Reads the most recent closed session bars.</summary>
    /// <param name="symbol">The instrument symbol.</param>
    /// <param name="session">The session name.</param>
    /// <param name="count">How many closed sessions.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The session bars, the absences, and what the read cost.</returns>
    [McpServerTool(ReadOnly = true, Idempotent = true, Title = "Get latest session bars")]
    [Description(
        "Reads the most recent CLOSED session bars for an instrument. Anchored on the last session whose "
        + "close is at or before now, NEVER the one in progress — asked during today's session it answers "
        + "with the sessions before it rather than with a partial one. This is usually the tool to reach for "
        + "over get_session_bars, which needs explicit dates. Each bar is DERIVED from cached finer bars at "
        + "the session's own base resolution rather than fetched as a bar that size, and exists ONLY when "
        + "every base bucket the calendar expects inside the session is stored and all of them came from one "
        + "contract; a trade date that could not be built whole is listed under `absent` with a reason — "
        + "Incomplete, SpansRoll or ProvenanceUnknown — and never as a partial bar. The fourth reason, "
        + "NotClosed, cannot appear here: only closed sessions are asked about. The response reports "
        + "`venueRequests` and `fetchedBuckets`, and only the first is evidence of a bar fetch: "
        + "`venueRequests` of 0 is the exact test that no bars were fetched, NOT that the vendor went "
        + "untouched — a read the coverage ledger covers still resolves the contract list, so it is "
        + "venue-dependent — while `fetchedBuckets` counts the BASE buckets this call wrote or revised. "
        + "Never returns a truncated series. Three things refuse a count, each naming it: one over this "
        + "server's row cap; one the calendar holds too few closed sessions to satisfy; and one whose "
        + "sessions together span more BASE buckets than a single gap-detection pass will enumerate, since "
        + "the sessions are served by one covering read from the first open to the last close. The remedy "
        + "for all three is the same — ask for fewer sessions. `contracts` says which contracts produced "
        + "these sessions; "
        + "each session bar comes from exactly one, so a roll falls BETWEEN two trade dates. The session "
        + "name is a closed vocabulary: an unknown one is an error listing the configured names, never an "
        + "empty series.")]
    public async Task<ToolPayloads.SessionBarSeries> GetLatestSessionBars(
        [Description("The instrument symbol, e.g. ES.")] string symbol,
        [Description(
            "The session name: full, rth, asia or europe as shipped, or any name the operator configured.")]
        string session,
        [Description("How many closed sessions to return, oldest first.")] int count,
        CancellationToken cancellationToken)
    {
        InstrumentId instrument = _resolver.Resolve(symbol);
        SessionDefinition definition = Definition(session);

        // THE TRADE DATES ARE WALKED OFF THE CALENDAR, exactly as in GetSessionBars and with the same two
        // consequences: no date can repeat, so the service's duplicate-date ArgumentException is unreachable
        // and uncaught; and every date carries a session the calendar knows. Here the list is narrower still
        // -- every one of these has already CLOSED -- so the service's NotClosed cannot arise at all.
        IReadOnlyList<DateOnly> tradeDates =
            _guards.ValidateSessionCount(count, definition, _calendar, _clock.GetUtcNow());

        SessionBarReadResult result = await ReadAsync(
            instrument, definition, tradeDates, cancellationToken).ConfigureAwait(false);

        return Payload(instrument, definition, result);
    }

    /// <summary>Resolves a session name, or refuses naming the configured ones.</summary>
    /// <param name="session">The name the caller asked for.</param>
    /// <returns>The definition.</returns>
    /// <exception cref="McpException">The name is not in the vocabulary.</exception>
    /// <remarks>
    /// The catalogue answers a miss with a <see cref="KeyNotFoundException"/> — a .NET type, not a statement
    /// to a caller — so it is translated here on the same terms as
    /// <see cref="InstrumentResolver.Resolve"/> does for a symbol. The message already lists the known names;
    /// only the type changes.
    /// </remarks>
    private SessionDefinition Definition(string session) =>
        ExceptionTranslation.Try(
            () => _catalog.Resolve(session),
            static ex => ex is KeyNotFoundException or ArgumentException);

    /// <summary>Builds the wire payload from one read.</summary>
    /// <param name="instrument">The instrument the read was for.</param>
    /// <param name="definition">The session the read was for.</param>
    /// <param name="result">What the read returned.</param>
    /// <returns>The payload.</returns>
    private static ToolPayloads.SessionBarSeries Payload(
        InstrumentId instrument,
        SessionDefinition definition,
        SessionBarReadResult result) =>
        new(
            instrument.Symbol,
            definition.Name,
            definition.BaseResolutionMinutes,
            [.. result.Bars.Select(ToolPayloads.ToPoint)],
            [.. result.Absent.Select(ToolPayloads.ToAbsence)],
            result.FetchedBuckets,
            result.VenueRequests,
            ToolPayloads.ToSessionCoverage(result.Bars),
            result.History);

    private async Task<SessionBarReadResult> ReadAsync(
        InstrumentId instrument,
        SessionDefinition definition,
        IReadOnlyList<DateOnly> tradeDates,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _sessions.GetAsync(instrument, definition, tradeDates, cancellationToken)
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
