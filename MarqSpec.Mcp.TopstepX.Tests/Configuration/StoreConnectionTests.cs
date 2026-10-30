using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace MarqSpec.Mcp.TopstepX.Tests.Configuration;

/// <summary>
/// The store connection string has one source, including the local fallback (gh#843).
/// </summary>
public sealed class StoreConnectionTests
{
    [Fact]
    public void Resolve_WhenUnset_UsesTheLocalFallback()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();

        StoreConnection.Resolve(configuration).Should().Be(StoreConnection.LocalFallback);
        StoreConnection.LocalFallback.Should().Contain("changeme-local");
    }

    [Fact]
    public void Resolve_WhenConfigured_UsesThatString()
    {
        const string Configured =
            "Host=db;Port=5432;Database=topstepx_mcp;Username=topstepx;Password=secret";
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = Configured,
            })
            .Build();

        StoreConnection.Resolve(configuration).Should().Be(Configured);
    }

    /// <summary>
    /// The database EF opens is the database the lock string names, including the local fallback.
    /// </summary>
    [Theory]
    [InlineData(null, "localhost", "topstepx_mcp")]
    [InlineData(
        "Host=db.example;Port=5432;Database=shared_store;Username=topstepx;Password=secret",
        "db.example",
        "shared_store")]
    public void TheLockAndEfUseTheSameDatabase(string? configured, string host, string database)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        StripAmbientEnvironment(builder);
        Dictionary<string, string?> settings = new()
        {
            ["Mcp:Transport"] = "Stdio",
        };
        if (configured is not null)
        {
            settings["ConnectionStrings:Default"] = configured;
        }

        builder.Configuration.AddInMemoryCollection(settings);
        Program.ConfigureServices(builder, new McpOptions { Transport = McpTransport.Stdio });
        using WebApplication app = builder.Build();
        using IServiceScope scope = app.Services.CreateScope();
        string? opened = scope.ServiceProvider.GetRequiredService<TopstepXDbContext>()
            .Database.GetConnectionString();
        string resolved = StoreConnection.Resolve(
            scope.ServiceProvider.GetRequiredService<IConfiguration>());

        NpgsqlConnectionStringBuilder fromEf = new(opened);
        NpgsqlConnectionStringBuilder fromLock = new(resolved);
        fromEf.Database.Should().Be(fromLock.Database);
        fromEf.Host.Should().Be(fromLock.Host);
        fromLock.Host.Should().Be(host);
        fromLock.Database.Should().Be(database);
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
