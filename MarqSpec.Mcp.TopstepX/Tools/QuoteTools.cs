using System.ComponentModel;
using MarqSpec.Mcp.TopstepX.MarketData;
using ModelContextProtocol.Server;

namespace MarqSpec.Mcp.TopstepX.Tools;

/// <summary>
/// <c>get_quote</c> — the latest stored bid and ask for a symbol.
/// </summary>
/// <remarks>
/// The recorder writes the row. This tool only reads it. It does not open a market hub and it
/// does not search the venue. There is no <see cref="McpServerToolTypeAttribute"/>: the assembly
/// scan would publish the tool while <see cref="Configuration.MarketDataOptions.GetQuoteEnabled"/>
/// is off. <c>Program</c> calls <c>WithTools</c> only when that switch is on.
/// </remarks>
/// <param name="reader">The stored-quote read.</param>
public sealed class QuoteTools(LatestQuoteReader reader)
{
    private readonly LatestQuoteReader _reader = reader;

    /// <summary>Reads the latest stored quote for a symbol.</summary>
    /// <param name="symbol">The instrument symbol.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The quote, or a miss, or an unknown symbol. None of those throw.</returns>
    [McpServerTool(ReadOnly = true, Idempotent = true, Title = "Get quote")]
    [Description(
        "Reads the latest stored bid, ask and last for an instrument. Prices are raw decimals in the "
        + "contract's price units, not tick-rounded. `lastAt` is when that last was seen. "
        + "`volume` is session volume and is omitted unless a quote carried it, which is the usual case. "
        + "`ageSeconds` dates the bid and ask, not last or volume. "
        + "`stale` is true when that age is past the server's stale window. A stale quote is still returned. "
        + "status is ok when a row was stored, missing when this server serves the symbol and the "
        + "table has no row, or reports the symbol as not served. status failed means the store "
        + "did not answer, which is not a miss. None of those throw.")]
    public async Task<ToolPayloads.Quote> GetQuote(
        [Description("The instrument symbol, e.g. ES.")] string symbol,
        CancellationToken cancellationToken)
    {
        LatestQuoteReading reading = await _reader.ReadAsync(symbol, cancellationToken).ConfigureAwait(false);
        return new ToolPayloads.Quote(
            reading.Symbol,
            reading.Status,
            reading.ContractId,
            reading.Bid,
            reading.Ask,
            reading.Last,
            reading.LastAt,
            reading.Volume,
            reading.QuoteTimeUtc,
            reading.AgeSeconds,
            reading.Stale,
            reading.Reason);
    }
}
