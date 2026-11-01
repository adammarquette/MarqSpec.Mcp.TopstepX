using System.Data;
using System.Data.Common;
using System.Globalization;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
/// terminated. The applying command raises a notice once it is running, then waits on a
/// session advisory lock this test already holds. A <see cref="TaskCompletionSource"/> is
/// completed from that notice. There is no poll and no delay. The thirty-second token
/// only fails the test when that notice never arrives.
/// </remarks>
[Collection(SchemaCollection.Name)]
public sealed class ColdStartSessionTerminationTests
{
    /// <summary>
    /// Object id of the lock the terminating test holds. Distinct from
    /// <see cref="MigrationApplyLock.ObjectId"/>, and included in the lock count so a leak fails.
    /// </summary>
    private const int TerminatingLockObjectId = 843991;

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
        BlockFirstCommandInterceptor interceptor = new();
        ReplaceContext(builder.Services, connectionString, interceptor);
        builder.Services.Replace(ServiceDescriptor.Singleton<IMigrationApplier>(
            new TerminatingApplier(connectionString, interceptor)));
        return builder.Build();
    }

    /// <summary>
    /// Swaps the context <c>ConfigureServices</c> registered for one whose first migration
    /// command can be held until this test terminates the backend.
    /// </summary>
    private static void ReplaceContext(
        IServiceCollection services,
        string connectionString,
        BlockFirstCommandInterceptor interceptor)
    {
        Type configuration = typeof(IDbContextOptionsConfiguration<TopstepXDbContext>);
        ServiceDescriptor[] remove = services
            .Where(descriptor =>
                descriptor.ServiceType == typeof(TopstepXDbContext)
                || descriptor.ServiceType == typeof(DbContextOptions<TopstepXDbContext>)
                || descriptor.ServiceType == typeof(DbContextOptions)
                || descriptor.ServiceType == configuration)
            .ToArray();
        foreach (ServiceDescriptor descriptor in remove)
        {
            services.Remove(descriptor);
        }

        services.AddDbContext<TopstepXDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql => npgsql.UseVector())
                .AddInterceptors(interceptor));
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
              AND objid IN (@migrationObjectId, @terminatingObjectId)
              AND database = (SELECT oid FROM pg_database WHERE datname = current_database())
            """,
            connection);
        command.Parameters.Add(new NpgsqlParameter("classId", MigrationApplyLock.ClassId));
        command.Parameters.Add(new NpgsqlParameter("migrationObjectId", MigrationApplyLock.ObjectId));
        command.Parameters.Add(new NpgsqlParameter(
            "terminatingObjectId",
            TerminatingLockObjectId));
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
    /// Holds a session advisory lock, then lets the migration's first command run a function
    /// that notices once it has started and then waits on that lock. The notice completes
    /// <see cref="BlockFirstCommandInterceptor.CommandActive"/>. The migration's own exception is
    /// what <c>MigrateAsync</c> has to classify.
    /// </summary>
    private sealed class TerminatingApplier(string connectionString, BlockFirstCommandInterceptor interceptor)
        : IMigrationApplier
    {
        public async Task ApplyAsync(
            DatabaseFacade database,
            int commandTimeoutSeconds,
            CancellationToken cancellationToken)
        {
            await using NpgsqlConnection holder = new(connectionString);
            await holder.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(
                holder,
                LockSql("pg_advisory_lock"),
                cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(holder, CreateFunctionSql(), cancellationToken).ConfigureAwait(false);

            using CancellationTokenSource stopKill = new();
            Task kill = TerminateWhenBlockedAsync(holder, stopKill.Token);
            Exception? failure = null;
            interceptor.Arm();
            try
            {
                try
                {
                    await new MigrationApplier()
                        .ApplyAsync(database, commandTimeoutSeconds, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception migrationFailure)
                {
                    failure = migrationFailure;
                }

                if (failure is null)
                {
                    await stopKill.CancelAsync().ConfigureAwait(false);
                }

                Exception? killFailure = null;
                try
                {
                    await kill.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    killFailure = ex;
                }

                if (failure is null)
                {
                    throw new InvalidOperationException(
                        "The migration finished before the backend was terminated. Rewrote="
                        + interceptor.Rewrote + ".",
                        killFailure);
                }

                if (killFailure is not null)
                {
                    throw new InvalidOperationException(
                        "The apply failed and pg_terminate_backend did not confirm. Apply: "
                        + failure.GetType().Name + ": " + failure.Message,
                        killFailure);
                }

                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            }
            finally
            {
                if (holder.State == ConnectionState.Open)
                {
                    try
                    {
                        await ExecuteAsync(
                            holder,
                            "DROP FUNCTION IF EXISTS marqspec_block_for_terminate()",
                            CancellationToken.None).ConfigureAwait(false);
                        await ExecuteAsync(
                            holder,
                            LockSql("pg_advisory_unlock"),
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (NpgsqlException)
                    {
                        // Closing the holder releases the session lock. The empty database is
                        // discarded. Cleanup must not mask the apply's exception.
                    }
                }
            }
        }

        private async Task TerminateWhenBlockedAsync(
            NpgsqlConnection holder,
            CancellationToken cancellationToken)
        {
            // A deadlock guard, not a race. The pass path completes when the notice arrives.
            // If it never does, kill the backend we know about so the command cannot sit until
            // the migration command timeout.
            using CancellationTokenSource deadlockGuard = new(TimeSpan.FromSeconds(30));
            using CancellationTokenSource wait = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                deadlockGuard.Token);
            try
            {
                await interceptor.CommandActive.Task.WaitAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException) when (deadlockGuard.IsCancellationRequested)
            {
                Exception? killFailure = await KillKnownBackendAsync(holder).ConfigureAwait(false);
                throw new InvalidOperationException(
                    "The applying command did not report that it had started. Backend "
                    + interceptor.BackendPid + ".",
                    killFailure);
            }

            await KillAsync(holder, interceptor.BackendPid).ConfigureAwait(false);
        }

        private async Task<Exception?> KillKnownBackendAsync(NpgsqlConnection holder)
        {
            if (interceptor.BackendPid == 0)
            {
                return null;
            }

            try
            {
                await KillAsync(holder, interceptor.BackendPid).ConfigureAwait(false);
                return null;
            }
            catch (Exception killFailure)
            {
                // The notice never arrived, so the test is already failing. The kill's own
                // exception stays on that failure instead of being discarded.
                return killFailure;
            }
        }

        private static async Task KillAsync(NpgsqlConnection holder, int pid)
        {
            if (pid == 0)
            {
                throw new InvalidOperationException("The applying connection has no backend pid.");
            }

            await using NpgsqlCommand kill = new("SELECT pg_terminate_backend(@pid)", holder);
            kill.Parameters.Add(new NpgsqlParameter("pid", pid));
            object? signalled = await kill.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false);
            if (signalled is not true)
            {
                throw new InvalidOperationException(
                    "pg_terminate_backend did not signal backend " + pid + ".");
            }
        }

        private static async Task ExecuteAsync(
            NpgsqlConnection connection,
            string sql,
            CancellationToken cancellationToken)
        {
            await using NpgsqlCommand command = new(sql, connection);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        private static string LockSql(string function) =>
            string.Create(
                CultureInfo.InvariantCulture,
                $"SELECT {function}({MigrationApplyLock.ClassId}, {TerminatingLockObjectId})");

        private static string CreateFunctionSql() =>
            string.Create(
                CultureInfo.InvariantCulture,
                $"""
                CREATE OR REPLACE FUNCTION marqspec_block_for_terminate() RETURNS void
                LANGUAGE plpgsql AS $fn$
                BEGIN
                  RAISE NOTICE '{BlockFirstCommandInterceptor.ApplyingNotice}';
                  PERFORM pg_advisory_lock({MigrationApplyLock.ClassId}, {TerminatingLockObjectId});
                END;
                $fn$;
                """);
    }

    /// <summary>
    /// Rewrites the first migration command into a function that notices once it has started,
    /// then waits on the lock. Commands
    /// before <see cref="Arm"/>, including the startup probe, are left alone.
    /// </summary>
    private sealed class BlockFirstCommandInterceptor : DbCommandInterceptor
    {
        /// <summary>The notice text the function raises once it has started.</summary>
        public const string ApplyingNotice = "marqspec-applying";

        private int _armed;
        private int _rewritten;

        /// <summary>Completed when the running command has raised its notice.</summary>
        public TaskCompletionSource CommandActive { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The backend running the rewritten command. Zero until that command is armed.</summary>
        public int BackendPid { get; private set; }

        /// <summary>Whether a command was rewritten.</summary>
        public bool Rewrote => Volatile.Read(ref _rewritten) == 1;

        /// <summary>The next command is the one to hold open.</summary>
        public void Arm() => Volatile.Write(ref _armed, 1);

        /// <inheritdoc />
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Rewrite(command);
            return result;
        }

        /// <inheritdoc />
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Rewrite(command);
            return ValueTask.FromResult(result);
        }

        /// <inheritdoc />
        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result)
        {
            Rewrite(command);
            return result;
        }

        /// <inheritdoc />
        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Rewrite(command);
            return ValueTask.FromResult(result);
        }

        /// <inheritdoc />
        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            Rewrite(command);
            return result;
        }

        /// <inheritdoc />
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Rewrite(command);
            return ValueTask.FromResult(result);
        }

        private void Rewrite(DbCommand command)
        {
            if (Volatile.Read(ref _armed) == 0 || Interlocked.Exchange(ref _rewritten, 1) != 0)
            {
                return;
            }

            if (command.Connection is NpgsqlConnection connection)
            {
                BackendPid = connection.ProcessID;
                connection.Notice += (_, args) =>
                {
                    if (args.Notice.MessageText.Contains(ApplyingNotice, StringComparison.Ordinal))
                    {
                        CommandActive.TrySetResult();
                    }
                };
            }

            command.CommandType = CommandType.Text;
            command.Parameters.Clear();
            command.CommandText = "SELECT marqspec_block_for_terminate()";
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
