using System.ComponentModel;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MarqSpec.Mcp.TopstepX.Tools;

/// <summary>
/// What this server serves, and where the market is in its session.
/// </summary>
/// <remarks>
/// Every tool here is marked <c>ReadOnly</c>. That is not decoration — it is ADR-0002 stated in the one place
/// a client can actually read it.
/// </remarks>
[McpServerToolType]
public sealed class ReferenceTools(
    InstrumentRegistry registry,
    BarSessionCalendar calendar,
    IMarketDataGateway gateway,
    IOptions<MarketDataOptions> marketData,
    TimeProvider clock)
{
    private readonly InstrumentRegistry _registry = registry;
    private readonly BarSessionCalendar _calendar = calendar;
    private readonly IMarketDataGateway _gateway = gateway;
    private readonly MarketDataOptions _marketData = marketData.Value;
    private readonly TimeProvider _clock = clock;

    /// <summary>Lists the instruments this server serves, with their contract arithmetic.</summary>
    /// <returns>The instruments.</returns>
    [McpServerTool(ReadOnly = true, Idempotent = true, Title = "List instruments")]
    [Description(
        "Lists the futures instruments this server serves, with tick size, point value and session close. "
        + "Any symbol not in this list is rejected by the other tools rather than returning an empty series.")]
    public IReadOnlyList<ToolPayloads.InstrumentInfo> ListInstruments() =>
        [.. _registry.Instruments.Select(i =>
        {
            InstrumentSpec spec = _registry.SpecFor(i);
            return new ToolPayloads.InstrumentInfo(
                i.Symbol,
                spec.TickSize,
                spec.PointValue,
                spec.TickValue,
                _marketData.SessionCloseCentral);
        })];

    /// <summary>Resolves an instrument to the venue contracts quoting it.</summary>
    /// <param name="symbol">The instrument symbol, e.g. <c>ES</c>.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The contracts, the active one first.</returns>
    [McpServerTool(ReadOnly = true, Idempotent = true, Title = "Search contracts")]
    [Description(
        "Resolves an instrument symbol such as ES to the venue contract ids quoting it, active contract first. "
        + "Note the product code inside a contract id is not the trading symbol: ES resolves to a contract "
        + "whose product segment is EP.")]
    public async Task<IReadOnlyList<ToolPayloads.ContractInfo>> SearchContracts(
        [Description("The instrument symbol, e.g. ES or NQ.")] string symbol,
        CancellationToken cancellationToken)
    {
        InstrumentId instrument = Resolve(symbol);

        IReadOnlyList<VenueContract> contracts;
        try
        {
            contracts = await _gateway.ResolveContractsAsync(instrument, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (VenueException ex)
        {
            // Without this the SDK reports a bare "An error occurred invoking 'search_contracts'" and the
            // reason -- which is usually "no credentials yet" -- never reaches the caller at all. This is the
            // only tool here that touches the venue, and it was the only one missing the translation.
            throw new McpException("The venue could not answer: " + ex.Message);
        }

        if (contracts.Count == 0)
        {
            // An empty list here is NOT "no results". This instrument is on the served list, so the venue
            // knowing nothing about it means something is wrong with the request -- and the overwhelmingly
            // likely cause is the data tier, which returns an empty universe rather than an error.
            //
            // Returning [] would be the exact failure R-5.3 exists to prevent: indistinguishable from a
            // legitimate empty answer, and the caller reasons about the silence instead of the cause.
            throw new McpException(
                "The venue returned no contracts for '" + instrument.Symbol + "'. This instrument IS on this "
                + "server's list, so the likely cause is ProjectX__DataTier: the wrong market-data tier "
                + "returns an empty universe rather than an error. Practice credentials need Simulated; Live "
                + "needs a live data entitlement.");
        }

        return [.. contracts.Select(c => new ToolPayloads.ContractInfo(
            c.ContractId, c.Instrument.Symbol, c.IsActive, c.TickSize, c.TickValue))];
    }

    /// <summary>Reports where the market is in its session.</summary>
    /// <param name="symbol">The instrument symbol.</param>
    /// <param name="atUtc">The instant to evaluate, or null for now.</param>
    /// <returns>The session state.</returns>
    [McpServerTool(ReadOnly = true, Idempotent = true, Title = "Get market session")]
    [Description(
        "Reports whether the market is open, when the running session closes, and when the next one opens. "
        + "Worth calling before concluding that a series looks stale: futures run Sunday evening to Friday "
        + "afternoon Central, with a daily maintenance window, so a two-hour-old bar means something very "
        + "different on a Tuesday than at 03:00 on a Sunday.")]
    public ToolPayloads.SessionState GetMarketSession(
        [Description("The instrument symbol, e.g. ES.")] string symbol,
        [Description("The instant to evaluate, ISO-8601 UTC. Defaults to now.")] DateTimeOffset? atUtc = null)
    {
        InstrumentId instrument = Resolve(symbol);

        // The instant is bounded before the calendar is asked about it. This tool takes a MOMENT and no
        // window, so every bound built around ValidateWindow swept past it -- and an evening instant belongs
        // to the NEXT trade date, which on 9999-12-31 is a date DateOnly cannot hold, so TradeDateFor left
        // this boundary as a raw ArgumentOutOfRangeException (gh#110).
        DateTimeOffset at =
            ToolGuards.ValidateInstant((atUtc ?? _clock.GetUtcNow()).ToUniversalTime(), "atUtc");

        DateOnly? tradeDate = _calendar.TradeDateFor(at);
        DateOnly marketDate = MarketClock.MarketDate(at);
        bool isHoliday = _calendar.Holidays.Contains(marketDate);

        if (tradeDate is { } running)
        {
            DateTimeOffset close = MarketClock.FromMarket(running, _calendar.SessionClose);
            return new ToolPayloads.SessionState(
                instrument.Symbol,
                IsOpen: true,
                TradeDate: running,
                SessionCloseUtc: close.ToUniversalTime(),
                MinutesToClose: (int)Math.Max(0, (close - at).TotalMinutes),
                NextOpenUtc: null,
                IsHoliday: isHoliday);
        }

        return new ToolPayloads.SessionState(
            instrument.Symbol,
            IsOpen: false,
            TradeDate: null,
            SessionCloseUtc: null,
            MinutesToClose: null,
            NextOpenUtc: NextOpen(at),
            IsHoliday: isHoliday);
    }

    /// <summary>
    /// Finds when the market next opens.
    /// </summary>
    /// <param name="from">Where to start.</param>
    /// <returns>The next session open, or <see langword="null"/> if none within a fortnight.</returns>
    /// <remarks>
    /// <para>
    /// A forward scan rather than a closed-form calculation. The closed form has to reproduce every rule in
    /// the calendar — the weekend, the maintenance window, the holiday-suppresses-the-previous-evening rule —
    /// and a second implementation of those rules is a second place for them to be subtly wrong. The scan
    /// asks the calendar itself, so the two cannot disagree.
    /// </para>
    /// <para>
    /// <b>The scan finds a session, then reports that session's actual open</b> rather than the probe instant
    /// that happened to land inside it. Returning the probe made the answer depend on the coarseness of the
    /// step and on the seconds in "now" — it reported an open at 17:09 for a session that opens at 17:00,
    /// which is wrong in exactly the way an agent would act on without noticing.
    /// </para>
    /// <para>
    /// Bounded at a fortnight so a misconfigured calendar that never opens returns null rather than spinning,
    /// and that fortnight is clamped to <see cref="ToolGuards.CalendarHorizon"/> so the scan cannot walk off
    /// the end of the calendar (gh#110).
    /// </para>
    /// </remarks>
    private DateTimeOffset? NextOpen(DateTimeOffset from)
    {
        TimeSpan step = TimeSpan.FromMinutes(15);

        // CLAMPED to the calendar horizon rather than refused, and that difference is the point. The
        // fortnight is a TERMINATION guard, not a window anyone asked for: `from + 14 days` threw for an
        // instant three days inside the horizon -- one the session rules answer perfectly well -- so
        // refusing it would have refused a fortnight of legitimate questions to protect an addition (gh#110).
        // A scan that reaches the horizon without finding an open returns null, which is the same absence it
        // already returns for a calendar that never opens. Never a substituted time.
        DateTimeOffset limit = ToolGuards.CalendarHorizon - from < TimeSpan.FromDays(14)
            ? ToolGuards.CalendarHorizon
            : from + TimeSpan.FromDays(14);

        for (DateTimeOffset probe = from; probe <= limit; probe += step)
        {
            if (_calendar.TradeDateFor(probe) is not { } tradeDate)
            {
                continue;
            }

            // A trade date's session opens the PREVIOUS evening — that off-by-one-evening is the whole shape
            // of a futures session, and computing it from the trade date is what makes this exact.
            DateTimeOffset open =
                MarketClock.FromMarket(tradeDate.AddDays(-1), _calendar.SessionOpen).ToUniversalTime();

            // If that open is already behind us the scan started mid-session, which the caller has ruled out
            // before asking; fall back to the probe rather than reporting a time in the past.
            return open >= from ? open : probe.ToUniversalTime();
        }

        return null;
    }

    private InstrumentId Resolve(string symbol)
    {
        try
        {
            return _registry.Resolve(symbol);
        }
        catch (KeyNotFoundException ex)
        {
            // An unknown symbol is an ERROR, never an empty result. A wrong symbol and a quiet market must not
            // be indistinguishable to the caller (R-5.3).
            throw new McpException(ex.Message);
        }
        catch (ArgumentException ex)
        {
            throw new McpException(ex.Message);
        }
    }
}
