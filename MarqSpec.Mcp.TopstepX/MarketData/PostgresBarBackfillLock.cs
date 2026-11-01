using System.Data;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// Takes <c>pg_try_advisory_lock</c> for a backfill pass on a connection with pooling off (gh#899).
/// </summary>
/// <remarks>
/// The class id is <c>0x08990001</c>. It is distinct from the migration lock <c>0x08430001</c>, the
/// index-build lock <c>0x07580001</c>, the trading-cap lock <c>0x07110024</c>, and the per-account
/// writer lock <c>0x07720024</c> (gh#943). The connection string is never logged. <c>Keepalive</c>
/// is 30 seconds. Closing the connection releases the lock if the explicit unlock does not run.
/// </remarks>
public sealed class PostgresBarBackfillLock(string connectionString, ILogger<PostgresBarBackfillLock> logger)
    : IBarBackfillLock
{
    /// <summary>The advisory-lock class id, <c>0x08990001</c>.</summary>
    public const int ClassId = 0x08990001;

    /// <summary>The advisory-lock object id.</summary>
    public const int ObjectId = 899;

    /// <summary>TCP keepalive, in seconds, on the unpooled lock connection.</summary>
    public const int KeepaliveSeconds = 30;

    private readonly string _connectionString = connectionString;
    private readonly ILogger<PostgresBarBackfillLock> _logger = logger;

    /// <summary>The connection string this lock opens. Pooling is off and keepalive is set.</summary>
    public string DedicatedConnectionString
    {
        get
        {
            string keepalive = KeepaliveSeconds.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            return _connectionString.TrimEnd().TrimEnd(';')
                + ";Pooling=false;Keepalive=" + keepalive;
        }
    }

    /// <inheritdoc />
    public async Task<IBarBackfillLockHold?> TryAcquireAsync(CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(_connectionString);
        NpgsqlConnection connection = new(DedicatedConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using NpgsqlCommand command = new(
                "SELECT pg_try_advisory_lock(@classId, @objectId)",
                connection);
            command.Parameters.Add(new NpgsqlParameter("classId", NpgsqlDbType.Integer) { Value = ClassId });
            command.Parameters.Add(
                new NpgsqlParameter("objectId", NpgsqlDbType.Integer) { Value = ObjectId });
            object? granted = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (granted is not true)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            return new BarBackfillLockHold(connection, _logger);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>One held backfill lock on its own unpooled connection.</summary>
public sealed class BarBackfillLockHold(NpgsqlConnection connection, ILogger logger) : IBarBackfillLockHold
{
    private readonly NpgsqlConnection _connection = connection;
    private readonly ILogger _logger = logger;
    private int _disposed;

    /// <inheritdoc />
    public async Task<bool> StillHeldAsync(CancellationToken cancellationToken)
    {
        if (_connection.State != ConnectionState.Open)
        {
            return false;
        }

        try
        {
            await using NpgsqlCommand command = new(
                """
                SELECT EXISTS (
                    SELECT 1 FROM pg_locks
                    WHERE locktype = 'advisory'
                      AND classid = @classId
                      AND objid = @objectId
                      AND objsubid = 2
                      AND granted
                      AND pid = pg_backend_pid())
                """,
                _connection);
            command.Parameters.Add(
                new NpgsqlParameter("classId", NpgsqlDbType.Integer)
                {
                    Value = PostgresBarBackfillLock.ClassId,
                });
            command.Parameters.Add(
                new NpgsqlParameter("objectId", NpgsqlDbType.Integer)
                {
                    Value = PostgresBarBackfillLock.ObjectId,
                });
            object? held = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return held is true;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "The backfill lock could not be re-checked ({ExceptionType}).",
                exception.GetType().Name);
            return false;
        }
    }

    /// <summary>
    /// Closes the connection without the explicit unlock. The session end releases the lock.
    /// </summary>
    /// <returns>The task.</returns>
    internal async Task AbandonAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (_connection.State == ConnectionState.Open)
            {
                await using NpgsqlCommand release = new(
                    "SELECT pg_advisory_unlock(@classId, @objectId)",
                    _connection);
                release.Parameters.Add(
                    new NpgsqlParameter("classId", NpgsqlDbType.Integer)
                    {
                        Value = PostgresBarBackfillLock.ClassId,
                    });
                release.Parameters.Add(
                    new NpgsqlParameter("objectId", NpgsqlDbType.Integer)
                    {
                        Value = PostgresBarBackfillLock.ObjectId,
                    });
                await release.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "The backfill lock could not be released explicitly ({ExceptionType}). "
                + "Closing its connection releases it.",
                exception.GetType().Name);
        }

        await _connection.DisposeAsync().ConfigureAwait(false);
    }
}
