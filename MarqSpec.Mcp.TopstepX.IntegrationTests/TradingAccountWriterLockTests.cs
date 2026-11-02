using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// One Postgres session lock per account (gh#772). Two gates, one account: the second refuses
/// until the first releases.
/// </summary>
[Collection(SchemaCollection.Name)]
public sealed class TradingAccountWriterLockTests(SchemaFixture fixture)
{
    private const int Account = 100;

    private readonly SchemaFixture _fixture = fixture;

    [Fact]
    public async Task TwoLocks_OneAccount_SecondRefusesUntilTheFirstReleases()
    {
        await using TradingAccountWriterLock first = Lock();
        await using TradingAccountWriterLock second = Lock();

        (await first.TryHoldAsync(Account, CancellationToken.None)).Should().Be(TradingWriterLockTake.Held);
        (await second.TryHoldAsync(Account, CancellationToken.None))
            .Should().Be(TradingWriterLockTake.HeldByOther);
        (await second.TryHoldAsync(Account + 1, CancellationToken.None)).Should().Be(
            TradingWriterLockTake.Held,
            "a different account is a different writer");

        await first.DisposeAsync();

        (await second.TryHoldAsync(Account, CancellationToken.None)).Should().Be(TradingWriterLockTake.Held);
    }

    [Fact]
    public async Task ADisposedLock_HasReleasedOnTheServerWhenDisposeReturns()
    {
        // gh#995: closing the session returned before the backend exited, and the backend frees the lock
        // only on exit. One round rarely loses that race, so the test repeats it. Both dispose paths run.
        for (int round = 0; round < 40; round++)
        {
            TradingAccountWriterLock holder = Lock();
            (await holder.TryHoldAsync(Account, CancellationToken.None)).Should().Be(
                TradingWriterLockTake.Held,
                $"round {round}: the previous round's locks were disposed");

            if (round % 2 == 0)
            {
                await holder.DisposeAsync();
            }
            else
            {
                holder.Dispose();
            }

            (await GrantedHoldersAsync()).Should().Be(
                0,
                $"round {round}: a disposed lock has released on the server, not only closed its socket");
        }
    }

    [Fact]
    public async Task ADroppedSession_IsTakenBackWhenNobodyElseHoldsIt()
    {
        await using TradingAccountWriterLock holder = Lock();
        (await holder.TryHoldAsync(Account, CancellationToken.None)).Should().Be(TradingWriterLockTake.Held);
        await TerminateHolderAsync();

        (await holder.TryHoldAsync(Account, CancellationToken.None)).Should().Be(TradingWriterLockTake.Held);
    }

    [Fact]
    public async Task ADatabaseOutage_IsUnavailableRatherThanHeldByAnotherReplica()
    {
        await using TradingAccountWriterLock broken = new(
            "Host=127.0.0.1;Port=1;Username=x;Password=x;Database=x;Timeout=1",
            NullLogger<TradingAccountWriterLock>.Instance,
            TimeProvider.System);

        (await broken.TryHoldAsync(Account, CancellationToken.None))
            .Should().Be(TradingWriterLockTake.Unavailable);
    }

    [Fact]
    public async Task ADroppedSession_IsNotTreatedAsStillHeld()
    {
        await using TradingAccountWriterLock holder = Lock();
        (await holder.TryHoldAsync(Account, CancellationToken.None)).Should().Be(TradingWriterLockTake.Held);

        await TerminateHolderAsync();

        await using TradingAccountWriterLock other = Lock();
        (await other.TryHoldAsync(Account, CancellationToken.None)).Should().Be(TradingWriterLockTake.Held);
        (await holder.TryHoldAsync(Account, CancellationToken.None)).Should().Be(
            TradingWriterLockTake.HeldByOther,
            "the old session was terminated, so it must not keep writing");
    }

    private TradingAccountWriterLock Lock() =>
        new(
            _fixture.ConnectionString,
            NullLogger<TradingAccountWriterLock>.Instance,
            TimeProvider.System);

    private async Task TerminateHolderAsync()
    {
        await using NpgsqlConnection killer = new(_fixture.ConnectionString);
        await killer.OpenAsync();
        // With a timeout, pg_terminate_backend waits for the backend to exit, and its locks with it
        // (gh#995). Without one it returns once the signal is sent.
        await using NpgsqlCommand terminate = new("SELECT pg_terminate_backend(@pid, 5000)", killer);
        terminate.Parameters.AddWithValue("pid", await BackendPidAsync());
        (await terminate.ExecuteScalarAsync()).Should().Be(true);
    }

    private async Task<long> GrantedHoldersAsync()
    {
        await using NpgsqlConnection observer = new(_fixture.ConnectionString);
        await observer.OpenAsync();
        await using NpgsqlCommand command = new(
            """
            SELECT count(*)
            FROM pg_locks
            WHERE locktype = 'advisory'
              AND classid = @classId
              AND objid = @objectId
              AND granted
            """,
            observer);
        command.Parameters.AddWithValue("classId", TradingAccountWriterLock.AdvisoryLockClassId);
        command.Parameters.AddWithValue("objectId", Account);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<int> BackendPidAsync()
    {
        await using NpgsqlConnection observer = new(_fixture.ConnectionString);
        await observer.OpenAsync();
        await using NpgsqlCommand command = new(
            """
            SELECT pid
            FROM pg_locks
            WHERE locktype = 'advisory'
              AND classid = @classId
              AND objid = @objectId
              AND granted
            """,
            observer);
        command.Parameters.AddWithValue("classId", TradingAccountWriterLock.AdvisoryLockClassId);
        command.Parameters.AddWithValue("objectId", Account);
        object? pid = await command.ExecuteScalarAsync();
        pid.Should().BeOfType<int>();
        return (int)pid!;
    }
}
