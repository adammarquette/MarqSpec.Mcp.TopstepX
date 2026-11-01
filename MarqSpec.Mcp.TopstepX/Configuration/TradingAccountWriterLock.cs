using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// Holds a Postgres session advisory lock per account on one non-pooled connection (gh#772).
/// </summary>
/// <remarks>
/// The daily-cap lock is a different class id and is released after the charge. This one stays until
/// shutdown, or until the connection drops, which drops the lock with it. The next write looks up
/// <c>pg_locks</c> for this backend (<c>objsubid = 2</c>, the two-key advisory lock) and refuses
/// when that row is gone and the lock cannot be taken back. A probe that does not answer within
/// <see cref="CommandTimeoutSeconds"/> seconds, a wait for this process's mutex that lasts that
/// long, or a session that will not open, is <c>account_writer_unavailable</c>: the write is
/// refused and is not sent. Queued writers do not stay lined up behind that wait. The Railway template
/// still requires one server replica (gh#774); this lock is the gate, and that check is defence
/// in depth. Account ids are not kept in a field: the session is the record of which locks are
/// held, and closing it releases them.
/// </remarks>
public sealed class TradingAccountWriterLock : ITradingAccountWriterLock, IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Advisory-lock class for the account writer. Distinct from the daily-cap class
    /// <see cref="TradingDailyCapCoordinator.AdvisoryLockClassId"/>.
    /// </summary>
    public const int AdvisoryLockClassId = 0x0772_0024;

    /// <summary>How long one lock statement may run. A stall refuses the write.</summary>
    public const int CommandTimeoutSeconds = 2;

    private readonly string _connectionString;
    private readonly ILogger<TradingAccountWriterLock> _logger;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private NpgsqlConnection? _connection;
    private bool _disposed;

    /// <summary>Creates a lock that opens its session on the first write.</summary>
    /// <param name="connectionString">The store connection. Pooling is forced off for this session.</param>
    /// <param name="logger">Receives a refusal when another replica holds a lock, or the session drops.
    /// </param>
    public TradingAccountWriterLock(string connectionString, ILogger<TradingAccountWriterLock> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(logger);
        _connectionString = connectionString;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TradingWriterLockTake> TryHoldAsync(int accountId, CancellationToken cancellationToken)
    {
        // WaitAsync(timeout, token) finishes on a thread-pool timer. A busy pool can run that
        // callback after the caller's token has already fired, and the timeout then surfaces as
        // cancellation (gh#956). Waiting here measures the two seconds on this thread, so a late
        // timer cannot turn the expiry into a throw. A caller cancel still throws.
        if (!_mutex.Wait(TimeSpan.FromSeconds(CommandTimeoutSeconds), cancellationToken))
        {
            _logger.LogWarning(
                "Trading writes were refused. The writer lock wait expired.");
            return TradingWriterLockTake.Unavailable;
        }

        try
        {
            if (_disposed)
            {
                return TradingWriterLockTake.Unavailable;
            }

            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (!await EnsureSessionAsync(cancellationToken).ConfigureAwait(false))
                {
                    return TradingWriterLockTake.Unavailable;
                }

                bool? held = await QueryAsync(
                    """
                    SELECT EXISTS (
                        SELECT 1 FROM pg_locks
                        WHERE locktype = 'advisory'
                          AND classid = @classId
                          AND objid = @objectId
                          AND objsubid = 2
                          AND pid = pg_backend_pid()
                          AND granted)
                    """,
                    accountId,
                    cancellationToken).ConfigureAwait(false);
                if (held == true)
                {
                    return TradingWriterLockTake.Held;
                }

                if (held is null)
                {
                    DropSession();
                    continue;
                }

                bool? acquired = await QueryAsync(
                    "SELECT pg_try_advisory_lock(@classId, @objectId)",
                    accountId,
                    cancellationToken).ConfigureAwait(false);
                if (acquired == true)
                {
                    return TradingWriterLockTake.Held;
                }

                if (acquired is null)
                {
                    DropSession();
                    continue;
                }

                _logger.LogWarning(
                    "Trading writes were refused. Another replica holds the writer lock.");
                return TradingWriterLockTake.HeldByOther;
            }

            _logger.LogWarning(
                "Trading writes were refused. The writer lock session could not be confirmed.");
            return TradingWriterLockTake.Unavailable;
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>
    /// Holds this process's mutex until disposed. A test uses it so a second
    /// <see cref="TryHoldAsync"/> waits out <see cref="CommandTimeoutSeconds"/> without reflection.
    /// </summary>
    /// <returns>A handle that releases the mutex.</returns>
    internal async Task<IAsyncDisposable> HoldProcessMutexForTestsAsync()
    {
        await _mutex.WaitAsync().ConfigureAwait(false);
        return new MutexHold(_mutex);
    }

    /// <summary>Closes the session. Session advisory locks drop with the connection.</summary>
    public void Dispose() => CloseSession();

    /// <summary>Closes the session. Session advisory locks drop with the connection.</summary>
    public async ValueTask DisposeAsync()
    {
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await DisposeConnectionAsync().ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private void CloseSession()
    {
        _mutex.Wait();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _connection?.Dispose();
            _connection = null;
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task<bool> EnsureSessionAsync(CancellationToken cancellationToken)
    {
        if (_connection is { State: System.Data.ConnectionState.Open })
        {
            return true;
        }

        await DisposeConnectionAsync().ConfigureAwait(false);
        NpgsqlConnectionStringBuilder builder = new(_connectionString)
        {
            Pooling = false,
            Timeout = CommandTimeoutSeconds,
            CommandTimeout = CommandTimeoutSeconds,
            TcpKeepAlive = true,
            TcpKeepAliveTime = 5,
            TcpKeepAliveInterval = 1,
        };
        _connection = new NpgsqlConnection(builder.ConnectionString);
        try
        {
            await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "Trading writer lock could not open its session. Trading writes are refused.");
            await DisposeConnectionAsync().ConfigureAwait(false);
            return false;
        }
    }

    private async Task<bool?> QueryAsync(string sql, int accountId, CancellationToken cancellationToken)
    {
        try
        {
            await using NpgsqlCommand command = new(sql, _connection)
            {
                CommandTimeout = CommandTimeoutSeconds,
            };
            command.Parameters.Add(new NpgsqlParameter("classId", NpgsqlDbType.Integer)
            {
                Value = AdvisoryLockClassId,
            });
            command.Parameters.Add(new NpgsqlParameter("objectId", NpgsqlDbType.Integer)
            {
                Value = accountId,
            });
            object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return value is true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "Trading writer lock could not be probed. The session dropped.");
            return null;
        }
    }

    private void DropSession()
    {
        _connection?.Dispose();
        _connection = null;
    }

    private async Task DisposeConnectionAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }
    }

    private sealed class MutexHold(SemaphoreSlim mutex) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            mutex.Release();
            return ValueTask.CompletedTask;
        }
    }
}
