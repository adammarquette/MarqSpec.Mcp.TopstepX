using ProjectXWebSocketOptions = MarqSpec.Client.ProjectX.Configuration.WebSocketOptions;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// Closes every TopstepX login while a session conflict is suspected or has already stopped the recorder,
/// and holds the venue client's automatic reconnect off without forgetting the operator's setting.
/// </summary>
/// <remarks>
/// <para>
/// The client's <c>ReconnectPolicy</c> is internal. It reads <see cref="ProjectXWebSocketOptions.AutoReconnect"/>
/// on every <c>NextRetryDelay</c> and returns no delay when the flag is false (Client gh#69). SignalR asks
/// for that delay and arms the timer <b>before</b> <c>Reconnecting</c> fires, so turning the flag off inside
/// the disconnect handler is already too late for the first retry. The recorder turns the flag off before
/// <c>Connect</c>, and again when a probe starts.
/// </para>
/// <para>
/// The flag is put back only when this process is leaving without a latched stop, and it is put back to the
/// value it had, including <see langword="false"/>. A latched stop is one-way: a probe that later comes back
/// valid does not turn reconnect on again. Clearing a suspicion, ending a probe, or ending a renewal
/// backoff does not clear it.
/// </para>
/// <para>
/// Logins take <see cref="EnterLoginAsync"/> and hold it across the <c>loginKey</c> call, then re-check
/// <see cref="LoginPermitted"/> before the token is remembered. <see cref="BeginSuspecting"/> sets the
/// suspicion and returns without waiting: the SignalR callback that calls it must not block for an HTTP
/// timeout. The in-flight login observes the suspicion on its re-check and discards its result. A
/// <c>loginKey</c> request that has already been sent cannot be recalled. That window is irreducible:
/// the call may still have opened a session at the venue, and this process does not return or remember
/// that token.
/// </para>
/// </remarks>
public sealed class SessionLoginGate
{
    private readonly object _gate = new();
    private readonly object _recovery = new();
    private readonly SemaphoreSlim _login = new(1, 1);
    private readonly TaskCompletionSource _latched = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _stopLatched;
    private int _probeInFlight;
    private int _suspecting;
    private int _renewalBackingOff;
    private int _recoveryArmed;
    private int _recoveryFailures;
    private long _recoveryFailedAt;
    private bool _recoveryHasFailure;
    private string? _rejectedToken;
    private int _priorSaved;
    private bool _priorAutoReconnect;

    /// <summary>A conflict stop has been decided. Login stays closed.</summary>
    public bool StopLatched => Volatile.Read(ref _stopLatched) != 0;

    /// <summary>A validate probe is running. A second disconnect must not start another one.</summary>
    public bool ProbeInFlight => Volatile.Read(ref _probeInFlight) != 0;

    /// <summary>
    /// A hub drop is waiting on validate. Reconnect and login stay closed until the probe is valid
    /// and the stop latch is still clear.
    /// </summary>
    public bool Suspecting => Volatile.Read(ref _suspecting) != 0;

    /// <summary>
    /// A renewal validate failed while the held token was still fresh. Login stays closed until a later
    /// validate succeeds, until validate reports a token that can never become valid, or until the held
    /// token is inside its refresh buffer. This is not the one-way stop: <see cref="EndRenewalBackoff"/>
    /// clears it unless <see cref="StopLatched"/> is set.
    /// </summary>
    public bool RenewalBackingOff => Volatile.Read(ref _renewalBackingOff) != 0;

    /// <summary>Completes when <see cref="LatchStop"/> runs. The recorder watches this.</summary>
    public Task Latched => _latched.Task;

    /// <summary>Whether a call may post <c>loginKey</c>. Fresh held tokens are still readable.</summary>
    public bool LoginPermitted => !StopLatched && !ProbeInFlight && !Suspecting && !RenewalBackingOff;

    /// <summary>Latches the stop. Later clears and restores do not undo it.</summary>
    public void LatchStop()
    {
        if (Interlocked.Exchange(ref _stopLatched, 1) == 0)
        {
            _latched.TrySetResult();
        }
    }

    /// <summary>
    /// Marks a hub drop as unresolved and returns without taking the login lock, so a login that is
    /// already queued keeps it. Login stays closed while the suspicion is set. The in-flight login
    /// discards its token on the re-check after <c>loginKey</c> returns. A <c>loginKey</c> already
    /// on the wire is not recalled.
    /// </summary>
    public void BeginSuspecting() => MarkSuspecting();

    /// <summary>Sets the suspicion without waiting. <see cref="BeginSuspecting"/> does not block either.</summary>
    internal void MarkSuspecting() => Interlocked.Exchange(ref _suspecting, 1);

    /// <summary>Closes login after a renewal validate failed. A latched stop is left latched.</summary>
    public void BeginRenewalBackoff() => Interlocked.Exchange(ref _renewalBackingOff, 1);

    /// <summary>Opens login after a renewal validate succeeded, unless the stop is already latched.</summary>
    public void EndRenewalBackoff()
    {
        if (StopLatched)
        {
            return;
        }

        Interlocked.Exchange(ref _renewalBackingOff, 0);
    }

    /// <summary>
    /// A recovery <c>loginKey</c> is armed against a bearer validate has already rejected.
    /// The first attempt may run immediately. A failure waits on <see cref="HeldSessionRenewal.NextDelay"/>.
    /// </summary>
    internal bool RecoveryArmed => Volatile.Read(ref _recoveryArmed) != 0;

    /// <summary>
    /// Arms a recovery against <paramref name="rejectedToken"/>. Does not reset a failure already counted,
    /// so a later <c>ExpiredToken</c> cannot open a tight login loop. The bearer is not logged.
    /// </summary>
    /// <param name="rejectedToken">The bearer that must not be stored again. Empty is ignored.</param>
    internal void ArmRecovery(string? rejectedToken)
    {
        if (string.IsNullOrEmpty(rejectedToken))
        {
            return;
        }

        lock (_recovery)
        {
            _rejectedToken = rejectedToken;
            _recoveryArmed = 1;
        }
    }

    /// <summary>
    /// Whether an armed recovery may call the venue now. An unarmed gate returns <see langword="true"/>:
    /// the first connect is not budgeted.
    /// </summary>
    /// <param name="clock">The clock that stamped <see cref="NoteRecoveryFailure"/>.</param>
    /// <returns><see langword="false"/> when the last failure is still inside its backoff.</returns>
    internal bool RecoveryAttemptPermitted(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (Volatile.Read(ref _recoveryArmed) == 0)
        {
            return true;
        }

        lock (_recovery)
        {
            if (_recoveryArmed == 0 || !_recoveryHasFailure)
            {
                return true;
            }

            TimeSpan elapsed = clock.GetElapsedTime(_recoveryFailedAt);
            return elapsed >= HeldSessionRenewal.NextDelay(_recoveryFailures, 0);
        }
    }

    /// <summary>Whether <paramref name="token"/> is the bearer this recovery already rejected.</summary>
    /// <param name="token">A bearer the client just returned. Empty is not a match. Not logged.</param>
    /// <returns><see langword="true"/> when storing it would keep the dead session.</returns>
    internal bool IsRejectedToken(string? token)
    {
        if (string.IsNullOrEmpty(token) || Volatile.Read(ref _recoveryArmed) == 0)
        {
            return false;
        }

        lock (_recovery)
        {
            return _recoveryArmed != 0
                && string.Equals(_rejectedToken, token, StringComparison.Ordinal);
        }
    }

    /// <summary>Counts a recovery that did not produce a new bearer, and starts its backoff.</summary>
    /// <param name="clock">The clock the next <see cref="RecoveryAttemptPermitted"/> reads.</param>
    internal void NoteRecoveryFailure(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        lock (_recovery)
        {
            _recoveryArmed = 1;
            if (_recoveryFailures < 16)
            {
                _recoveryFailures++;
            }

            _recoveryFailedAt = clock.GetTimestamp();
            _recoveryHasFailure = true;
        }
    }

    /// <summary>Clears an armed recovery after a new bearer was stored, or after validate renewed one.</summary>
    internal void NoteRecoverySuccess()
    {
        lock (_recovery)
        {
            _recoveryArmed = 0;
            _recoveryFailures = 0;
            _recoveryHasFailure = false;
            _recoveryFailedAt = 0;
            _rejectedToken = null;
        }
    }

    /// <summary>Holds the login lock across one <c>loginKey</c> or refresh call.</summary>
    /// <param name="cancellationToken">Cancels the wait. The lock is not taken when this throws.</param>
    internal async Task EnterLoginAsync(CancellationToken cancellationToken) =>
        await _login.WaitAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Releases the lock taken by <see cref="EnterLoginAsync"/>.</summary>
    internal void ExitLogin() => _login.Release();

    /// <summary>Clears the suspicion after a valid probe, unless the stop is already latched.</summary>
    public void ClearSuspecting()
    {
        if (StopLatched)
        {
            return;
        }

        Interlocked.Exchange(ref _suspecting, 0);
    }

    /// <summary>Starts the single in-flight probe. Fails when a stop is latched or a probe is already running.</summary>
    /// <returns><see langword="true"/> when this caller owns the probe.</returns>
    public bool TryBeginProbe()
    {
        if (StopLatched)
        {
            return false;
        }

        return Interlocked.CompareExchange(ref _probeInFlight, 1, 0) == 0;
    }

    /// <summary>Releases the in-flight probe so a later re-probe can start. Does not clear a stop or a suspicion.</summary>
    public void EndProbe() => Interlocked.Exchange(ref _probeInFlight, 0);

    /// <summary>
    /// Remembers <see cref="ProjectXWebSocketOptions.AutoReconnect"/> the first time and then forces it off.
    /// A later call does not overwrite the remembered value.
    /// </summary>
    /// <param name="options">The options instance the venue client's policy already holds.</param>
    public void SuppressAutomaticRetry(ProjectXWebSocketOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (_gate)
        {
            if (Interlocked.CompareExchange(ref _priorSaved, 1, 0) == 0)
            {
                _priorAutoReconnect = options.AutoReconnect;
            }

            options.AutoReconnect = false;
        }
    }

    /// <summary>
    /// Puts <see cref="ProjectXWebSocketOptions.AutoReconnect"/> back to the value
    /// <see cref="SuppressAutomaticRetry"/> remembered. A latched stop leaves the flag off.
    /// </summary>
    /// <param name="options">The same options instance that was suppressed.</param>
    public void RestorePriorAutoReconnect(ProjectXWebSocketOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (StopLatched || Volatile.Read(ref _priorSaved) == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (StopLatched || Volatile.Read(ref _priorSaved) == 0)
            {
                return;
            }

            options.AutoReconnect = _priorAutoReconnect;
        }
    }

    /// <summary>
    /// The delay the client's policy returns for the first retry: none when automatic reconnect is off,
    /// otherwise the uncapped first step <c>min(InitialReconnectDelaySeconds, MaxReconnectDelaySeconds)</c>.
    /// Copied from <c>MarqSpec.Client.ProjectX</c> 3.0.0 <c>ReconnectPolicy.NextRetryDelay</c>, whose first
    /// step is the initial delay (default one second) and which returns no delay when
    /// <see cref="ProjectXWebSocketOptions.AutoReconnect"/> is false.
    /// </summary>
    /// <param name="options">The options the policy reads.</param>
    /// <returns>No delay when reconnect is off, which is SignalR's signal to stop.</returns>
    public static TimeSpan? FirstAutomaticRetryDelay(ProjectXWebSocketOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.AutoReconnect)
        {
            return null;
        }

        double delaySeconds = Math.Min(
            options.InitialReconnectDelaySeconds,
            options.MaxReconnectDelaySeconds);
        return TimeSpan.FromSeconds(delaySeconds);
    }
}
