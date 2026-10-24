using MarqSpec.Mcp.TopstepX.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// Runs <see cref="ITapeExportPass"/> once at start and then daily at
/// <see cref="TapeExportOptions.Schedule"/> (gh#755).
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
    TimeProvider clock,
    ILogger<TapeExportWorker> logger) : BackgroundService
{
    private readonly IServiceScopeFactory _scopes = scopes;
    private readonly TapeExportOptions _options = options.Value;
    private readonly McpOptions _mcp = mcp.Value;
    private readonly TimeProvider _clock = clock;
    private readonly ILogger<TapeExportWorker> _logger = logger;

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
                    exception,
                    "Scheduled tape export failed. Local tape was not deleted by this pass.");
            }

            DateTimeOffset next = TapeExportPolicy.NextRun(_options.ParsedSchedule(), _clock.GetUtcNow());
            TimeSpan delay = next - _clock.GetUtcNow();
            if (delay < TimeSpan.Zero)
            {
                delay = TimeSpan.Zero;
            }

            try
            {
                await Task.Delay(delay, _clock, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
