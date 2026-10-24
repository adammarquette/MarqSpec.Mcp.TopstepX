using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// Asks <c>POST /api/Auth/validate</c> whether the token this process already holds is still the venue's session.
/// </summary>
/// <remarks>
/// <para>
/// Reads <see cref="HeldSession"/> only. It does not call <c>GetAccessTokenAsync</c>,
/// <c>ValidateSessionAsync</c>, or <c>RefreshTokenAsync</c>. The first of those posts <c>loginKey</c> once
/// the client's cache is inside its last minute. The second clears the cached token on any unsuccessful
/// body, and the next read then logs in. Either one disconnects the trading platform.
/// </para>
/// <para>
/// No fresh token, a timeout, an unknown code, or a call that does not land is
/// <see cref="SessionProbeVerdict.Inconclusive"/> and does not log in.
/// <see cref="SessionConflictSignals.ExpiredToken"/> is <see cref="SessionProbeVerdict.NeverValid"/>:
/// the bearer can never become valid, and that is not another session. HTTP 401 is
/// <see cref="SessionProbeVerdict.Conflict"/>: the bearer was refused, which is the same practical outcome
/// as a killed session. A valid body stores <c>newToken</c> when one is present, and otherwise stores the
/// same bearer again so the 55-minute window slides. The token and the response body are not logged.
/// <see cref="ProbeAsync"/> and <see cref="ValidateHeldTokenAsync"/> share one lock, so a probe and the
/// renewer cannot validate at the same time.
/// </para>
/// </remarks>
public sealed class ProjectXSessionConflictProbe : ISessionConflictProbe
{
    /// <summary>The named <see cref="HttpClient"/> whose base address is the gateway.</summary>
    public const string HttpClientName = "ProjectXSessionConflict";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim _validate = new(1, 1);
    private readonly HeldSession _held;
    private int _validateWaiting;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<ProjectXSessionConflictProbe> _logger;
    private readonly TimeProvider _clock;

    /// <summary>Creates the probe.</summary>
    /// <param name="held">The token already held. Not a login.</param>
    /// <param name="http">Factory for <see cref="HttpClientName"/>.</param>
    /// <param name="logger">The logger. Vendor text is not written here.</param>
    /// <param name="clock">The clock the freshness decision uses.</param>
    public ProjectXSessionConflictProbe(
        HeldSession held,
        IHttpClientFactory http,
        ILogger<ProjectXSessionConflictProbe> logger,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(clock);
        _held = held;
        _http = http;
        _logger = logger;
        _clock = clock;
    }

    /// <summary>How many validate calls are waiting for the shared lock. The caller that holds it is not counted.</summary>
    internal int ValidateWaiting => Volatile.Read(ref _validateWaiting);

    /// <inheritdoc />
    public Task<SessionProbeVerdict> ProbeAsync(CancellationToken cancellationToken)
    {
        if (!_held.TryReadFresh(_clock, out string token))
        {
            _logger.LogWarning(
                "The session probe has no fresh TopstepX token to validate. The result is inconclusive, and this process will not call loginKey.");
            return Task.FromResult(SessionProbeVerdict.Inconclusive);
        }

        return ValidateBearerAsync(token, cancellationToken);
    }

    /// <summary>
    /// Posts validate with whatever bearer is held, including one inside the client's refresh buffer.
    /// The renewal loop uses this so a quiet process does not call <c>loginKey</c> at about 54 minutes.
    /// No held token is inconclusive and does not log in.
    /// </summary>
    /// <param name="cancellationToken">The stopping token.</param>
    /// <returns>What validate could decide.</returns>
    public Task<SessionProbeVerdict> ValidateHeldTokenAsync(CancellationToken cancellationToken)
    {
        if (!_held.TryRead(out string token))
        {
            return Task.FromResult(SessionProbeVerdict.Inconclusive);
        }

        return ValidateBearerAsync(token, cancellationToken);
    }

    private async Task<SessionProbeVerdict> ValidateBearerAsync(string token, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _validateWaiting);
        try
        {
            await _validate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Decrement(ref _validateWaiting);
            throw;
        }

        Interlocked.Decrement(ref _validateWaiting);
        try
        {
            using HttpClient http = _http.CreateClient(HttpClientName);
            using HttpRequestMessage request = new(HttpMethod.Post, "/api/Auth/validate");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using HttpResponseMessage response = await http
                .SendAsync(request, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _logger.LogWarning(
                    "The session probe was refused (HTTP {StatusCode}). The held session is being treated as ended, and this process will not call loginKey.",
                    (int)response.StatusCode);
                return SessionProbeVerdict.Conflict;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "The session probe could not validate the TopstepX session (HTTP {StatusCode}). The result is inconclusive, and this process will not call loginKey.",
                    (int)response.StatusCode);
                return SessionProbeVerdict.Inconclusive;
            }

            ValidatePayload? payload = await response.Content
                .ReadFromJsonAsync<ValidatePayload>(_json, cancellationToken)
                .ConfigureAwait(false);

            if (payload is null)
            {
                return SessionProbeVerdict.Inconclusive;
            }

            SessionProbeVerdict verdict = SessionConflictSignals.FromValidate(payload.Success, payload.ErrorCode);
            if (verdict == SessionProbeVerdict.Valid)
            {
                // A success with no newToken still slides the client's 55-minute window. The string
                // remembered here is never logged.
                string keep = string.IsNullOrEmpty(payload.NewToken) ? token : payload.NewToken;
                _held.Remember(keep, _clock);
            }

            return verdict;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "The session probe timed out before TopstepX answered. The result is inconclusive, and this process will not call loginKey.");
            return SessionProbeVerdict.Inconclusive;
        }
        catch (HttpRequestException)
        {
            _logger.LogWarning(
                "The session probe could not reach TopstepX to validate the session. The result is inconclusive, and this process will not call loginKey.");
            return SessionProbeVerdict.Inconclusive;
        }
        catch (JsonException)
        {
            _logger.LogWarning(
                "The session probe could not read the TopstepX validate response. The result is inconclusive, and this process will not call loginKey.");
            return SessionProbeVerdict.Inconclusive;
        }
        finally
        {
            _validate.Release();
        }
    }

    private sealed class ValidatePayload
    {
        public bool Success { get; set; }

        public int ErrorCode { get; set; }

        public string? NewToken { get; set; }
    }
}
