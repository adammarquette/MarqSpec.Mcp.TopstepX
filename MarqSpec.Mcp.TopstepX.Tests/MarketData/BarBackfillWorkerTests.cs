using System.Diagnostics;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// The hosted pass runs on HTTP only, skips when the lock is held, and does not fault the host (gh#899).
/// </summary>
[Collection(HostTelemetryCollection.Name)]
public sealed class BarBackfillWorkerTests
{
    private static readonly DateTimeOffset _now = new(2026, 8, 18, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Stdio_DoesNotRun_EvenWhenTheSwitchIsOn()
    {
        CapturingDriver driver = new();
        BarBackfillStatus status = new();
        BarBackfillWorker worker = Worker(driver, McpTransport.Stdio, enabled: true, status, new OpenLock());

        await worker.StartAsync(CancellationToken.None);
        await BackgroundServiceTestSupport.AwaitCompletionAsync(worker.ExecuteTask!);

        driver.Calls.Should().Be(0);
        worker.ExecuteTask!.IsFaulted.Should().BeFalse();
        status.Last!.StopReason.Should().Be(BarBackfillStop.SkippedTransport);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Http_RunsOnce()
    {
        CapturingDriver driver = new();
        BarBackfillStatus status = new();
        BarBackfillWorker worker = Worker(driver, McpTransport.Http, enabled: true, status, new OpenLock());

        await worker.StartAsync(CancellationToken.None);
        await BackgroundServiceTestSupport.AwaitCompletionAsync(worker.ExecuteTask!);

        driver.Calls.Should().Be(1);
        status.Last!.StopReason.Should().Be(BarBackfillStop.Completed);
        status.Last.Pages.Should().Be(2);
        status.Last.Errors.Should().Be(0);
        worker.ExecuteTask!.IsFaulted.Should().BeFalse();
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AHeldLock_SkipsWithLockHeld()
    {
        CapturingDriver driver = new();
        BarBackfillStatus status = new();
        BarBackfillWorker worker = Worker(driver, McpTransport.Http, enabled: true, status, new HeldLock());

        await worker.StartAsync(CancellationToken.None);
        await BackgroundServiceTestSupport.AwaitCompletionAsync(worker.ExecuteTask!);

        driver.Calls.Should().Be(0);
        status.Last!.StopReason.Should().Be(BarBackfillStop.LockHeld);
        worker.ExecuteTask!.IsFaulted.Should().BeFalse();
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ANon429Failure_IsLoggedAndDoesNotFaultTheHost()
    {
        InvalidOperationException failure = new("store write failed");
        CapturingDriver driver = new() { Failure = failure, FinishedPages = 2 };
        ListLogger logger = new();
        BarBackfillStatus status = new();
        BarBackfillWorker worker = Worker(
            driver, McpTransport.Http, enabled: true, status, new OpenLock(), logger);

        await worker.StartAsync(CancellationToken.None);
        await BackgroundServiceTestSupport.AwaitCompletionAsync(worker.ExecuteTask!);

        worker.ExecuteTask!.IsFaulted.Should().BeFalse();
        status.Last!.StopReason.Should().Be(BarBackfillStop.Failed);
        status.Last.Errors.Should().Be(1);
        status.Last.Pages.Should().Be(2);
        logger.Entries.Should().ContainSingle(entry => entry.Exception == failure);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Cancellation_EndsThePassWithoutFaultingTheHost()
    {
        CapturingDriver driver = new() { Cancel = true };
        BarBackfillStatus status = new();
        BarBackfillWorker worker = Worker(driver, McpTransport.Http, enabled: true, status, new OpenLock());

        await worker.StartAsync(CancellationToken.None);
        await BackgroundServiceTestSupport.AwaitCompletionAsync(worker.ExecuteTask!);

        worker.ExecuteTask!.IsFaulted.Should().BeFalse();
        status.Last!.StopReason.Should().Be(BarBackfillStop.Cancelled);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task EmptyAndRateLimitedPasses_RecordThoseCounters()
    {
        using HostTelemetry telemetry = new();
        using MetricCollector<long> empty = new(
            telemetry, HostTelemetry.Name, HostTelemetry.BarBackfillEmptyInstrument);
        using MetricCollector<long> limited = new(
            telemetry, HostTelemetry.Name, HostTelemetry.BarBackfillRateLimitedInstrument);
        using MetricCollector<long> stops = new(
            telemetry, HostTelemetry.Name, HostTelemetry.BarBackfillStopsInstrument);
        CapturingDriver driver = new()
        {
            Report = new BarBackfillReport(true, BarBackfillStop.RateLimited, 3, 3, 2, 0),
        };
        BarBackfillWorker worker = Worker(
            driver,
            McpTransport.Http,
            enabled: true,
            new BarBackfillStatus(),
            new OpenLock(),
            telemetry: telemetry);

        await worker.StartAsync(CancellationToken.None);
        await BackgroundServiceTestSupport.AwaitCompletionAsync(worker.ExecuteTask!);

        empty.GetMeasurementSnapshot().Should().ContainSingle().Which.Value.Should().Be(2);
        limited.GetMeasurementSnapshot().Should().ContainSingle().Which.Value.Should().Be(1);
        stops.GetMeasurementSnapshot().Should().Contain(measurement =>
            measurement.Tags[HostTelemetry.ReasonTag] as string == BarBackfillStop.RateLimited);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AFailure_RecordsTheExceptionTypeAndThePagesAlreadyFinished()
    {
        using HostTelemetry telemetry = new();
        using MetricCollector<long> errors = new(
            telemetry, HostTelemetry.Name, HostTelemetry.BarBackfillErrorsInstrument);
        using MetricCollector<long> pages = new(
            telemetry, HostTelemetry.Name, HostTelemetry.BarBackfillPagesInstrument);
        InvalidOperationException failure = new("store write failed");
        CapturingDriver driver = new() { Failure = failure, FinishedPages = 2 };
        BarBackfillStatus status = new();
        BarBackfillWorker worker = Worker(
            driver, McpTransport.Http, enabled: true, status, new OpenLock(), telemetry: telemetry);

        await worker.StartAsync(CancellationToken.None);
        await BackgroundServiceTestSupport.AwaitCompletionAsync(worker.ExecuteTask!);

        status.Last!.Pages.Should().Be(2);
        errors.GetMeasurementSnapshot().Should().ContainSingle()
            .Which.Tags[HostTelemetry.ExceptionTag].Should().Be(failure.GetType().Name);
        pages.GetMeasurementSnapshot().Should().ContainSingle().Which.Value.Should().Be(2);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task APass_RecordsTheSpanAndTheCounters()
    {
        using HostTelemetry telemetry = new();
        using MetricCollector<long> pages = new(
            telemetry, HostTelemetry.Name, HostTelemetry.BarBackfillPagesInstrument);
        using MetricCollector<long> bars = new(
            telemetry, HostTelemetry.Name, HostTelemetry.BarBackfillBarsInstrument);
        using MetricCollector<long> stops = new(
            telemetry, HostTelemetry.Name, HostTelemetry.BarBackfillStopsInstrument);
        CapturingDriver driver = new();
        BarBackfillWorker worker = Worker(
            driver,
            McpTransport.Http,
            enabled: true,
            new BarBackfillStatus(),
            new OpenLock(),
            telemetry: telemetry);
        List<Activity> spans = [];
        using ActivityListener listener = new()
        {
            ShouldListenTo = source => ReferenceEquals(source, telemetry.Activities),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Add,
        };
        ActivitySource.AddActivityListener(listener);

        await worker.StartAsync(CancellationToken.None);
        await BackgroundServiceTestSupport.AwaitCompletionAsync(worker.ExecuteTask!);

        spans.Should().ContainSingle(span => span.OperationName == "bars.backfill")
            .Which.GetTagItem(HostTelemetry.OutcomeTag).Should().Be(BarBackfillStop.Completed);
        pages.GetMeasurementSnapshot().Should().ContainSingle().Which.Value.Should().Be(2);
        bars.GetMeasurementSnapshot().Should().ContainSingle().Which.Value.Should().Be(4);
        stops.GetMeasurementSnapshot().Should().ContainSingle()
            .Which.Tags[HostTelemetry.ReasonTag].Should().Be(BarBackfillStop.Completed);
        await worker.StopAsync(CancellationToken.None);
    }

    private static BarBackfillWorker Worker(
        CapturingDriver driver,
        McpTransport transport,
        bool enabled,
        BarBackfillStatus status,
        IBarBackfillLock gate,
        ListLogger? logger = null,
        HostTelemetry? telemetry = null)
    {
        ServiceCollection services = new();
        services.AddScoped<IBarBackfillRunner>(_ => driver);
        ServiceProvider provider = services.BuildServiceProvider();
        return new BarBackfillWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new BarBackfillOptions { Enabled = enabled, ResolutionsMinutes = "1" }),
            Options.Create(new McpOptions { Transport = transport }),
            new StoreAvailabilityHolder(),
            gate,
            status,
            telemetry ?? new HostTelemetry(),
            new FakeTimeProvider(_now),
            logger ?? new ListLogger());
    }

    private sealed class CapturingDriver : IBarBackfillRunner
    {
        public int Calls { get; private set; }

        public Exception? Failure { get; init; }

        public bool Cancel { get; init; }

        public int FinishedPages { get; init; }

        public BarBackfillReport Report { get; init; } =
            new(true, BarBackfillStop.Completed, 2, 1, 0, 4);

        public BarBackfillProgress Progress { get; private set; }

        public void Watch(IBarBackfillLockProbe? probe)
        {
        }

        public Task<BarBackfillReport> RunAsync(CancellationToken cancellationToken)
        {
            Calls++;
            Progress = new BarBackfillProgress(FinishedPages, 0, 0);
            if (Cancel)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (Failure is not null)
            {
                throw Failure;
            }

            return Task.FromResult(Report);
        }
    }

    private sealed class OpenLock : IBarBackfillLock
    {
        public Task<IBarBackfillLockHold?> TryAcquireAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IBarBackfillLockHold?>(new Hold());

        private sealed class Hold : IBarBackfillLockHold
        {
            public Task<bool> StillHeldAsync(CancellationToken cancellationToken) =>
                Task.FromResult(true);

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class HeldLock : IBarBackfillLock
    {
        public Task<IBarBackfillLockHold?> TryAcquireAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IBarBackfillLockHold?>(null);
    }

    private sealed class ListLogger : ILogger<BarBackfillWorker>
    {
        public List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, exception));
    }
}
