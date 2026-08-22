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
        IReadOnlyList<VenueContract> contracts =
            await _gateway.ResolveContractsAsync(instrument, cancellationToken).ConfigureAwait(false);

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
        DateTimeOffset at = (atUtc ?? _clock.GetUtcNow()).ToUniversalTime();

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
    /// Walks forward to the next instant the market is open.
    /// </summary>
    /// <param name="from">Where to start.</param>
    /// <returns>The next open, or <see langword="null"/> if none within a fortnight.</returns>
    /// <remarks>
    /// A forward scan in 15-minute steps rather than a closed-form calculation. The closed form has to
    /// reproduce every rule in the calendar — the weekend, the maintenance window, the holiday-suppresses-the
    /// -previous-evening rule — and a second implementation of those rules is a second place for them to be
    /// subtly wrong. The scan asks the calendar itself, so the two cannot disagree.
    /// <para>
    /// Bounded at a fortnight so a misconfigured calendar that never opens returns null rather than spinning.
    /// </para>
    /// </remarks>
    private DateTimeOffset? NextOpen(DateTimeOffset from)
    {
        TimeSpan step = TimeSpan.FromMinutes(15);
        DateTimeOffset limit = from + TimeSpan.FromDays(14);

        for (DateTimeOffset probe = from; probe <= limit; probe += step)
        {
            if (_calendar.TradeDateFor(probe) is not null)
            {
                return probe.ToUniversalTime();
            }
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
