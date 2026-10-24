namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// The TopstepX bearer this process already holds, remembered when a permitted login returned it
/// and when <c>POST /api/Auth/validate</c> returned a <c>newToken</c>.
/// </summary>
/// <remarks>
/// <para>
/// The venue client's cache is private. <see cref="ISessionConflictProbe"/> must not call
/// <c>GetAccessTokenAsync</c> to read it: that method posts <c>/api/Auth/loginKey</c> once the local
/// cache is inside its last minute, which is about 54 minutes after the token was stored (the client
/// keeps a token for 55 minutes). A probe that logged in to inspect a conflict would be the login
/// that disconnects the trading platform.
/// </para>
/// <para>
/// Freshness uses the same window as that client, measured on <see cref="TimeProvider.GetTimestamp"/>
/// and <see cref="TimeProvider.GetElapsedTime(long)"/>. A token is fresh while less than
/// <see cref="CacheLifetime"/> minus <see cref="RefreshBuffer"/> has elapsed since it was stored.
/// A forward jump of the wall clock does not age it. The monotonic clock does not advance while
/// Linux is suspended, so after a long suspend the token can look fresh here and be dead at the
/// venue. The string itself is never written to a log. The timestamp and the later read must come
/// from the same <see cref="TimeProvider"/>.
/// </para>
/// </remarks>
public sealed class HeldSession
{
    /// <summary>
    /// How long the venue client treats a token as cached after login or a validate rotation.
    /// Copied from <c>MarqSpec.Client.ProjectX</c> 3.0.0, where <c>AuthenticationService</c> sets the
    /// cache expiry 55 minutes out.
    /// </summary>
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(55);

    /// <summary>
    /// The client refreshes when the cache is inside this buffer, so a probe must too.
    /// Copied from <c>MarqSpec.Client.ProjectX</c> 3.0.0, where <c>GetAccessTokenAsync</c> posts
    /// <c>loginKey</c> inside a one-minute buffer.
    /// </summary>
    public static readonly TimeSpan RefreshBuffer = TimeSpan.FromMinutes(1);

    private readonly object _gate = new();
    private string? _token;
    private long _rememberedAt;
    private bool _hasStamp;

    /// <summary>Stores a token this process is allowed to keep using, without logging it.</summary>
    /// <param name="token">The bearer. Empty is ignored.</param>
    /// <param name="clock">
    /// The clock whose <see cref="TimeProvider.GetTimestamp"/> marks when the token was received.
    /// Later freshness reads must use this same instance.
    /// </param>
    public void Remember(string? token, TimeProvider clock)
    {
        if (string.IsNullOrEmpty(token))
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(clock);
        lock (_gate)
        {
            _token = token;
            _rememberedAt = clock.GetTimestamp();
            _hasStamp = true;
        }
    }

    /// <summary>Drops the held token. The next read is not fresh, and nothing here starts a login.</summary>
    public void Forget()
    {
        lock (_gate)
        {
            ClearUnderLock();
        }
    }

    /// <summary>
    /// Drops the held token only when it is still <paramref name="token"/>. A login that stored a
    /// different bearer while validate was in flight keeps that bearer.
    /// </summary>
    /// <param name="token">The bearer that was validated. Empty never matches.</param>
    /// <returns><see langword="true"/> when this call cleared the held bearer.</returns>
    internal bool ForgetIf(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        lock (_gate)
        {
            if (!string.Equals(_token, token, StringComparison.Ordinal))
            {
                return false;
            }

            ClearUnderLock();
            return true;
        }
    }

    /// <summary>
    /// Stores <paramref name="keep"/> only when the held bearer is still <paramref name="expected"/>.
    /// A login that replaced it while validate was in flight is left alone.
    /// </summary>
    /// <param name="expected">The bearer that was posted. Null matches only a missing token, which is not stored.</param>
    /// <param name="keep">The bearer to store. Empty is ignored.</param>
    /// <param name="clock">The clock whose timestamp marks the store. The same instance later freshness reads use.</param>
    /// <returns><see langword="true"/> when <paramref name="keep"/> was stored.</returns>
    internal bool RememberIfCurrent(string? expected, string? keep, TimeProvider clock)
    {
        if (string.IsNullOrEmpty(keep))
        {
            return false;
        }

        ArgumentNullException.ThrowIfNull(clock);
        lock (_gate)
        {
            if (!string.Equals(_token, expected, StringComparison.Ordinal))
            {
                return false;
            }

            _token = keep;
            _rememberedAt = clock.GetTimestamp();
            _hasStamp = true;
            return true;
        }
    }

    /// <summary>Whether the held bearer is still <paramref name="token"/>.</summary>
    /// <param name="token">The bearer a validate posted. Empty is never current.</param>
    /// <returns><see langword="true"/> when a result for <paramref name="token"/> still applies.</returns>
    internal bool IsHeld(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        lock (_gate)
        {
            return string.Equals(_token, token, StringComparison.Ordinal);
        }
    }

    /// <summary>Reads any held token, including one inside the client's refresh buffer.</summary>
    /// <param name="token">The bearer, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="false"/> when nothing is held. This does not start a login.</returns>
    public bool TryRead(out string token)
    {
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(_token))
            {
                token = _token;
                return true;
            }
        }

        token = string.Empty;
        return false;
    }

    /// <summary>Reads the held token when it is still outside the client's refresh buffer.</summary>
    /// <param name="clock">The same clock passed to <see cref="Remember"/>.</param>
    /// <param name="token">The bearer, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="false"/> when nothing is held or the client would already refresh it.</returns>
    public bool TryReadFresh(TimeProvider clock, out string token)
    {
        ArgumentNullException.ThrowIfNull(clock);
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(_token) && _hasStamp && !IsNearExpiryUnderLock(clock))
            {
                token = _token;
                return true;
            }
        }

        token = string.Empty;
        return false;
    }

    /// <summary>
    /// Whether the held token is inside the client's refresh buffer or past <see cref="CacheLifetime"/>.
    /// A missing token is not near expiry.
    /// </summary>
    /// <param name="clock">The same clock passed to <see cref="Remember"/>.</param>
    /// <returns><see langword="true"/> when a login would be the client's next step for this token.</returns>
    internal bool IsNearExpiryOrExhausted(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        lock (_gate)
        {
            if (string.IsNullOrEmpty(_token) || !_hasStamp)
            {
                return false;
            }

            return IsNearExpiryUnderLock(clock);
        }
    }

    private bool IsNearExpiryUnderLock(TimeProvider clock)
    {
        TimeSpan elapsed = clock.GetElapsedTime(_rememberedAt);
        return elapsed >= CacheLifetime - RefreshBuffer;
    }

    private void ClearUnderLock()
    {
        _token = null;
        _rememberedAt = 0;
        _hasStamp = false;
    }
}
