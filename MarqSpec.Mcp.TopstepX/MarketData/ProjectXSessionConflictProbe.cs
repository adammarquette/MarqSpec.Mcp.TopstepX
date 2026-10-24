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
/// No fresh token, a timeout, an expired-token code, an unknown code, or a call that does not land is
/// <see cref="SessionProbeVerdict.Inconclusive"/> and does not log in. HTTP 401 is
/// <see cref="SessionProbeVerdict.Conflict"/>: the bearer was refused, which is the same practical outcome
/// as a killed session. A <c>newToken</c> on a valid body is remembered and never logged. The response
/// body is not logged.
/// </para>
/// </remarks>
public sealed class ProjectXSessionConflictProbe : ISessionConflictProbe
{
    /// <summary>The named <see cref="HttpClient"/> whose base address is the gateway.</summary>
    public const string HttpClientName = "ProjectXSessionConflict";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private readonly HeldSession _held;
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

    /// <inheritdoc />
    public async Task<SessionProbeVerdict> ProbeAsync(CancellationToken cancellationToken)
    {
        if (!_held.TryReadFresh(_clock.GetUtcNow(), out string token))
        {
            _logger.LogWarning(
                "The session probe has no fresh TopstepX token to validate. The result is inconclusive, and this process will not call loginKey.");
            return SessionProbeVerdict.Inconclusive;
        }

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
            if (verdict == SessionProbeVerdict.Valid && !string.IsNullOrEmpty(payload.NewToken))
            {
                _held.Remember(payload.NewToken, _clock.GetUtcNow());
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
    }

    private sealed class ValidatePayload
    {
        public bool Success { get; set; }

        public int ErrorCode { get; set; }

        public string? NewToken { get; set; }
    }
}
