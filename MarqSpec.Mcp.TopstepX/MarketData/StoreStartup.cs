using System.Globalization;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// Reaching the store at startup: naming what was tried, and waiting a bounded time for it.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a seam so the password can be tested for.</b> Formatting the target at the call site would put
/// the one line that handles a credential-bearing string somewhere no unit test reaches; here every case
/// asserts the sentinel never appears. This repository is public and the sibling ProjectX client has already
/// leaked and rotated a real credential once.
/// </para>
/// <para>
/// Both behaviours answer the same container failure (gh#514). <c>Program</c> substitutes a
/// <c>Host=localhost</c> connection string when <c>ConnectionStrings__Default</c> is unset, so a task whose
/// secret never landed degrades with a warning that — before this — named nothing, and the operator went and
/// looked at a database that was fine. And ECS has no cross-service ordering, so on a cold start the server
/// can reach the migration before Postgres answers at all: a single probe then leaves the task degraded for
/// its whole life, healthy and serving refusals, until someone restarts it.
/// </para>
/// <para>
/// <b>Waiting is not validation.</b> Nothing here fails startup — an absent store is still a supported state
/// that degrades to a refusal at the point of use (ADR-0007), and the plain <c>dotnet run</c> HTTP recipe
/// starts with no database by design. A failed migration against a database that <i>did</i> answer is a
/// different thing entirely and still fails the process; that decision is <c>Program.MigrateAsync</c>'s and is
/// untouched here.
/// </para>
/// </remarks>
public static class StoreStartup
{
    /// <summary>The longest gap between probes. Backoff doubles from one second and stops here.</summary>
    /// <remarks>
    /// Capped rather than unbounded because the bound is short: an exponential gap that reached a minute would
    /// spend most of a ninety-second wait asleep and miss a store that arrived at second thirty-five.
    /// </remarks>
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(5);

    /// <summary>The first gap between probes.</summary>
    private static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Reduces a connection string to the coordinates an operator needs, and to nothing else.
    /// </summary>
    /// <param name="connectionString">The connection string the context was built with.</param>
    /// <returns>
    /// A phrase naming host, port, database and user — never the password, and never the string itself.
    /// </returns>
    /// <remarks>
    /// The point of the four is that they separate the two causes that look identical in a log. A secret that
    /// failed to land leaves the substituted <c>host=localhost</c> standing, which no deployment ever
    /// configures; a database that is genuinely down names the real host. Neither is distinguishable from a
    /// warning that says only "the store is unavailable".
    /// </remarks>
    public static string DescribeTarget(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return "an unset connection string";
        }

        NpgsqlConnectionStringBuilder parsed;

        try
        {
            parsed = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            // Deliberately not echoing the string. This is the branch least likely to be exercised, and it is
            // the one holding a value nobody has been able to parse -- including any password inside it.
            return "a connection string that could not be parsed";
        }
        catch (FormatException)
        {
            return "a connection string that could not be parsed";
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"host={Or(parsed.Host)} port={parsed.Port} database={Or(parsed.Database)} user={Or(parsed.Username)}");

        static string Or(string? value) => string.IsNullOrWhiteSpace(value) ? "(unset)" : value;
    }

    /// <summary>
    /// Probes the store, retrying with capped backoff until it answers or the bound elapses.
    /// </summary>
    /// <param name="canConnect">The probe — one round trip, answering whether the store is there.</param>
    /// <param name="connectionString">The connection string, reduced to a target for every line logged.</param>
    /// <param name="wait">How long to keep retrying. <see cref="TimeSpan.Zero"/> probes exactly once.</param>
    /// <param name="clock">The clock the bound and the backoff are measured against.</param>
    /// <param name="logger">Where the attempts and the final warning go.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>
    /// <see cref="StoreAvailability.Available"/> if the store answered — note this says nothing about the
    /// migration, which is the caller's next step — or an unavailable marker naming the target if it did not.
    /// </returns>
    /// <remarks>
    /// The first probe happens unconditionally, so a zero bound is one probe and no delay: byte-for-byte
    /// today's behaviour, which is what every stdio launch and every documented local recipe relies on.
    /// </remarks>
    public static async Task<StoreAvailability> ReachAsync(
        Func<CancellationToken, Task<bool>> canConnect,
        string? connectionString,
        TimeSpan wait,
        TimeProvider clock,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(canConnect);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        string target = DescribeTarget(connectionString);
        long started = clock.GetTimestamp();
        TimeSpan backoff = FirstBackoff;
        int attempts = 0;

        while (true)
        {
            attempts++;

            if (await canConnect(cancellationToken).ConfigureAwait(false))
            {
                return StoreAvailability.Available();
            }

            TimeSpan remaining = wait - clock.GetElapsedTime(started);

            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            TimeSpan delay = backoff < remaining ? backoff : remaining;

            // Information, not Warning: a store that is still coming up is the expected state during a cold
            // start, and warning on every attempt would train an operator to ignore the line that matters.
            logger.LogInformation(
                "The store at {Target} did not answer on attempt {Attempt}; retrying in {Delay:0.#} s, "
                + "up to {Wait:0} s in total.",
                target,
                attempts,
                delay.TotalSeconds,
                wait.TotalSeconds);

            await Task.Delay(delay, clock, cancellationToken).ConfigureAwait(false);

            backoff = backoff < MaxBackoff ? Min(backoff + backoff, MaxBackoff) : MaxBackoff;
        }

        // One line, not a stack trace. This is the first thing a new operator meets, and the stack trace it
        // used to print named a socket rather than the thing they need to do.
        StoreAvailability unavailable = StoreAvailability.Unavailable(
            wait <= TimeSpan.Zero
                ? string.Create(CultureInfo.InvariantCulture, $"Nothing answered at {target}.")
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"Nothing answered at {target} after {attempts} attempts over {wait.TotalSeconds:0} s."));

        logger.LogWarning("{Explanation}", unavailable.Explanation);

        return unavailable;

        static TimeSpan Min(TimeSpan left, TimeSpan right) => left < right ? left : right;
    }
}
