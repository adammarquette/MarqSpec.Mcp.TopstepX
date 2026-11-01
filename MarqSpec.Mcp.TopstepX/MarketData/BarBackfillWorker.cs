using System.Diagnostics;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// Runs <see cref="BarBackfillDriver"/> once at startup when backfill is on (gh#899).
/// </summary>
/// <remarks>
/// The service is registered either way. Stdio returns without reading. HTTP is the same gate the tape
/// export uses for transport: the server and the recorder are both HTTP, and a second replica skips
/// when the advisory lock is held. A failure is logged with the exception and does not fault the host.
/// </remarks>
public sealed class BarBackfillWorker(
    IServiceScopeFactory scopes,
    IOptions<BarBackfillOptions> options,
    IOptions<McpOptions> mcp,
    StoreAvailabilityHolder store,
    IBarBackfillLock gate,
    BarBackfillStatus status,
    HostTelemetry telemetry,
    TimeProvider clock,
    ILogger<BarBackfillWorker> logger) : BackgroundService
{
    private readonly IServiceScopeFactory _scopes = scopes;
    private readonly BarBackfillOptions _options = options.Value;
    private readonly McpOptions _mcp = mcp.Value;
    private readonly StoreAvailabilityHolder _store = store;
    private readonly IBarBackfillLock _gate = gate;
    private readonly BarBackfillStatus _status = status;
    private readonly HostTelemetry _telemetry = telemetry;
    private readonly TimeProvider _clock = clock;
    private readonly ILogger<BarBackfillWorker> _logger = logger;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Bar backfill is off. The switch is false.");
            return;
        }

        if (_mcp.Transport != McpTransport.Http)
        {
            _logger.LogInformation(
                "Bar backfill skipped because the transport is {Transport}. Only HTTP runs it.",
                _mcp.Transport);
            Finish(BarBackfillStop.SkippedTransport, pages: 0, errors: 0, _clock.GetUtcNow());
            return;
        }

        DateTimeOffset started = _clock.GetUtcNow();
        using Activity? span = _telemetry.StartBarBackfill();
        try
        {
            if (!_store.Value.IsAvailable)
            {
                _logger.LogWarning(
                    "The backfill switch is on but the store is not reachable, so the pass did not run.");
                Finish(BarBackfillStop.StoreUnavailable, pages: 0, errors: 0, started, span);
                return;
            }

            IAsyncDisposable? hold = await _gate.TryAcquireAsync(stoppingToken).ConfigureAwait(false);
            if (hold is null)
            {
                _logger.LogInformation(
                    "Bar backfill skipped because another process holds the lock.");
                Finish(BarBackfillStop.LockHeld, pages: 0, errors: 0, started, span);
                return;
            }

            await using (hold)
            {
                using IServiceScope scope = _scopes.CreateScope();
                BarBackfillReport report = await scope.ServiceProvider
                    .GetRequiredService<IBarBackfillRunner>()
                    .RunAsync(stoppingToken)
                    .ConfigureAwait(false);
                Finish(report, started, span);
            }
        }
        catch (OperationCanceledException)
        {
            Finish(BarBackfillStop.Cancelled, pages: 0, errors: 0, started, span);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Bar backfill failed ({ExceptionType}). The host stays up.",
                exception.GetType().Name);
            _telemetry.BarBackfillError(exception.GetType().Name);
            Finish(BarBackfillStop.Failed, pages: 0, errors: 1, started, span);
        }
    }

    private void Finish(BarBackfillReport report, DateTimeOffset started, Activity? span)
    {
        _telemetry.BarBackfillPages(report.Pages);
        _telemetry.BarBackfillBars(report.BarsWritten);
        _telemetry.BarBackfillEmpty(report.EmptyPages);
        if (report.StopReason == BarBackfillStop.RateLimited)
        {
            _telemetry.BarBackfillRateLimited();
        }

        Finish(report.StopReason, report.Pages, errors: 0, started, span);
        _logger.LogInformation(
            "Bar backfill pass stopped: {StopReason}. Pages {Pages}, bars written {BarsWritten}, "
            + "history requests {VenueRequests}, empty pages {EmptyPages}.",
            report.StopReason,
            report.Pages,
            report.BarsWritten,
            report.VenueRequests,
            report.EmptyPages);
    }

    private void Finish(
        string reason,
        int pages,
        int errors,
        DateTimeOffset started,
        Activity? span = null)
    {
        DateTimeOffset ended = _clock.GetUtcNow();
        _status.Record(new BarBackfillStatus.Snapshot(started, ended, reason, pages, errors));
        _telemetry.BarBackfillStopped(reason);
        span?.SetTag(HostTelemetry.OutcomeTag, reason);
    }
}
