using System.Diagnostics;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>What <see cref="LatestQuoteReader"/> found. A miss is a result, not an exception.</summary>
/// <param name="Symbol">The symbol the caller asked for, normalised when it was served.</param>
/// <param name="Status">
/// <see cref="LatestQuoteStatus.Ok"/>, <see cref="LatestQuoteStatus.Missing"/>,
/// <see cref="LatestQuoteStatus.UnknownSymbol"/>, or <see cref="LatestQuoteStatus.Failed"/>.
/// </param>
/// <param name="ContractId">The stored front, when a row named one.</param>
    /// <param name="Bid">Best bid, in the contract's price units. Null when that side is absent.</param>
    /// <param name="Ask">Best ask. Null when that side is absent.</param>
    /// <param name="Last">Last trade. Null when it is unknown or too old to carry.</param>
    /// <param name="LastAt">When <paramref name="Last"/> was seen. Null when last is omitted.</param>
    /// <param name="Volume">Session volume from a quote. Null when the quote did not carry it.</param>
    /// <param name="QuoteTimeUtc">The venue stamp, when the row had one.</param>
    /// <param name="AgeSeconds">
    /// Seconds since the database accepted the bid and ask. Null when there is no row.
    /// This does not date last or volume.
    /// </param>
/// <param name="Stale">True when the row is older than the configured stale window.</param>
/// <param name="Reason">Why a failed read failed. Null unless the status is failed.</param>
public sealed record LatestQuoteReading(
    string Symbol,
    string Status,
    string? ContractId,
    decimal? Bid,
    decimal? Ask,
    decimal? Last,
    DateTimeOffset? LastAt,
    long? Volume,
    DateTimeOffset? QuoteTimeUtc,
    double? AgeSeconds,
    bool Stale,
    string? Reason);

/// <summary>The wire values of <see cref="LatestQuoteReading.Status"/>.</summary>
public static class LatestQuoteStatus
{
    /// <summary>A row was stored for the instrument.</summary>
    public const string Ok = "ok";

    /// <summary>The symbol is served, and the table has no row for it.</summary>
    public const string Missing = "missing";

    /// <summary>The symbol is not one this server serves.</summary>
    public const string UnknownSymbol = "unknown_symbol";

    /// <summary>The store did not answer. This is not a miss.</summary>
    public const string Failed = "failed";
}

/// <summary>
/// Reads the latest stored quote for a symbol. It does not open a market hub and it does not
/// search the venue. The front is the newest row the recorder wrote for the instrument.
/// </summary>
public sealed class LatestQuoteReader
{
    private readonly ILatestQuoteStore _store;
    private readonly InstrumentRegistry _registry;
    private readonly MarketDataOptions _market;
    private readonly BarSessionCalendar _calendar;
    private readonly TimeProvider _clock;
    private readonly HostTelemetry _telemetry;
    private readonly ILogger<LatestQuoteReader> _logger;

    /// <summary>Creates the reader.</summary>
    /// <param name="store">The latest-quote table.</param>
    /// <param name="registry">The configured instruments.</param>
    /// <param name="market">The stale window.</param>
    /// <param name="calendar">The CME session calendar. A carried last stops at its boundary.</param>
    /// <param name="clock">The clock a carried last is aged on.</param>
    /// <param name="telemetry">The read span and counter.</param>
    /// <param name="logger">Store failures are logged with the exception. It is not venue text.</param>
    public LatestQuoteReader(
        ILatestQuoteStore store,
        InstrumentRegistry registry,
        IOptions<MarketDataOptions> market,
        BarSessionCalendar calendar,
        TimeProvider clock,
        HostTelemetry telemetry,
        ILogger<LatestQuoteReader> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(market);
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(telemetry);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _registry = registry;
        _market = market.Value;
        _calendar = calendar;
        _clock = clock;
        _telemetry = telemetry;
        _logger = logger;
    }

    /// <summary>
    /// The latest quote for <paramref name="symbol"/>, or a miss, a failure, or an unknown symbol.
    /// </summary>
    /// <param name="symbol">The instrument symbol, for example <c>ES</c>.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>A result. Unknown, missing and failed do not throw.</returns>
    public async Task<LatestQuoteReading> ReadAsync(string symbol, CancellationToken cancellationToken)
    {
        using Activity? span = _telemetry.StartQuoteRead();
        if (!_registry.IsServed(symbol))
        {
            Mark(span, QuoteReadOutcome.Unknown);
            return new LatestQuoteReading(
                symbol ?? string.Empty,
                LatestQuoteStatus.UnknownSymbol,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                false,
                null);
        }

        InstrumentId instrument = _registry.Resolve(symbol);
        LatestQuoteFront? front;
        try
        {
            front = await _store.FindFrontAsync(instrument.Symbol, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The front query is its own statement. A failure here is not "no row".
            return Fail(span, instrument.Symbol, null, QuoteReadFailure.FrontLookupFailed, ex);
        }

        if (front is null)
        {
            Mark(span, QuoteReadOutcome.Miss);
            return Missing(instrument.Symbol, null);
        }

        LatestQuoteRow? row;
        try
        {
            row = await _store.FindAsync(front.Venue, front.ContractId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The row query is the second statement. Counting it as a miss would hide an outage.
            return Fail(span, instrument.Symbol, front.ContractId, QuoteReadFailure.StoreFailed, ex);
        }

        if (row is null)
        {
            Mark(span, QuoteReadOutcome.Miss);
            return Missing(instrument.Symbol, front.ContractId);
        }

        bool stale = row.AgeSeconds > _market.LatestQuoteStaleAfter.TotalSeconds;
        Mark(span, stale ? QuoteReadOutcome.Stale : QuoteReadOutcome.Fresh);
        DateTimeOffset now = _clock.GetUtcNow();
        DateOnly? session = _calendar.TradeDateFor(now);
        bool keepLast = row.Last != 0 && LatestQuoteCarry.Keep(row.LastAt, row.TradeDate, now, session);
        bool keepVolume = row.Volume != 0
            && LatestQuoteCarry.Keep(row.VolumeAt, row.TradeDate, now, session);
        return new LatestQuoteReading(
            instrument.Symbol,
            LatestQuoteStatus.Ok,
            row.ContractId,
            row.Bid == 0 ? null : row.Bid,
            row.Ask == 0 ? null : row.Ask,
            keepLast ? row.Last : null,
            keepLast ? row.LastAt : null,
            keepVolume ? row.Volume : null,
            row.QuoteTimeUtc,
            row.AgeSeconds,
            stale,
            null);
    }

    private LatestQuoteReading Fail(
        Activity? span,
        string symbol,
        string? contractId,
        string reason,
        Exception exception)
    {
        span?.SetTag(HostTelemetry.OutcomeTag, QuoteReadOutcome.ReadFailed);
        span?.SetTag(HostTelemetry.ReasonTag, reason);
        span?.SetTag(HostTelemetry.ExceptionTag, exception.GetType().Name);
        span?.SetStatus(ActivityStatusCode.Error);
        _telemetry.QuoteReadFailed(reason, exception.GetType().Name);
        _logger.LogWarning(exception, "A latest-quote read failed ({Reason}).", reason);
        return new LatestQuoteReading(
            symbol,
            LatestQuoteStatus.Failed,
            contractId,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            false,
            reason);
    }

    private void Mark(Activity? span, string outcome)
    {
        span?.SetTag(HostTelemetry.OutcomeTag, outcome);
        _telemetry.QuoteRead(outcome);
    }

    private static LatestQuoteReading Missing(string symbol, string? contractId) =>
        new(
            symbol,
            LatestQuoteStatus.Missing,
            contractId,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            false,
            null);
}
