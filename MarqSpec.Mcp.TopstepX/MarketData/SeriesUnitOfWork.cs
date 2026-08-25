using System.Data;
using MarqSpec.Mcp.TopstepX.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// The one transaction shape everything that writes a bar series uses — and the only place that decides its
/// isolation level and what a serialization failure means.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="IsolationLevel.RepeatableRead"/>, not the default.</b> A projection reads the bars and then
/// the values standing over them and reconciles the second against the first. Under <c>READ COMMITTED</c>
/// those are two snapshots, so a concurrent fill can commit between them — and the pass then deletes values it
/// never saw the bars for (gh#73). One snapshot makes that unrepresentable rather than unlikely.
/// </para>
/// <para>
/// <b>Not <c>SERIALIZABLE</c>.</b> SSI would additionally catch the write skew of gh#80, and would pay for it
/// with predicate locks over a whole-series scan — which escalate from page to relation on <c>Bars</c> and
/// then abort fills of <i>unrelated</i> instruments. The anomaly gh#73 is about is read skew between two
/// statements, which snapshot isolation already forbids.
/// </para>
/// <para>
/// <b>The cost is real and is paid here.</b> Under <c>READ COMMITTED</c> two writers touching one row were a
/// silent last-writer-wins. Under snapshot isolation the loser is aborted with <c>40001</c>, and that is not
/// rare: <see cref="IndicatorProjector"/>'s reconcile is <i>unscoped by bucket range</i>, so a whole-series
/// sweep is a whole-series <b>write set</b>. Two fills whose fetched ranges share no bucket still delete the
/// same unjustified rows. <see cref="BarCacheService"/>'s coverage ledger reaches it with no bars at all: two
/// callers asking for the same range the venue answers empty both <i>write</i> one row — and since gh#122 that
/// is one statement whether the row was already there or not. Reasoning about the
/// ranges a fill fetched says nothing about the rows it writes. And since gh#133 the projection's
/// <i>value</i> write is one statement too, so a pass whose snapshot missed another pass's rows meets a
/// <c>40001</c> there rather than a <c>23505</c> — the same trade the bar write and the ledger made, and the
/// reason the retry below is what absorbs all three.
/// </para>
/// <para>
/// <b>So it retries — once, and the bound is the argument.</b> A retry here is not a gamble. In every shape of
/// this conflict the transaction that won committed <i>exactly the work the loser was missing</i>: the rows it
/// meant to delete are already gone, the coverage row already answers the range, the bars it could not see are
/// now stored. The second attempt therefore runs over a strictly better-informed store and normally succeeds.
/// A <b>second</b> collision is not that — it is sustained contention on one series, which is worth reporting
/// rather than looping on, so it becomes a <see cref="StoreContentionException"/> naming the condition.
/// </para>
/// <para>
/// <b>And not a lock either — decided, not deferred.</b> Snapshot isolation permits the write skew this
/// retry cannot reach: two fills of <i>adjacent</i> ranges share no bar, no coverage row and no indicator key,
/// so there is nothing for the store to refuse and the later one seeds its projection from the first bar its
/// own snapshot can see. Serialising fills per series would need a session-level advisory lock taken
/// <i>before</i> <c>BeginTransaction</c>, and that is not taken here: a session lock was observed
/// still holding its key after the connection that took it had gone back to Npgsql's pool, released only when
/// unrelated traffic happened to reuse that connection. The values the skew leaves are stale rather than lost
/// — a projection is reproducible from the bars, so the next pass corrects them — and a wedged lock is not
/// recomputable from anything. Reasoning, measurements and the alternatives:
/// <c>documentation/adr/0012-fills-are-not-serialised.md</c> (gh#104), requirement <c>R-2.11</c>.
/// </para>
/// <para>
/// <b>The venue is never called from inside.</b> Callers fetch first and hand in what they already hold, so a
/// retry costs zero vendor requests and a paced page-walk — up to a minute of deliberate sleeping for a cold
/// year — never sits inside an open snapshot pinning <c>xmin</c>.
/// </para>
/// </remarks>
internal static class SeriesUnitOfWork
{
    /// <summary>The isolation level every series write uses. Stated once, on purpose.</summary>
    public const IsolationLevel Isolation = IsolationLevel.RepeatableRead;

    /// <summary>How many attempts a serialization failure is worth, including the first.</summary>
    public const int MaxAttempts = 2;

    /// <summary>
    /// Runs a unit of work over one series, retrying once if the store refuses to serialise it.
    /// </summary>
    /// <typeparam name="T">What the body returns.</typeparam>
    /// <param name="database">The store. Its change tracker is cleared between attempts.</param>
    /// <param name="what">The series, in words that reach a caller — e.g. <c>"ES 5m"</c>.</param>
    /// <param name="body">
    /// The work. <b>Must not call the venue or anything else that cannot simply be run again</b>, and must
    /// leave its result derivable from the store, because it may run twice.
    /// </param>
    /// <param name="logger">Where a retry is announced. A retry that says nothing is invisible to a test.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>Whatever the body returned on the attempt that committed.</returns>
    /// <exception cref="StoreContentionException">Every attempt lost to a concurrent writer.</exception>
    public static async Task<T> RunAsync<T>(
        TopstepXDbContext database,
        string what,
        Func<CancellationToken, Task<T>> body,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await AttemptAsync(database, body, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsSerializationFailure(exception))
            {
                // The tracker still holds the aborted attempt's entities, and they were decided against a
                // snapshot that is now known to be stale. Replaying them would be the same defect in a
                // different hat, so the retry re-reads everything rather than re-saving anything.
                database.ChangeTracker.Clear();

                if (attempt >= MaxAttempts)
                {
                    throw new StoreContentionException(what, attempt, exception);
                }

                logger.LogInformation(
                    "A serialization failure aborted the write of {Series}; retrying once against the store "
                    + "the other writer has now committed.",
                    what);
            }
        }
    }

    /// <summary>
    /// Whether an exception is Postgres refusing to serialise this transaction against a concurrent one.
    /// </summary>
    /// <param name="exception">The exception, at any depth.</param>
    /// <returns><see langword="true"/> for <c>40001</c> or <c>40P01</c>.</returns>
    /// <remarks>
    /// Only the two states Postgres itself documents as "retry the transaction". Deliberately <b>not</b>
    /// <c>PostgresException.IsTransient</c>, which also covers connection faults — re-running a unit of work
    /// is the wrong response to those. Deliberately not a catch on
    /// <see cref="InvalidOperationException"/> either: EF wraps a serialization failure in one, and so does
    /// <see cref="IndicatorProjector"/>'s whole-series guard, which must never be retried away.
    /// </remarks>
    public static bool IsSerializationFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres
                && (string.Equals(postgres.SqlState, PostgresErrorCodes.SerializationFailure, StringComparison.Ordinal)
                    || string.Equals(postgres.SqlState, PostgresErrorCodes.DeadlockDetected, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<T> AttemptAsync<T>(
        TopstepXDbContext database,
        Func<CancellationToken, Task<T>> body,
        CancellationToken cancellationToken)
    {
        // The in-memory provider used by the unit tier has no transactions, so this is conditional. What is
        // not conditional is that the body runs exactly once per attempt.
        IDbContextTransaction? transaction = database.Database.IsRelational()
            ? await database.Database
                .BeginTransactionAsync(Isolation, cancellationToken)
                .ConfigureAwait(false)
            : null;

        try
        {
            T result = await body(cancellationToken).ConfigureAwait(false);

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            return result;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
