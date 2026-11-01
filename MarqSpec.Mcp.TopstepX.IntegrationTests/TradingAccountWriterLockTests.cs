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

        (await first.TryHoldAsync(Account, CancellationToken.None)).Should().BeTrue();
        (await second.TryHoldAsync(Account, CancellationToken.None)).Should().BeFalse();
        (await second.TryHoldAsync(Account + 1, CancellationToken.None)).Should().BeTrue(
            "a different account is a different writer");

        await first.DisposeAsync();

        (await second.TryHoldAsync(Account, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task ADroppedSession_IsNotTreatedAsStillHeld()
    {
        await using TradingAccountWriterLock holder = Lock();
        (await holder.TryHoldAsync(Account, CancellationToken.None)).Should().BeTrue();

        await using NpgsqlConnection killer = new(_fixture.ConnectionString);
        await killer.OpenAsync();
        await using (NpgsqlCommand terminate = new("SELECT pg_terminate_backend(@pid)", killer))
        {
            terminate.Parameters.AddWithValue("pid", await BackendPidAsync());
            (await terminate.ExecuteScalarAsync()).Should().Be(true);
        }

        await using TradingAccountWriterLock other = Lock();
        (await other.TryHoldAsync(Account, CancellationToken.None)).Should().BeTrue();
        (await holder.TryHoldAsync(Account, CancellationToken.None)).Should().BeFalse(
            "the old session was terminated, so it must not keep writing");
    }

    private TradingAccountWriterLock Lock() =>
        new(_fixture.ConnectionString, NullLogger<TradingAccountWriterLock>.Instance);

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
