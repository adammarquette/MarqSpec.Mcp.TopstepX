using System.Data;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// Holds one Postgres session advisory lock for the whole of <c>MigrateAsync</c>'s apply (gh#843).
/// </summary>
/// <remarks>
/// <para>
/// The lock is a guard against concurrent applies. It is taken on a <b>dedicated, unpooled</b>
/// connection before the apply and released in a <see langword="finally"/>, so a second process
/// waits until the first has finished the whole schema.
/// </para>
/// <para>
/// The key is <c>pg_advisory_lock(138608641, 843)</c>. <c>138608641</c> is <c>0x08430001</c>, distinct
/// from the index-build class <c>0x07580001</c> (<c>123207681</c>) and the trading-cap class
/// <c>0x07110024</c>. The index-build lock stays inside that migration, on the connection EF keeps
/// open for its five statements. This one wraps the apply from outside.
/// </para>
/// <para>
/// The acquire tries <c>pg_try_advisory_lock</c> first. When another session already holds the
/// lock, this logs once and then blocks on <c>pg_advisory_lock</c> for
/// <c>Migrations__LockWaitSeconds</c>. That wait is separate from the per-command timeout. A
/// normal multi-command apply finishes inside the default. A wait that reaches the cap throws
/// <see cref="MigrationLockWaitTimedOutException"/>, which startup treats as the store being
/// unavailable. A holder that stays up does not block the waiter forever.
/// <c>pg_advisory_lock</c> is not called when the try succeeds: the lock is re-entrant in a
/// session, and a second acquire would need a second unlock. Closing the unpooled connection
/// releases the lock if the explicit unlock cannot run.
/// </para>
/// </remarks>
internal static class MigrationApplyLock
{
    /// <summary>The advisory-lock class id, <c>0x08430001</c>.</summary>
    internal const int ClassId = 0x08430001;

    /// <summary>The advisory-lock object id. The class id is what keeps this lock distinct.</summary>
    internal const int ObjectId = 843;

    /// <summary>
    /// Opens an unpooled connection, holds the migration lock around <paramref name="apply"/>, and
    /// releases the lock afterwards, including when <paramref name="apply"/> throws.
    /// </summary>
    /// <param name="connectionString">The store the migration runs against. Not logged.</param>
    /// <param name="apply">The migration apply. It runs only after the lock is held.</param>
    /// <param name="logger">
    /// One information line when the lock is already held, and a warning if the explicit unlock fails.
    /// </param>
    /// <param name="lockWaitTimeoutSeconds">
    /// How long the blocking acquire may wait, in seconds. Zero is refused: Npgsql treats it as
    /// no timeout.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the connect and the lock wait. The unlock is not cancelled.
    /// </param>
    /// <returns>The task.</returns>
    internal static async Task ApplyUnderLockAsync(
        string connectionString,
        Func<CancellationToken, Task> apply,
        ILogger logger,
        int lockWaitTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(apply);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfLessThan(lockWaitTimeoutSeconds, 1);

        // Appended, not rebuilt. NpgsqlConnectionStringBuilder.ConnectionString omits the password
        // unless Persist Security Info is set, and a lock connection that cannot authenticate never
        // reaches the apply. A later Pooling key wins, so this turns pooling off without copying
        // the credential into a second string we would have to log.
        string dedicated = connectionString.TrimEnd().TrimEnd(';') + ";Pooling=false";
        await using NpgsqlConnection connection = new(dedicated);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            bool held = await TryAcquireAsync(connection, cancellationToken).ConfigureAwait(false);
            if (!held)
            {
                logger.LogInformation(
                    "The migration lock is held by another process. Waiting up to {TimeoutSeconds}s.",
                    lockWaitTimeoutSeconds);
                await using NpgsqlCommand acquire = Command(
                    connection,
                    "SELECT pg_advisory_lock(@classId, @objectId)",
                    timeoutSeconds: lockWaitTimeoutSeconds);
                try
                {
                    await acquire.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (NpgsqlException ex) when (IsLockWaitTimeout(ex, cancellationToken))
                {
                    // Observed on a blocked pg_advisory_lock: NpgsqlException "Exception while reading
                    // from stream" with an inner TimeoutException "Timeout during reading attempt".
                    // A migration command can time out the same way. This catch is only the wait.
                    throw new MigrationLockWaitTimedOutException(lockWaitTimeoutSeconds, ex);
                }
            }

            await apply(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await ReleaseAsync(connection, logger).ConfigureAwait(false);
        }
    }

    private static async Task ReleaseAsync(NpgsqlConnection connection, ILogger logger)
    {
        if (connection.State != ConnectionState.Open)
        {
            return;
        }

        try
        {
            await using NpgsqlCommand release = Command(
                connection,
                "SELECT pg_advisory_unlock(@classId, @objectId)",
                timeoutSeconds: 30);
            await release.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // The connection is unpooled. Disposing it closes the session, and Postgres releases a
            // session advisory lock when the session ends. The warning is so a failed unlock is not
            // silent; it must not mask the apply's own exception.
            logger.LogWarning(
                exception,
                "The migration lock could not be released explicitly. Closing its connection releases it.");
        }
    }

    private static bool IsLockWaitTimeout(NpgsqlException exception, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is TimeoutException)
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<bool> TryAcquireAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand probe = Command(
            connection,
            "SELECT pg_try_advisory_lock(@classId, @objectId)",
            timeoutSeconds: 30);
        return (bool)(await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, string sql, int timeoutSeconds)
    {
        NpgsqlCommand command = new(sql, connection) { CommandTimeout = timeoutSeconds };
        command.Parameters.Add(new NpgsqlParameter("classId", NpgsqlDbType.Integer) { Value = ClassId });
        command.Parameters.Add(new NpgsqlParameter("objectId", NpgsqlDbType.Integer) { Value = ObjectId });
        return command;
    }
}

/// <summary>
/// The migration lock was still held when <c>Migrations__LockWaitSeconds</c> elapsed (gh#843).
/// </summary>
/// <remarks>
/// Startup treats this as the store being unavailable. It is not a schema defect, and it is not the
/// per-command timeout: that one still fails the process so the next start can rebuild an index.
/// </remarks>
internal sealed class MigrationLockWaitTimedOutException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="seconds">The wait that elapsed.</param>
    /// <param name="inner">The timeout Npgsql raised for the blocking acquire.</param>
    internal MigrationLockWaitTimedOutException(int seconds, Exception inner)
        : base("The migration lock wait timed out.", inner)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(seconds, 1);
        Seconds = seconds;
    }

    /// <summary>How long the wait was allowed to run, in seconds.</summary>
    internal int Seconds { get; }
}
