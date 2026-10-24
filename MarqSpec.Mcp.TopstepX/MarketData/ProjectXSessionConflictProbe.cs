using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.Client.ProjectX.Authentication;
using MarqSpec.Client.ProjectX.Exceptions;
using Microsoft.Extensions.Logging;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// Asks <c>POST /api/Auth/validate</c> whether this process's token is still the venue's session.
/// </summary>
/// <remarks>
/// <para>
/// Uses the token <see cref="IAuthenticationService.GetAccessTokenAsync"/> already holds. It does not call
/// <see cref="IAuthenticationService.ValidateSessionAsync"/>, because that method clears the token on any
/// unsuccessful body and the next read then calls <c>loginKey</c> — a new login, which is what disconnects
/// the trading platform. It does not call <see cref="IAuthenticationService.RefreshTokenAsync"/>.
/// </para>
/// <para>
/// The response body is not logged. A vendor sentence is free text, and this repository has already leaked
/// a credential through a log line once.
/// </para>
/// </remarks>
public sealed class ProjectXSessionConflictProbe : ISessionConflictProbe
{
    /// <summary>The named <see cref="HttpClient"/> whose base address is the gateway.</summary>
    public const string HttpClientName = "ProjectXSessionConflict";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private readonly IAuthenticationService _authentication;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<ProjectXSessionConflictProbe> _logger;

    /// <summary>Creates the probe.</summary>
    /// <param name="authentication">The process's token cache. Not used to start a login on failure.</param>
    /// <param name="http">Factory for <see cref="HttpClientName"/>.</param>
    /// <param name="logger">The logger.</param>
    public ProjectXSessionConflictProbe(
        IAuthenticationService authentication,
        IHttpClientFactory http,
        ILogger<ProjectXSessionConflictProbe> logger)
    {
        ArgumentNullException.ThrowIfNull(authentication);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(logger);
        _authentication = authentication;
        _http = http;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<SessionProbeVerdict> ProbeAsync(CancellationToken cancellationToken)
    {
        string token;
        try
        {
            token = await _authentication.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AuthenticationException exception)
        {
            _logger.LogWarning(exception, "The session probe could not read the current TopstepX token.");
            return SessionProbeVerdict.Inconclusive;
        }

        if (string.IsNullOrEmpty(token))
        {
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

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "The session probe could not validate the TopstepX session (HTTP {StatusCode}).",
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

            return SessionConflictSignals.FromValidate(payload.Success, payload.ErrorCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(exception, "The session probe could not reach TopstepX to validate the session.");
            return SessionProbeVerdict.Inconclusive;
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "The session probe could not read the TopstepX validate response.");
            return SessionProbeVerdict.Inconclusive;
        }
    }

    private sealed class ValidatePayload
    {
        public bool Success { get; set; }

        public int ErrorCode { get; set; }
    }
}
