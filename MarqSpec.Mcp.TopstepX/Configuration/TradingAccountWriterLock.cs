using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// Holds a Postgres session advisory lock per account on one non-pooled connection (gh#772).
/// </summary>
/// <remarks>
/// The daily-cap lock is a different class id and is released after the charge. This one stays until
/// shutdown, or until the connection drops, which drops the lock with it. The next write probes
/// <c>pg_advisory_lock_held</c> on that same session and refuses when the lock is gone and cannot be
/// taken back. The Railway template still requires one server replica (gh#774); this lock is the
/// gate, and that check is defence in depth.
/// </remarks>
public sealed class TradingAccountWriterLock : ITradingAccountWriterLock, IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Advisory-lock class for the account writer. Distinct from the daily-cap class
    /// <see cref="TradingDailyCapCoordinator.AdvisoryLockClassId"/>.
    /// </summary>
    public const int AdvisoryLockClassId = 0x0772_0024;

    private readonly string _connectionString;
    private readonly ILogger<TradingAccountWriterLock> _logger;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly HashSet<int> _held = [];
    private NpgsqlConnection? _connection;
    private bool _disposed;

    /// <summary>Creates a lock that opens its session on the first write.</summary>
    /// <param name="connectionString">The store connection. Pooling is forced off for this session.</param>
    /// <param name="logger">Receives a refusal when another replica holds the account, or the session drops.</param>
    public TradingAccountWriterLock(string connectionString, ILogger<TradingAccountWriterLock> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(logger);
        _connectionString = connectionString;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> TryHoldAsync(int accountId, CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return false;
            }

            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (!await EnsureSessionAsync(cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }

                if (_held.Contains(accountId)
                    && await LockIsHeldAsync(accountId, cancellationToken).ConfigureAwait(false) == true)
                {
                    return true;
                }

                _held.Remove(accountId);
                bool? acquired = await TryLockAsync(accountId, cancellationToken).ConfigureAwait(false);
                if (acquired is null)
                {
                    DropSession();
                    continue;
                }

                if (acquired == false)
                {
                    _logger.LogWarning(
                        "Trading writes for account {AccountId} were refused. "
                        + "Another replica holds the writer lock.",
                        accountId);
                    return false;
                }

                _held.Add(accountId);
                return true;
            }

            _logger.LogWarning(
                "Trading writes for account {AccountId} were refused. The writer lock session is gone.",
                accountId);
            return false;
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>
    /// Closes the session. Session advisory locks drop with the connection. The async dispose
    /// unlocks first so a failed unlock is logged.
    /// </summary>
    public void Dispose()
    {
        _mutex.Wait();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _held.Clear();
            _connection?.Dispose();
            _connection = null;
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>Unlocks every account this process held and closes the session.</summary>
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
            if (_connection is { State: System.Data.ConnectionState.Open })
            {
                foreach (int accountId in _held)
                {
                    try
                    {
                        await ExecuteAsync(
                            "SELECT pg_advisory_unlock(@classId, @objectId)",
                            accountId,
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        _logger.LogWarning(
                            exception,
                            "Trading writer lock for account {AccountId} did not unlock. "
                            + "Closing the session drops it.",
                            accountId);
                    }
                }
            }

            _held.Clear();
            await DisposeConnectionAsync().ConfigureAwait(false);
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
        _held.Clear();
        NpgsqlConnectionStringBuilder builder = new(_connectionString) { Pooling = false };
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

    private async Task<bool?> LockIsHeldAsync(int accountId, CancellationToken cancellationToken)
    {
        try
        {
            object? value = await ExecuteAsync(
                "SELECT pg_advisory_lock_held(@classId, @objectId)",
                accountId,
                cancellationToken).ConfigureAwait(false);
            return value is true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "Trading writer lock for account {AccountId} could not be probed. The session dropped it.",
                accountId);
            return null;
        }
    }

    private async Task<bool?> TryLockAsync(int accountId, CancellationToken cancellationToken)
    {
        try
        {
            object? value = await ExecuteAsync(
                "SELECT pg_try_advisory_lock(@classId, @objectId)",
                accountId,
                cancellationToken).ConfigureAwait(false);
            return value is true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "Trading writer lock for account {AccountId} could not be taken. The session dropped.",
                accountId);
            return null;
        }
    }

    private async Task<object?> ExecuteAsync(string sql, int accountId, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(sql, _connection) { CommandTimeout = 30 };
        command.Parameters.Add(new NpgsqlParameter("classId", NpgsqlDbType.Integer)
        {
            Value = AdvisoryLockClassId,
        });
        command.Parameters.Add(new NpgsqlParameter("objectId", NpgsqlDbType.Integer)
        {
            Value = accountId,
        });
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private void DropSession()
    {
        _held.Clear();
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
}
