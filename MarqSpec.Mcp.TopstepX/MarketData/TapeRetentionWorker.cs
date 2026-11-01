using MarqSpec.Mcp.TopstepX.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// Runs <see cref="TapeRetentionPass"/> on the process that writes the tape (gh#763).
/// </summary>
/// <remarks>
/// <para>
/// <b>HTTP and <c>MarketData__RecordTape</c>, both.</b> The server process leaves recording off, so
/// this service returns without deleting. A stdio child returns the same way. While the recorder
/// does not yet hold a lease, the pass waits. Two recorders briefly overlap on a redeploy; the one
/// that holds the lease is the one that prunes.
/// </para>
/// <para>
/// The first pass runs once the lease is held. The next runs <see cref="Interval"/> later. A failure
/// does not fault the host: a faulted <see cref="BackgroundService.ExecuteTask"/> is what
/// <c>Program.AnyFaulted</c> reads. Rows a failed pass already committed stay deleted, and the next
/// interval tries again.
/// </para>
/// </remarks>
public sealed class TapeRetentionWorker(
    IServiceScopeFactory scopes,
    IOptions<MarketDataOptions> marketData,
    IOptions<McpOptions> mcp,
    TradeTapeRecorder recorder,
    TimeProvider clock,
    ILogger<TapeRetentionWorker> logger) : BackgroundService
{
    /// <summary>How long after a pass the next one waits.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    /// <summary>How often to look again while this process does not hold a recorder lease.</summary>
    public static readonly TimeSpan LeasePoll = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopes = scopes;
    private readonly MarketDataOptions _marketData = marketData.Value;
    private readonly McpOptions _mcp = mcp.Value;
    private readonly TradeTapeRecorder _recorder = recorder;
    private readonly TimeProvider _clock = clock;
    private readonly ILogger<TapeRetentionWorker> _logger = logger;

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

        bool announcedWait = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!_recorder.HoldsRecorderLease)
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
                await scope.ServiceProvider.GetRequiredService<TapeRetentionPass>()
                    .PruneAsync(stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    "Tape retention failed ({ExceptionType}). "
                    + "Rows already removed in this pass stay removed.",
                    exception.GetType().Name);
            }

            if (!await DelayAsync(Interval, stoppingToken).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    private async Task<bool> DelayAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
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
