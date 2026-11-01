using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Telemetry;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>What one backfill run did.</summary>
/// <param name="Ran">False when the switch was off.</param>
/// <param name="StopReason">One of <see cref="BarBackfillStop"/>.</param>
/// <param name="Pages">Pages the cache was asked for.</param>
/// <param name="VenueRequests">History requests those pages issued.</param>
/// <param name="EmptyPages">Pages the venue answered empty. Recorded as coverage, not as a failure.</param>
/// <param name="BarsWritten">Buckets the store wrote from the venue's answer.</param>
public sealed record BarBackfillReport(
    bool Ran,
    string StopReason,
    int Pages,
    int VenueRequests,
    int EmptyPages,
    int BarsWritten);

/// <summary>Pages, bars and empty answers completed so far. A thrown pass still has these.</summary>
/// <param name="Pages">Pages the cache finished.</param>
/// <param name="BarsWritten">Buckets written from those pages.</param>
/// <param name="EmptyPages">Empty venue pages among them.</param>
public readonly record struct BarBackfillProgress(int Pages, int BarsWritten, int EmptyPages);

/// <summary>One backfill pass. The worker calls this so a test can substitute the pass.</summary>
public interface IBarBackfillRunner
{
    /// <summary>What the pass has finished. Updated as pages complete.</summary>
    BarBackfillProgress Progress { get; }

    /// <summary>Asks the lock between windows. Null skips the check.</summary>
    /// <param name="probe">The held lock, or null.</param>
    void Watch(IBarBackfillLockProbe? probe);

    /// <summary>Runs one pass.</summary>
    /// <param name="cancellationToken">The host cancellation token.</param>
    /// <returns>The run report.</returns>
    Task<BarBackfillReport> RunAsync(CancellationToken cancellationToken);
}

/// <summary>Whether the backfill lock is still held.</summary>
public interface IBarBackfillLockProbe
{
    /// <summary>True when this session still holds the lock.</summary>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>False when the lock is gone.</returns>
    Task<bool> StillHeldAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Fills uncovered bar ranges through <see cref="IBarBackfillCache"/> (gh#899). One pass, then it returns.
/// </summary>
public sealed class BarBackfillDriver(
    IBarBackfillCache cache,
    IOptions<BarBackfillOptions> options,
    InstrumentRegistry registry,
    TimeProvider clock,
    ILogger<BarBackfillDriver> logger) : IBarBackfillRunner
{
    private readonly IBarBackfillCache _cache = cache;
    private readonly BarBackfillOptions _options = options.Value;
    private readonly InstrumentRegistry _registry = registry;
    private readonly TimeProvider _clock = clock;
    private readonly ILogger<BarBackfillDriver> _logger = logger;
    private IBarBackfillLockProbe? _probe;
    private int _pages;
    private int _venueRequests;
    private int _emptyPages;
    private int _barsWritten;

    /// <inheritdoc />
    public BarBackfillProgress Progress => new(_pages, _barsWritten, _emptyPages);

    /// <inheritdoc />
    public void Watch(IBarBackfillLockProbe? probe) => _probe = probe;

    /// <summary>Runs one pass, or returns immediately when backfill is off.</summary>
    /// <param name="cancellationToken">The host cancellation token.</param>
    /// <returns>The run report.</returns>
    public async Task<BarBackfillReport> RunAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Bar backfill is off. Backfill__Enabled is false.");
            return new BarBackfillReport(false, BarBackfillStop.Disabled, 0, 0, 0, 0);
        }

        IReadOnlyList<InstrumentId> instruments = Instruments();
        _pages = 0;
        _venueRequests = 0;
        _emptyPages = 0;
        _barsWritten = 0;
        BarBackfillLane.Enter();
        try
        {
            foreach (InstrumentId instrument in instruments)
            {
                foreach (int resolution in _options.Resolutions)
                {
                    IReadOnlyList<BarBackfillWindow> windows = BarBackfillPlan.Windows(
                        resolution,
                        _clock.GetUtcNow(),
                        _options.NotBeforeUtc);
                    _logger.LogInformation(
                        "Bar backfill {Symbol} {ResolutionMinutes}m: {Windows} window(s), {Pages} page(s).",
                        instrument.Symbol,
                        resolution,
                        windows.Count,
                        windows.Sum(window => window.Pages.Count));

                    bool windowStarted = false;
                    foreach (BarBackfillWindow window in windows)
                    {
                        if (windowStarted && _probe is not null
                            && !await _probe.StillHeldAsync(cancellationToken).ConfigureAwait(false))
                        {
                            return new BarBackfillReport(
                                true,
                                BarBackfillStop.LockLost,
                                _pages,
                                _venueRequests,
                                _emptyPages,
                                _barsWritten);
                        }

                        windowStarted = true;
                        foreach (Domain.MarketData.BarRange page in window.Pages)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            BarBackfillPageResult read;
                            try
                            {
                                read = await _cache
                                    .ReadAsync(instrument, resolution, page, cancellationToken)
                                    .ConfigureAwait(false);
                            }
                            catch (VenueException exception) when (exception.ErrorCode == 429)
                            {
                                _logger.LogWarning(
                                    "Bar backfill stopped on HTTP 429 after {Pages} page(s) "
                                    + "and {VenueRequests} history request(s).",
                                    _pages,
                                    _venueRequests);
                                return new BarBackfillReport(
                                    true,
                                    BarBackfillStop.RateLimited,
                                    _pages,
                                    _venueRequests,
                                    _emptyPages,
                                    _barsWritten);
                            }

                            _pages++;
                            _venueRequests += read.VenueRequests;
                            _barsWritten += read.BarsWritten;
                            if (read.VenueRequests > 0 && read.Bars == 0)
                            {
                                _emptyPages++;
                                _logger.LogInformation(
                                    "Bar backfill {Symbol} {ResolutionMinutes}m {Start:o} to {End:o} "
                                    + "was empty. Coverage records it.",
                                    instrument.Symbol,
                                    resolution,
                                    page.Start,
                                    page.End);
                            }
                            else
                            {
                                _logger.LogInformation(
                                    "Bar backfill {Symbol} {ResolutionMinutes}m {Start:o} to {End:o}: "
                                    + "{Bars} bars, {VenueRequests} history request(s).",
                                    instrument.Symbol,
                                    resolution,
                                    page.Start,
                                    page.End,
                                    read.Bars,
                                    read.VenueRequests);
                            }
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation(
                "Bar backfill cancelled after {Pages} page(s).",
                _pages);
            return new BarBackfillReport(
                true,
                BarBackfillStop.Cancelled,
                _pages,
                _venueRequests,
                _emptyPages,
                _barsWritten);
        }
        finally
        {
            BarBackfillLane.Exit();
        }

        _logger.LogInformation(
            "Bar backfill finished: {Pages} page(s), {VenueRequests} history request(s), "
            + "{EmptyPages} empty page(s). Stop reason {StopReason}.",
            _pages,
            _venueRequests,
            _emptyPages,
            BarBackfillStop.Completed);
        return new BarBackfillReport(
            true,
            BarBackfillStop.Completed,
            _pages,
            _venueRequests,
            _emptyPages,
            _barsWritten);
    }

    private IReadOnlyList<InstrumentId> Instruments()
    {
        if (string.IsNullOrWhiteSpace(_options.Instruments))
        {
            return _registry.Instruments;
        }

        return _options.Instruments
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(symbol => new InstrumentId(symbol))
            .ToArray();
    }
}
