using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Npgsql;

namespace MarqSpec.Mcp.TopstepX.Tools;

/// <summary>
/// Turns a fault in <i>this server's own database</i> into a sentence a caller can act on, for every tool at
/// once — <c>R-5.7</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Registered as a call-tool filter, not called from a tool.</b> Every <c>tools/call</c> goes through the
/// SDK's filter pipeline, so a tool added tomorrow is covered by having been registered rather than by its
/// author remembering a <c>try</c>. That is the gh#69 lesson stated as wiring: a rule enforced in three of
/// four places is not a rule, and <c>MarketDataTools.ReadAsync</c> — the only place that translated anything
/// — is reached by exactly two of the fifteen tools on this surface.
/// </para>
/// <para>
/// <b>It therefore says only what a filter can know, which is less than one call site knows.</b> It sees an
/// exception type and a SqlState; it does not see which unit of work was open, what shared it, or whether a
/// write reached disk. Every sentence below is written to that limit on purpose. A message drafted from
/// <c>BarCacheService</c>'s point of view — "the coverage ledger and the indicator projection over the same
/// series" — is a fact about <see cref="SeriesUnitOfWork"/> being handed to fifteen tools, true today only
/// because every unique key in the schema happens to be bars-family. Detail that belongs to one unit of work
/// belongs in the exception that unit of work raises, where it is known.
/// </para>
/// <para>
/// <b>An unknown outcome is stated as unknown.</b> Postgres can commit and then lose the connection before
/// the acknowledgement arrives; Npgsql raises a bare <see cref="NpgsqlException"/> with no SqlState and EF
/// wraps it, and at that point the rows may well be on disk. Reporting a completed operation as not having
/// happened is the first failure <c>.github/copilot-instructions.md</c> asks a reviewer about, so the
/// no-SqlState branch claims no outcome at all. Where the server itself answered, a rollback <i>is</i>
/// established — an error response and an aborted transaction are one event — and only there is it claimed.
/// </para>
/// <para>
/// <b>Transient and permanent are told apart by SqlState class, not by CLR type.</b>
/// <see cref="NpgsqlException"/> is the provider's base type, so a <see cref="PostgresException"/> arrives on
/// the same catch and an unapplied migration answering <c>42P01</c> would otherwise be reported as a passing
/// condition to retry — a caller sent round a loop it can never come out of, which is the failure this guard
/// exists to prevent, one layer up. Neither list is a default: a code in neither is reported as unclassified,
/// because "retry unless" is the permissive shape this repository reviews against.
/// </para>
/// <para>
/// <b>A lost race is reported, not retried and not reported as a success</b> (gh#89). The row a duplicate key
/// collided on really is in the store — the other writer put it there — so on an idempotent upsert it looks
/// like a success achieved by proxy. It is not one: the collision aborts the <b>whole</b> transaction, so
/// answering "fine" would return work assembled inside a transaction that rolled back. Retrying here is
/// equally wrong — a boundary retry re-runs the <i>whole tool call</i>, including a paced page-walk that
/// already cost vendor requests. <see cref="SeriesUnitOfWork"/> is where a retry belongs and it is bounded
/// there on purpose. So the caller is told plainly what happened and that a retry is served from what the
/// other writer committed — which is true, and cheap, and is a decision the caller is entitled to make.
/// </para>
/// <para>
/// <b>Nothing on the fill path can currently produce that <c>23505</c>, and the branch stays anyway.</b> The
/// three writes that could — the bars, the coverage ledger, the indicator projection — are all
/// <c>ON CONFLICT … DO UPDATE</c> now (gh#103, gh#122, gh#133; epic gh#80). The schema still has unique keys
/// and a writer added later can still hit one, and this filter is served on behalf of every tool rather than
/// of the fill path, so deleting the branch would be narrowing a general guard to today's call sites. What it
/// does mean is that the branch is pinned by <c>StoreFaultReportingTests</c> against a fabricated exception
/// rather than by a real race, which is stated in <c>StoreFaultBoundaryTests</c> rather than glossed.
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
            // NO SqlState ANYWHERE IN THE CHAIN, WHICH MEANS THE OUTCOME IS UNKNOWN. Npgsql raises a bare
            // NpgsqlException over an IOException or a TimeoutException when the conversation ended before
            // the server answered -- and "before the server answered" includes after COMMIT was sent and
            // committed. The write may be on disk; the acknowledgement is what was lost. Saying "nothing was
            // written" here would report a completed operation as not having happened, which is the first
            // rule in .github/copilot-instructions.md and the reason this branch states a fact rather than an
            // outcome. StoreAvailability covers the store being down at startup; this is it going away after.
            return "The store stopped answering while this call was running, so the call did not complete "
                + "and the fate of anything it had written is UNKNOWN — the acknowledgement was lost, which "
                + "is not the same as the write being lost. This is a condition of this server's database, "
                + "not of the venue and not of the request: check that Postgres is running and reachable on "
                + "ConnectionStrings__Default. Reading back is safe and is how to establish what landed; a "
                + "call that records something new may record it twice if it is simply repeated.";
        }

        // The server itself answered, so its transaction did abort -- an error response and a rollback are
        // the same event in Postgres. That is the one durable claim this boundary can make, and it holds for
        // every branch below.
        string state = " (Postgres " + postgres.SqlState + ".)";

        if (string.Equals(postgres.SqlState, PostgresErrorCodes.UniqueViolation, StringComparison.Ordinal))
        {
            // Only what a CALL-TOOL FILTER can know. Which rows, and what else shared the transaction, is a
            // fact about the unit of work the tool built -- SeriesUnitOfWork's is bars, coverage ledger and
            // projection, but this guard is served on behalf of all fifteen tools and does not know whose
            // key was hit. That detail belongs where the fact is, not in a sentence handed to every tool.
            return "Another writer committed rows this call collided on, so this call's transaction was "
                + "rolled back and none of its own work was kept. The rows it collided on are in the store — "
                + "the other writer committed them — so a retry reads what that writer committed and does "
                + "only what is still missing." + state;
        }

        if (IsPermanent(postgres.SqlState))
        {
            return "The store refused this call because of a defect in this server itself — its schema, its "
                + "configuration or its credentials — not because of a transient condition. Retrying will "
                + "not help: the same call fails the same way until this server is fixed. Check that the "
                + "migrations have been applied to the database named in ConnectionStrings__Default and that "
                + "its user can reach it." + state;
        }

        if (IsTransient(postgres.SqlState))
        {
            return "The store could not complete this call and answered with a transient condition, so its "
                + "transaction was rolled back and none of its work was kept. This is a condition of this "
                + "server's database, not of the venue and not of the request; retry, and if it repeats the "
                + "store itself needs attention." + state;
        }

        // FAIL CLOSED. An unrecognised SqlState is not evidence that asking again works, and "retry unless X"
        // is the permissive default this repository's checklist names as its recurring defect shape. State
        // the code -- it identifies the condition exactly and an operator can look it up -- and say plainly
        // that whether a retry helps is not known here.
        return "The store refused this call with a condition this server does not classify, so its "
            + "transaction was rolled back and none of its work was kept. Whether a retry would succeed is "
            + "UNKNOWN: look the SqlState up before repeating the call." + state;
    }

    /// <summary>
    /// Whether a SqlState means <i>this deployment is broken</i> rather than <i>the store is busy</i>.
    /// </summary>
    /// <param name="sqlState">What the server answered.</param>
    /// <returns><see langword="true"/> when retrying cannot help.</returns>
    /// <remarks>
    /// Classified on the SqlState <b>class</b> — the first two characters — because that is what the
    /// PostgreSQL error-code table groups by, and the guard already reads it. Class <c>42</c> is
    /// syntax-error-or-access-rule-violation: an undefined table, column or function, and <c>42501</c>
    /// insufficient privilege. <c>3D</c> is an invalid catalogue name and <c>28</c> an invalid authorization
    /// specification. Every one of them is a fact about what this server asked for or who it asked as, and
    /// none becomes true by being asked again — the unapplied migration answering <c>42P01</c> is the case
    /// that made this a finding.
    /// </remarks>
    private static bool IsPermanent(string sqlState) =>
        Class(sqlState) is "42" or "3D" or "28";

    /// <summary>
    /// Whether a SqlState is a condition of an environment, worth asking again.
    /// </summary>
    /// <param name="sqlState">What the server answered.</param>
    /// <returns><see langword="true"/> when a retry is the right advice.</returns>
    /// <remarks>
    /// A whitelist, not "everything that is not permanent" — the classes named here are the ones whose
    /// meaning is *the store could not do this now*. <c>08</c> connection exception, <c>53</c> insufficient
    /// resources, <c>57</c> operator intervention, <c>40</c> transaction rollback (which is where
    /// <c>40001</c> serialisation failure lives, the condition <c>R-2.10</c> is about). Anything outside both
    /// lists is reported as unclassified rather than swept into either, so adding a code is a deliberate act.
    /// </remarks>
    private static bool IsTransient(string sqlState) =>
        Class(sqlState) is "08" or "53" or "57" or "40";

    private static string Class(string sqlState) =>
        sqlState.Length >= 2 ? sqlState[..2] : sqlState;

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
