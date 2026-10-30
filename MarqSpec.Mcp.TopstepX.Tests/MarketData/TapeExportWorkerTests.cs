using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// The worker waits for the schedule, and it exports only while this process holds the recorder lease.
/// </summary>
public sealed class TapeExportWorkerTests
{
    private static readonly DateTimeOffset _before = new(2026, 10, 17, 5, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(McpTransport.Stdio, true)]
    [InlineData(McpTransport.Http, false)]
    [InlineData(McpTransport.Stdio, false)]
    public async Task ExecuteAsync_WhenNotHttpOrDisabled_DoesNotExport(McpTransport transport, bool enabled)
    {
        ScriptedPass pass = new();
        FakeTimeProvider clock = new(_before);
        TapeExportWorker worker = Worker(pass, transport, enabled, clock, leaseHeld: true);

        await worker.StartAsync(CancellationToken.None);
        await BackgroundServiceTestSupport.AwaitCompletionAsync(worker.ExecuteTask!);

        pass.Exports.Should().Be(0);
        worker.ExecuteTask!.IsFaulted.Should().BeFalse();
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_WhenTheScheduleHasNotArrived_DoesNotExport()
    {
        ScriptedPass pass = new();
        FakeTimeProvider clock = new(_before);
        TapeExportWorker worker = Worker(pass, McpTransport.Http, enabled: true, clock, leaseHeld: true);

        await worker.StartAsync(CancellationToken.None);
        await WaitUntilArmed(worker, 1);
        clock.Advance(TimeSpan.FromMinutes(59));

        pass.Exports.Should().Be(0);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_WhenTheScheduleArrivesAndTheLeaseIsHeld_ExportsOnce()
    {
        ScriptedPass pass = new();
        FakeTimeProvider clock = new(_before);
        TapeExportWorker worker = Worker(pass, McpTransport.Http, enabled: true, clock, leaseHeld: true);

        await worker.StartAsync(CancellationToken.None);
        await WaitUntilArmed(worker, 1);
        clock.Advance(TimeSpan.FromHours(1));
        await pass.Called.WaitAsync(TimeSpan.FromSeconds(5));

        pass.Exports.Should().Be(1);
        worker.ExecuteTask!.IsFaulted.Should().BeFalse();
        worker.ExecuteTask.IsCompleted.Should().BeFalse();
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_WhenStartedAfterTheSchedule_WaitsUntilTheNextDay()
    {
        ScriptedPass pass = new();
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 17, 7, 0, 0, TimeSpan.Zero));
        TapeExportWorker worker = Worker(pass, McpTransport.Http, enabled: true, clock, leaseHeld: true);

        await worker.StartAsync(CancellationToken.None);
        await WaitUntilArmed(worker, 1);
        clock.Advance(TimeSpan.FromHours(22));
        pass.Exports.Should().Be(0);

        clock.Advance(TimeSpan.FromHours(1));
        await pass.Called.WaitAsync(TimeSpan.FromSeconds(5));

        pass.Exports.Should().Be(1);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_WhenTheLeaseIsNotHeld_SkipsThePassUntilALaterSchedule()
    {
        ScriptedPass pass = new();
        FakeTimeProvider clock = new(_before);
        HoldingGate gate = new() { Held = false };
        TapeExportWorker worker = Worker(pass, McpTransport.Http, enabled: true, clock, gate);

        await worker.StartAsync(CancellationToken.None);
        await WaitUntilArmed(worker, 1);
        clock.Advance(TimeSpan.FromHours(1));
        await WaitUntilArmed(worker, 2);
        pass.Exports.Should().Be(0);

        gate.Held = true;
        clock.Advance(TimeSpan.FromDays(1));
        await pass.Called.WaitAsync(TimeSpan.FromSeconds(5));

        pass.Exports.Should().Be(1);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_WhenThePassThrows_StaysUnfaulted()
    {
        ScriptedPass pass = new() { Error = new InvalidOperationException("disk full") };
        FakeTimeProvider clock = new(_before);
        TapeExportWorker worker = Worker(pass, McpTransport.Http, enabled: true, clock, leaseHeld: true);

        await worker.StartAsync(CancellationToken.None);
        await WaitUntilArmed(worker, 1);
        clock.Advance(TimeSpan.FromHours(1));
        await pass.Called.WaitAsync(TimeSpan.FromSeconds(5));

        pass.Exports.Should().Be(1);
        worker.ExecuteTask!.IsFaulted.Should().BeFalse();
        await worker.StopAsync(CancellationToken.None);
        worker.ExecuteTask.IsFaulted.Should().BeFalse();
    }

    private static async Task WaitUntilArmed(TapeExportWorker worker, int count)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (worker.DelaysArmed < count)
        {
            await Task.Delay(1, timeout.Token);
        }
    }

    private static TapeExportWorker Worker(
        ScriptedPass pass,
        McpTransport transport,
        bool enabled,
        FakeTimeProvider clock,
        bool leaseHeld) =>
        Worker(pass, transport, enabled, clock, new HoldingGate { Held = leaseHeld });

    private static TapeExportWorker Worker(
        ScriptedPass pass,
        McpTransport transport,
        bool enabled,
        FakeTimeProvider clock,
        HoldingGate gate)
    {
        ServiceCollection services = new();
        services.AddScoped<ITapeExportPass>(_ => pass);
        ServiceProvider provider = services.BuildServiceProvider();

        return new TapeExportWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new TapeExportOptions { Enabled = enabled }),
            Options.Create(new McpOptions { Transport = transport }),
            gate,
            clock,
            NullLogger<TapeExportWorker>.Instance);
    }

    private sealed class HoldingGate : ITapeExportSchedulerGate
    {
        public bool Held { get; set; }

        public bool RecorderHoldsAClaim => Held;
    }

    private sealed class ScriptedPass : ITapeExportPass
    {
        private readonly TaskCompletionSource _called = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Exports { get; private set; }

        public Task Called => _called.Task;

        public Exception? Error { get; init; }

        public Task<TapeExportReport> ExportAsync(DateOnly? onlyDay, CancellationToken cancellationToken)
        {
            Exports++;
            _called.TrySetResult();
            if (Error is not null)
            {
                return Task.FromException<TapeExportReport>(Error);
            }

            return Task.FromResult(new TapeExportReport(0, 0, 0, 0));
        }

        public Task<TapeExportReport> ImportAsync(DateOnly? onlyDay, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
