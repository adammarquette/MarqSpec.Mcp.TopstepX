using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// The scheduled worker runs only for an HTTP host that asked for it, and a thrown pass does not
/// fault the host.
/// </summary>
public sealed class TapeExportWorkerTests
{
    [Theory]
    [InlineData(McpTransport.Stdio, true)]
    [InlineData(McpTransport.Http, false)]
    [InlineData(McpTransport.Stdio, false)]
    public async Task ExecuteAsync_WhenNotHttpOrDisabled_DoesNotExport(McpTransport transport, bool enabled)
    {
        ScriptedPass pass = new();
        TapeExportWorker worker = Worker(pass, transport, enabled);

        await worker.StartAsync(CancellationToken.None);
        await BackgroundServiceTestSupport.AwaitCompletionAsync(worker.ExecuteTask!);

        pass.Exports.Should().Be(0);
        worker.ExecuteTask!.IsFaulted.Should().BeFalse();
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_WhenHttpAndEnabled_ExportsBeforeTheNextSchedule()
    {
        ScriptedPass pass = new();
        TapeExportWorker worker = Worker(pass, McpTransport.Http, enabled: true);

        await worker.StartAsync(CancellationToken.None);
        await pass.Called.WaitAsync(TimeSpan.FromSeconds(5));

        pass.Exports.Should().Be(1);
        worker.ExecuteTask!.IsFaulted.Should().BeFalse();
        worker.ExecuteTask.IsCompleted.Should().BeFalse("the worker waits for the next UTC schedule");
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_WhenThePassThrows_StaysUnfaulted()
    {
        ScriptedPass pass = new() { Error = new InvalidOperationException("disk full") };
        TapeExportWorker worker = Worker(pass, McpTransport.Http, enabled: true);

        await worker.StartAsync(CancellationToken.None);
        await pass.Called.WaitAsync(TimeSpan.FromSeconds(5));

        pass.Exports.Should().Be(1);
        worker.ExecuteTask!.IsFaulted.Should().BeFalse();
        await worker.StopAsync(CancellationToken.None);
        worker.ExecuteTask.IsFaulted.Should().BeFalse();
    }

    private static TapeExportWorker Worker(ScriptedPass pass, McpTransport transport, bool enabled)
    {
        ServiceCollection services = new();
        services.AddScoped<ITapeExportPass>(_ => pass);
        ServiceProvider provider = services.BuildServiceProvider();

        return new TapeExportWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new TapeExportOptions { Enabled = enabled }),
            Options.Create(new McpOptions { Transport = transport }),
            TimeProvider.System,
            NullLogger<TapeExportWorker>.Instance);
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
