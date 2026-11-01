using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>Whether this process currently holds a recorder lease.</summary>
public interface ITapeRetentionLease
{
    /// <summary>True while <see cref="TradeTapeRecorder"/> holds at least one instrument claim.</summary>
    bool HoldsLease { get; }
}

/// <summary>Reads <see cref="TradeTapeRecorder.HoldsRecorderLease"/>.</summary>
public sealed class RecorderTapeRetentionLease(TradeTapeRecorder recorder) : ITapeRetentionLease
{
    private readonly TradeTapeRecorder _recorder = recorder;

    /// <inheritdoc />
    public bool HoldsLease => _recorder.HoldsRecorderLease;
}

/// <summary>
/// Runs <see cref="ITapeRetentionPass"/> on the process that writes the tape (gh#763, ADR-0027).
/// </summary>
/// <remarks>
/// <para>
/// HTTP and <c>MarketData__RecordTape</c> are both required. The server process leaves recording
/// off, so this service returns without deleting. The first pass waits <see cref="StartupDelay"/>
/// so a crash-looping deploy does not prune before anyone notices. Later passes wait
/// <see cref="Interval"/>. The lease is read again inside the pass, before every batch.
/// </para>
/// <para>
/// A failure does not fault the host. The log carries the exception message. Rows a failed pass
/// already committed stay deleted.
/// </para>
/// </remarks>
public sealed class TapeRetentionWorker(
    IServiceScopeFactory scopes,
    IOptions<MarketDataOptions> marketData,
    IOptions<McpOptions> mcp,
    ITapeRetentionLease lease,
    TimeProvider clock,
    ILogger<TapeRetentionWorker> logger,
    HostTelemetry telemetry) : BackgroundService
{
    /// <summary>How long after a pass the next one waits.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    /// <summary>How long after process start the first pass waits.</summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(15);

    /// <summary>How often to look again while this process does not hold a recorder lease.</summary>
    public static readonly TimeSpan LeasePoll = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopes = scopes;
    private readonly MarketDataOptions _marketData = marketData.Value;
    private readonly McpOptions _mcp = mcp.Value;
    private readonly ITapeRetentionLease _lease = lease;
    private readonly TimeProvider _clock = clock;
    private readonly ILogger<TapeRetentionWorker> _logger = logger;
    private readonly HostTelemetry _telemetry = telemetry;

    private int _delaysArmed;

    /// <summary>How many delays this process has armed. Tests wait on this.</summary>
    internal int DelaysArmed => Volatile.Read(ref _delaysArmed);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_mcp.Transport != McpTransport.Http || !_marketData.RecordTape)
        {
            _logger.LogInformation(
                "Tape retention is off in this process (transport {Transport}, record {RecordTape}).",
                _mcp.Transport,
                _marketData.RecordTape);
            return;
        }

        if (!await DelayAsync(StartupDelay, stoppingToken).ConfigureAwait(false))
        {
            return;
        }

        bool announcedWait = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!_lease.HoldsLease)
            {
                if (!announcedWait)
                {
                    _logger.LogInformation(
                        "Tape retention is waiting until this process holds a recorder lease.");
                    announcedWait = true;
                }

                if (!await DelayAsync(LeasePoll, stoppingToken).ConfigureAwait(false))
                {
                    return;
                }

                continue;
            }

            announcedWait = false;
            try
            {
                using IServiceScope scope = _scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ITapeRetentionPass>()
                    .PruneAsync(() => _lease.HoldsLease, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _telemetry.TapePruneFailed(TapePruneReason.PruneError);
                _logger.LogError(
                    exception,
                    "Tape retention failed: {Error}",
                    exception.Message);
            }

            if (!await DelayAsync(Interval, stoppingToken).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    private async Task<bool> DelayAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        Interlocked.Increment(ref _delaysArmed);
        try
        {
            await Task.Delay(delay, _clock, stoppingToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
