using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// A store that is not there when the server starts, and arrives shortly after.
/// </summary>
/// <remarks>
/// <para>
/// This is the ECS cold start, and it needs a real container because the claim is about a real socket. The
/// unit tier proves the retry loop's arithmetic against a fake clock; what it cannot prove is that a
/// <c>CanConnectAsync</c> against a port with nothing behind it returns <c>false</c> rather than throwing, that
/// it does so quickly enough for a bounded wait to make several attempts, and that the context which has
/// already failed to connect goes on to migrate cleanly once the store answers. A connection that faulted the
/// wait, or a pooled connection poisoned by the failed attempts, would leave the server degraded exactly as
/// the single probe did — and no in-memory provider can tell you either way.
/// </para>
/// <para>
/// Compose gates the server on <c>pg_isready</c> and so never sees this. ECS has no cross-service ordering, so
/// on a cold start or a Postgres task replacement the server can reach the migration first; before gh#514 a
/// single probe then left the task degraded for its whole life, healthy and serving refusals.
/// </para>
/// </remarks>
public sealed class StoreStartupWaitTests
{
    /// <summary>
    /// A wait of sixty seconds against a store that appears after about five ends available and migrated.
    /// </summary>
    [Fact]
    public async Task AWaitingStartup_EndsAvailableAndMigrated_WhenTheStoreArrivesLate()
    {
        int port = ReserveHostPort();
        string connection =
            $"Host=localhost;Port={port};Database=topstepx_mcp_test;Username=topstepx;Password=test-only";

        // The port is chosen here rather than by the container, which is the one place this suite departs from
        // the QA contract's "let the container pick its port". It has to: the whole claim is that the server
        // holds a connection string for a store that does not exist yet, and a container-assigned port only
        // exists once the container does. The port is taken from the ephemeral range and released immediately,
        // so the window another process could take it in is the five seconds below.
        await using PostgreSqlContainer postgres = new PostgreSqlBuilder("timescale/timescaledb-ha:pg17")
            .WithDatabase("topstepx_mcp_test")
            .WithUsername("topstepx")
            .WithPassword("test-only")
            .WithPortBinding(port, 5432)
            .Build();

        Task arriving = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            await postgres.StartAsync();
        });

        await using TopstepXDbContext database = new(
            new DbContextOptionsBuilder<TopstepXDbContext>()
                .UseNpgsql(connection, npgsql => npgsql.UseVector())
                .Options);

        StoreAvailability reached = await StoreStartup.ReachAsync(
            token => database.Database.CanConnectAsync(token),
            connection,
            TimeSpan.FromSeconds(60),
            TimeProvider.System,
            NullLogger.Instance,
            CancellationToken.None);

        // Awaited so a container that failed to start surfaces as its own exception rather than as an
        // unexplained "the store never answered".
        await arriving;

        reached.IsAvailable.Should().BeTrue(
            "a bounded wait exists so a store that arrives after the server does not degrade it for the "
            + "life of the task");

        // The context that spent the wait failing to connect still migrates.
        await database.Database.MigrateAsync();

        (await database.Database.GetAppliedMigrationsAsync()).Should().NotBeEmpty();
    }

    /// <summary>
    /// A zero wait against the same absent store degrades immediately — today's behaviour, unchanged, and the
    /// reason the option defaults to zero.
    /// </summary>
    [Fact]
    public async Task AZeroWait_DegradesImmediately_AgainstAStoreThatIsNotThere()
    {
        int port = ReserveHostPort();
        string connection =
            $"Host=localhost;Port={port};Database=topstepx_mcp_test;Username=topstepx;Password=test-only";

        await using TopstepXDbContext database = new(
            new DbContextOptionsBuilder<TopstepXDbContext>()
                .UseNpgsql(connection, npgsql => npgsql.UseVector())
                .Options);

        StoreAvailability reached = await StoreStartup.ReachAsync(
            token => database.Database.CanConnectAsync(token),
            connection,
            TimeSpan.Zero,
            TimeProvider.System,
            NullLogger.Instance,
            CancellationToken.None);

        reached.IsAvailable.Should().BeFalse();

        // gh#551: the coordinates reach the log StoreStartup.ReachAsync writes, never Explanation --
        // Require() turns Explanation into the McpException a bearer-token holder receives, and under
        // ADR-0021's non-loopback instance that holder is not necessarily the operator who should learn the
        // database's host, port, name and username. The unit tier (StoreStartupTests) pins the log side
        // against a fake clock; this only needs to confirm neither the coordinates nor the password leak into
        // the caller-facing text a real Npgsql connection string produces.
        reached.Explanation.Should().NotContain("host=localhost").And.NotContain($"port={port}");
        reached.Explanation.Should().NotContain("test-only", "the password never reaches an operator-facing line");
        reached.Explanation.Should().Contain(
            "ConnectionStrings__Default", "the caller-facing text still carries the operator-facing fix");
    }

    /// <summary>Takes a free port from the ephemeral range and releases it.</summary>
    /// <returns>A port nothing was listening on a moment ago.</returns>
    private static int ReserveHostPort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
