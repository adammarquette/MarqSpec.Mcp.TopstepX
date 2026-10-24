using System.Collections.Concurrent;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// Process-local idempotency store. A replay inside <see cref="Lifetime"/> returns the original result
/// and does not transmit again (gh#712).
/// </summary>
/// <remarks>
/// Registered as a singleton so two requests in one process share it. A restart forgets every key, which is
/// the limit of an in-memory store: the next call with that key is a new attempt. The audit column that
/// carries the key is <c>varchar(256)</c>, so a longer key is not stored.
/// </remarks>
public sealed class InMemoryTradingIdempotencyStore(TimeProvider clock) : ITradingIdempotencyStore
{
    /// <summary>How long a stored result is replayed before the key may be used again.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    /// <summary>Matches <c>TradingAuditLog.IdempotencyKey</c>.</summary>
    public const int MaxKeyLength = 256;

    private readonly TimeProvider _clock = clock;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<TradingIdempotencyLease> LeaseAsync(
        string idempotencyKey,
        int accountId,
        string tool,
        string requestHash,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > MaxKeyLength)
        {
            return Task.FromResult(TradingIdempotencyLease.Unkeyed());
        }

        return LeaseCoreAsync(idempotencyKey, accountId, tool, requestHash, cancellationToken);
    }

    private async Task<TradingIdempotencyLease> LeaseCoreAsync(
        string idempotencyKey,
        int accountId,
        string tool,
        string requestHash,
        CancellationToken cancellationToken)
    {
        SemaphoreSlim mutex = _locks.GetOrAdd(idempotencyKey, static _ => new SemaphoreSlim(1, 1));
        await mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DateTimeOffset now = _clock.GetUtcNow();
            if (_entries.TryGetValue(idempotencyKey, out Entry? existing))
            {
                if (now - existing.StoredAt >= Lifetime)
                {
                    _entries.TryRemove(idempotencyKey, out _);
                }
                else if (existing.AccountId == accountId
                    && string.Equals(existing.Tool, tool, StringComparison.Ordinal)
                    && string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
                {
                    return TradingIdempotencyLease.Held(
                        mutex,
                        existing.Result,
                        conflicts: false,
                        canRemember: false,
                        remember: null);
                }
                else
                {
                    return TradingIdempotencyLease.Held(
                        mutex,
                        prior: null,
                        conflicts: true,
                        canRemember: false,
                        remember: null);
                }
            }

            void Remember(TradingRiskGateResult result) =>
                _entries[idempotencyKey] = new Entry(accountId, tool, requestHash, result, _clock.GetUtcNow());

            return TradingIdempotencyLease.Held(
                mutex,
                prior: null,
                conflicts: false,
                canRemember: true,
                remember: Remember);
        }
        catch
        {
            mutex.Release();
            throw;
        }
    }

    private sealed record Entry(
        int AccountId,
        string Tool,
        string RequestHash,
        TradingRiskGateResult Result,
        DateTimeOffset StoredAt);
}
