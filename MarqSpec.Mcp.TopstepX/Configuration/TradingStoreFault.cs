using System.Data.Common;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// A connection failure, a broken socket, a command timeout, or the EF wrapper around one (gh#958).
/// Programming errors are not store outages. Caller cancellation is decided by the gate, from the
/// caller's token, because a command timeout can surface as cancellation too.
/// </summary>
internal static class TradingStoreFault
{
    /// <summary>True when <paramref name="exception"/> is transient store infrastructure.</summary>
    /// <param name="exception">The exception that left a store call.</param>
    /// <returns>True for a store outage. False for a programming error.</returns>
    public static bool IsInfrastructure(Exception exception)
    {
        // EF wraps a refused connection in InvalidOperationException ("transient failure").
        // That wrapper is a store outage. An InvalidOperationException with no store cause
        // is our own programming error and must propagate.
        bool store = false;
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is OutOfMemoryException)
            {
                return false;
            }

            if (current is DbException
                or DbUpdateException
                or TimeoutException
                or IOException
                or SocketException
                or OperationCanceledException)
            {
                store = true;
            }
        }

        return store;
    }
}
