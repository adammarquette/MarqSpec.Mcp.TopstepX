using System.Data.Common;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// A connection failure, a broken socket, a command timeout, or the EF wrapper around one (gh#958).
/// Schema and constraint failures are not outages. A bare
/// <see cref="OperationCanceledException"/> is an outage only when the caller's token has not
/// fired: pass that token, or call the overload that treats <see cref="CancellationToken.None"/>
/// as already safe. Command timeouts surface as this exception on that token.
/// </summary>
internal static class TradingStoreFault
{
    /// <summary>
    /// True when <paramref name="exception"/> is transient store infrastructure.
    /// A cancelled caller token is never an outage.
    /// </summary>
    /// <param name="exception">The exception that left a store call.</param>
    /// <param name="cancellationToken">
    /// The caller's token. When it has fired, this returns false so cancellation still throws.
    /// <see cref="CancellationToken.None"/> has not fired, so a bare cancellation is an outage.
    /// </param>
    /// <returns>True for a store outage. False for a programming error or a caller cancel.</returns>
    public static bool IsInfrastructure(
        Exception exception,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        return Classify(exception);
    }

    private static bool Classify(Exception exception)
    {
        if (exception is OutOfMemoryException)
        {
            return false;
        }

        if (exception is AggregateException aggregate)
        {
            foreach (Exception inner in aggregate.InnerExceptions)
            {
                if (inner is OutOfMemoryException)
                {
                    return false;
                }
            }

            foreach (Exception inner in aggregate.InnerExceptions)
            {
                if (Classify(inner))
                {
                    return true;
                }
            }

            return false;
        }

        // Class 42 is schema (42P01 undefined table). Class 23 is a constraint (23505).
        if (exception is PostgresException postgres)
        {
            return IsOutageSqlState(postgres.SqlState);
        }

        if (exception is NpgsqlException npgsql)
        {
            if (npgsql.InnerException is not null && Classify(npgsql.InnerException))
            {
                return true;
            }

            return string.IsNullOrEmpty(npgsql.SqlState) || IsOutageSqlState(npgsql.SqlState);
        }

        // Only these wrappers. An ArgumentException that happens to carry a store inner still throws.
        if (exception is DbUpdateException update)
        {
            // No inner means the save failed and the provider did not say why. An inner is classified
            // on its own, so a unique violation stays a constraint failure.
            return update.InnerException is null || Classify(update.InnerException);
        }

        if (exception is InvalidOperationException)
        {
            return exception.InnerException is not null && Classify(exception.InnerException);
        }

        if (exception is TimeoutException or SocketException or IOException or OperationCanceledException)
        {
            return true;
        }

        // A test double's DbException, and any provider exception that is not a Postgres error.
        return exception is DbException;
    }

    /// <summary>Connection and shutdown states are outages. Schema and constraint classes are not.</summary>
    private static bool IsOutageSqlState(string? sqlState)
    {
        if (string.IsNullOrEmpty(sqlState))
        {
            return false;
        }

        if (sqlState.StartsWith("42", StringComparison.Ordinal)
            || sqlState.StartsWith("23", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }
}
