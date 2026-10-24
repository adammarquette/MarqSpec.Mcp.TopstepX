using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.Mcp.TopstepX.Data;

/// <summary>
/// Sends the per-chunk trade index only when the prepare step marked this session (gh#756).
/// </summary>
/// <remarks>
/// <para>
/// <c>CREATE INDEX … WITH (timescaledb.transaction_per_chunk)</c> commits once per chunk. Plain Postgres
/// rejects that reloption, and Timescale will not run it inside the prepare procedure. The migration's
/// prepare step sets <see cref="SessionSetting"/> to <c>1</c> only when <c>Trades</c> is a hypertable. This
/// gate then leaves the top-level statement alone. Any other session rewrites it to <c>SELECT 1</c> before
/// it is sent, which is what keeps a database without Timescale able to start.
/// </para>
/// <para>
/// A failed statement does not release a session advisory lock. This interceptor releases it on that
/// failure. Npgsql resets a pooled connection with <c>DISCARD ALL</c>, and that reset releases advisory
/// locks when it is sent. The failure path unlocks here rather than leaving the key for the reset. The
/// probe and the unlock copy the migration command's timeout, so they are not left on Npgsql's 30-second
/// default. An unlock that throws <see cref="DbException"/>, <see cref="InvalidOperationException"/>, or
/// <see cref="TimeoutException"/> is logged and swallowed, so the original command error stays the one
/// <c>CommandFailed</c> reports. <see cref="OperationCanceledException"/> and fatal exceptions propagate.
/// When the context has no <see cref="ILoggerFactory"/>, or resolving one throws, the warning goes to
/// <see cref="NullLogger"/> and is not rethrown.
/// </para>
/// </remarks>
internal sealed class TradeInstrumentIndexBuildInterceptor : DbCommandInterceptor
{
    /// <summary>The session flag the prepare procedure sets. <c>1</c> means the next statement builds per chunk.</summary>
    internal const string SessionSetting = "marqspec.trade_index_per_chunk";

    /// <summary>The reloption plain Postgres rejects. Present only on the top-level build statement.</summary>
    internal const string PerChunkMarker = "timescaledb.transaction_per_chunk";

    /// <summary>Releases one level of the build lock. A session that does not hold it returns false.</summary>
    internal const string ReleaseLockSql = "SELECT pg_advisory_unlock(123207681, 190601);";

    /// <inheritdoc />
    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result)
    {
        Gate(command);
        return base.NonQueryExecuting(command, eventData, result);
    }

    /// <inheritdoc />
    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await GateAsync(command, cancellationToken).ConfigureAwait(false);
        return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData)
    {
        ReleaseIfOurs(command, eventData);
        base.CommandFailed(command, eventData);
    }

    /// <inheritdoc />
    public override Task CommandFailedAsync(
        DbCommand command,
        CommandErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        ReleaseIfOurs(command, eventData);
        return base.CommandFailedAsync(command, eventData, cancellationToken);
    }

    /// <summary><c>1</c> is the only mark that means the per-chunk statement should be sent.</summary>
    /// <param name="setting">The value of <see cref="SessionSetting"/>, or null when it was never set.</param>
    /// <returns><see langword="true"/> when the top-level per-chunk statement must run.</returns>
    internal static bool SessionRequestsPerChunk(object? setting) =>
        setting is string value && value == "1";

    private static void Gate(DbCommand command)
    {
        if (!IsPerChunkStatement(command) || command.Connection is null)
        {
            return;
        }

        using DbCommand probe = command.Connection.CreateCommand();
        probe.CommandText = "SELECT current_setting('" + SessionSetting + "', true);";
        probe.CommandTimeout = command.CommandTimeout;
        probe.Transaction = command.Transaction;
        if (!SessionRequestsPerChunk(probe.ExecuteScalar()))
        {
            command.CommandText = "SELECT 1";
        }
    }

    private static async Task GateAsync(DbCommand command, CancellationToken cancellationToken)
    {
        if (!IsPerChunkStatement(command) || command.Connection is null)
        {
            return;
        }

        await using DbCommand probe = command.Connection.CreateCommand();
        probe.CommandText = "SELECT current_setting('" + SessionSetting + "', true);";
        probe.CommandTimeout = command.CommandTimeout;
        probe.Transaction = command.Transaction;
        if (!SessionRequestsPerChunk(await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)))
        {
            command.CommandText = "SELECT 1";
        }
    }

    private static bool IsPerChunkStatement(DbCommand command) =>
        command.CommandText.Contains(PerChunkMarker, StringComparison.Ordinal);

    private static void ReleaseIfOurs(DbCommand command, CommandErrorEventData eventData)
    {
        if (command.Connection is not { State: ConnectionState.Open } connection)
        {
            return;
        }

        if (!command.CommandText.Contains(PerChunkMarker, StringComparison.Ordinal)
            && !command.CommandText.Contains("marqspec_build_ix_trades_instrument_time", StringComparison.Ordinal))
        {
            return;
        }

        using DbCommand unlock = connection.CreateCommand();
        unlock.CommandText = ReleaseLockSql;
        unlock.CommandTimeout = command.CommandTimeout;
        try
        {
            unlock.ExecuteScalar();
        }
        catch (Exception exception) when (IsRecoverableUnlockFailure(exception))
        {
            // The build error is the one startup has to log. A database error, a closed connection, or a
            // timeout on the unlock must not replace it. Cancellation and fatal exceptions still propagate.
            LogUnlockFailure(eventData, exception);
        }
    }

    /// <summary>
    /// Database, state, and timeout failures are recoverable here. Cancellation is not: it has to stay
    /// visible. Fatal exceptions are not in this set, so they are not swallowed either.
    /// </summary>
    private static bool IsRecoverableUnlockFailure(Exception exception) =>
        exception is DbException or InvalidOperationException or TimeoutException
        && exception is not OperationCanceledException;

    private static void LogUnlockFailure(CommandErrorEventData eventData, Exception exception)
    {
        // No logger is an explicit NullLogger, not a swallowed warning. Resolving one must not replace
        // the command error, so a failure there keeps the same fallback.
        ILogger logger = NullLogger<TradeInstrumentIndexBuildInterceptor>.Instance;
        try
        {
            ILoggerFactory? factory = eventData.Context?.GetService<ILoggerFactory>();
            if (factory is not null)
            {
                logger = factory.CreateLogger<TradeInstrumentIndexBuildInterceptor>();
            }
        }
        catch (Exception resolveFailure) when (resolveFailure is InvalidOperationException or ObjectDisposedException)
        {
            logger = NullLogger<TradeInstrumentIndexBuildInterceptor>.Instance;
        }

        logger.LogWarning(
            exception,
            "The trade index build lock was not released. The original command error is unchanged.");
    }
}
