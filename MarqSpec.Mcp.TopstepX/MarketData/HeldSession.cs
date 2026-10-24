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
/// Freshness uses the same window as that client. A token is fresh while its local expiry is more
/// than one minute away. The string itself is never written to a log.
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
    private DateTimeOffset _expiresAt;

    /// <summary>Stores a token this process is allowed to keep using, without logging it.</summary>
    /// <param name="token">The bearer. Empty is ignored.</param>
    /// <param name="now">When it was received. The local expiry is <see cref="CacheLifetime"/> later.</param>
    public void Remember(string? token, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(token))
        {
            return;
        }

        lock (_gate)
        {
            _token = token;
            _expiresAt = now + CacheLifetime;
        }
    }

    /// <summary>Drops the held token. The next read is not fresh, and nothing here starts a login.</summary>
    public void Forget()
    {
        lock (_gate)
        {
            _token = null;
            _expiresAt = default;
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
    /// <param name="now">The clock the decision is made on.</param>
    /// <param name="token">The bearer, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="false"/> when nothing is held or the client would already refresh it.</returns>
    public bool TryReadFresh(DateTimeOffset now, out string token)
    {
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(_token) && _expiresAt > now + RefreshBuffer)
            {
                token = _token;
                return true;
            }
        }

        token = string.Empty;
        return false;
    }
}
