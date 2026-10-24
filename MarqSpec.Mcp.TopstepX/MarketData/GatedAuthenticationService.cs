using MarqSpec.Client.ProjectX.Authentication;
using MarqSpec.Client.ProjectX.Exceptions;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// The process's <see cref="IAuthenticationService"/>. A fresh held token is returned as-is. A login
/// (<c>loginKey</c>) runs only when the token is not fresh and <see cref="SessionLoginGate.LoginPermitted"/>
/// is still true.
/// </summary>
/// <remarks>
/// <para>
/// SignalR's hub <c>AccessTokenProvider</c> and the REST <c>AuthenticationHandler</c> both call
/// <see cref="GetAccessTokenAsync"/>. While a probe is in flight, a conflict is suspected, or a stop
/// is latched, an aged token must not refresh: that refresh is the login that disconnects the trading
/// platform. A token that is still fresh is returned so a tool can keep calling with it. When it ages
/// out, the call fails instead of logging in.
/// </para>
/// <para>
/// <see cref="ValidateSessionAsync"/> is not forwarded. The client's implementation clears the cached
/// token on any unsuccessful body, and the next read then calls <c>loginKey</c>. The session probe
/// posts validate itself and does not use this method.
/// </para>
/// </remarks>
public sealed class GatedAuthenticationService : IAuthenticationService
{
    private readonly IAuthenticationService _inner;
    private readonly HeldSession _held;
    private readonly SessionLoginGate _gate;
    private readonly TimeProvider _clock;

    /// <summary>Creates the gate around the venue client's authentication service.</summary>
    /// <param name="inner">The client service that posts <c>loginKey</c>. Not the interface registration.</param>
    /// <param name="held">Where a permitted token, and a validate <c>newToken</c>, are remembered.</param>
    /// <param name="gate">Whether a login is currently allowed.</param>
    /// <param name="clock">The clock the freshness decision uses.</param>
    public GatedAuthenticationService(
        IAuthenticationService inner,
        HeldSession held,
        SessionLoginGate gate,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(clock);
        _inner = inner;
        _held = held;
        _gate = gate;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (_held.TryReadFresh(_clock.GetUtcNow(), out string fresh))
        {
            return fresh;
        }

        if (!_gate.LoginPermitted)
        {
            throw Closed();
        }

        string token = await _inner.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        _held.Remember(token, _clock.GetUtcNow());
        return token;
    }

    /// <inheritdoc />
    public async Task RefreshTokenAsync(CancellationToken cancellationToken = default)
    {
        if (!_gate.LoginPermitted)
        {
            throw Closed();
        }

        await _inner.RefreshTokenAsync(cancellationToken).ConfigureAwait(false);
        if (_gate.LoginPermitted)
        {
            string token = await _inner.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            _held.Remember(token, _clock.GetUtcNow());
        }
    }

    /// <summary>
    /// Does not call the client. Its validate clears the token whenever the body is unsuccessful.
    /// </summary>
    /// <param name="cancellationToken">Ignored. No call is made.</param>
    /// <returns><see langword="false"/>.</returns>
    public Task<bool> ValidateSessionAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    /// <inheritdoc />
    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        _held.Forget();
        await _inner.LogoutAsync(cancellationToken).ConfigureAwait(false);
    }

    private static AuthenticationException Closed() =>
        new("TopstepX login is closed because a session conflict is suspected or this process has stopped. This call will not use loginKey.");
}
