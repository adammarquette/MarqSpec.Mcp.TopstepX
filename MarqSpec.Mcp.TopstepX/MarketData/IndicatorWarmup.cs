using MarqSpec.Mcp.TopstepX.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// Replays stored indicator series at process start so the first tool read is a probe, not an 8.3 s
/// projection (ADR-0014, gh#350).
/// </summary>
/// <remarks>
/// <para>
/// <b>HTTP and an explicit switch, both.</b> Choosing the HTTP transport is not consent to warm.
/// A Cowork stdio child against a large store would stall the handshake. The loop returns
/// immediately unless the transport is HTTP and <see cref="MarketDataOptions.WarmIndicators"/> is
/// on — the same shape as <see cref="TradeTapeRecorder"/>.
/// </para>
/// <para>
/// <b>No captive dependency.</b> <see cref="IndicatorRebuilder"/> is scoped; this service is a
/// singleton. The replay opens a scope through <see cref="IServiceScopeFactory"/>.
/// </para>
/// <para>
/// <b><see cref="ExecuteAsync"/> catches rather than faulting the host.</b> A faulted
/// <see cref="BackgroundService.ExecuteTask"/> is what <c>Program.AnyFaulted</c> reads, and would
/// turn an ordinary stdio EOF into a crash (gh#76). A warmup failure is logged; the first cold
/// read then pays the projection.
/// </para>
/// </remarks>
public sealed class IndicatorWarmup(
    IServiceScopeFactory scopes,
    IOptions<MarketDataOptions> market,
    IOptions<McpOptions> mcp,
    StoreAvailabilityHolder store,
    ILogger<IndicatorWarmup> logger) : BackgroundService
{
    private readonly IServiceScopeFactory _scopes = scopes;
    private readonly MarketDataOptions _market = market.Value;
    private readonly McpOptions _mcp = mcp.Value;
    private readonly StoreAvailabilityHolder _store = store;
    private readonly ILogger<IndicatorWarmup> _logger = logger;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (_mcp.Transport != McpTransport.Http || !_market.WarmIndicators)
            {
                _logger.LogInformation(
                    "Indicator warmup is off (transport {Transport}, WarmIndicators {WarmIndicators}).",
                    _mcp.Transport,
                    _market.WarmIndicators);
                return;
            }

            if (!_store.Value.IsAvailable)
            {
                _logger.LogWarning(
                    "WarmIndicators is on but the store is not reachable, so warmup did not run. "
                    + "A cold read will pay the projection. {Explanation}",
                    _store.Value.Explanation);
                return;
            }

            using IServiceScope scope = _scopes.CreateScope();
            IndicatorRebuildResult result = await scope.ServiceProvider
                .GetRequiredService<IndicatorRebuilder>()
                .RebuildAsync(onlyInstrument: null, stoppingToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Indicator warmup complete: {Total} values changed; {Rewritten} series rewritten.",
                result.ValuesChanged,
                result.SeriesRewritten);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown. Not a fault.
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Indicator warmup failed. The host will keep serving; a cold read will pay the projection.");
        }
    }
}
