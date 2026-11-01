using MarqSpec.Mcp.TopstepX.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// Runs <see cref="BarBackfillDriver"/> once at startup when <c>Backfill__Enabled</c> is true (gh#899).
/// </summary>
/// <remarks>
/// The service is registered either way. While the switch is off, <see cref="ExecuteAsync"/> returns
/// without opening a scope. A failure is logged and does not fault the host.
/// </remarks>
public sealed class BarBackfillWorker(
    IServiceScopeFactory scopes,
    IOptions<BarBackfillOptions> options,
    StoreAvailabilityHolder store,
    ILogger<BarBackfillWorker> logger) : BackgroundService
{
    private readonly IServiceScopeFactory _scopes = scopes;
    private readonly BarBackfillOptions _options = options.Value;
    private readonly StoreAvailabilityHolder _store = store;
    private readonly ILogger<BarBackfillWorker> _logger = logger;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (!_options.Enabled)
            {
                _logger.LogInformation("Bar backfill is off. Backfill__Enabled is false.");
                return;
            }

            if (!_store.Value.IsAvailable)
            {
                _logger.LogWarning(
                    "Backfill__Enabled is on but the store is not reachable, so the pass did not run.");
                return;
            }

            using IServiceScope scope = _scopes.CreateScope();
            BarBackfillReport report = await scope.ServiceProvider
                .GetRequiredService<BarBackfillDriver>()
                .RunAsync(stoppingToken)
                .ConfigureAwait(false);
            _logger.LogInformation(
                "Bar backfill pass stopped: {StopReason}. Pages {Pages}, "
                + "history requests {VenueRequests}, empty pages {EmptyPages}.",
                report.StopReason,
                report.Pages,
                report.VenueRequests,
                report.EmptyPages);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
        catch (Exception exception)
        {
            _logger.LogError(
                "Bar backfill failed ({ExceptionType}). The host stays up.",
                exception.GetType().FullName);
        }
    }
}
