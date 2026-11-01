using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Telemetry;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>A stored quote plus how old its receipt is. A stale quote is still returned.</summary>
/// <param name="Quote">The row.</param>
/// <param name="Age">
/// The reader's clock minus <see cref="LatestQuoteDraft.ReceivedAt"/>, clamped at zero.
/// </param>
/// <param name="Stale">True when <paramref name="Age"/> is past the configured threshold.</param>
public sealed record LatestQuoteReading(LatestQuoteDraft Quote, TimeSpan Age, bool Stale);

/// <summary>
/// Reads <c>LatestQuotes</c>. It does not open a market hub and it does not fail a stale row.
/// </summary>
/// <remarks>
/// No tool calls this yet. The next slice is the one that exposes it.
/// </remarks>
public sealed class LatestQuoteReader
{
    private readonly ILatestQuoteStore _store;
    private readonly MarketDataOptions _market;
    private readonly TimeProvider _clock;
    private readonly HostTelemetry _telemetry;

    /// <summary>Creates the reader.</summary>
    /// <param name="store">The table.</param>
    /// <param name="market">The stale threshold.</param>
    /// <param name="clock">The clock age is measured on.</param>
    /// <param name="telemetry">Miss, stale, fresh, and refusal counters.</param>
    public LatestQuoteReader(
        ILatestQuoteStore store,
        IOptions<MarketDataOptions> market,
        TimeProvider clock,
        HostTelemetry telemetry)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(market);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(telemetry);
        _store = store;
        _market = market.Value;
        _clock = clock;
        _telemetry = telemetry;
    }

    /// <summary>The latest stored quote for one contract, or null when the table has none.</summary>
    /// <param name="venue">The venue id.</param>
    /// <param name="contractId">The contract id.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The quote and its age. Null when nothing is stored.</returns>
    /// <exception cref="ArgumentException">The venue or the contract id is blank.</exception>
    public async Task<LatestQuoteReading?> ReadAsync(
        string venue,
        string contractId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(venue) || string.IsNullOrWhiteSpace(contractId))
        {
            _telemetry.QuoteRead(QuoteReadOutcome.Refused);
            throw new ArgumentException("A latest-quote read needs a venue and a contract id.");
        }

        LatestQuoteDraft? row = await _store.FindAsync(venue, contractId, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            _telemetry.QuoteRead(QuoteReadOutcome.Miss);
            return null;
        }

        TimeSpan age = _clock.GetUtcNow() - row.ReceivedAt;
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        bool stale = age > _market.LatestQuoteStaleAfter;
        _telemetry.QuoteRead(stale ? QuoteReadOutcome.Stale : QuoteReadOutcome.Fresh);
        return new LatestQuoteReading(row, age, stale);
    }
}
