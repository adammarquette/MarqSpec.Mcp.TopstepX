namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// Per-contract retry state for a refused quote subscribe.
/// </summary>
/// <remarks>
/// The hub thread and the flush loop both call this. Every access takes <see cref="_gate"/>,
/// and callers never see the dictionary. A disconnect can clear the set while a retry is due.
/// </remarks>
internal sealed class LatestQuoteSubscribeRetries
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);

    /// <summary>True when <paramref name="contractId"/> is refused and its next try is still ahead.</summary>
    /// <param name="contractId">The contract.</param>
    /// <param name="now">The clock.</param>
    /// <returns>True when the loop should not subscribe it yet.</returns>
    public bool IsWaiting(string contractId, DateTimeOffset now)
    {
        lock (_gate)
        {
            return _pending.TryGetValue(contractId, out Pending? pending) && now < pending.NextAttempt;
        }
    }

    /// <summary>
    /// Contracts whose retry time has arrived. A snapshot, so a later clear cannot break it.
    /// </summary>
    /// <param name="now">The clock.</param>
    /// <returns>The due contracts.</returns>
    public IReadOnlyList<DueRetry> Due(DateTimeOffset now)
    {
        lock (_gate)
        {
            return
            [
                .. _pending
                    .Where(pair => now >= pair.Value.NextAttempt)
                    .Select(pair => new DueRetry(pair.Key, pair.Value.Instrument)),
            ];
        }
    }

    /// <summary>Records a refusal. The wait starts at <paramref name="initial"/> and doubles.</summary>
    /// <param name="contractId">The contract.</param>
    /// <param name="instrument">The symbol, for the log.</param>
    /// <param name="now">The clock.</param>
    /// <param name="initial">The first wait. Also the wait after a recovery.</param>
    /// <param name="maximum">The longest wait.</param>
    /// <returns>True when this is the first refusal since the last recovery or clear.</returns>
    public bool NoteRefused(
        string contractId,
        string instrument,
        DateTimeOffset now,
        TimeSpan initial,
        TimeSpan maximum)
    {
        lock (_gate)
        {
            if (_pending.TryGetValue(contractId, out Pending? pending))
            {
                pending.Backoff = Grow(pending.Backoff, maximum);
                pending.NextAttempt = now + pending.Backoff;
                return false;
            }

            _pending[contractId] = new Pending
            {
                Instrument = instrument,
                Backoff = initial,
                NextAttempt = now + initial,
            };
            return true;
        }
    }

    /// <summary>Drops a contract that subscribed. The symbol, or null if it was not refused.</summary>
    /// <param name="contractId">The contract.</param>
    /// <returns>The symbol that recovered, or null.</returns>
    public string? NoteRecovered(string contractId)
    {
        lock (_gate)
        {
            return _pending.Remove(contractId, out Pending? pending) ? pending.Instrument : null;
        }
    }

    /// <summary>The current wait for <paramref name="contractId"/>, or null when it is not refused.</summary>
    /// <param name="contractId">The contract.</param>
    /// <returns>The wait.</returns>
    public TimeSpan? Backoff(string contractId)
    {
        lock (_gate)
        {
            return _pending.TryGetValue(contractId, out Pending? pending) ? pending.Backoff : null;
        }
    }

    /// <summary>Drops refused contracts that are no longer the front.</summary>
    /// <param name="wanted">The contracts that are still fronts.</param>
    public void ForgetExcept(IReadOnlyCollection<string> wanted)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        lock (_gate)
        {
            foreach (string contractId in _pending.Keys.Where(id => !wanted.Contains(id)).ToArray())
            {
                _pending.Remove(contractId);
            }
        }
    }

    /// <summary>Drops every refusal. The hub thread calls this on disconnect.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _pending.Clear();
        }
    }

    private static TimeSpan Grow(TimeSpan current, TimeSpan maximum)
    {
        long ticks = current.Ticks * 2;
        return ticks >= maximum.Ticks ? maximum : TimeSpan.FromTicks(ticks);
    }

    /// <summary>One contract the loop should subscribe again.</summary>
    /// <param name="ContractId">The contract.</param>
    /// <param name="Instrument">The symbol.</param>
    internal readonly record struct DueRetry(string ContractId, string Instrument);

    private sealed class Pending
    {
        public required string Instrument { get; init; }

        public TimeSpan Backoff { get; set; }

        public DateTimeOffset NextAttempt { get; set; }
    }
}
