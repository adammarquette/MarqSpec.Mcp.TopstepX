using System.Net.Sockets;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// Two applies against one empty database, and a connection that drops after the store has answered
/// (gh#843).
/// </summary>
/// <remarks>
/// The lock is a Postgres session advisory lock, so an in-memory stand-in cannot represent it. The
/// gate inside the applier is what makes the overlap observable: the first apply holds the lock and
/// waits, and the test reads <c>pg_locks</c> for the second session waiting on the same key. A run
/// that merely started two tasks and hoped would stay green the day the lock was removed.
/// </remarks>
[Collection(SchemaCollection.Name)]
public sealed class MigrationApplyLockTests
{
    private readonly SchemaFixture _fixture;

    public MigrationApplyLockTests(SchemaFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task TwoConcurrentApplies_AgainstOneEmptyDatabase_BothSucceedWithoutOverlapping()
    {
        string name = "lock" + Guid.NewGuid().ToString("N");
        await using TopstepXDbContext created = await _fixture.CreateEmptyDatabaseAsync(name);
        string connectionString = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            Database = name,
        }.ConnectionString;

        ApplyGate gate = new();
        await using WebApplication first = Build(connectionString, required: false, gate);
        await using WebApplication second = Build(connectionString, required: false, gate);

        Task<StoreAvailability> firstApply = Program.MigrateAsync(first);
        StoreAvailability[] results;
        try
        {
            await gate.FirstEntered.WaitAsync(TimeSpan.FromSeconds(30));
            Task<StoreAvailability> secondApply = Program.MigrateAsync(second);
            bool sawWaiter = await WaitForWaiterAsync(connectionString);
            sawWaiter.Should().BeTrue(
                "the second apply has to wait on the session lock, not race the first");
            gate.ReleaseFirst();
            results = await Task.WhenAll(firstApply, secondApply);
        }
        finally
        {
            // A failed assertion must not leave the first apply parked on the gate.
            gate.ReleaseFirst();
        }

        results.Should().OnlyContain(availability => availability.IsAvailable);
        gate.MaxInside.Should().Be(1);
        (await CountLocksAsync(connectionString, waitingOnly: false)).Should().Be(0);
        await using NpgsqlConnection check = new(connectionString);
        await check.OpenAsync();
        await using NpgsqlCommand table = new(
            """SELECT to_regclass('public."TapeLeases"') IS NOT NULL""",
            check);
        (await table.ExecuteScalarAsync()).Should().Be(true);
    }

    public static TheoryData<string> DroppedConnections { get; } = ["socket", "57P01", "57P03"];

    /// <summary>
    /// The store answered, then the connection dropped during the apply. Required exits. It must not
    /// come back as an unavailable marker the process would keep serving. The drop is a socket reset
    /// or a real Postgres <c>57P01</c> / <c>57P03</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(DroppedConnections))]
    public async Task MigrateAsync_WhenTheConnectionDropsAndTheStoreIsRequired_Exits(string kind)
    {
        Exception failure = Dropped(kind);
        await using WebApplication app = Build(
            _fixture.ConnectionString,
            required: true,
            gate: null,
            failure: failure);

        Func<Task> act = () => Program.MigrateAsync(app);

        await act.Should().ThrowAsync<StoreRequiredException>();
        (await CountLocksAsync(_fixture.ConnectionString, waitingOnly: false)).Should().Be(
            0,
            "a throwing apply still releases the session lock");
    }

    /// <summary>
    /// The same drop with the store optional still degrades, which is ADR-0007's split. <c>57P01</c>
    /// and <c>57P03</c> are that drop, carrying their real <c>SqlState</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(DroppedConnections))]
    public async Task MigrateAsync_WhenTheConnectionDropsAndTheStoreIsNotRequired_Degrades(string kind)
    {
        Exception failure = Dropped(kind);
        if (failure is PostgresException postgres)
        {
            postgres.SqlState.Should().BeOneOf("57P01", "57P03");
        }

        await using WebApplication app = Build(
            _fixture.ConnectionString,
            required: false,
            gate: null,
            failure: failure);

        StoreAvailability availability = await Program.MigrateAsync(app);

        availability.IsAvailable.Should().BeFalse();
        availability.Explanation.Should().Contain("The connection dropped while applying migrations.");
        (await CountLocksAsync(_fixture.ConnectionString, waitingOnly: false)).Should().Be(
            0,
            "a throwing apply still releases the session lock");
    }

    /// <summary>
    /// The second apply is cancelled while it is waiting on the lock. Its session must not keep a lock,
    /// and the holder releases its own when it finishes (gh#843).
    /// </summary>
    [Fact]
    public async Task MigrateAsync_WhenCancelledWhileWaitingForTheLock_ReleasesIt()
    {
        string name = "cancel" + Guid.NewGuid().ToString("N");
        await using TopstepXDbContext created = await _fixture.CreateEmptyDatabaseAsync(name);
        string connectionString = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            Database = name,
        }.ConnectionString;

        ApplyGate gate = new();
        await using WebApplication holder = Build(connectionString, required: false, gate, migrate: false);
        await using WebApplication waiter = Build(connectionString, required: false, gate, migrate: false);

        Task<StoreAvailability> holding = Program.MigrateAsync(holder);
        try
        {
            await gate.FirstEntered.WaitAsync(TimeSpan.FromSeconds(30));
            Task<StoreAvailability> waiting = Program.MigrateAsync(waiter);
            bool sawWaiter = await WaitForWaiterAsync(connectionString);
            sawWaiter.Should().BeTrue(
                "the second apply has to be blocked on the session lock before it is cancelled");

            waiter.Lifetime.StopApplication();

            Func<Task> cancelled = () => waiting.WaitAsync(TimeSpan.FromSeconds(30));
            Exception thrown = (await cancelled.Should().ThrowAsync<Exception>()).Which;
            (thrown is OperationCanceledException
                || (thrown is PostgresException postgres
                    && postgres.SqlState == PostgresErrorCodes.QueryCanceled))
                .Should().BeTrue(
                    "the stopping token has to end the lock wait, and the failure was "
                    + thrown.GetType().Name + ": " + thrown.Message);

            (await CountLocksAsync(connectionString, waitingOnly: true)).Should().Be(0);
            (await CountLocksAsync(connectionString, waitingOnly: false)).Should().Be(
                1,
                "the holder still owns the lock after the cancelled waiter has gone");
        }
        finally
        {
            gate.ReleaseFirst();
        }

        (await holding).IsAvailable.Should().BeTrue();
        (await CountLocksAsync(connectionString, waitingOnly: false)).Should().Be(
            0,
            "the holder releases the session lock when its apply finishes");
    }

    /// <summary>
    /// A held lock is logged once, then the wait ends at the timeout instead of blocking with none.
    /// </summary>
    [Fact]
    public async Task ApplyUnderLockAsync_WhenTheLockIsHeld_LogsOnceAndTimesOut()
    {
        await using NpgsqlConnection holder = new(_fixture.ConnectionString);
        await holder.OpenAsync();
        await using (NpgsqlCommand hold = LockCommand(holder, "SELECT pg_advisory_lock(@classId, @objectId)"))
        {
            await hold.ExecuteNonQueryAsync();
        }

        ListLogger logger = new();
        try
        {
            Func<Task> waiting = () => MigrationApplyLock.ApplyUnderLockAsync(
                _fixture.ConnectionString,
                _ => Task.CompletedTask,
                logger,
                lockWaitTimeoutSeconds: 1,
                CancellationToken.None);

            await waiting.Should().ThrowAsync<NpgsqlException>();
            logger.Lines.Should().ContainSingle()
                .Which.Should().Contain("Waiting up to 1s.");
            (await CountLocksAsync(_fixture.ConnectionString, waitingOnly: false)).Should().Be(1);
        }
        finally
        {
            await using NpgsqlCommand unlock = LockCommand(
                holder,
                "SELECT pg_advisory_unlock(@classId, @objectId)");
            await unlock.ExecuteNonQueryAsync();
        }

        (await CountLocksAsync(_fixture.ConnectionString, waitingOnly: false)).Should().Be(0);
    }

    private static NpgsqlCommand LockCommand(NpgsqlConnection connection, string sql)
    {
        NpgsqlCommand command = new(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("classId", MigrationApplyLock.ClassId));
        command.Parameters.Add(new NpgsqlParameter("objectId", MigrationApplyLock.ObjectId));
        return command;
    }

    private static WebApplication Build(
        string connectionString,
        bool required,
        ApplyGate? gate,
        bool migrate = true,
        Exception? failure = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        StripAmbientEnvironment(builder);
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = connectionString,
            ["Store:StartupWaitSeconds"] = "0",
            ["Store:Required"] = required ? "true" : "false",
            ["Mcp:Transport"] = "Stdio",
        });
        Program.ConfigureServices(builder, new McpOptions { Transport = McpTransport.Stdio });
        if (gate is null)
        {
            builder.Services.Replace(ServiceDescriptor.Singleton<IMigrationApplier>(
                new DroppingApplier(failure ?? Dropped("socket"))));
        }
        else if (!migrate)
        {
            builder.Services.Replace(ServiceDescriptor.Singleton<IMigrationApplier>(
                new ParkingApplier(gate)));
        }
        else
        {
            builder.Services.Replace(ServiceDescriptor.Singleton<IMigrationApplier>(
                new GatedMigrationApplier(new MigrationApplier(), gate)));
        }

        return builder.Build();
    }

    private static async Task<bool> WaitForWaiterAsync(string connectionString)
    {
        using CancellationTokenSource budget = new(TimeSpan.FromSeconds(30));
        try
        {
            while (true)
            {
                if (await CountLocksAsync(connectionString, waitingOnly: true) >= 1)
                {
                    return true;
                }

                await Task.Delay(50, budget.Token);
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task<int> CountLocksAsync(string connectionString, bool waitingOnly)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        string granted = waitingOnly ? "AND NOT granted" : string.Empty;
        await using NpgsqlCommand command = new(
            $"""
            SELECT count(*)::int
            FROM pg_locks
            WHERE locktype = 'advisory'
              AND classid = @classId
              AND objid = @objectId
              {granted}
            """,
            connection);
        command.Parameters.Add(new NpgsqlParameter("classId", MigrationApplyLock.ClassId));
        command.Parameters.Add(new NpgsqlParameter("objectId", MigrationApplyLock.ObjectId));
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static void StripAmbientEnvironment(WebApplicationBuilder builder)
    {
        IConfigurationBuilder sources = builder.Configuration;
        foreach (EnvironmentVariablesConfigurationSource ambient in sources.Sources
            .OfType<EnvironmentVariablesConfigurationSource>()
            .Where(source => string.IsNullOrEmpty(source.Prefix))
            .ToList())
        {
            sources.Sources.Remove(ambient);
        }
    }

    private sealed class ListLogger : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Lines.Add(formatter(state, exception));
        }
    }

    /// <summary>Counts how many applies are inside the migration at once, and holds the first one.</summary>
    private sealed class ApplyGate
    {
        private readonly object _sync = new();
        private readonly TaskCompletionSource _firstEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _releaseFirst =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _inside;
        private int _entries;

        public Task FirstEntered => _firstEntered.Task;

        public int MaxInside { get; private set; }

        public async Task EnterAsync()
        {
            bool first;
            lock (_sync)
            {
                _inside++;
                _entries++;
                if (_inside > MaxInside)
                {
                    MaxInside = _inside;
                }

                first = _entries == 1;
            }

            if (first)
            {
                _firstEntered.TrySetResult();
                await _releaseFirst.Task.ConfigureAwait(false);
            }
        }

        public void Leave()
        {
            lock (_sync)
            {
                _inside--;
            }
        }

        public void ReleaseFirst() => _releaseFirst.TrySetResult();
    }

    private sealed class GatedMigrationApplier(IMigrationApplier inner, ApplyGate gate) : IMigrationApplier
    {
        public async Task ApplyAsync(
            DatabaseFacade database,
            int commandTimeoutSeconds,
            CancellationToken cancellationToken)
        {
            await gate.EnterAsync().ConfigureAwait(false);
            try
            {
                await inner.ApplyAsync(database, commandTimeoutSeconds, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                gate.Leave();
            }
        }
    }

    /// <summary>
    /// Holds the lock inside the applier and does not migrate. The cancel test only needs the wait.
    /// </summary>
    private sealed class ParkingApplier(ApplyGate gate) : IMigrationApplier
    {
        public Task ApplyAsync(
            DatabaseFacade database,
            int commandTimeoutSeconds,
            CancellationToken cancellationToken)
            => gate.EnterAsync();
    }

    /// <summary>The store answered, and the apply then failed with <paramref name="failure"/>.</summary>
    private sealed class DroppingApplier(Exception failure) : IMigrationApplier
    {
        public Task ApplyAsync(
            DatabaseFacade database,
            int commandTimeoutSeconds,
            CancellationToken cancellationToken)
            => Task.FromException(failure);
    }

    /// <summary>
    /// A socket reset, or a <see cref="PostgresException"/> whose <c>SqlState</c> is the real
    /// <c>57P01</c> or <c>57P03</c> code.
    /// </summary>
    private static Exception Dropped(string kind) => kind switch
    {
        "socket" => new NpgsqlException(
            "The connection dropped while applying migrations.",
            new SocketException((int)SocketError.ConnectionReset)),
        "57P01" => new PostgresException(
            "terminating connection due to administrator command",
            "FATAL",
            "FATAL",
            "57P01"),
        "57P03" => new PostgresException(
            "the database system is starting up",
            "FATAL",
            "FATAL",
            "57P03"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown drop."),
    };
}
