namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// Remembers one gated trading result per caller-supplied idempotency key (ADR-0024 §5, gh#712).
/// </summary>
/// <remarks>
/// A replay of the same account, tool and request hash returns the stored result and does not transmit again.
/// The same key with a different request is a conflict and must not transmit either. Keys are process-local
/// and expire after <see cref="InMemoryTradingIdempotencyStore.Lifetime"/>.
/// </remarks>
public interface ITradingIdempotencyStore
{
    /// <summary>
    /// Holds the key while the caller decides whether to transmit.
    /// </summary>
    /// <param name="idempotencyKey">The caller-supplied key. Blank or overlong keys are not stored.</param>
    /// <param name="accountId">The account on the command.</param>
    /// <param name="tool">The MCP tool name.</param>
    /// <param name="requestHash">The normalised request digest.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>A lease the caller disposes when the attempt has finished.</returns>
    Task<TradingIdempotencyLease> LeaseAsync(
        string idempotencyKey,
        int accountId,
        string tool,
        string requestHash,
        CancellationToken cancellationToken);
}

/// <summary>One held idempotency key.</summary>
public sealed class TradingIdempotencyLease : IAsyncDisposable
{
    private readonly SemaphoreSlim? _mutex;
    private readonly Action<TradingRiskGateResult>? _remember;
    private int _disposed;

    private TradingIdempotencyLease(
        TradingRiskGateResult? prior,
        bool conflicts,
        bool canRemember,
        SemaphoreSlim? mutex,
        Action<TradingRiskGateResult>? remember)
    {
        Prior = prior;
        Conflicts = conflicts;
        CanRemember = canRemember;
        _mutex = mutex;
        _remember = remember;
    }

    /// <summary>The result stored for this exact request, when one is still inside the window.</summary>
    public TradingRiskGateResult? Prior { get; }

    /// <summary>The key is already stored for a different account, tool or request hash.</summary>
    public bool Conflicts { get; }

    /// <summary>The caller may store the result it is about to compute. A hit or a conflict may not.</summary>
    public bool CanRemember { get; }

    /// <summary>A lease that does not participate in idempotency.</summary>
    /// <returns>An unkeyed lease.</returns>
    public static TradingIdempotencyLease Unkeyed() => new(null, false, false, null, null);

    /// <summary>A lease that holds <paramref name="mutex"/> until disposed.</summary>
    /// <param name="mutex">The key lock. Released on dispose.</param>
    /// <param name="prior">The stored result, or <see langword="null"/>.</param>
    /// <param name="conflicts">Whether the key belongs to a different request.</param>
    /// <param name="canRemember">Whether <see cref="Remember"/> may store a result.</param>
    /// <param name="remember">Stores the result while the lock is held.</param>
    /// <returns>The lease.</returns>
    public static TradingIdempotencyLease Held(
        SemaphoreSlim mutex,
        TradingRiskGateResult? prior,
        bool conflicts,
        bool canRemember,
        Action<TradingRiskGateResult>? remember) =>
        new(prior, conflicts, canRemember, mutex, remember);

    /// <summary>Stores <paramref name="result"/> for this key. A no-op when <see cref="CanRemember"/> is false.</summary>
    /// <param name="result">The result to replay next time.</param>
    public void Remember(TradingRiskGateResult result)
    {
        if (CanRemember)
        {
            _remember?.Invoke(result);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _mutex?.Release();
        }

        return ValueTask.CompletedTask;
    }
}
