using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Npgsql;

namespace MarqSpec.Mcp.TopstepX.Tools;

/// <summary>
/// Turns a fault in <i>this server's own database</i> into a sentence a caller can act on, for every tool at
/// once.
/// </summary>
/// <remarks>
/// <para>
/// <b>Registered as a call-tool filter, not called from a tool.</b> Every <c>tools/call</c> goes through the
/// SDK's filter pipeline, so a tool added tomorrow is covered by having been registered rather than by its
/// author remembering a <c>try</c>. That is the gh#69 lesson stated as wiring: a rule enforced in three of
/// four places is not a rule, and <c>MarketDataTools.ReadAsync</c> — the only place that translated anything
/// — is reached by exactly two of the eleven tools on this surface.
/// </para>
/// <para>
/// <b>What a lost race means to the caller: it is reported, not retried and not reported as a success</b>
/// (gh#89). Two fills of overlapping ranges both read <c>storedBuckets</c> outside the transaction, both find
/// a bucket absent and both <c>INSERT</c> it; the loser gets <c>23505</c>. The rows it collided on really are
/// in the store — the winner put them there — so on an idempotent upsert a duplicate key looks like a success
/// achieved by proxy. It is not one, for two reasons. The collision aborts the <b>whole</b> transaction, and
/// that transaction is not only the bars: it is the coverage ledger and the indicator projection over the same
/// series, none of which the winner wrote on this caller's behalf. And answering "fine" would return a series
/// assembled inside a transaction that rolled back, with <c>fetchedBuckets</c> counting writes that never
/// landed. Retrying here is equally wrong: a boundary retry re-runs the <i>whole tool call</i>, including a
/// paced page-walk that already cost vendor requests. <see cref="SeriesUnitOfWork"/> is where a retry belongs
/// and it is bounded there on purpose. So the caller is told plainly what happened and that a retry will be
/// served from what the other writer committed — which is true, and cheap, and is a decision the caller is
/// entitled to make.
/// </para>
/// <para>
/// <b>Narrow catches, never <c>catch (Exception)</c>.</b> A store fault is a transient condition of an
/// environment; a programming error is a defect in this repository. Reporting the second as the first tells an
/// operator to retry a call that will never succeed and buries the defect under a transient-looking sentence
/// — the very failure this guard exists to prevent, one layer up. <see cref="InvalidOperationException"/> in
/// particular is <b>deliberately absent</b>: <c>IndicatorProjector</c>'s whole-series guard is one, and it
/// means an invariant was violated.
/// </para>
/// <para>
/// <b>The provider's own text is not echoed.</b> An <see cref="McpException"/> message is propagated to the
/// remote endpoint, and a connection-level Npgsql message carries the host, port and database it failed to
/// reach. The SqlState is stated instead — it identifies the condition exactly and is not a coordinate — and
/// the original is kept as the inner exception, where the server's own log still has all of it.
/// </para>
/// </remarks>
public static class StoreFaultGuard
{
    /// <summary>
    /// The filter the composition root registers. Wraps every tool call.
    /// </summary>
    /// <remarks>
    /// A property rather than a method so the wiring reads as one line and there is exactly one of it.
    /// </remarks>
    public static McpRequestFilter<CallToolRequestParams, CallToolResult> Filter { get; } =
        next => (request, cancellationToken) => GuardAsync(() => next(request, cancellationToken));

    /// <summary>
    /// Runs a call, translating a fault in the store — and only that — into a stated condition.
    /// </summary>
    /// <typeparam name="T">What the call returns.</typeparam>
    /// <param name="call">The call.</param>
    /// <returns>Whatever the call returned, untouched.</returns>
    /// <exception cref="McpException">The store faulted.</exception>
    public static async ValueTask<T> GuardAsync<T>(Func<ValueTask<T>> call)
    {
        ArgumentNullException.ThrowIfNull(call);

        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (StoreContentionException contention)
        {
            // Already a sentence, written where the fact is known -- SeriesUnitOfWork names the series and
            // how many attempts were spent. Translating it here rather than in one tool is what stops the
            // rule living in one place and being absent from the rest.
            throw new McpException(contention.Message, contention);
        }
        catch (DbUpdateException save)
        {
            // EF wraps every write failure in this, whatever the provider said underneath.
            throw new McpException(Describe(save), save);
        }
        catch (NpgsqlException query)
        {
            // And a READ failure is not wrapped at all: a dropped connection or a missing catalogue arrives
            // raw from the provider, on tools like get_indicators that never write anything.
            throw new McpException(Describe(query), query);
        }
    }

    /// <summary>
    /// Says what a store fault was, in words a caller can act on.
    /// </summary>
    /// <param name="fault">The fault, whose inner exceptions are searched for the provider's own.</param>
    /// <returns>The message.</returns>
    public static string Describe(Exception fault)
    {
        PostgresException? postgres = Postgres(fault);

        if (postgres is null)
        {
            // No SqlState at all: the server never answered. StoreAvailability covers the case where it was
            // already down when this process started; this is the one where it went away afterwards.
            return "The store could not be reached while answering this call, so nothing was written. This "
                + "is a condition of this server's database, not of the venue and not of the request — check "
                + "that Postgres is running and reachable on ConnectionStrings__Default, then retry.";
        }

        string state = " (Postgres " + postgres.SqlState + ".)";

        if (string.Equals(postgres.SqlState, PostgresErrorCodes.UniqueViolation, StringComparison.Ordinal))
        {
            return "Another request wrote the same rows while this call was writing them, so this call's "
                + "transaction was rolled back and none of its work was kept. The rows it collided on are in "
                + "the store — the other writer committed them — but the rest of this call's unit of work, "
                + "the coverage ledger and the indicator projection over the same series, is not. Retry: the "
                + "retry reads what the other writer committed and fills only what is still missing." + state;
        }

        return "The store could not complete this call, so its transaction was rolled back and nothing was "
            + "partly written. This is a condition of this server's database, not of the venue and not of the "
            + "request; retry, and if it repeats the store itself needs attention." + state;
    }

    private static PostgresException? Postgres(Exception fault)
    {
        for (Exception? current = fault; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return postgres;
            }
        }

        return null;
    }
}
