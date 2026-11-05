namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// Collapses a burst of quotes to the latest one per contract, and releases them on a timer.
/// </summary>
/// <remarks>
/// The store sees at most one row per contract per flush. Quotes that arrive inside the gap
/// replace the pending row and are not written yet. A flushed row is remembered so a later
/// bid or ask can keep a last from the same session. That memory is not the database: a
/// reconnect clears it, and a retired contract is dropped.
/// </remarks>
public sealed class LatestQuoteCoalescer
{
    private readonly object _gate = new();
    private readonly Dictionary<string, LatestQuoteDraft> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LatestQuoteDraft> _seen = new(StringComparer.Ordinal);
    private DateTimeOffset? _lastFlush;

    /// <summary>Keeps <paramref name="quote"/> as the pending row for its contract.</summary>
    /// <param name="quote">The quote or trade.</param>
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

            // A trade before any quote has no book. It must not invent a row of zeros.
            if (prior is null && !quote.BookKnown)
            {
                return;
            }

            _pending[key] = prior is { } kept ? Merge(kept, quote) : quote;
        }
    }

    /// <summary>Drops one contract from the pending set and from the flushed memory.</summary>
    /// <param name="venue">The venue id.</param>
    /// <param name="contractId">The contract that is no longer the front.</param>
    public void Forget(string venue, string contractId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(venue);
        ArgumentException.ThrowIfNullOrWhiteSpace(contractId);
        string key = venue + "\n" + contractId;
        lock (_gate)
        {
            _pending.Remove(key);
            _seen.Remove(key);
        }
    }

    /// <summary>Drops every contract. A hub reconnect must not keep the book from before the drop.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _pending.Clear();
            _seen.Clear();
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
    /// A zero last or volume is a field the payload left out. A quote's zero bid or ask is that
    /// side gone. A trade does not replace the book. An older trade does not replace a newer last
    /// or move the venue time backwards. A carried last or volume stops at the session boundary
    /// and at <see cref="LatestQuoteCarry.MaxAge"/>.
    /// </summary>
    private static LatestQuoteDraft Merge(LatestQuoteDraft prior, LatestQuoteDraft next)
    {
        bool nextLast = next.Last != 0 && !IsOlder(next.LastAt, prior.LastAt);
        bool keepLast = !nextLast && prior.Last != 0 && LatestQuoteCarry.Keep(
            prior.LastAt, prior.TradeDate, next.ReceivedAt, next.TradeDate);
        bool nextVolume = next.Volume != 0 && !IsOlder(next.VolumeAt, prior.VolumeAt);
        bool keepVolume = !nextVolume && prior.Volume != 0 && LatestQuoteCarry.Keep(
            prior.VolumeAt, prior.TradeDate, next.ReceivedAt, next.TradeDate);

        return next with
        {
            Bid = next.BookKnown ? next.Bid : prior.Bid,
            Ask = next.BookKnown ? next.Ask : prior.Ask,
            Last = nextLast ? next.Last : keepLast ? prior.Last : 0,
            Volume = nextVolume ? next.Volume : keepVolume ? prior.Volume : 0,
            LastAt = nextLast ? next.LastAt : keepLast ? prior.LastAt : null,
            VolumeAt = nextVolume ? next.VolumeAt : keepVolume ? prior.VolumeAt : null,
            QuoteTimeUtc = Later(prior.QuoteTimeUtc, next.QuoteTimeUtc),
            BookKnown = next.BookKnown || prior.BookKnown,
            TradeDate = next.TradeDate,
        };
    }

    private static bool IsOlder(DateTimeOffset? next, DateTimeOffset? prior) =>
        next is not null && prior is not null && next.Value < prior.Value;

    private static DateTimeOffset? Later(DateTimeOffset? prior, DateTimeOffset? next)
    {
        if (prior is null)
        {
            return next;
        }

        if (next is null || next.Value < prior.Value)
        {
            return prior;
        }

        return next;
    }

    private static string Key(LatestQuoteDraft quote) => quote.Venue + "\n" + quote.ContractId;
}
