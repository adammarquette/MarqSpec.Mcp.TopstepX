namespace MarqSpec.Mcp.TopstepX.Domain.MarketData;

/// <summary>
/// Binds a tape-derived <see cref="VolumeProfile"/> around an <see cref="ILevelMethod.Detect"/> call.
/// </summary>
/// <remarks>
/// <para>
/// This is the fourth path gh#319 had to name. <see cref="ILevelMethod.Detect"/> cannot see cells;
/// they are request-scoped. The three alternatives already refused on that interface stay refused:
/// Detect is not widened, <see cref="KeyLevelOptions"/> does not carry a tape, and a POC derived
/// from bar volume is a spreading rule. Constructor injection is for process-lifetime values (the
/// session calendar). A profile is a fact about this request's window.
/// </para>
/// <para>
/// Binding around the call keeps every volume method constructible without a tape, so they stay
/// inside <c>LevelMethodCatalog.All</c> and the roll / family / ordering sweeps still see them.
/// Detect reads the bound profile after the roll and ordering guards. A Detect that sees no bind
/// refuses rather than answering from OHLCV.
/// </para>
/// </remarks>
public sealed class VolumeProfileScope : IDisposable
{
    private static readonly AsyncLocal<VolumeProfile?> _bound = new();

    private readonly VolumeProfile? _previous;
    private bool _disposed;

    /// <summary>Binds <paramref name="profile"/> for the current asynchronous flow.</summary>
    /// <param name="profile">The tape-derived profile this request computed.</param>
    public VolumeProfileScope(VolumeProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _previous = _bound.Value;
        _bound.Value = profile;
    }

    /// <summary>
    /// The profile bound for this request, or a refusal that names the spreading rule.
    /// </summary>
    /// <exception cref="InvalidOperationException">Nothing is bound.</exception>
    public static VolumeProfile Require() =>
        _bound.Value ?? throw new InvalidOperationException(
            "Volume-derived levels need a tape-derived profile bound for this request. "
            + "A point of control computed from bars is a spreading rule (gh#319).");

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _bound.Value = _previous;
        _disposed = true;
    }
}
