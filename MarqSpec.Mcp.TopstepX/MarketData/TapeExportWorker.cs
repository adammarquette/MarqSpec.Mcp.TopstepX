using MarqSpec.Mcp.TopstepX.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// Runs <see cref="ITapeExportPass"/> at <see cref="TapeExportOptions.Schedule"/> when this process
/// holds a recorder lease (gh#755). A restart does not export before the next schedule.
/// </summary>
/// <remarks>
/// <para>
/// <b>HTTP and an explicit switch, both.</b> Choosing the HTTP transport is not consent to export
/// or to delete. A stdio child returns immediately. The pass is scoped; this service is a singleton,
/// so each run opens a scope.
/// </para>
/// <para>
/// <b>A failure does not fault the host.</b> A faulted <see cref="BackgroundService.ExecuteTask"/>
/// is what <c>Program.AnyFaulted</c> reads. The next occurrence tries again. A failed pass writes
/// nothing and deletes nothing: the export is a copy.
/// </para>
/// </remarks>
public sealed class TapeExportWorker(
    IServiceScopeFactory scopes,
    IOptions<TapeExportOptions> options,
    IOptions<McpOptions> mcp,
    ITapeExportSchedulerGate gate,
    TimeProvider clock,
    ILogger<TapeExportWorker> logger) : BackgroundService
{
    private readonly IServiceScopeFactory _scopes = scopes;
    private readonly TapeExportOptions _options = options.Value;
    private readonly McpOptions _mcp = mcp.Value;
    private readonly ITapeExportSchedulerGate _gate = gate;
    private readonly TimeProvider _clock = clock;
    private readonly ILogger<TapeExportWorker> _logger = logger;

    private int _delaysArmed;

    /// <summary>How many schedule delays this process has armed. Tests wait on this instead of yielding.</summary>
    internal int DelaysArmed => Volatile.Read(ref _delaysArmed);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_mcp.Transport != McpTransport.Http || !_options.Enabled)
        {
            _logger.LogInformation(
                "Tape export is off (transport {Transport}, daily copy {DailyCopy}).",
                _mcp.Transport,
                _options.Enabled);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            DateTimeOffset next = TapeExportPolicy.NextRun(_options.ParsedSchedule(), _clock.GetUtcNow());
            TimeSpan delay = next - _clock.GetUtcNow();
            if (delay < TimeSpan.Zero)
            {
                delay = TimeSpan.Zero;
            }

            try
            {
                Task waiting = Task.Delay(delay, _clock, stoppingToken);
                Interlocked.Increment(ref _delaysArmed);
                await waiting.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            if (!_gate.RecorderHoldsAClaim)
            {
                _logger.LogInformation(
                    "Tape export waited for the schedule and skipped the pass because this process holds no recorder lease.");
                continue;
            }

            try
            {
                using IServiceScope scope = _scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ITapeExportPass>()
                    .ExportAsync(onlyDay: null, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    "Scheduled tape export failed ({ExceptionType}). Local tape was not deleted by this pass.",
                    exception.GetType().Name);
            }
        }
    }
}

/// <summary>Whether this process may run the scheduled export. The recorder's lease is the claim.</summary>
public interface ITapeExportSchedulerGate
{
    /// <summary>True when <see cref="TradeTapeRecorder"/> holds at least one instrument claim.</summary>
    bool RecorderHoldsAClaim { get; }
}

/// <summary>Reads <see cref="TradeTapeRecorder.HoldsRecorderLease"/> on the hosted recorder instance.</summary>
public sealed class RecorderLeaseGate(TradeTapeRecorder recorder) : ITapeExportSchedulerGate
{
    private readonly TradeTapeRecorder _recorder = recorder;

    /// <inheritdoc />
    public bool RecorderHoldsAClaim => _recorder.HoldsRecorderLease;
}
