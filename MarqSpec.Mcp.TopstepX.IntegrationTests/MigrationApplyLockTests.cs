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

    /// <summary>
    /// The store answered, then the connection dropped during the apply. Required exits. It must not
    /// come back as an unavailable marker the process would keep serving.
    /// </summary>
    [Fact]
    public async Task MigrateAsync_WhenTheConnectionDropsAndTheStoreIsRequired_Exits()
    {
        await using WebApplication app = Build(_fixture.ConnectionString, required: true, gate: null);

        Func<Task> act = () => Program.MigrateAsync(app);

        await act.Should().ThrowAsync<StoreRequiredException>();
    }

    /// <summary>
    /// The same drop with the store optional still degrades, which is ADR-0007's split.
    /// </summary>
    [Fact]
    public async Task MigrateAsync_WhenTheConnectionDropsAndTheStoreIsNotRequired_Degrades()
    {
        await using WebApplication app = Build(_fixture.ConnectionString, required: false, gate: null);

        StoreAvailability availability = await Program.MigrateAsync(app);

        availability.IsAvailable.Should().BeFalse();
        availability.Explanation.Should().Contain("The connection dropped while applying migrations.");
    }

    private static WebApplication Build(string connectionString, bool required, ApplyGate? gate)
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
            builder.Services.Replace(ServiceDescriptor.Singleton<IMigrationApplier>(new DroppingApplier()));
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

    /// <summary>The store answered, and the apply then lost the socket.</summary>
    private sealed class DroppingApplier : IMigrationApplier
    {
        public Task ApplyAsync(
            DatabaseFacade database,
            int commandTimeoutSeconds,
            CancellationToken cancellationToken)
            => throw new NpgsqlException(
                "The connection dropped while applying migrations.",
                new SocketException((int)SocketError.ConnectionReset));
    }
}
