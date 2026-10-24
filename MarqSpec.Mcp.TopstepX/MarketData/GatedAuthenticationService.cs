using MarqSpec.Client.ProjectX.Authentication;
using MarqSpec.Client.ProjectX.Exceptions;
using MarqSpec.Mcp.TopstepX.Telemetry;

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
/// <para>
/// The permitted check and the <c>loginKey</c> call are one critical section. After the call returns,
/// the gate is checked again and a token that lost the race is not remembered and not returned. A
/// <c>loginKey</c> that was already sent cannot be recalled. <see cref="RefreshTokenAsync"/> is the
/// exception: a refresh that has already completed is remembered, so a later read does not log in again.
/// </para>
/// </remarks>
public sealed class GatedAuthenticationService : IAuthenticationService
{
    private readonly IAuthenticationService _inner;
    private readonly HeldSession _held;
    private readonly SessionLoginGate _gate;
    private readonly TimeProvider _clock;
    private readonly HostTelemetry _telemetry;

    /// <summary>Creates the gate around the venue client's authentication service.</summary>
    /// <param name="inner">The client service that posts <c>loginKey</c>. Not the interface registration.</param>
    /// <param name="held">Where a permitted token, and a validate <c>newToken</c>, are remembered.</param>
    /// <param name="gate">Whether a login is currently allowed.</param>
    /// <param name="clock">The clock the freshness decision uses. The same instance <paramref name="held"/> was stored with.</param>
    /// <param name="telemetry">Records a login refusal. The bearer is not a tag.</param>
    public GatedAuthenticationService(
        IAuthenticationService inner,
        HeldSession held,
        SessionLoginGate gate,
        TimeProvider clock,
        HostTelemetry telemetry)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(telemetry);
        _inner = inner;
        _held = held;
        _gate = gate;
        _clock = clock;
        _telemetry = telemetry;
    }

    /// <inheritdoc />
    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (_held.TryReadFresh(_clock, out string fresh))
        {
            return fresh;
        }

        await _gate.EnterLoginAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_held.TryReadFresh(_clock, out fresh))
            {
                return fresh;
            }

            if (!_gate.LoginPermitted)
            {
                throw Closed();
            }

            string token = await _inner.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);

            // The loginKey call, if it happened, is already on the wire. A suspicion or a stop that
            // arrived while it was in flight wins: do not remember the token and do not return it.
            if (!_gate.LoginPermitted)
            {
                _held.Forget();
                throw Closed();
            }

            _held.Remember(token, _clock);
            return token;
        }
        finally
        {
            _gate.ExitLogin();
        }
    }

    /// <inheritdoc />
    public async Task RefreshTokenAsync(CancellationToken cancellationToken = default)
    {
        await _gate.EnterLoginAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_gate.LoginPermitted)
            {
                throw Closed();
            }

            await _inner.RefreshTokenAsync(cancellationToken).ConfigureAwait(false);

            // The refresh already happened. Remember its token even if the gate closed during the call,
            // so the next read is a cache hit and does not post loginKey a second time.
            string token = await _inner.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            _held.Remember(token, _clock);
        }
        finally
        {
            _gate.ExitLogin();
        }
    }

    /// <summary>
    /// Does not call the client. Its validate clears the token whenever the body is unsuccessful.
    /// </summary>
    /// <param name="cancellationToken">Ignored. No call is made.</param>
    /// <returns>This method does not return.</returns>
    /// <exception cref="NotSupportedException">Always. The session probe posts validate itself.</exception>
    public Task<bool> ValidateSessionAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "ValidateSessionAsync is not supported. The session probe posts /api/Auth/validate itself. "
            + "The venue client's validate clears its cache on an unsuccessful body, and the next read would call loginKey.");

    /// <inheritdoc />
    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        _held.Forget();
        await _inner.LogoutAsync(cancellationToken).ConfigureAwait(false);
    }

    private AuthenticationException Closed()
    {
        using (_telemetry.SessionRenewalRefusal(SessionRenewalRefusal.Closed))
        {
        }

        return new AuthenticationException(
            "TopstepX login is closed because a session conflict is suspected or this process has stopped. This call will not use loginKey.");
    }
}
