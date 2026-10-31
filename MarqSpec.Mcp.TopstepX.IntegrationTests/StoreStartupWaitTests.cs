using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.EntityFrameworkCore;
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
    /// A store that is absent when the wait starts, and arrives only after that wait has already missed,
    /// ends available and migrated.
    /// </summary>
    /// <remarks>
    /// The container is not started on a timer. <see cref="StoreStartup.ReachAsync"/> runs first, against a
    /// port with nothing behind it, and the container starts only after two real <c>CanConnectAsync</c>
    /// probes have returned false — the first miss, and the retry. Run 41163750189 (gh#880) spent 1 m 28 s
    /// and still saw <c>IsAvailable</c> false: a 5 s delay and a 60 s product budget overlapped, and
    /// Timescale's start outlived the budget. The same SHA passed in 54.8 s when start finished inside it.
    /// The product bound did what it is configured to do. This test's clock was the flake.
    /// </remarks>
    [Fact]
    public async Task AWaitingStartup_EndsAvailableAndMigrated_WhenTheStoreArrivesLate()
    {
        // Six minutes covers a Timescale pull and health check. Eight minutes keeps the product wait
        // open past this test's own timeout, so a slow start fails here — naming which wait did not
        // finish — rather than as IsAvailable false from a budget the container could not meet.
        TimeSpan budget = TimeSpan.FromMinutes(6);
        TimeSpan wait = TimeSpan.FromMinutes(8);
        const int missesBeforeArrival = 2;

        int port = ReserveHostPort();
        string connection =
            $"Host=localhost;Port={port};Database=topstepx_mcp_test;Username=topstepx;Password=test-only";

        // The port is chosen here rather than by the container, which is the one place this suite departs
        // from the QA contract's "let the container pick its port". It has to: the whole claim is that the
        // server holds a connection string for a store that does not exist yet, and a container-assigned port
        // only exists once the container does. The port is taken from the ephemeral range and released
        // immediately. Another process can take it until this test binds it, after the misses below.
        await using PostgreSqlContainer postgres = new PostgreSqlBuilder("timescale/timescaledb-ha:pg17")
            .WithDatabase("topstepx_mcp_test")
            .WithUsername("topstepx")
            .WithPassword("test-only")
            .WithPortBinding(port, 5432)
            .Build();

        await using TopstepXDbContext database = new(
            new DbContextOptionsBuilder<TopstepXDbContext>()
                .UseNpgsql(connection, npgsql => npgsql.UseVector())
                .Options);

        int misses = 0;
        TaskCompletionSource<int> missedTwice = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CapturingLogger<StoreStartupWaitTests> logger = new();
        Task<StoreAvailability>? waiting = null;

        using CancellationTokenSource clock = new(budget);

        try
        {
            waiting = StoreStartup.ReachAsync(
                async token =>
                {
                    bool connected = await database.Database.CanConnectAsync(token);

                    if (!connected)
                    {
                        int seen = Interlocked.Increment(ref misses);

                        if (seen >= missesBeforeArrival)
                        {
                            missedTwice.TrySetResult(seen);
                        }
                    }

                    return connected;
                },
                connection,
                wait,
                TimeProvider.System,
                logger,
                clock.Token);

            await UntilTheBudgetAsync(
                Task.WhenAny(missedTwice.Task, waiting),
                clock,
                () =>
                    "the waiter never recorded "
                    + missesBeforeArrival
                    + " failed CanConnectAsync probes inside the "
                    + (int)budget.TotalSeconds
                    + " s budget, so the store was not started. Failed probes so far: "
                    + Volatile.Read(ref misses)
                    + ".");

            if (waiting.IsCompleted)
            {
                StoreAvailability early = await waiting;

                throw new InvalidOperationException(
                    "startup returned before the store was started (available="
                    + early.IsAvailable
                    + ", failed probes="
                    + Volatile.Read(ref misses)
                    + "). A late arrival has to follow the waiter.");
            }

            int seenBeforeStart = await missedTwice.Task;

            seenBeforeStart.Should().BeGreaterThanOrEqualTo(
                missesBeforeArrival,
                "the container starts only after startup has probed an absent store more than once");

            // StartAsync waits on the container's own readiness. A failure to start surfaces as that
            // call's exception; only this test's budget becomes the timeout below.
            await UntilTheBudgetAsync(
                postgres.StartAsync(clock.Token),
                clock,
                () =>
                    "the late store's container did not become ready inside the "
                    + (int)budget.TotalSeconds
                    + " s budget, after "
                    + seenBeforeStart
                    + " failed probes.");

            StoreAvailability reached = await UntilTheBudgetAsync(
                waiting,
                clock,
                () =>
                    "startup was still waiting after the container reported ready, inside the "
                    + (int)budget.TotalSeconds
                    + " s budget. Failed probes: "
                    + Volatile.Read(ref misses)
                    + ".");

            reached.IsAvailable.Should().BeTrue(
                "a bounded wait exists so a store that arrives after the server does not degrade it for the "
                + "life of the task. The container was started only after {0} failed probes. Log: {1}",
                seenBeforeStart,
                string.Join(" | ", logger.Messages));

            // The context that spent the wait failing to connect still migrates.
            await UntilTheBudgetAsync(
                database.Database.MigrateAsync(clock.Token),
                clock,
                () =>
                    "the store answered, but MigrateAsync did not finish inside the "
                    + (int)budget.TotalSeconds
                    + " s budget.");

            (await UntilTheBudgetAsync(
                database.Database.GetAppliedMigrationsAsync(clock.Token),
                clock,
                () =>
                    "the store migrated, but reading applied migrations did not finish inside the "
                    + (int)budget.TotalSeconds
                    + " s budget.")).Should().NotBeEmpty();
        }
        finally
        {
            if (waiting is { IsCompleted: false })
            {
                await clock.CancelAsync();

                try
                {
                    await waiting;
                }
                catch (OperationCanceledException)
                {
                    // The failure that left the waiter running is the one the test reports.
                }
            }
        }
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

        // A capturing logger, not NullLogger: gh#551's split moved the coordinates onto the log line and out
        // of Explanation, and the negative half below (Explanation excludes them) proves nothing about the
        // positive half without something that can show the log still carries them -- against a real
        // Npgsql connection string, not only the unit tier's literal.
        CapturingLogger<StoreStartupWaitTests> logger = new();

        StoreAvailability reached = await StoreStartup.ReachAsync(
            token => database.Database.CanConnectAsync(token),
            connection,
            TimeSpan.Zero,
            TimeProvider.System,
            logger,
            CancellationToken.None);

        reached.IsAvailable.Should().BeFalse();

        // gh#551: the coordinates reach the log StoreStartup.ReachAsync writes, never Explanation --
        // Require() turns Explanation into the McpException a bearer-token holder receives, and under
        // ADR-0021's non-loopback instance that holder is not necessarily the operator who should learn the
        // database's host, port, name and username. The unit tier (StoreStartupTests) pins the log side
        // against a fake clock; this confirms both halves against a real Npgsql connection string: the
        // coordinates still reach the log, and neither they nor the password leak into the caller-facing text.
        logger.Messages.Should().ContainSingle(m => m.Contains("host=localhost", StringComparison.Ordinal))
            .Which.Should().Contain($"port={port}");
        reached.Explanation.Should().NotContain("host=localhost").And.NotContain($"port={port}");
        reached.Explanation.Should().NotContain("test-only", "the password never reaches an operator-facing line");
        reached.Explanation.Should().Contain(
            "ConnectionStrings__Default", "the caller-facing text still carries the operator-facing fix");
    }

    /// <summary>
    /// Awaits <paramref name="work"/> until it finishes or <paramref name="budget"/> fires.
    /// </summary>
    /// <typeparam name="T">The task's result.</typeparam>
    /// <param name="work">The wait, the container start, or the migration.</param>
    /// <param name="budget">The one clock for the whole late-arrival test.</param>
    /// <param name="failure">
    /// What to say when the clock fires. Built at that moment, so a probe count is the count then.
    /// </param>
    /// <returns>The result of <paramref name="work"/>.</returns>
    /// <exception cref="TimeoutException">
    /// <paramref name="budget"/> elapsed. The message is <paramref name="failure"/>.
    /// </exception>
    private static async Task<T> UntilTheBudgetAsync<T>(
        Task<T> work,
        CancellationTokenSource budget,
        Func<string> failure)
    {
        try
        {
            return await work.WaitAsync(budget.Token);
        }
        catch (OperationCanceledException cancelled) when (budget.IsCancellationRequested)
        {
            throw new TimeoutException(failure(), cancelled);
        }
    }

    /// <summary>
    /// Awaits <paramref name="work"/> until it finishes or <paramref name="budget"/> fires.
    /// </summary>
    /// <param name="work">A task that has no result of its own.</param>
    /// <param name="budget">The one clock for the whole late-arrival test.</param>
    /// <param name="failure">
    /// What to say when the clock fires. Built at that moment, so a probe count is the count then.
    /// </param>
    /// <exception cref="TimeoutException">
    /// <paramref name="budget"/> elapsed. The message is <paramref name="failure"/>.
    /// </exception>
    private static async Task UntilTheBudgetAsync(
        Task work,
        CancellationTokenSource budget,
        Func<string> failure)
    {
        try
        {
            await work.WaitAsync(budget.Token);
        }
        catch (OperationCanceledException cancelled) when (budget.IsCancellationRequested)
        {
            throw new TimeoutException(failure(), cancelled);
        }
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
