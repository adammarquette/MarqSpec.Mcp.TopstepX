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
    private DateTimeOffset? _lastFlush;

    /// <summary>Keeps <paramref name="quote"/> as the pending row for its contract.</summary>
    /// <param name="quote">The quote.</param>
    public void Offer(LatestQuoteDraft quote)
    {
        ArgumentNullException.ThrowIfNull(quote);
        lock (_gate)
        {
            _pending[Key(quote)] = quote;
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
        _pending.Clear();
        _lastFlush = now;
        return batch;
    }

    private static string Key(LatestQuoteDraft quote) => quote.Venue + "\n" + quote.ContractId;
}
