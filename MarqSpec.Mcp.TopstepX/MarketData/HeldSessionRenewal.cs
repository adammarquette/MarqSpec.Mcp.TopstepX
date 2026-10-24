using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Telemetry;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// When to post <c>POST /api/Auth/validate</c> so <see cref="HeldSession"/> stays fresh without <c>loginKey</c>.
/// </summary>
/// <remarks>
/// The venue client (<c>MarqSpec.Client.ProjectX</c> 3.0.0) keeps a token for 55 minutes and posts
/// <c>loginKey</c> once it is inside the last minute, about 54 minutes after it was stored. A quiet
/// process that never calls REST would otherwise hit that login on the next hub drop. Validate with the
/// bearer already held creates no new session. The happy path fires at 30 minutes, plus or minus 5 minutes
/// of jitter, and is clamped so it is still at least 10 minutes before that 54-minute cliff.
/// </remarks>
public static class HeldSessionRenewal
{
    /// <summary>The renewal interval before jitter.</summary>
    public const double IntervalMinutes = 30;

    /// <summary>Jitter either side of <see cref="IntervalMinutes"/>.</summary>
    public const double JitterMinutes = 5;

    /// <summary>How long before the client's refresh cliff a renewal must still have fired.</summary>
    public static readonly TimeSpan LeadBeforeCliff = TimeSpan.FromMinutes(10);

    /// <summary>The first wait after a renewal that did not succeed. Later failures double it.</summary>
    public static readonly TimeSpan FailureInitial = TimeSpan.FromSeconds(30);

    /// <summary>The failure wait stops growing here.</summary>
    public static readonly TimeSpan FailureCap = TimeSpan.FromMinutes(5);

    /// <summary>How often to look for a token that has not been stored yet. This wait does not close login.</summary>
    public static readonly TimeSpan NoTokenPoll = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The delay before the next validate. Failure counts start at 1. A zero count is the happy path and
    /// reads <paramref name="jitterUnit"/> from 0 (earliest) through 1 (latest).
    /// </summary>
    /// <param name="consecutiveFailures">How many renewals in a row have failed. Zero is the steady interval.</param>
    /// <param name="jitterUnit">A unit interval. Values outside 0..1 are clamped. Ignored once failures have started.</param>
    /// <returns>The delay.</returns>
    public static TimeSpan NextDelay(int consecutiveFailures, double jitterUnit)
    {
        if (consecutiveFailures > 0)
        {
            int steps = Math.Min(consecutiveFailures - 1, 16);
            double seconds = FailureInitial.TotalSeconds * Math.Pow(2, steps);
            if (seconds > FailureCap.TotalSeconds)
            {
                seconds = FailureCap.TotalSeconds;
            }

            return TimeSpan.FromSeconds(seconds);
        }

        if (double.IsNaN(jitterUnit))
        {
            jitterUnit = 0.5;
        }

        jitterUnit = Math.Clamp(jitterUnit, 0, 1);
        double minutes = IntervalMinutes + ((jitterUnit * (JitterMinutes * 2)) - JitterMinutes);
        TimeSpan delay = TimeSpan.FromMinutes(minutes);
        TimeSpan latest = HeldSession.CacheLifetime - HeldSession.RefreshBuffer - LeadBeforeCliff;
        if (delay > latest)
        {
            delay = latest;
        }

        return delay;
    }
}

/// <summary>
/// Keeps <see cref="HeldSession"/> inside the venue client's freshness window by posting validate with the
/// bearer already held. It never calls <c>loginKey</c>.
/// </summary>
/// <remarks>
/// <para>
/// No token yet is a short poll, and login stays permitted so the first connect can still log in. A held
/// token, including one already inside the refresh buffer, is validated. Success stores <c>newToken</c> when
/// the body has one, and otherwise stores the same bearer again so the 55-minute window slides. The token
/// is not logged.
/// </para>
/// <para>
/// A failure backs off and closes login while the held token is still fresh. Login opens again when a
/// later validate succeeds, when validate reports <c>ExpiredToken</c> (the bearer can never become valid),
/// or when the held token is inside its refresh buffer, so one <c>loginKey</c> can recover the process.
/// That close is not the one-way stop. A positive conflict (HTTP 401, <c>InvalidSession</c>,
/// <c>SessionNotFound</c>) latches the stop when <see cref="MarketDataOptions.StopOnSessionConflict"/> is
/// on. Once the stop is latched this loop does not post validate again.
/// </para>
/// </remarks>
public sealed class HeldSessionRenewer : BackgroundService
{
    private readonly HeldSession _held;
    private readonly ProjectXSessionConflictProbe _probe;
    private readonly SessionLoginGate _gate;
    private readonly TimeProvider _clock;
    private readonly MarketDataOptions _market;
    private readonly ILogger<HeldSessionRenewer> _logger;
    private readonly HostTelemetry _telemetry;
    private readonly Func<double> _jitterUnit;

    /// <summary>Creates the renewer.</summary>
    /// <param name="held">The token already held.</param>
    /// <param name="probe">Posts validate. Does not call <c>loginKey</c>.</param>
    /// <param name="gate">Closed on a positive conflict, and for the duration of a renewal backoff.</param>
    /// <param name="clock">The clock the wait and the freshness decision use.</param>
    /// <param name="market">Whether a positive conflict latches the stop.</param>
    /// <param name="logger">The logger. The bearer is not written here.</param>
    /// <param name="telemetry">Records a renewal refusal. The bearer is not a tag.</param>
    /// <param name="jitterUnit">
    /// Returns a unit interval for the happy-path jitter. Production passes <see cref="Random.Shared"/>.
    /// </param>
    public HeldSessionRenewer(
        HeldSession held,
        ProjectXSessionConflictProbe probe,
        SessionLoginGate gate,
        TimeProvider clock,
        IOptions<MarketDataOptions> market,
        ILogger<HeldSessionRenewer> logger,
        HostTelemetry telemetry,
        Func<double>? jitterUnit = null)
    {
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(market);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(telemetry);
        _held = held;
        _probe = probe;
        _gate = gate;
        _clock = clock;
        _market = market.Value;
        _logger = logger;
        _telemetry = telemetry;
        _jitterUnit = jitterUnit ?? (() => Random.Shared.NextDouble());
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (StopIfLatched())
                {
                    return;
                }

                if (!_held.TryRead(out _))
                {
                    // The first login has not happened. Waiting here must not close the gate.
                    await Task.Delay(HeldSessionRenewal.NoTokenPoll, _clock, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                TimeSpan delay = HeldSessionRenewal.NextDelay(failures, _jitterUnit());
                await Task.Delay(delay, _clock, stoppingToken).ConfigureAwait(false);
                if (StopIfLatched())
                {
                    return;
                }

                if (!_held.TryRead(out _))
                {
                    continue;
                }

                SessionProbeVerdict verdict = await _probe
                    .ValidateHeldTokenAsync(stoppingToken)
                    .ConfigureAwait(false);
                if (verdict == SessionProbeVerdict.Valid)
                {
                    failures = 0;
                    _gate.EndRenewalBackoff();
                    _logger.LogInformation(
                        "The held TopstepX session was renewed with POST /api/Auth/validate. This process did not call loginKey.");
                    continue;
                }

                if (verdict == SessionProbeVerdict.Conflict)
                {
                    if (_market.StopOnSessionConflict)
                    {
                        _gate.LatchStop();
                    }

                    Record(SessionRenewalRefusal.Conflict);
                    _logger.LogWarning(
                        "Renewing the held TopstepX session reported a conflict. This process will not call loginKey.");
                    if (_gate.StopLatched)
                    {
                        return;
                    }

                    failures = Math.Max(failures, 1);
                    continue;
                }

                if (verdict == SessionProbeVerdict.NeverValid)
                {
                    failures++;
                    Record(SessionRenewalRefusal.Expired);
                    if (_gate.StopLatched)
                    {
                        return;
                    }

                    // The bearer can never become valid. Drop it so the next read is a login, not another
                    // validate of a token the venue has already refused.
                    _held.Forget();
                    _gate.EndRenewalBackoff();
                    _logger.LogWarning(
                        "Renewing the held TopstepX session returned an expired token. Login may run once so this process can recover.");
                    continue;
                }

                if (NoteFailure(ref failures, SessionRenewalRefusal.Outage) == RenewalStep.Stop)
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                if (NoteFailure(ref failures, SessionRenewalRefusal.Outage) == RenewalStep.Stop)
                {
                    return;
                }
            }
        }
    }

    /// <summary>Records one latched refusal and says whether the loop should leave.</summary>
    /// <returns><see langword="true"/> when validate must not be posted.</returns>
    private bool StopIfLatched()
    {
        if (!_gate.StopLatched)
        {
            return false;
        }

        Record(SessionRenewalRefusal.Latched);
        return true;
    }

    /// <summary>
    /// Counts a renewal that did not succeed. A token that is still fresh closes login. A token inside
    /// the refresh buffer, or a stop that latched while this call was in flight, does not.
    /// </summary>
    /// <param name="failures">The consecutive-failure count. Incremented here.</param>
    /// <param name="reasonWhenStillFresh">The refusal reason while the token is still outside the refresh buffer.</param>
    /// <returns><see cref="RenewalStep.Stop"/> when the stop is latched and the loop must leave.</returns>
    private RenewalStep NoteFailure(ref int failures, string reasonWhenStillFresh)
    {
        failures++;
        if (_gate.StopLatched)
        {
            Record(SessionRenewalRefusal.Latched);
            return RenewalStep.Stop;
        }

        if (_held.IsNearExpiryOrExhausted(_clock))
        {
            _gate.EndRenewalBackoff();
            Record(SessionRenewalRefusal.Exhausted);
            _logger.LogWarning(
                "Renewing the held TopstepX session failed and the held token is inside its refresh window. Login may run once so this process can recover.");
            return RenewalStep.Continue;
        }

        _gate.BeginRenewalBackoff();
        Record(reasonWhenStillFresh);
        _logger.LogWarning(
            "Renewing the held TopstepX session failed. This process will not call loginKey and will try validate again after {Backoff}.",
            HeldSessionRenewal.NextDelay(failures, 0));
        return RenewalStep.Continue;
    }

    private void Record(string reason)
    {
        using (_telemetry.SessionRenewalRefusal(reason))
        {
        }
    }

    private enum RenewalStep
    {
        Continue = 0,
        Stop = 1,
    }
}
