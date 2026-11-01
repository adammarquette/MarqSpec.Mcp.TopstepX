using System.Data;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// Takes <c>pg_try_advisory_lock</c> for a backfill pass on a connection with pooling off (gh#899).
/// </summary>
/// <remarks>
/// The class id is <c>0x08990001</c>, distinct from the migration lock <c>0x08430001</c>, the index-build
/// lock <c>0x07580001</c>, and the trading-cap lock <c>0x07110024</c>. The connection string is never logged.
/// Closing the connection releases the lock if the explicit unlock does not run.
/// </remarks>
public sealed class PostgresBarBackfillLock(string connectionString, ILogger<PostgresBarBackfillLock> logger)
    : IBarBackfillLock
{
    /// <summary>The advisory-lock class id, <c>0x08990001</c>.</summary>
    public const int ClassId = 0x08990001;

    /// <summary>The advisory-lock object id.</summary>
    public const int ObjectId = 899;

    private readonly string _connectionString = connectionString;
    private readonly ILogger<PostgresBarBackfillLock> _logger = logger;

    /// <inheritdoc />
    public async Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(_connectionString);
        string dedicated = _connectionString.TrimEnd().TrimEnd(';') + ";Pooling=false";
        NpgsqlConnection connection = new(dedicated);
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

            return new Hold(connection, _logger);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class Hold(NpgsqlConnection connection, ILogger logger) : IAsyncDisposable
    {
        private readonly NpgsqlConnection _connection = connection;
        private readonly ILogger _logger = logger;

        public async ValueTask DisposeAsync()
        {
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
}
