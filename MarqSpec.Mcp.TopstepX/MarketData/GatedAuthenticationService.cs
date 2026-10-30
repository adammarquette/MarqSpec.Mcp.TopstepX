using MarqSpec.Client.ProjectX.Authentication;
using MarqSpec.Client.ProjectX.Exceptions;
using MarqSpec.Mcp.TopstepX.Telemetry;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// The process's <see cref="IAuthenticationService"/>. A fresh held token is returned as-is. A login
/// (<c>loginKey</c>) runs only when the token is not fresh and the gate still allows it.
/// </summary>
/// <remarks>
/// <para>
/// SignalR's hub <c>AccessTokenProvider</c> and the REST <c>AuthenticationHandler</c> both call
/// <see cref="GetAccessTokenAsync"/>. While a probe is in flight, a conflict is suspected, or a stop
/// is latched, an aged token must not refresh: that refresh is the login that disconnects the trading
/// platform. A token that is still fresh is returned so a tool can keep calling with it. When it ages
/// out, the call fails instead of logging in. A renewal backoff is the exception once the token is
/// inside the client's refresh buffer: that read may recover without waiting for the renewer's next
/// validate.
/// </para>
/// <para>
/// A recovery is armed when validate reports <c>ExpiredToken</c> or the held token is inside its
/// refresh window after a failed renewal. The first attempt runs immediately and calls
/// <see cref="IAuthenticationService.RefreshTokenAsync"/>, which is what forces
/// <c>MarqSpec.Client.ProjectX</c> 3.0.0 past its own cache: <c>GetAccessTokenAsync</c> returns the
/// cached bearer while that cache is more than one minute from a 55-minute expiry measured at login.
/// A bearer equal to the rejected one is a failed attempt and is not stored. A failure waits on the
/// renewal backoff, so the next REST call does not post <c>loginKey</c> again until that wait has
/// elapsed. The recovery login disconnects whatever session is live on the account.
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

            if (!LoginAllowedForThisRead())
            {
                throw Closed();
            }

            if (ShouldRecover())
            {
                return await RecoverAsync(cancellationToken).ConfigureAwait(false);
            }

            string token = await _inner.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);

            if (_held.TryReadFresh(_clock, out fresh))
            {
                return fresh;
            }

            // The loginKey call, if it happened, is already on the wire. A suspicion or a stop that
            // arrived while it was in flight wins: do not remember the token and do not return it.
            if (!LoginAllowedForThisRead())
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

            if (_gate.RecoveryArmed)
            {
                await RecoverAsync(cancellationToken).ConfigureAwait(false);
                return;
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

    /// <summary>
    /// Posts one budgeted recovery. <see cref="IAuthenticationService.RefreshTokenAsync"/> runs first so
    /// the venue client's cache cannot hand back the rejected bearer without a <c>loginKey</c>.
    /// </summary>
    /// <param name="cancellationToken">Cancels the venue call. A cancel does not count as a failed login.</param>
    /// <returns>The new bearer.</returns>
    private async Task<string> RecoverAsync(CancellationToken cancellationToken)
    {
        if (_held.TryReadFresh(_clock, out string fresh))
        {
            _gate.NoteRecoverySuccess();
            _gate.EndRenewalBackoff();
            return fresh;
        }

        if (!_gate.RecoveryAttemptPermitted(_clock) || !LoginAllowedForThisRead())
        {
            throw Closed();
        }

        string token;
        try
        {
            await _inner.RefreshTokenAsync(cancellationToken).ConfigureAwait(false);
            token = await _inner.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _gate.NoteRecoveryFailure(_clock);
            RecordRecovery(SessionRecoveryLogin.Failed);
            throw;
        }

        if (_held.TryReadFresh(_clock, out fresh))
        {
            _gate.NoteRecoverySuccess();
            _gate.EndRenewalBackoff();
            return fresh;
        }

        if (_gate.IsRejectedToken(token))
        {
            _gate.NoteRecoveryFailure(_clock);
            RecordRecovery(SessionRecoveryLogin.CachedToken);
            throw new AuthenticationException(
                "TopstepX returned the bearer this process already rejected. This call will not store it, and it will not call loginKey again until the recovery backoff has elapsed.");
        }

        if (!LoginAllowedForThisRead())
        {
            _gate.NoteRecoveryFailure(_clock);
            throw Closed();
        }

        _gate.NoteRecoverySuccess();
        _gate.EndRenewalBackoff();
        RecordRecovery(SessionRecoveryLogin.Succeeded);
        _held.Remember(token, _clock);
        return token;
    }

    /// <summary>
    /// A stale token whose only close is a renewal backoff may log in at the 54-minute cliff.
    /// Suspecting, an in-flight probe, and a latched stop stay closed.
    /// </summary>
    /// <returns><see langword="true"/> when this read may post <c>loginKey</c>.</returns>
    private bool LoginAllowedForThisRead() => _gate.LoginPermitted || BackoffOnly();

    /// <summary>Whether renewal backoff is the only reason login is closed.</summary>
    /// <returns><see langword="true"/> when a stale token may recover.</returns>
    private bool BackoffOnly() =>
        _gate.RenewalBackingOff && !_gate.StopLatched && !_gate.Suspecting && !_gate.ProbeInFlight;

    /// <summary>
    /// Whether this read is a budgeted recovery. A stale token during a renewal backoff is armed
    /// against the bearer already held, so the cliff does not wait for the next validate.
    /// </summary>
    /// <returns><see langword="true"/> when <see cref="RecoverAsync"/> must run.</returns>
    private bool ShouldRecover()
    {
        if (_gate.RecoveryArmed)
        {
            return true;
        }

        if (BackoffOnly() && _held.TryRead(out string stale))
        {
            _gate.ArmRecovery(stale);
            return true;
        }

        return false;
    }

    private AuthenticationException Closed()
    {
        using (_telemetry.SessionRenewalRefusal(RefusalReason()))
        {
        }

        return new AuthenticationException(
            "TopstepX login is closed because a session conflict is suspected or this process has stopped. This call will not use loginKey.");
    }

    private string RefusalReason()
    {
        if (_gate.StopLatched)
        {
            return SessionRenewalRefusal.ClosedLatched;
        }

        if (_gate.Suspecting)
        {
            return SessionRenewalRefusal.ClosedSuspecting;
        }

        if (_gate.ProbeInFlight)
        {
            return SessionRenewalRefusal.ClosedProbe;
        }

        return SessionRenewalRefusal.ClosedBackoff;
    }

    private void RecordRecovery(string outcome)
    {
        using (_telemetry.SessionRecoveryLogin(outcome))
        {
        }
    }
}
