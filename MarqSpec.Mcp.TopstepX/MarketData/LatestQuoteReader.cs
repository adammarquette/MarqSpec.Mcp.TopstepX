using System.Diagnostics;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Telemetry;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>What <see cref="LatestQuoteReader"/> found. A miss is a result, not an exception.</summary>
/// <param name="Symbol">The symbol the caller asked for, normalised when it was served.</param>
/// <param name="Status">
/// <see cref="LatestQuoteStatus.Ok"/>, <see cref="LatestQuoteStatus.Missing"/>, or
/// <see cref="LatestQuoteStatus.UnknownSymbol"/>.
/// </param>
/// <param name="ContractId">The front contract, when the venue named one.</param>
/// <param name="Bid">Best bid, in the contract's price units. Null unless the status is ok.</param>
/// <param name="Ask">Best ask.</param>
/// <param name="Last">Last trade.</param>
/// <param name="Volume">Session volume, in contracts.</param>
/// <param name="QuoteTimeUtc">The venue stamp, when the row had one.</param>
/// <param name="AgeSeconds">Seconds since the database accepted the row. Null when there is no row.</param>
/// <param name="Stale">True when the row is older than the configured stale window.</param>
public sealed record LatestQuoteReading(
    string Symbol,
    string Status,
    string? ContractId,
    decimal? Bid,
    decimal? Ask,
    decimal? Last,
    long? Volume,
    DateTimeOffset? QuoteTimeUtc,
    double? AgeSeconds,
    bool Stale);

/// <summary>The wire values of <see cref="LatestQuoteReading.Status"/>.</summary>
public static class LatestQuoteStatus
{
    /// <summary>A row was stored for the front contract.</summary>
    public const string Ok = "ok";

    /// <summary>
    /// The symbol is served, and there is no row for its front. Or the venue named no front.
    /// </summary>
    public const string Missing = "missing";

    /// <summary>The symbol is not one this server serves.</summary>
    public const string UnknownSymbol = "unknown_symbol";
}

/// <summary>
/// Reads the latest stored quote for a symbol. It does not open a market hub.
/// </summary>
/// <remarks>
/// The front contract is the venue search's first result, the same contract the recorder writes.
/// Age comes from <see cref="ILatestQuoteStore.FindAsync"/>, which uses the database clock.
/// </remarks>
public sealed class LatestQuoteReader
{
    private readonly ILatestQuoteStore _store;
    private readonly IMarketDataGateway _gateway;
    private readonly InstrumentRegistry _registry;
    private readonly MarketDataOptions _market;
    private readonly HostTelemetry _telemetry;
    private readonly ILogger<LatestQuoteReader> _logger;

    /// <summary>Creates the reader.</summary>
    /// <param name="store">The latest-quote table.</param>
    /// <param name="gateway">Resolves the front contract. This reader does not use a hub.</param>
    /// <param name="registry">The configured instruments.</param>
    /// <param name="market">The stale window.</param>
    /// <param name="telemetry">The read span and counter.</param>
    /// <param name="logger">Type-only logs. Vendor text is not written.</param>
    public LatestQuoteReader(
        ILatestQuoteStore store,
        IMarketDataGateway gateway,
        InstrumentRegistry registry,
        IOptions<MarketDataOptions> market,
        HostTelemetry telemetry,
        ILogger<LatestQuoteReader> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(market);
        ArgumentNullException.ThrowIfNull(telemetry);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _gateway = gateway;
        _registry = registry;
        _market = market.Value;
        _telemetry = telemetry;
        _logger = logger;
    }

    /// <summary>The latest quote for <paramref name="symbol"/>, or a miss or an unknown symbol.</summary>
    /// <param name="symbol">The instrument symbol, for example <c>ES</c>.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>A result. Unknown and missing do not throw.</returns>
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
                false);
        }

        InstrumentId instrument = _registry.Resolve(symbol);
        string? contractId = await FrontAsync(instrument, cancellationToken).ConfigureAwait(false);
        if (contractId is null)
        {
            Mark(span, QuoteReadOutcome.Miss);
            return Missing(instrument.Symbol, null);
        }

        LatestQuoteRow? row = await _store
            .FindAsync(_gateway.VenueId, contractId, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            Mark(span, QuoteReadOutcome.Miss);
            return Missing(instrument.Symbol, contractId);
        }

        bool stale = row.AgeSeconds > _market.LatestQuoteStaleAfter.TotalSeconds;
        Mark(span, stale ? QuoteReadOutcome.Stale : QuoteReadOutcome.Fresh);
        return new LatestQuoteReading(
            instrument.Symbol,
            LatestQuoteStatus.Ok,
            row.ContractId,
            row.Bid,
            row.Ask,
            row.Last,
            row.Volume,
            row.QuoteTimeUtc,
            row.AgeSeconds,
            stale);
    }

    private async Task<string?> FrontAsync(InstrumentId instrument, CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<VenueContract> contracts = await _gateway
                .ResolveContractsAsync(instrument, cancellationToken)
                .ConfigureAwait(false);
            return contracts.Count == 0 ? null : contracts[0].ContractId;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (VenueException ex)
        {
            // Caught as VenueException, not Exception: the tool-surface gate reads the clause type.
            LogFrontFailure(instrument, ex);
            return null;
        }
        catch (Exception ex)
        {
            LogFrontFailure(instrument, ex);
            return null;
        }
    }

    private void LogFrontFailure(InstrumentId instrument, Exception exception)
    {
        _logger.LogWarning(
            "Latest-quote front resolution failed for {Instrument} ({ExceptionType}). "
            + "The venue error is not logged.",
            instrument.Symbol,
            exception.GetType().Name);
    }

    private void Mark(Activity? span, string outcome)
    {
        span?.SetTag(HostTelemetry.OutcomeTag, outcome);
        _telemetry.QuoteRead(outcome);
    }

    private static LatestQuoteReading Missing(string symbol, string? contractId) =>
        new(symbol, LatestQuoteStatus.Missing, contractId, null, null, null, null, null, null, false);
}
