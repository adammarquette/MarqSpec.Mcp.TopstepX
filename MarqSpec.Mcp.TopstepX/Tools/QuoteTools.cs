using System.ComponentModel;
using MarqSpec.Mcp.TopstepX.MarketData;
using ModelContextProtocol.Server;

namespace MarqSpec.Mcp.TopstepX.Tools;

/// <summary>
/// <c>get_quote</c> — the latest bid and ask from the market hub.
/// </summary>
/// <remarks>
/// There is no REST quote. The number is the cached <c>GatewayQuote</c> for the resolved
/// front contract, or a refusal. A bar close is never substituted (gh#351).
/// </remarks>
/// <param name="quotes">The live cache. It shares the tape recorder's hub.</param>
[McpServerToolType]
public sealed class QuoteTools(LiveQuoteService quotes)
{
    private readonly LiveQuoteService _quotes = quotes;

    /// <summary>Reads the latest live quote for one configured instrument.</summary>
    /// <param name="symbol">The instrument symbol.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The cached quote, with age and whether the hub is up.</returns>
    [McpServerTool(ReadOnly = true, Idempotent = true, Title = "Get quote")]
    [Description(
        "The latest live bid, ask and last from the SignalR market hub. There is no REST quote, "
        + "and this tool never answers from a bar close. bid, ask, last and volume are the quote's "
        + "numbers. timestamp is absent when the quote carried no time. ageSeconds is how old it is. "
        + "stale is true when the hub is down or the age is past MarketData__QuoteStaleAfter. "
        + "bidSize and askSize are absent: the quote event carries no size, and a missing size is "
        + "not zero. No quote before MarketData__QuoteWait refuses. Stdio does not subscribe. "
        + "MarketData__ServeQuotes is off unless set. HTTP is not consent. The hub the tape recorder "
        + "already holds is reused; this tool does not open a second one while RecordTape is on.")]
    public Task<ToolPayloads.Quote> GetQuote(
        [Description("The instrument symbol, e.g. ES.")] string symbol,
        CancellationToken cancellationToken) =>
        _quotes.ReadAsync(symbol, cancellationToken);
}
