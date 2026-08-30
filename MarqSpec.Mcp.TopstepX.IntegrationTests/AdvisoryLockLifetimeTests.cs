using System.Data;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// What an advisory lock actually does here — to Npgsql's pool, and to the snapshot it would guard
/// (gh#104, <see href="../documentation/adr/0012-fills-are-not-serialised.md">ADR-0012</see>).
/// </summary>
/// <remarks>
/// <para>
/// <b>These make an observation; they do not test a feature.</b> gh#104 asked whether fills of one series
/// should be serialised, and required the answer to rest on what a lock <i>was seen to do</i> rather than on
/// what the documentation says it does. Nothing in the product takes an advisory lock, and after ADR-0012
/// nothing is expected to. What these pin is the evidence that decision was made on, so it cannot quietly go
/// out of date the next time the provider is upgraded — and so the next agent who reaches for
/// <c>pg_advisory_lock</c> meets the two traps before writing the code rather than after.
/// </para>
/// <para>
/// <b>Observed on Npgsql 10.0.3 against <c>timescale/timescaledb-ha:pg17</c></b>, which is the pairing this
/// server ships. Both findings are properties of that pairing rather than of Postgres alone, which is exactly
/// why they are pinned in this tier: the unit tier has no pool, no connection and no snapshot to observe.
/// </para>
/// <para>
/// The observer is always a <b>non-pooled</b> connection and always uses <c>pg_try_advisory_lock</c>, never
/// <c>pg_advisory_lock</c>. Non-pooled because a pooled one could be handed the very connection under test,
/// and advisory locks are re-entrant within a session — so a pooled observer could report "free" for a lock
/// that is merely its own. And <c>try</c> because a blocking wait in a test is a hang rather than a failure.
/// </para>
/// </remarks>
/// <param name="fixture">The shared container.</param>
[Collection(SchemaCollection.Name)]
public sealed class AdvisoryLockLifetimeTests(SchemaFixture fixture)
{
    /// <summary>
    /// The advisory-lock class these tests take their keys under.
    /// </summary>
    /// <remarks>
    /// A constant rather than a random draw, so a failure is reproducible. The collection runs sequentially
    /// and each test owns its own object id, so the keys cannot collide inside the shared container.
    /// </remarks>
    private const int LockClass = 104;

    /// <summary>How long a deliberately blocked statement is allowed to stay blocked.</summary>
    /// <remarks>
    /// A bound, not a pause. Nothing waits for it on the happy path — the wait ends when the holder unlocks —
    /// but a test that can block forever is a hung agent rather than a red run.
    /// </remarks>
    private static readonly TimeSpan _blockedLongEnough = TimeSpan.FromSeconds(30);

    private readonly SchemaFixture _fixture = fixture;

    [Fact]
    public async Task ASessionAdvisoryLockOnAnExplicitlyOpenedConnection_HoldsAcrossTheWholeUnitOfWork()
    {
        // FIRST, the shape that WORKS, so that "we could not make it work" is never mistaken for the reason
        // gh#104 went the way it did. Open the connection explicitly and the session -- and with it the lock --
        // outlives every statement and the whole transaction. That is the ordering the issue requires: the
        // lock is granted BEFORE BeginTransaction, so the snapshot is taken after it and is not already stale.
        //
        // What it costs is that the connection's lifetime becomes the caller's problem, and the test below is
        // what that costs when a release is missed.
        const int objectId = 1;

        await using TopstepXDbContext store = _fixture.CreateContext();
        await store.Database.OpenConnectionAsync();

        try
        {
            await store.Database
                .ExecuteSqlRawAsync("SELECT pg_advisory_lock({0}, {1})", LockClass, objectId);

            (await AnotherSessionCanTakeAsync(LockClass, objectId)).Should().BeFalse(
                "a lock that excludes nobody is not a lock, and this is the half that makes it one");

            // The level SeriesUnitOfWork uses. Spelled out rather than referenced, because that type is
            // internal and what is under test here is the SPAN rather than the level.
            await using (IDbContextTransaction transaction =
                await store.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead))
            {
                await store.Database.ExecuteSqlRawAsync("SELECT 1");

                (await AdvisoryLocksHeldAsync(LockClass, objectId)).Should().Be(
                    1, "the lock has to span the transaction, or the snapshot it guards is unguarded");

                await transaction.CommitAsync();
            }

            (await AdvisoryLocksHeldAsync(LockClass, objectId)).Should().Be(
                1,
                "a SESSION lock outlives its transaction -- which is why this one and not "
                + "pg_advisory_xact_lock, and why its lifetime is a pooling question rather than a "
                + "transaction one");
        }
        finally
        {
            await store.Database
                .ExecuteSqlRawAsync("SELECT pg_advisory_unlock({0}, {1})", LockClass, objectId);
            await store.Database.CloseConnectionAsync();
        }
    }

    [Fact]
    public async Task ASessionAdvisoryLockLeftHeld_OUTLIVESTheRequest_AndSitsOnAnIdleConnectionUntilItIsReused()
    {
        // THE OBSERVATION gh#104 asked for, and it came out the WORSE of the two ways it could.
        //
        // The prediction going in was that Npgsql's reset on return to the pool would release the lock, making
        // the failure mode "a lock that silently holds nothing". It is the other one: the reset is DEFERRED
        // and prepended to the next command on that connection, so a connection sitting idle in the pool goes
        // on holding every advisory lock its last user took. `held` is 1 after the context is disposed, and an
        // unrelated session still cannot take the key.
        //
        // That is the failure the issue named as worse than no lock at all. The owner of the lock is a request
        // that has finished; nothing is running that could release it; and what eventually does is an
        // ARBITRARY LATER REQUEST happening to be handed that same physical connection. Every fill of that
        // series blocks until then, and under a pool with spare connections and a quiet instrument, "until
        // then" has no bound anyone can state.
        //
        // The statement below is the naive one -- ExecuteSqlRawAsync on a context with no open connection and
        // no transaction, which is what "take the lock before BeginTransaction" looks like if you write the
        // obvious line. EF opens the connection for it and closes it again the moment it returns.
        const int objectId = 2;

        // A PRIVATE POOL OF EXACTLY ONE, so which physical connection the second context receives is decided
        // rather than hoped for. Npgsql keys its pools on the whole connection string, so a distinct
        // ApplicationName is a distinct pool -- and MaxPoolSize 1 makes the reuse below the same connection by
        // construction. Without it this test would be asserting on whichever connection the shared pool
        // happened to hand back.
        string pooled = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            ApplicationName = "gh104-advisory-lock-lifetime",
            MaxPoolSize = 1,
        }.ConnectionString;

        try
        {
            await using (TopstepXDbContext taker = Context(pooled))
            {
                await taker.Database
                    .ExecuteSqlRawAsync("SELECT pg_advisory_lock({0}, {1})", LockClass, objectId);
            }

            (await AdvisoryLocksHeldAsync(LockClass, objectId)).Should().Be(
                1,
                "the connection went back to the pool still holding it -- the reset that would have released "
                + "it has not been sent, and will not be until something else uses this connection");

            (await AnotherSessionCanTakeAsync(LockClass, objectId)).Should().BeFalse(
                "and it is a REAL lock while it sits there, so this is a fill of this series blocking on a "
                + "request that finished");

            await using (TopstepXDbContext next = Context(pooled))
            {
                await next.Database.ExecuteSqlRawAsync("SELECT 1");
            }

            (await AdvisoryLocksHeldAsync(LockClass, objectId)).Should().Be(
                0,
                "the release is tied to the connection being used again, not to the request that took the "
                + "lock -- so who ends the wedge, and when, is decided by unrelated traffic");
        }
        finally
        {
            // The private pool is this test's own, so it is torn down here rather than left holding a
            // connection into the rest of the collection.
            await using NpgsqlConnection ofThisPool = new(pooled);
            NpgsqlConnection.ClearPool(ofThisPool);
        }
    }

    [Fact]
    public async Task ATransactionScopedAdvisoryLock_IsGrantedAFTERTheSnapshotItWouldGuardIsAlreadyFixed()
    {
        // THE OTHER TRAP, and the one that forecloses the escape from the first. pg_advisory_xact_lock has no
        // pooling problem at all -- the server releases it at commit or rollback, unconditionally, whatever
        // the client forgot. It is therefore the obvious answer to the test above, and it does not work.
        //
        // A REPEATABLE READ transaction fixes its snapshot at the first statement that needs one, and the
        // statement that TAKES the lock is that statement. So the snapshot is already fixed while the lock is
        // still being waited for, and by the time it is granted the view it is guarding is stale -- which
        // makes it a lock that measures as working and excludes nothing that matters.
        //
        // Driven rather than argued: a holder takes the key, the waiter begins REPEATABLE READ and blocks on
        // it, a third session commits a row WHILE the waiter is blocked, and the holder then releases. If the
        // snapshot were taken when the lock was granted, the waiter would see that row.
        string venue = ConcurrencyHarness.Venue();
        const int objectId = 3;

        await using NpgsqlConnection holder = await OpenUnpooledAsync();
        await ExecuteAsync(holder, "SELECT pg_advisory_lock(@classid, @objid)", LockClass, objectId);

        await using NpgsqlConnection waiter = await OpenUnpooledAsync();
        await ExecuteAsync(waiter, "BEGIN ISOLATION LEVEL REPEATABLE READ");

        Task blocked = ExecuteAsync(waiter, "SELECT pg_advisory_xact_lock(@classid, @objid)", LockClass, objectId);

        try
        {
            await WaitUntilBlockedAsync(LockClass, objectId);

            await using (NpgsqlConnection writer = await OpenUnpooledAsync())
            {
                await using NpgsqlCommand insert = new(
                    "INSERT INTO \"Bars\" (\"Venue\", \"Instrument\", \"ResolutionMinutes\", \"BucketStart\", "
                    + "\"Open\", \"High\", \"Low\", \"Close\", \"Volume\", \"ContractId\", \"RecordedAt\") "
                    + "VALUES (@venue, 'ES', 5, @bucket, 1, 2, 0.5, 1.5, 7, 'CON.F.US.EP.Z26', @bucket)",
                    writer);
                insert.Parameters.AddWithValue("venue", venue);
                insert.Parameters.AddWithValue("bucket", ConcurrencyHarness.Bucket(0));
                (await insert.ExecuteNonQueryAsync()).Should().Be(1, "the row has to actually be committed");
            }
        }
        finally
        {
            await ExecuteAsync(holder, "SELECT pg_advisory_unlock(@classid, @objid)", LockClass, objectId);
        }

        await blocked;

        long seen = await ScalarAsync(waiter, "SELECT count(*) FROM \"Bars\" WHERE \"Venue\" = @venue", venue);
        await ExecuteAsync(waiter, "ROLLBACK");

        seen.Should().Be(
            0,
            "the snapshot was fixed by the statement that went on to WAIT for the lock, so the row committed "
            + "during the wait is invisible -- a transaction-scoped lock guards a view taken before it was "
            + "granted, which is why the only correct shape is a session lock taken before BeginTransaction");

        long afterwards = await ScalarAsync(
            waiter, "SELECT count(*) FROM \"Bars\" WHERE \"Venue\" = @venue", venue);

        afterwards.Should().Be(
            1,
            "and the control: outside that transaction the row is plainly there, so the zero above is the "
            + "snapshot's age and not a failed insert");
    }

    /// <summary>Opens a context on a specific connection string.</summary>
    /// <param name="connectionString">The connection string, and therefore the pool.</param>
    /// <returns>The context. The caller disposes it.</returns>
    private static TopstepXDbContext Context(string connectionString) =>
        new(new DbContextOptionsBuilder<TopstepXDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.UseVector())
            .Options);

    /// <summary>Runs a statement, optionally carrying an advisory-lock key.</summary>
    /// <param name="connection">The connection.</param>
    /// <param name="sql">The statement.</param>
    /// <param name="classId">The lock class, when the statement takes one.</param>
    /// <param name="objectId">The lock object id, when the statement takes one.</param>
    /// <returns>The task.</returns>
    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        string sql,
        int? classId = null,
        int? objectId = null)
    {
        await using NpgsqlCommand command = new(sql, connection);

        if (classId is not null)
        {
            command.Parameters.AddWithValue("classid", classId.Value);
            command.Parameters.AddWithValue("objid", objectId!.Value);
        }

        command.CommandTimeout = (int)_blockedLongEnough.TotalSeconds;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Runs a count over one venue's bars.</summary>
    /// <param name="connection">The connection.</param>
    /// <param name="sql">The statement.</param>
    /// <param name="venue">The venue id.</param>
    /// <returns>The count.</returns>
    private static async Task<long> ScalarAsync(NpgsqlConnection connection, string sql, string venue)
    {
        await using NpgsqlCommand command = new(sql, connection);
        command.Parameters.AddWithValue("venue", venue);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Waits until Postgres reports somebody waiting — not granted — on an advisory key.
    /// </summary>
    /// <param name="classId">The lock class.</param>
    /// <param name="objectId">The lock object id.</param>
    /// <returns>The task.</returns>
    /// <remarks>
    /// The interleaving has to be a fact rather than a hope: the row must be committed <i>while</i> the waiter
    /// is blocked, and the only way to know it is blocked is to see the ungranted lock in <c>pg_locks</c>.
    /// Racing a fixed delay against a connection handshake would make this test pass by luck.
    /// </remarks>
    private async Task WaitUntilBlockedAsync(int classId, int objectId)
    {
        using CancellationTokenSource giveUp = new(_blockedLongEnough);

        while (await AdvisoryLocksHeldAsync(classId, objectId, granted: false) == 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), giveUp.Token);
        }
    }

    /// <summary>
    /// How many advisory locks the server holds under a key, counted from outside the pool.
    /// </summary>
    /// <param name="classId">The lock class.</param>
    /// <param name="objectId">The lock object id.</param>
    /// <param name="granted">Whether to count granted locks, or the sessions waiting for one.</param>
    /// <returns>The number of rows in <c>pg_locks</c> matching.</returns>
    /// <remarks>
    /// <c>pg_locks</c> is server-side truth and does not care which session asks, which is why the count is
    /// the primary observation and the <c>try</c> elsewhere is corroboration rather than the other way round.
    /// </remarks>
    private async Task<int> AdvisoryLocksHeldAsync(int classId, int objectId, bool granted = true)
    {
        await using NpgsqlConnection observer = await OpenUnpooledAsync();

        await using NpgsqlCommand count = new(
            "SELECT count(*) FROM pg_locks "
            + "WHERE locktype = 'advisory' AND classid::bigint = @classid AND objid::bigint = @objid "
            + "AND granted = @granted",
            observer);
        count.Parameters.AddWithValue("classid", (long)classId);
        count.Parameters.AddWithValue("objid", (long)objectId);
        count.Parameters.AddWithValue("granted", granted);

        return (int)(long)(await count.ExecuteScalarAsync())!;
    }

    /// <summary>Whether a session that is definitely not the one under test can take the same lock.</summary>
    /// <param name="classId">The lock class.</param>
    /// <param name="objectId">The lock object id.</param>
    /// <returns><see langword="true"/> if the lock was free. It is released again before returning.</returns>
    private async Task<bool> AnotherSessionCanTakeAsync(int classId, int objectId)
    {
        await using NpgsqlConnection challenger = await OpenUnpooledAsync();

        await using NpgsqlCommand take = new("SELECT pg_try_advisory_lock(@classid, @objid)", challenger);
        take.Parameters.AddWithValue("classid", classId);
        take.Parameters.AddWithValue("objid", objectId);

        bool taken = (bool)(await take.ExecuteScalarAsync())!;

        if (taken)
        {
            // Released explicitly. Closing the connection would do it too -- but this file is about not
            // trusting that, and a probe that cleans up via the mechanism under observation is a test
            // agreeing with itself.
            await using NpgsqlCommand release = new("SELECT pg_advisory_unlock(@classid, @objid)", challenger);
            release.Parameters.AddWithValue("classid", classId);
            release.Parameters.AddWithValue("objid", objectId);
            await release.ExecuteScalarAsync();
        }

        return taken;
    }

    /// <summary>Opens a connection outside the pool, so it is always a fresh, unrelated session.</summary>
    /// <returns>The open connection. The caller disposes it.</returns>
    private async Task<NpgsqlConnection> OpenUnpooledAsync()
    {
        NpgsqlConnectionStringBuilder unpooled = new(_fixture.ConnectionString) { Pooling = false };
        NpgsqlConnection connection = new(unpooled.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }
}
