using System.Data.Common;
using System.Diagnostics;
using System.Reflection;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Migrations;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// <c>MigrateAsync</c> uses the configured command timeout, then puts the previous one back (gh#756).
/// </summary>
[Collection(SchemaCollection.Name)]
public sealed class MigrationCommandTimeoutTests
{
    private readonly SchemaFixture _fixture;

    public MigrationCommandTimeoutTests(SchemaFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task MigrateAsync_WhenCommandTimeoutSecondsIsConfigured_UsesItOnTheMigrationCommands()
    {
        string name = "timeout" + Guid.NewGuid().ToString("N");
        await using TopstepXDbContext created = await _fixture.CreateEmptyDatabaseAsync(name);
        string connectionString = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            Database = name,
        }.ConnectionString;
        NpgsqlConnectionStringBuilder expected = new(connectionString);

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        StripAmbientEnvironment(builder);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = connectionString,
            ["Migrations:CommandTimeoutSeconds"] = "123",
            ["Mcp:Transport"] = "Stdio",
        });
        Program.ConfigureServices(builder, new McpOptions { Transport = McpTransport.Stdio });
        await using WebApplication app = builder.Build();

        EfCommandObserver observer = new();
        using IDisposable subscription = DiagnosticListener.AllListeners.Subscribe(observer);
        try
        {
            StoreAvailability availability = await Program.MigrateAsync(app);
            availability.IsAvailable.Should().BeTrue();
        }
        finally
        {
            observer.DisposeSources();
            subscription.Dispose();
        }

        observer.Commands
            .Where(command => command.Host == expected.Host
                && command.Port == expected.Port
                && command.Database == expected.Database
                && command.Text.Contains("marqspec_build_ix_trades_instrument_time", StringComparison.Ordinal))
            .Should().NotBeEmpty()
            .And.OnlyContain(command => command.Timeout == 123);
    }

    [Fact]
    public async Task ApplyAsync_WhenTheMigrationReturns_RestoresTheTimeoutItReplaced()
    {
        await using TopstepXDbContext database = _fixture.CreateContext();
        database.Database.SetCommandTimeout(7);
        NpgsqlConnectionStringBuilder expected = new(_fixture.ConnectionString);

        EfCommandObserver observer = new();
        using IDisposable subscription = DiagnosticListener.AllListeners.Subscribe(observer);
        try
        {
            await MigrationCommandTimeout.ApplyAsync(database.Database, 123);
        }
        finally
        {
            observer.DisposeSources();
            subscription.Dispose();
        }

        database.Database.GetCommandTimeout().Should().Be(7);
        observer.Commands
            .Where(command => command.Host == expected.Host
                && command.Port == expected.Port
                && command.Database == expected.Database)
            .Should().NotBeEmpty()
            .And.OnlyContain(command => command.Timeout == 123);
    }

    [Fact]
    public async Task ApplyAsync_WhenMigrateAsyncIsCancelled_RestoresTheTimeoutItReplaced()
    {
        await using TopstepXDbContext database = _fixture.CreateContext();
        database.Database.SetCommandTimeout(7);
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();

        // This is the early cancel. MigrateAsync observes the token in its existence check, before any
        // migration command runs. The mid-build failure is the other test. Restoring the previous timeout
        // only after a successful return would leave 123 in place on both.
        Func<Task> act = () => MigrationCommandTimeout.ApplyAsync(database.Database, 123, cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        database.Database.GetCommandTimeout().Should().Be(7);
    }

    [Fact]
    public async Task ApplyAsync_WhenAMigrationCommandFails_RestoresTheTimeoutItReplaced()
    {
        string name = "cmdfail" + Guid.NewGuid().ToString("N");
        string connectionString = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            Database = name,
        }.ConnectionString;
        await using (TopstepXDbContext prepared = await _fixture.CreateEmptyDatabaseAsync(name))
        {
            await prepared.GetService<IMigrator>()
                .MigrateAsync(MigrationBeforeTheInstrumentTimeIndex());
        }

        FailTheIndexBuild fail = new();
        await using TopstepXDbContext database = new(new DbContextOptionsBuilder<TopstepXDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.UseVector())
            .AddInterceptors(fail)
            .Options);
        database.Database.SetCommandTimeout(7);

        // The index migration has started: the procedure exists, and the CALL that builds the index is
        // rewritten to a statement the server rejects. That is a command failure, not the existence check.
        Func<Task> act = () => MigrationCommandTimeout.ApplyAsync(database.Database, 123);

        await act.Should().ThrowAsync<PostgresException>().Where(exception => exception.SqlState == "22012");
        fail.RewroteTheBuild.Should().BeTrue();
        database.Database.GetCommandTimeout().Should().Be(7);
    }

    [Fact]
    public async Task MigrateAsync_WhenTheStoreAnswers_PassesTheHostStoppingToken()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        StripAmbientEnvironment(builder);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = _fixture.ConnectionString,
            ["Mcp:Transport"] = "Stdio",
        });
        Program.ConfigureServices(builder, new McpOptions { Transport = McpTransport.Stdio });
        RecordingMigrationApplier recording = new();
        builder.Services.Replace(ServiceDescriptor.Singleton<IMigrationApplier>(recording));
        await using WebApplication app = builder.Build();

        StoreAvailability availability = await Program.MigrateAsync(app);

        availability.IsAvailable.Should().BeTrue();
        recording.Called.Should().BeTrue();
        recording.Token.Should().Be(app.Lifetime.ApplicationStopping);
    }

    private static string MigrationBeforeTheInstrumentTimeIndex()
    {
        List<string> ids = typeof(AddTradeInstrumentTimeIndex).Assembly
            .GetTypes()
            .Select(type => type.GetCustomAttribute<MigrationAttribute>())
            .Where(attribute => attribute is not null)
            .Select(attribute => attribute!.Id)
            .Order(StringComparer.Ordinal)
            .ToList();
        int index = ids.FindIndex(id =>
            id.EndsWith("AddTradeInstrumentTimeIndex", StringComparison.Ordinal));
        index.Should().BeGreaterThan(0);
        return ids[index - 1];
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

    private sealed class RecordingMigrationApplier : IMigrationApplier
    {
        public bool Called { get; private set; }

        public CancellationToken Token { get; private set; }

        public Task ApplyAsync(DatabaseFacade database, int commandTimeoutSeconds, CancellationToken cancellationToken)
        {
            Called = true;
            Token = cancellationToken;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Turns the index-build <c>CALL</c> into a statement the server rejects, after the migration has
    /// already created the procedure.
    /// </summary>
    private sealed class FailTheIndexBuild : DbCommandInterceptor
    {
        public bool RewroteTheBuild { get; private set; }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            Rewrite(command);
            return result;
        }

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
            if (!command.CommandText.Contains(
                "CALL public.marqspec_build_ix_trades_instrument_time",
                StringComparison.Ordinal))
            {
                return;
            }

            RewroteTheBuild = true;
            command.CommandText = "SELECT 1/0";
        }
    }
}
