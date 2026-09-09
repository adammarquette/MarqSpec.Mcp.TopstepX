using FluentAssertions;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// What the server says, and how long it waits, when the store does not answer at startup.
/// </summary>
/// <remarks>
/// <para>
/// Two failures this pins, both of which read as ordinary operation in a container. The first: a warning that
/// named nothing about what it tried sends an operator to look at the database when the real cause is a secret
/// that never landed, because a substituted <c>Host=localhost</c> is invisible in the log. The second: ECS has
/// no cross-service ordering, so a cold start can reach the migration before Postgres answers at all, and a
/// single probe degrades the server for the life of the task — healthy, and serving refusals (gh#514).
/// </para>
/// <para>
/// <b>The password is the whole reason this is a seam rather than a formatted string at the call site.</b> The
/// connection string carries one, this repository is public, and the sibling ProjectX client has already leaked
/// and rotated a real credential once. Every case here therefore asserts the sentinel never reaches a log line
/// — not only the line under test.
/// </para>
/// </remarks>
public sealed class StoreStartupTests
{
    /// <summary>The string <c>Program</c> substitutes when <c>ConnectionStrings__Default</c> is unset.</summary>
    private const string Fallback =
        "Host=localhost;Port=5432;Database=topstepx_mcp;Username=topstepx;Password=changeme-local";

    /// <summary>The password in <see cref="Fallback"/>, which must never appear anywhere.</summary>
    private const string Sentinel = "changeme-local";

    /// <summary>
    /// The description names all four coordinates an operator needs to tell a missing secret from a stopped
    /// database.
    /// </summary>
    [Fact]
    public void TheTargetDescription_NamesHostPortDatabaseAndUser()
    {
        string target = StoreStartup.DescribeTarget(Fallback);

        target.Should().Contain("host=localhost")
            .And.Contain("port=5432")
            .And.Contain("database=topstepx_mcp")
            .And.Contain("user=topstepx");
    }

    /// <summary>The password is not in the description, and neither is the word.</summary>
    [Fact]
    public void TheTargetDescription_CarriesNoPassword()
    {
        StoreStartup.DescribeTarget(Fallback).Should()
            .NotContain(Sentinel)
            .And.NotContain("assword");
    }

    /// <summary>
    /// A connection string Npgsql cannot parse still yields a describable target rather than an echo of the
    /// string — echoing it would print the password on the one path least likely to be tested.
    /// </summary>
    [Fact]
    public void TheTargetDescription_RefusesToEchoAnUnparseableString()
    {
        string target = StoreStartup.DescribeTarget("Host=localhost;Password=" + Sentinel + ";;=broken");

        target.Should().NotContain(Sentinel);
    }

    /// <summary>An unset connection string is named as unset, not as an empty target.</summary>
    [Fact]
    public void TheTargetDescription_SaysSoWhenNothingIsConfigured()
    {
        StoreStartup.DescribeTarget(null).Should().NotBeNullOrWhiteSpace();
        StoreStartup.DescribeTarget(string.Empty).Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// The warning an operator actually reads names the target. This is the acceptance criterion of gh#514.
    /// </summary>
    [Fact]
    public async Task TheUnreachableWarning_NamesTheTarget()
    {
        CollectingLogger logger = new();

        StoreAvailability result = await StoreStartup.ReachAsync(
            _ => Task.FromResult(false),
            Fallback,
            TimeSpan.Zero,
            new FakeTimeProvider(),
            logger,
            CancellationToken.None);

        result.IsAvailable.Should().BeFalse();

        string warning = logger.Lines.Should().ContainSingle(line => line.Level == LogLevel.Warning)
            .Subject.Message;

        warning.Should().Contain("localhost")
            .And.Contain("5432")
            .And.Contain("topstepx_mcp")
            .And.Contain("user=topstepx");
    }

    /// <summary>
    /// The coordinates reach only the log. <see cref="StoreAvailability.Require"/> turns
    /// <see cref="StoreAvailability.Explanation"/> into the <see cref="McpException"/> every store-requiring
    /// tool call answers with, and under ADR-0021's non-loopback instance the caller holding that bearer token
    /// is not necessarily the operator who should learn the database's host, port, name and username
    /// (PR #548 review, gh#551).
    /// </summary>
    [Fact]
    public async Task TheCallerFacingException_NamesTheFixButNotTheCoordinates()
    {
        CollectingLogger logger = new();

        StoreAvailability result = await StoreStartup.ReachAsync(
            _ => Task.FromResult(false),
            Fallback,
            TimeSpan.Zero,
            new FakeTimeProvider(),
            logger,
            CancellationToken.None);

        string warning = logger.Lines.Should().ContainSingle(line => line.Level == LogLevel.Warning)
            .Subject.Message;

        warning.Should().Contain("host=localhost")
            .And.Contain("port=5432")
            .And.Contain("database=topstepx_mcp")
            .And.Contain("user=topstepx");

        McpException thrown = result.Invoking(static r => r.Require()).Should().Throw<McpException>().Which;

        thrown.Message.Should().NotContain("host=localhost")
            .And.NotContain("port=5432")
            .And.NotContain("database=topstepx_mcp")
            .And.NotContain("user=topstepx");
        thrown.Message.Should().Contain("ConnectionStrings__Default");
    }

    /// <summary>Nothing credential-shaped reaches any line, at any level, on the unreachable path.</summary>
    [Fact]
    public async Task NoLogLine_CarriesThePassword_WhenTheStoreNeverAnswers()
    {
        CollectingLogger logger = new();
        FakeTimeProvider clock = new();

        Task<StoreAvailability> pending = StoreStartup.ReachAsync(
            _ => Task.FromResult(false),
            Fallback,
            TimeSpan.FromSeconds(20),
            clock,
            logger,
            CancellationToken.None);

        StoreAvailability result = await DriveAsync(pending, clock, TimeSpan.FromSeconds(80));

        result.IsAvailable.Should().BeFalse();
        logger.Lines.Should().NotBeEmpty();
        logger.Lines.Should().OnlyContain(line => !line.Message.Contains(Sentinel, StringComparison.Ordinal));
        logger.Lines.Should().OnlyContain(line => !line.Message.Contains("assword", StringComparison.Ordinal));
    }

    /// <summary>
    /// The default is today's behaviour exactly: one probe, no delay, no retry. Anything else would change
    /// what a stdio client already relies on.
    /// </summary>
    [Fact]
    public async Task AZeroWait_ProbesExactlyOnce()
    {
        int probes = 0;

        StoreAvailability result = await StoreStartup.ReachAsync(
            _ =>
            {
                probes++;
                return Task.FromResult(false);
            },
            Fallback,
            TimeSpan.Zero,
            new FakeTimeProvider(),
            new CollectingLogger(),
            CancellationToken.None);

        probes.Should().Be(1);
        result.IsAvailable.Should().BeFalse();
    }

    /// <summary>A store that answers on the first probe never waits, whatever the bound is.</summary>
    [Fact]
    public async Task AStoreThatAnswersImmediately_IsAvailable_AndWarnsAboutNothing()
    {
        CollectingLogger logger = new();

        StoreAvailability result = await StoreStartup.ReachAsync(
            _ => Task.FromResult(true),
            Fallback,
            TimeSpan.FromSeconds(90),
            new FakeTimeProvider(),
            logger,
            CancellationToken.None);

        result.IsAvailable.Should().BeTrue();
        logger.Lines.Should().BeEmpty();
    }

    /// <summary>
    /// The ECS case: Postgres is not there when the server starts and arrives shortly after. A non-zero bound
    /// keeps probing and the server ends up available rather than degraded for the life of the task.
    /// </summary>
    [Fact]
    public async Task ANonZeroWait_KeepsProbing_UntilTheStoreAnswers()
    {
        int probes = 0;
        CollectingLogger logger = new();
        FakeTimeProvider clock = new();

        Task<StoreAvailability> pending = StoreStartup.ReachAsync(
            _ => Task.FromResult(++probes >= 3),
            Fallback,
            TimeSpan.FromSeconds(60),
            clock,
            logger,
            CancellationToken.None);

        StoreAvailability result = await DriveAsync(pending, clock, TimeSpan.FromSeconds(120));

        result.IsAvailable.Should().BeTrue();
        probes.Should().Be(3);

        // Each attempt is announced at Information, naming the same target as the warning would.
        logger.Lines.Should().HaveCount(2);
        logger.Lines.Should().OnlyContain(line => line.Level == LogLevel.Information);
        logger.Lines.Should().OnlyContain(line => line.Message.Contains("topstepx_mcp", StringComparison.Ordinal));
    }

    /// <summary>
    /// The bound is a bound. A store that never arrives degrades the server rather than holding startup open,
    /// and the warning still names the target.
    /// </summary>
    /// <remarks>
    /// The elapsed-time assertion is the one that pins the clamp
    /// (<c>delay = backoff &lt; remaining ? backoff : remaining</c>): with every probe synchronous, the fake
    /// clock advances by nothing but the delays between them, so the total elapsed at completion is bounded by
    /// the configured wait only because the last delay is clamped to what remains of it. Deleting the clamp
    /// (mutating the ternary to <c>delay = backoff</c>) lets the final, uncapped backoff overshoot the ten
    /// second bound and turns this assertion red — confirmed locally by making that edit, watching this test
    /// fail, and reverting it (gh#551).
    /// </remarks>
    [Fact]
    public async Task ANonZeroWait_GivesUpAtTheBound_AndStillNamesTheTarget()
    {
        int probes = 0;
        CollectingLogger logger = new();
        FakeTimeProvider clock = new();
        DateTimeOffset start = clock.GetUtcNow();

        Task<StoreAvailability> pending = StoreStartup.ReachAsync(
            _ =>
            {
                probes++;
                return Task.FromResult(false);
            },
            Fallback,
            TimeSpan.FromSeconds(10),
            clock,
            logger,
            CancellationToken.None);

        StoreAvailability result = await DriveAsync(pending, clock, TimeSpan.FromSeconds(70));

        result.IsAvailable.Should().BeFalse();
        probes.Should().BeGreaterThan(1, "a non-zero bound means more than the single probe wait=0 makes");

        (clock.GetUtcNow() - start).Should().BeLessOrEqualTo(
            TimeSpan.FromSeconds(10),
            "every probe here is synchronous, so the fake clock advances only by the delays between "
            + "attempts -- the clamp is what keeps their sum from overshooting the bound");

        string warning = logger.Lines.Should().ContainSingle(line => line.Level == LogLevel.Warning)
            .Subject.Message;

        warning.Should().Contain("localhost").And.Contain("topstepx_mcp");
    }

    /// <summary>
    /// Drives a pending wait by advancing the fake clock, so a test of a sixty-second bound costs no
    /// wall-clock time. The real-time <see cref="Task.WaitAsync(TimeSpan)"/> is a safety net: a wait that
    /// stops making progress fails the test rather than hanging the run.
    /// </summary>
    /// <param name="pending">The wait under test.</param>
    /// <param name="clock">The clock it is delaying against.</param>
    /// <param name="horizon">How much fake time to grant before giving up on it.</param>
    /// <returns>The wait's answer.</returns>
    private static async Task<StoreAvailability> DriveAsync(
        Task<StoreAvailability> pending,
        FakeTimeProvider clock,
        TimeSpan horizon)
    {
        DateTimeOffset deadline = clock.GetUtcNow() + horizon;

        while (!pending.IsCompleted && clock.GetUtcNow() < deadline)
        {
            // Several yields per step: the worker has to get from "the probe answered no" to "the delay is
            // registered" before advancing means anything, and one yield does not reliably buy that.
            for (int i = 0; i < 8 && !pending.IsCompleted; i++)
            {
                await Task.Yield();
            }

            if (pending.IsCompleted)
            {
                break;
            }

            clock.Advance(TimeSpan.FromMilliseconds(100));
        }

        return await pending.WaitAsync(TimeSpan.FromSeconds(30));
    }

    /// <summary>Captures every line at every level, because the password claim is about all of them.</summary>
    private sealed class CollectingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Lines { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            Lines.Add((logLevel, formatter(state, exception)));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
