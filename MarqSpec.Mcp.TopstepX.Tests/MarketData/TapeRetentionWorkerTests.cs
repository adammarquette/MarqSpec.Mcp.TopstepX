using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// The retention worker stays off unless this process records, waits out the startup delay,
/// and does not fault the host when a pass throws (gh#763).
/// </summary>
public sealed class TapeRetentionWorkerTests
{
    private static readonly DateTimeOffset _start = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(McpTransport.Stdio, true)]
    [InlineData(McpTransport.Http, false)]
    [InlineData(McpTransport.Stdio, false)]
    public async Task ExecuteAsync_WhenNotHttpOrNotRecording_DoesNotPrune(McpTransport transport, bool record)
    {
        ScriptedPass pass = new();
        FakeTimeProvider clock = new(_start);
        TapeRetentionWorker worker = Worker(pass, transport, record, clock, leaseHeld: true);

        await worker.StartAsync(CancellationToken.None);
        await BackgroundServiceTestSupport.AwaitCompletionAsync(worker.ExecuteTask!);

        pass.Calls.Should().Be(0);
        worker.ExecuteTask!.IsFaulted.Should().BeFalse();
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_BeforeTheStartupDelay_DoesNotPrune()
    {
        ScriptedPass pass = new();
        FakeTimeProvider clock = new(_start);
        TapeRetentionWorker worker = Worker(pass, McpTransport.Http, record: true, clock, leaseHeld: true);

        await worker.StartAsync(CancellationToken.None);
        await WaitUntilArmed(worker, 1);
        clock.Advance(TapeRetentionWorker.StartupDelay - TimeSpan.FromMinutes(1));

        pass.Calls.Should().Be(0);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_WhenTheDelayElapsesAndTheLeaseIsHeld_PrunesOnce()
    {
        ScriptedPass pass = new();
        FakeTimeProvider clock = new(_start);
        TapeRetentionWorker worker = Worker(pass, McpTransport.Http, record: true, clock, leaseHeld: true);

        await worker.StartAsync(CancellationToken.None);
        await WaitUntilArmed(worker, 1);
        clock.Advance(TapeRetentionWorker.StartupDelay);
        await pass.Called.WaitAsync(TimeSpan.FromSeconds(5));

        pass.Calls.Should().Be(1);
        worker.ExecuteTask!.IsFaulted.Should().BeFalse();
        worker.ExecuteTask.IsCompleted.Should().BeFalse();
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_WhenTheLeaseIsNotHeld_WaitsAndThenPrunes()
    {
        ScriptedPass pass = new();
        FakeTimeProvider clock = new(_start);
        HoldingLease lease = new() { Held = false };
        TapeRetentionWorker worker = Worker(pass, McpTransport.Http, record: true, clock, lease);

        await worker.StartAsync(CancellationToken.None);
        await WaitUntilArmed(worker, 1);
        clock.Advance(TapeRetentionWorker.StartupDelay);
        await WaitUntilArmed(worker, 2);
        pass.Calls.Should().Be(0);

        lease.Held = true;
        clock.Advance(TapeRetentionWorker.LeasePoll);
        await pass.Called.WaitAsync(TimeSpan.FromSeconds(5));

        pass.Calls.Should().Be(1);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_WhenThePassThrows_StaysUnfaultedAndLogsTheMessage()
    {
        ScriptedPass pass = new() { Error = new InvalidOperationException("disk full") };
        FakeTimeProvider clock = new(_start);
        ListLogger logs = new();
        using HostTelemetry telemetry = new();
        using MetricCollector<long> failures = new(
            telemetry, HostTelemetry.Name, HostTelemetry.TapePruneFailuresInstrument);
        TapeRetentionWorker worker = Worker(
            pass, McpTransport.Http, record: true, clock, leaseHeld: true, logs, telemetry);

        await worker.StartAsync(CancellationToken.None);
        await WaitUntilArmed(worker, 1);
        clock.Advance(TapeRetentionWorker.StartupDelay);
        await pass.Called.WaitAsync(TimeSpan.FromSeconds(5));
        // Called is signalled before the throw, so the worker's catch can log after this wait.
        using (CancellationTokenSource logged = new(TimeSpan.FromSeconds(5)))
        {
            while (!logs.Messages.ToArray().Any(
                message => message.Contains("disk full", StringComparison.Ordinal)))
            {
                await Task.Delay(1, logged.Token);
            }
        }

        pass.Calls.Should().Be(1);
        worker.ExecuteTask!.IsFaulted.Should().BeFalse();
        logs.Messages.ToArray().Should().Contain(
            message => message.Contains("disk full", StringComparison.Ordinal));
        failures.LastMeasurement!.Value.Should().Be(1);
        failures.LastMeasurement.Tags[HostTelemetry.ReasonTag]
            .Should().Be(TapePruneReason.PruneError);
        await worker.StopAsync(CancellationToken.None);
        worker.ExecuteTask.IsFaulted.Should().BeFalse();
    }

    private static async Task WaitUntilArmed(TapeRetentionWorker worker, int count)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (worker.DelaysArmed < count)
        {
            await Task.Delay(1, timeout.Token);
        }
    }

    private static TapeRetentionWorker Worker(
        ScriptedPass pass,
        McpTransport transport,
        bool record,
        FakeTimeProvider clock,
        bool leaseHeld,
        ListLogger? logs = null,
        HostTelemetry? telemetry = null) =>
        Worker(pass, transport, record, clock, new HoldingLease { Held = leaseHeld }, logs, telemetry);

    private static TapeRetentionWorker Worker(
        ScriptedPass pass,
        McpTransport transport,
        bool record,
        FakeTimeProvider clock,
        HoldingLease lease,
        ListLogger? logs = null,
        HostTelemetry? telemetry = null)
    {
        ServiceCollection services = new();
        services.AddScoped<ITapeRetentionPass>(_ => pass);
        ServiceProvider provider = services.BuildServiceProvider();

        return new TapeRetentionWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new MarketDataOptions { RecordTape = record }),
            Options.Create(new McpOptions { Transport = transport }),
            lease,
            clock,
            logs ?? new ListLogger(),
            telemetry ?? new HostTelemetry());
    }

    private sealed class HoldingLease : ITapeRetentionLease
    {
        public bool Held { get; set; }

        public bool HoldsLease => Held;
    }

    private sealed class ScriptedPass : ITapeRetentionPass
    {
        private readonly TaskCompletionSource _called =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls { get; private set; }

        public Exception? Error { get; init; }

        public Task Called => _called.Task;

        public Task<TapeRetentionRun> PruneAsync(Func<bool> holdsLease, CancellationToken cancellationToken)
        {
            Calls++;
            _called.TrySetResult();
            if (Error is not null)
            {
                throw Error;
            }

            return Task.FromResult(new TapeRetentionRun(0, 0, 0, !holdsLease(), DateTimeOffset.UnixEpoch));
        }
    }

    private sealed class ListLogger : ILogger<TapeRetentionWorker>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}
