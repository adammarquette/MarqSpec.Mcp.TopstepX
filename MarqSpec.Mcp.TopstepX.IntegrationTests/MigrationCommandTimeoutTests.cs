using System.Diagnostics;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.DependencyInjection;
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
}
