namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>What a session probe was able to decide.</summary>
public enum SessionProbeVerdict
{
    /// <summary>The validate call succeeded. This process still holds the session.</summary>
    Valid = 0,

    /// <summary>
    /// The API reported <c>InvalidSession</c> or <c>SessionNotFound</c>. Another login ended this one.
    /// </summary>
    Conflict = 1,

    /// <summary>
    /// The probe could not decide: no fresh token was held, the code was unknown, the call timed out,
    /// or the call did not land. That is not evidence of another session, and it is not a reason to
    /// log in or reconnect.
    /// </summary>
    Inconclusive = 2,

    /// <summary>
    /// <c>ExpiredToken</c>. The bearer can never become valid. This is not another session, so it does
    /// not latch the stop. The renewal loop may allow a budgeted <c>loginKey</c> so the process can recover.
    /// </summary>
    NeverValid = 3,
}

/// <summary>
/// What one validate of a held bearer decided, and whether that decision still applies to it.
/// </summary>
/// <param name="Verdict">The probe verdict.</param>
/// <param name="AppliedToHeld">
/// <see langword="true"/> when the held bearer is still the one that was posted and a store or a drop
/// for that bearer completed. A login that replaced the bearer while validate was in flight leaves this
/// <see langword="false"/>.
/// </param>
/// <param name="Bearer">The bearer that was posted. Null when nothing was posted. Not for logging.</param>
public readonly record struct HeldTokenValidation(
    SessionProbeVerdict Verdict,
    bool AppliedToHeld,
    string? Bearer);

/// <summary>
/// Reads whether this process's TopstepX session is still the one the venue will accept.
/// </summary>
/// <remarks>
/// The recorder calls this after a hub drop that did not already name a conflict. A transport blip and
/// another login look the same on the socket, and only the validate call tells them apart.
/// </remarks>
public interface ISessionConflictProbe
{
    /// <summary>Validates the current session without starting a new login.</summary>
    /// <param name="cancellationToken">The stopping token.</param>
    /// <returns>What the probe could decide.</returns>
    Task<SessionProbeVerdict> ProbeAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Classifies the two signals that mean another TopstepX session has this login (gh#757).
/// </summary>
/// <remarks>
/// <para>
/// TopstepX allows one device session. The trading platform and this recorder share the login, and the
/// client's automatic reconnect logs in again, which disconnects the platform. A hub close is a conflict
/// only when its text contains a documented <c>ValidateErrorCode</c> name. Anything else is probed with
/// the token already held, and only <c>InvalidSession</c>, <c>SessionNotFound</c>, or HTTP 401 stops
/// the recorder. An inconclusive probe does not log in and does not reconnect.
/// </para>
/// <para>
/// Validate codes are the gateway's <c>ValidateErrorCode</c>, quoted from
/// <c>https://api.topstepx.com/swagger/v1/swagger.json</c>: 0 Success, 1 <c>InvalidSession</c>, 2
/// <c>SessionNotFound</c>, 3 <c>ExpiredToken</c>, 4 <c>UnknownError</c>. Expiry is not a conflict — the
/// session aged out, nobody else took it. The realtime guide does not publish hub close sentences, so
/// phrases such as "another session" are not matched.
/// </para>
/// </remarks>
public static class SessionConflictSignals
{
    /// <summary>Gateway <c>ValidateErrorCode.InvalidSession</c>.</summary>
    public const int InvalidSession = 1;

    /// <summary>Gateway <c>ValidateErrorCode.SessionNotFound</c>.</summary>
    public const int SessionNotFound = 2;

    /// <summary>Gateway <c>ValidateErrorCode.ExpiredToken</c>. Not a conflict.</summary>
    public const int ExpiredToken = 3;

    /// <summary>Gateway <c>ValidateErrorCode.UnknownError</c>. Not a conflict.</summary>
    public const int UnknownError = 4;

    // Documented ValidateErrorCode names only. The gateway realtime guide does not publish the
    // hub's close sentences, so undocumented phrases are not treated as a conflict.
    private static readonly string[] _hubPhrases =
    [
        "invalid session",
        "invalidsession",
        "session not found",
        "sessionnotfound",
    ];

    /// <summary>Whether a hub close message is a session conflict rather than a transport drop.</summary>
    /// <param name="errorMessage">The status change's error text, if any.</param>
    /// <param name="exceptionMessage">The status change's exception text, if any.</param>
    /// <returns><see langword="true"/> when either text names another session.</returns>
    public static bool IsHubConflict(string? errorMessage, string? exceptionMessage) =>
        ContainsPhrase(errorMessage) || ContainsPhrase(exceptionMessage);

    /// <summary>
    /// Maps a <c>/api/Auth/validate</c> result. Success is <see cref="SessionProbeVerdict.Valid"/>.
    /// <see cref="InvalidSession"/> and <see cref="SessionNotFound"/> are
    /// <see cref="SessionProbeVerdict.Conflict"/>. <see cref="ExpiredToken"/> is
    /// <see cref="SessionProbeVerdict.NeverValid"/>. Every other code is
    /// <see cref="SessionProbeVerdict.Inconclusive"/>.
    /// </summary>
    /// <param name="success">The response <c>success</c> flag.</param>
    /// <param name="errorCode">The response <c>errorCode</c>. Zero when the field was absent.</param>
    /// <returns>The verdict.</returns>
    public static SessionProbeVerdict FromValidate(bool success, int errorCode)
    {
        if (success)
        {
            return SessionProbeVerdict.Valid;
        }

        if (errorCode is InvalidSession or SessionNotFound)
        {
            return SessionProbeVerdict.Conflict;
        }

        if (errorCode == ExpiredToken)
        {
            return SessionProbeVerdict.NeverValid;
        }

        return SessionProbeVerdict.Inconclusive;
    }

    private static bool ContainsPhrase(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        foreach (string phrase in _hubPhrases)
        {
            if (message.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
