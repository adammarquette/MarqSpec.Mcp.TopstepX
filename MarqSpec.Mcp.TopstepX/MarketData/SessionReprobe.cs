namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// How long the recorder waits before another <c>POST /api/Auth/validate</c> after an inconclusive probe.
/// </summary>
/// <remarks>
/// This interval is not <c>MarketDataOptions.SessionConflictBackoff</c>. That value, including zero,
/// only finishes a stop. A zero stop backoff must not become a tight validate loop.
/// </remarks>
public static class SessionReprobe
{
    /// <summary>The first inconclusive probe waits this long. Later attempts double it.</summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(1);

    /// <summary>The wait stops growing here.</summary>
    public static readonly TimeSpan MaximumInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The delay before attempt <paramref name="attempt"/>, counting from zero.
    /// Attempt 0 is <see cref="MinimumInterval"/>; each later attempt doubles until <see cref="MaximumInterval"/>.
    /// </summary>
    /// <param name="attempt">How many inconclusive re-probes have already been scheduled. Negative is treated as zero.</param>
    /// <returns>The delay.</returns>
    public static TimeSpan DelayForAttempt(int attempt)
    {
        if (attempt < 0)
        {
            attempt = 0;
        }

        // 2^8 seconds is already past the cap. Clamping the exponent avoids overflow.
        int capped = Math.Min(attempt, 8);
        double seconds = MinimumInterval.TotalSeconds * Math.Pow(2, capped);
        if (seconds > MaximumInterval.TotalSeconds)
        {
            seconds = MaximumInterval.TotalSeconds;
        }

        return TimeSpan.FromSeconds(seconds);
    }
}
