namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// Collapses a burst of quotes to the latest one per contract, and releases them on a timer.
/// </summary>
/// <remarks>
/// The store sees at most one row per contract per flush. Quotes that arrive inside the gap
/// replace the pending row and are not written yet.
/// </remarks>
public sealed class LatestQuoteCoalescer
{
    private readonly object _gate = new();
    private readonly Dictionary<string, LatestQuoteDraft> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LatestQuoteDraft> _seen = new(StringComparer.Ordinal);
    private DateTimeOffset? _lastFlush;

    /// <summary>Keeps <paramref name="quote"/> as the pending row for its contract.</summary>
    /// <param name="quote">The quote.</param>
    public void Offer(LatestQuoteDraft quote)
    {
        ArgumentNullException.ThrowIfNull(quote);
        lock (_gate)
        {
            string key = Key(quote);
            if (!_pending.TryGetValue(key, out LatestQuoteDraft? prior))
            {
                _seen.TryGetValue(key, out prior);
            }

            // A trade arrives without a bid. It must not invent a row of zeros.
            if (prior is null && quote.Bid == 0 && quote.Ask == 0)
            {
                return;
            }

            _pending[key] = prior is { } kept ? Merge(kept, quote) : quote;
        }
    }

    /// <summary>
    /// Puts a failed flush back, without replacing a quote that arrived while the flush was in flight.
    /// </summary>
    /// <param name="quotes">The batch the store did not keep.</param>
    public void Reoffer(IReadOnlyList<LatestQuoteDraft> quotes)
    {
        ArgumentNullException.ThrowIfNull(quotes);
        lock (_gate)
        {
            foreach (LatestQuoteDraft quote in quotes)
            {
                string key = Key(quote);
                if (!_pending.ContainsKey(key))
                {
                    _pending[key] = quote;
                }
            }
        }
    }

    /// <summary>
    /// The pending rows, when a flush is due. Empty when nothing is pending or the gap has not elapsed.
    /// </summary>
    /// <param name="now">The clock.</param>
    /// <param name="interval">The minimum gap since the previous flush.</param>
    /// <returns>The rows to write. The pending set is cleared when this is non-empty.</returns>
    public IReadOnlyList<LatestQuoteDraft> TakeIfDue(DateTimeOffset now, TimeSpan interval)
    {
        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                return [];
            }

            if (_lastFlush is { } last && now - last < interval)
            {
                return [];
            }

            return TakeUnderLock(now);
        }
    }

    /// <summary>Every pending row, ignoring the gap. Used when the writer stops.</summary>
    /// <param name="now">The clock, recorded as the flush time.</param>
    /// <returns>The rows to write.</returns>
    public IReadOnlyList<LatestQuoteDraft> TakeAll(DateTimeOffset now)
    {
        lock (_gate)
        {
            return TakeUnderLock(now);
        }
    }

    private List<LatestQuoteDraft> TakeUnderLock(DateTimeOffset now)
    {
        List<LatestQuoteDraft> batch = [.. _pending.Values];
        foreach (LatestQuoteDraft quote in batch)
        {
            _seen[Key(quote)] = quote;
        }

        _pending.Clear();
        _lastFlush = now;
        return batch;
    }

    /// <summary>
    /// A zero last or volume on a later quote is a field the payload left out, not a new
    /// price. Bid and ask of zero on a trade are the same. A positive value replaces.
    /// </summary>
    private static LatestQuoteDraft Merge(LatestQuoteDraft prior, LatestQuoteDraft next) =>
        next with
        {
            Bid = next.Bid != 0 ? next.Bid : prior.Bid,
            Ask = next.Ask != 0 ? next.Ask : prior.Ask,
            Last = next.Last != 0 ? next.Last : prior.Last,
            Volume = next.Volume != 0 ? next.Volume : prior.Volume,
            QuoteTimeUtc = next.QuoteTimeUtc ?? prior.QuoteTimeUtc,
        };

    private static string Key(LatestQuoteDraft quote) => quote.Venue + "\n" + quote.ContractId;
}
