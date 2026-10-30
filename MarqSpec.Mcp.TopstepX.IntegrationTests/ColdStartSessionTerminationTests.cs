using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// A cold start whose migrating session is killed with <c>pg_terminate_backend</c> (gh#843).
/// </summary>
/// <remarks>
/// The hand-built <c>57P01</c> and <c>57P03</c> exceptions cover the classifier. This is the
/// case the issue describes: the store answered, then the backend applying the schema was
/// terminated. What comes out of <c>MigrateAsync</c> is EF's
/// <see cref="InvalidOperationException"/> wrapped around <c>PostgresException</c> <c>57P01</c>.
/// </remarks>
[Collection(SchemaCollection.Name)]
public sealed class ColdStartSessionTerminationTests
{
    private readonly SchemaFixture _fixture;

    public ColdStartSessionTerminationTests(SchemaFixture fixture) => _fixture = fixture;

    /// <summary>
    /// <c>Store__Required=false</c> stays up. The killed apply comes back unavailable.
    /// </summary>
    [Fact]
    public async Task MigrateAsync_WhenTheApplyingBackendIsTerminated_AndTheStoreIsNotRequired_Degrades()
    {
        string connectionString = await EmptyDatabaseAsync();
        CapturedLogs logs = new();
        await using WebApplication app = Build(connectionString, required: false, logs);

        StoreAvailability availability = await Program.MigrateAsync(app);

        availability.IsAvailable.Should().BeFalse();
        availability.Explanation.Should().Contain("The connection dropped while applying migrations.");
        logs.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Warning
            && entry.Message.Contains(
                "The connection dropped while applying migrations.",
                StringComparison.Ordinal));
        (await CountLocksAsync(connectionString)).Should().Be(0);
    }

    /// <summary>
    /// <c>Store__Required=true</c> logs an error and leaves through <see cref="StoreRequiredException"/>,
    /// whose exit code is non-zero.
    /// </summary>
    [Fact]
    public async Task MigrateAsync_WhenTheApplyingBackendIsTerminated_AndTheStoreIsRequired_Exits()
    {
        string connectionString = await EmptyDatabaseAsync();
        CapturedLogs logs = new();
        await using WebApplication app = Build(connectionString, required: true, logs);

        Func<Task> act = () => Program.MigrateAsync(app);

        await act.Should().ThrowAsync<StoreRequiredException>();
        StoreRequiredException.ExitCode.Should().BeGreaterThan(0);
        logs.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Error
            && entry.Message.Contains("Store__Required", StringComparison.Ordinal));
        (await CountLocksAsync(connectionString)).Should().Be(0);
    }

    private async Task<string> EmptyDatabaseAsync()
    {
        string name = "term" + Guid.NewGuid().ToString("N");
        await using TopstepXDbContext created = await _fixture.CreateEmptyDatabaseAsync(name);
        return new NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            Database = name,
        }.ConnectionString;
    }

    private static WebApplication Build(string connectionString, bool required, CapturedLogs logs)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        StripAmbientEnvironment(builder);
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = connectionString,
            ["Store:StartupWaitSeconds"] = "0",
            ["Store:Required"] = required ? "true" : "false",
            ["Mcp:Transport"] = "Stdio",
        });
        Program.ConfigureServices(builder, new McpOptions { Transport = McpTransport.Stdio });
        builder.Services.Replace(ServiceDescriptor.Singleton<IMigrationApplier>(
            new TerminatingApplier(connectionString)));
        return builder.Build();
    }

    private static async Task<int> CountLocksAsync(string connectionString)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            """
            SELECT count(*)::int
            FROM pg_locks
            WHERE locktype = 'advisory'
              AND classid = @classId
              AND objid = @objectId
              AND database = (SELECT oid FROM pg_database WHERE datname = current_database())
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

    /// <summary>
    /// Opens the connection the migration will use, then signals that backend once it is running a
    /// command. The migration's own exception is what <c>MigrateAsync</c> has to classify.
    /// </summary>
    private sealed class TerminatingApplier(string connectionString) : IMigrationApplier
    {
        public async Task ApplyAsync(
            DatabaseFacade database,
            int commandTimeoutSeconds,
            CancellationToken cancellationToken)
        {
            await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            int pid = ((NpgsqlConnection)database.GetDbConnection()).ProcessID;
            if (pid == 0)
            {
                throw new InvalidOperationException("The applying connection has no backend pid.");
            }

            using CancellationTokenSource stop =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task<bool> terminated = TerminateWhenActiveAsync(pid, stop.Token);
            try
            {
                await new MigrationApplier()
                    .ApplyAsync(database, commandTimeoutSeconds, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception migrationFailure)
            {
                bool killed = await WaitForKillAsync(terminated).ConfigureAwait(false);
                if (!killed)
                {
                    throw new InvalidOperationException(
                        "The apply failed before pg_terminate_backend ran: " + migrationFailure,
                        migrationFailure);
                }

                throw;
            }

            stop.Cancel();
            throw new InvalidOperationException(
                "The migration finished before pg_terminate_backend reached backend " + pid + ".");
        }

        private async Task<bool> TerminateWhenActiveAsync(int pid, CancellationToken cancellationToken)
        {
            await using NpgsqlConnection connection = new(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            using CancellationTokenSource budget =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(60));
            while (true)
            {
                await using NpgsqlCommand stateCommand = new(
                    "SELECT state FROM pg_stat_activity WHERE pid = @pid",
                    connection);
                stateCommand.Parameters.Add(new NpgsqlParameter("pid", pid));
                string? state = (string?)await stateCommand.ExecuteScalarAsync(budget.Token)
                    .ConfigureAwait(false);
                if (state == "active")
                {
                    await using NpgsqlCommand kill = new(
                        "SELECT pg_terminate_backend(@pid)",
                        connection);
                    kill.Parameters.Add(new NpgsqlParameter("pid", pid));
                    object? signalled = await kill.ExecuteScalarAsync(budget.Token).ConfigureAwait(false);
                    if (signalled is not true)
                    {
                        throw new InvalidOperationException(
                            "pg_terminate_backend did not signal backend " + pid + ".");
                    }

                    return true;
                }

                await Task.Delay(20, budget.Token).ConfigureAwait(false);
            }
        }

        private static async Task<bool> WaitForKillAsync(Task<bool> terminated)
        {
            try
            {
                return await terminated.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    private sealed class CapturedLogs : ILoggerProvider, ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => this;

        public void Dispose()
        {
        }

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
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
