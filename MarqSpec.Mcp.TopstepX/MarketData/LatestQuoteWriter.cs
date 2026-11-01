using System.Diagnostics;
using MarqSpec.Client.ProjectX.Api.Models;
using MarqSpec.Client.ProjectX.WebSocket;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Telemetry;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// Upserts the latest quote from the market hub the tape recorder already holds.
/// </summary>
/// <remarks>
/// <para>
/// This process does not call <c>ConnectMarketHubAsync</c> and does not touch the session-login
/// gate. When the tape switch is off, or this switch is off, or the transport is stdio, the
/// writer returns without subscribing. A server that does not record tape therefore never opens
/// a hub for quotes.
/// </para>
/// <para>
/// Quotes are coalesced and flushed on <see cref="MarketDataOptions.LatestQuoteFlush"/>. One
/// flush is one store statement. A failed flush is logged, put back, and retried more slowly,
/// so it cannot stop the host. Fronts are resolved again on
/// <see cref="MarketDataOptions.LatestQuoteFrontRefresh"/>.
/// </para>
/// </remarks>
public sealed class LatestQuoteWriter : BackgroundService
{
    /// <summary>Used when no gateway is registered. One spelling, everywhere.</summary>
    private const string FallbackVenueId = "projectx";

    /// <summary>How long the last flush may wait after the host asks the writer to stop.</summary>
    private static readonly TimeSpan _shutdownFlushTimeout = TimeSpan.FromSeconds(2);

    private readonly IServiceScopeFactory? _scopes;
    private readonly InstrumentRegistry _registry;
    private readonly MarketDataOptions _market;
    private readonly McpTransport _transport;
    private readonly TimeProvider _clock;
    private readonly ILogger<LatestQuoteWriter> _logger;
    private readonly HostTelemetry _telemetry;
    private readonly LatestQuoteCoalescer _coalescer = new();
    private readonly object _sync = new();
    // _live and _instrumentByContract are touched only under _sync, including the hub thread.
    private readonly HashSet<string> _live = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _instrumentByContract = new(StringComparer.Ordinal);
    private readonly LatestQuoteSubscribeRetries _retries = new();

    private IProjectXWebSocketClient? _hub;
    private ILatestQuoteStore? _store;
    private Func<InstrumentId, CancellationToken, Task<string?>>? _resolveFront;
    private string? _venue;
    private int _hooked;
    private int _resubscribe;
    private TimeSpan _gap;
    private DateTimeOffset _nextRefresh;
    private bool _loggedFlushFailure;

    /// <summary>The production constructor. The hub and the store are resolved per operation.</summary>
    /// <param name="scopes">Opens a scope for the hub, the contract search, and the store.</param>
    /// <param name="registry">The configured instruments.</param>
    /// <param name="market">The switches and the flush gap.</param>
    /// <param name="mcp">Which transport this process is.</param>
    /// <param name="clock">The flush clock.</param>
    /// <param name="logger">The logger. Vendor error text is not written.</param>
    /// <param name="telemetry">Write counts.</param>
    public LatestQuoteWriter(
        IServiceScopeFactory scopes,
        InstrumentRegistry registry,
        IOptions<MarketDataOptions> market,
        IOptions<McpOptions> mcp,
        TimeProvider clock,
        ILogger<LatestQuoteWriter> logger,
        HostTelemetry telemetry)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(market);
        ArgumentNullException.ThrowIfNull(mcp);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(telemetry);
        _scopes = scopes;
        _registry = registry;
        _market = market.Value;
        _transport = mcp.Value.Transport;
        _clock = clock;
        _logger = logger;
        _telemetry = telemetry;
    }

    /// <summary>The test constructor. The hub is the double the test raises quotes on.</summary>
    /// <param name="hub">The market hub. This writer never connects it.</param>
    /// <param name="registry">The configured instruments.</param>
    /// <param name="market">The switches and the flush gap.</param>
    /// <param name="transport">Which transport the test is pretending to be.</param>
    /// <param name="clock">The flush clock.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="telemetry">Write counts.</param>
    /// <param name="store">Where flushes go.</param>
    /// <param name="resolveFront">The front contract id, or null when the venue has none.</param>
    /// <param name="venue">The venue id stored on the row.</param>
    internal LatestQuoteWriter(
        IProjectXWebSocketClient hub,
        InstrumentRegistry registry,
        IOptions<MarketDataOptions> market,
        McpTransport transport,
        TimeProvider clock,
        ILogger<LatestQuoteWriter> logger,
        HostTelemetry telemetry,
        ILatestQuoteStore store,
        Func<InstrumentId, CancellationToken, Task<string?>> resolveFront,
        string venue)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(market);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(telemetry);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(resolveFront);
        ArgumentException.ThrowIfNullOrWhiteSpace(venue);
        _registry = registry;
        _market = market.Value;
        _transport = transport;
        _clock = clock;
        _logger = logger;
        _telemetry = telemetry;
        _hub = hub;
        _store = store;
        _resolveFront = resolveFront;
        _venue = venue;
    }

    private bool Enabled =>
        _transport == McpTransport.Http && _market.RecordTape && _market.RecordLatestQuotes;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled)
        {
            _logger.LogInformation(
                "Latest quotes are off (transport {Transport}, RecordTape {RecordTape}, "
                + "RecordLatestQuotes {RecordLatestQuotes}).",
                _transport,
                _market.RecordTape,
                _market.RecordLatestQuotes);
            return;
        }

        if (!AttachHub())
        {
            _logger.LogWarning(
                "RecordLatestQuotes is on but the venue client is not registered. "
                + "No quote will be stored until ProjectX credentials and a data tier are set.");
            return;
        }

        _gap = _market.LatestQuoteFlush;
        _nextRefresh = _clock.GetUtcNow() + _market.LatestQuoteFrontRefresh;
        PinVenue();
        Hook();
        Interlocked.Exchange(ref _resubscribe, 1);
        try
        {
            await FlushLoopAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
        finally
        {
            Unhook();
            await ReleasePriceSubscriptionsAsync().ConfigureAwait(false);
            await ShutdownFlushAsync().ConfigureAwait(false);
        }
    }

    private bool AttachHub()
    {
        if (_hub is not null)
        {
            return true;
        }

        if (_scopes is null)
        {
            return false;
        }

        using IServiceScope scope = _scopes.CreateScope();
        _hub = scope.ServiceProvider.GetService<IProjectXWebSocketClient>();
        return _hub is not null;
    }

    private void PinVenue()
    {
        string venue = Volatile.Read(ref _venue) ?? ReadVenue();
        Volatile.Write(ref _venue, venue);
    }

    private string ReadVenue()
    {
        if (_scopes is null)
        {
            return FallbackVenueId;
        }

        using IServiceScope scope = _scopes.CreateScope();
        IMarketDataGateway? gateway = scope.ServiceProvider.GetService<IMarketDataGateway>();
        return string.IsNullOrWhiteSpace(gateway?.VenueId) ? FallbackVenueId : gateway.VenueId;
    }

    private void Hook()
    {
        if (_hub is null || Interlocked.Exchange(ref _hooked, 1) != 0)
        {
            return;
        }

        _hub.PriceUpdateReceived += OnPrice;
        _hub.ConnectionStatusChanged += OnConnectionStatusChanged;
    }

    private void Unhook()
    {
        if (_hub is null || Interlocked.Exchange(ref _hooked, 0) == 0)
        {
            return;
        }

        _hub.PriceUpdateReceived -= OnPrice;
        _hub.ConnectionStatusChanged -= OnConnectionStatusChanged;
    }

    private async Task FlushLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_clock.GetUtcNow() >= _nextRefresh)
                {
                    Interlocked.Exchange(ref _resubscribe, 1);
                    _nextRefresh = _clock.GetUtcNow() + _market.LatestQuoteFrontRefresh;
                }

                if (_hub is not null
                    && _hub.MarketHubState == ConnectionState.Connected
                    && Interlocked.CompareExchange(ref _resubscribe, 0, 1) == 1)
                {
                    await SubscribeFrontsAsync(stoppingToken).ConfigureAwait(false);
                }

                await RetryRefusedAsync(stoppingToken).ConfigureAwait(false);

                await FlushAsync(
                        _coalescer.TakeIfDue(_clock.GetUtcNow(), _gap),
                        stoppingToken)
                    .ConfigureAwait(false);

                await Task.Delay(_gap, _clock, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "A latest-quote pass failed. The recorder keeps running.");
                try
                {
                    await Task.Delay(_gap, _clock, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
            }
        }
    }

    private async Task SubscribeFrontsAsync(CancellationToken cancellationToken)
    {
        if (_hub is null || _hub.MarketHubState != ConnectionState.Connected)
        {
            Interlocked.Exchange(ref _resubscribe, 1);
            return;
        }

        // The client's automatic resubscribe may also restore price streams on this hub.
        // A second SubscribeToPriceUpdatesAsync for the same contract is idempotent at the venue.
        using Activity? span = _telemetry.StartQuoteSubscribe();
        Dictionary<string, string> current = CopyMap();
        Dictionary<string, string> wanted = new(StringComparer.Ordinal);
        bool unresolved = false;
        Exception? resolveError = null;
        foreach (InstrumentId instrument in _registry.Instruments)
        {
            (string? contractId, Exception? error) = await ResolveAsync(instrument, cancellationToken)
                .ConfigureAwait(false);
            if (error is not null)
            {
                unresolved = true;
                resolveError ??= error;
                KeepCurrent(current, wanted, instrument.Symbol);
                continue;
            }

            if (string.IsNullOrWhiteSpace(contractId))
            {
                NoteFrontUnresolved(instrument.Symbol);
                unresolved = true;
                KeepCurrent(current, wanted, instrument.Symbol);
                continue;
            }

            wanted[contractId] = instrument.Symbol;
        }

        _retries.ForgetExcept(wanted.Keys);
        await DropRetiredAsync(wanted, cancellationToken).ConfigureAwait(false);
        ReplaceMap(wanted);

        Exception? subscribeError = null;
        foreach (KeyValuePair<string, string> pair in wanted)
        {
            if (AlreadyLive(pair.Key) || _retries.IsWaiting(pair.Key, _clock.GetUtcNow()))
            {
                continue;
            }

            try
            {
                await _hub.SubscribeToPriceUpdatesAsync(pair.Key, cancellationToken).ConfigureAwait(false);
                NoteSubscribed(pair.Key);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                subscribeError ??= ex;
                NoteRefused(pair.Key, pair.Value, ex);
            }
        }

        if (subscribeError is not null)
        {
            Mark(span, QuoteWriteOutcome.SubscribeRefused, subscribeError);
        }
        else if (unresolved)
        {
            Mark(span, QuoteWriteOutcome.FrontUnresolved, resolveError);
        }
        else
        {
            Mark(span, QuoteWriteOutcome.Subscribed, null);
        }
    }

    private async Task RetryRefusedAsync(CancellationToken cancellationToken)
    {
        if (_hub is null || _hub.MarketHubState != ConnectionState.Connected)
        {
            return;
        }

        foreach (LatestQuoteSubscribeRetries.DueRetry due in _retries.Due(_clock.GetUtcNow()))
        {
            try
            {
                await _hub.SubscribeToPriceUpdatesAsync(due.ContractId, cancellationToken)
                    .ConfigureAwait(false);
                NoteSubscribed(due.ContractId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                NoteRefused(due.ContractId, due.Instrument, ex);
            }
        }
    }

    private static void KeepCurrent(
        Dictionary<string, string> current,
        Dictionary<string, string> wanted,
        string symbol)
    {
        foreach (KeyValuePair<string, string> pair in current)
        {
            if (pair.Value == symbol)
            {
                wanted[pair.Key] = symbol;
            }
        }
    }

    private void NoteFrontUnresolved(string symbol)
    {
        _telemetry.QuoteWrite(QuoteWriteOutcome.FrontUnresolved, 1);
        _telemetry.QuoteFailure(QuoteWriteOutcome.FrontUnresolved);
        _logger.LogError(
            "Latest-quote front resolution failed for {Instrument}. The venue error is not logged.",
            symbol);
    }

    private void NoteSubscribed(string contractId)
    {
        string? recovered = _retries.NoteRecovered(contractId);
        if (recovered is not null)
        {
            _logger.LogInformation(
                "The latest-quote subscription recovered for {Instrument}.",
                recovered);
        }

        lock (_sync)
        {
            if (_hub is not null && _hub.MarketHubState == ConnectionState.Connected)
            {
                _live.Add(contractId);
            }
        }
    }

    private void NoteRefused(string contractId, string instrument, Exception exception)
    {
        _telemetry.QuoteWrite(QuoteWriteOutcome.SubscribeRefused, 1);
        _telemetry.QuoteFailure(QuoteWriteOutcome.SubscribeRefused, exception.GetType().Name);
        bool first = _retries.NoteRefused(
            contractId,
            instrument,
            _clock.GetUtcNow(),
            _market.LatestQuoteSubscribeBackoff,
            MarketDataOptions.MaxLatestQuoteSubscribeBackoff);
        if (first)
        {
            _logger.LogWarning(
                exception,
                "The latest-quote subscription was refused for {Instrument} ({ExceptionType}).",
                instrument,
                exception.GetType().Name);
            return;
        }

        _logger.LogWarning(
            "The latest-quote subscription was refused for {Instrument} ({ExceptionType}).",
            instrument,
            exception.GetType().Name);
    }

    private static void Mark(Activity? span, string outcome, Exception? error)
    {
        span?.SetTag(HostTelemetry.OutcomeTag, outcome);
        if (error is null)
        {
            return;
        }

        span?.SetTag(HostTelemetry.ExceptionTag, error.GetType().Name);
        span?.SetStatus(ActivityStatusCode.Error);
    }

    private Dictionary<string, string> CopyMap()
    {
        lock (_sync)
        {
            return new Dictionary<string, string>(_instrumentByContract, StringComparer.Ordinal);
        }
    }

    private void ReplaceMap(Dictionary<string, string> wanted)
    {
        lock (_sync)
        {
            _instrumentByContract.Clear();
            foreach (KeyValuePair<string, string> pair in wanted)
            {
                _instrumentByContract[pair.Key] = pair.Value;
            }
        }
    }

    private async Task DropRetiredAsync(
        Dictionary<string, string> wanted,
        CancellationToken cancellationToken)
    {
        string[] retired;
        lock (_sync)
        {
            retired = [.. _live.Where(id => !wanted.ContainsKey(id))];
        }

        if (_hub is null)
        {
            return;
        }

        foreach (string contractId in retired)
        {
            if (_hub.MarketHubState == ConnectionState.Connected)
            {
                try
                {
                    await _hub.UnsubscribeFromPriceUpdatesAsync(contractId, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "A latest-quote unsubscribe did not finish.");
                    _telemetry.QuoteFailure(QuoteWriteOutcome.UnsubscribeFailed, ex.GetType().Name);
                }
            }

            lock (_sync)
            {
                _live.Remove(contractId);
            }
        }
    }

    private bool AlreadyLive(string contractId)
    {
        lock (_sync)
        {
            return _live.Contains(contractId);
        }
    }

    private async Task<(string? ContractId, Exception? Error)> ResolveAsync(
        InstrumentId instrument,
        CancellationToken cancellationToken)
    {
        if (_resolveFront is not null)
        {
            try
            {
                string? contractId = await _resolveFront(instrument, cancellationToken).ConfigureAwait(false);
                return (contractId, null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Latest-quote front resolution failed for {Instrument}.",
                    instrument.Symbol);
                _telemetry.QuoteWrite(QuoteWriteOutcome.FrontUnresolved, 1);
                _telemetry.QuoteFailure(QuoteWriteOutcome.FrontUnresolved, ex.GetType().Name);
                return (null, ex);
            }
        }

        if (_scopes is null)
        {
            return (null, null);
        }

        using IServiceScope scope = _scopes.CreateScope();
        IMarketDataGateway? gateway = scope.ServiceProvider.GetService<IMarketDataGateway>();
        if (gateway is null)
        {
            return (null, null);
        }

        try
        {
            IReadOnlyList<VenueContract> contracts = await gateway
                .ResolveContractsAsync(instrument, cancellationToken)
                .ConfigureAwait(false);
            return (contracts.Count == 0 ? null : contracts[0].ContractId, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Latest-quote front resolution failed for {Instrument}.",
                instrument.Symbol);
            _telemetry.QuoteWrite(QuoteWriteOutcome.FrontUnresolved, 1);
            _telemetry.QuoteFailure(QuoteWriteOutcome.FrontUnresolved, ex.GetType().Name);
            return (null, ex);
        }
    }

    private void OnPrice(object? sender, PriceUpdate update)
    {
        if (string.IsNullOrWhiteSpace(update.ContractId))
        {
            return;
        }

        string? instrument;
        lock (_sync)
        {
            if (!_instrumentByContract.TryGetValue(update.ContractId, out instrument))
            {
                return;
            }
        }

        if (!TryVolume(update.Volume, out long volume))
        {
            _telemetry.QuoteWrite(QuoteWriteOutcome.VolumeRejected, 1);
            _telemetry.QuoteFailure(QuoteWriteOutcome.VolumeRejected, nameof(OverflowException));
            return;
        }

        _coalescer.Offer(new LatestQuoteDraft(
            Volatile.Read(ref _venue) ?? FallbackVenueId,
            instrument,
            update.ContractId,
            update.BestBid,
            update.BestAsk,
            update.LastPrice,
            volume,
            VenueTime(update),
            _clock.GetUtcNow()));
    }

    private static bool TryVolume(decimal volume, out long contracts)
    {
        try
        {
            contracts = decimal.ToInt64(decimal.Truncate(volume));
            return true;
        }
        catch (OverflowException)
        {
            contracts = 0;
            return false;
        }
    }

    private void OnConnectionStatusChanged(object? sender, ConnectionStatusChange change)
    {
        if (change.CurrentState == ConnectionState.Connected)
        {
            Interlocked.Exchange(ref _resubscribe, 1);
            return;
        }

        if (change.CurrentState is ConnectionState.Disconnected
            or ConnectionState.Failed
            or ConnectionState.Reconnecting)
        {
            lock (_sync)
            {
                _live.Clear();
            }

            _retries.Clear();
        }
    }

    private async Task FlushAsync(IReadOnlyList<LatestQuoteDraft> batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
        {
            return;
        }

        using Activity? span = _telemetry.StartQuoteFlush();
        try
        {
            int written = await UpsertAsync(batch, cancellationToken).ConfigureAwait(false);
            Mark(span, QuoteWriteOutcome.Written, null);
            _telemetry.QuoteWrite(QuoteWriteOutcome.Written, written);
            _gap = _market.LatestQuoteFlush;
            _loggedFlushFailure = false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _coalescer.Reoffer(batch);
            throw;
        }
        catch (Exception ex)
        {
            Mark(span, QuoteWriteOutcome.Failed, ex);
            _coalescer.Reoffer(batch);
            _telemetry.QuoteWrite(QuoteWriteOutcome.Failed, batch.Count);
            _telemetry.QuoteFailure(QuoteWriteOutcome.Failed, ex.GetType().Name);
            BackOff();
            if (!_loggedFlushFailure)
            {
                _loggedFlushFailure = true;
                _logger.LogWarning(
                    ex,
                    "A latest-quote flush failed. The batch stays queued and the next attempt waits longer.");
            }
            else
            {
                _logger.LogWarning(
                    "A latest-quote flush failed. The batch stays queued and the next attempt waits longer.");
            }
        }
    }

    private void BackOff()
    {
        TimeSpan previous = _gap;
        long ticks = previous.Ticks * 2;
        _gap = ticks >= MarketDataOptions.MaxLatestQuoteFlush.Ticks
            ? MarketDataOptions.MaxLatestQuoteFlush
            : TimeSpan.FromTicks(ticks);
    }

    private async Task<int> UpsertAsync(
        IReadOnlyList<LatestQuoteDraft> batch,
        CancellationToken cancellationToken)
    {
        if (_store is not null)
        {
            return await _store.UpsertAsync(batch, cancellationToken).ConfigureAwait(false);
        }

        if (_scopes is null)
        {
            return 0;
        }

        using IServiceScope scope = _scopes.CreateScope();
        ILatestQuoteStore store = scope.ServiceProvider.GetRequiredService<ILatestQuoteStore>();
        return await store.UpsertAsync(batch, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReleasePriceSubscriptionsAsync()
    {
        if (_hub is null || _hub.MarketHubState != ConnectionState.Connected)
        {
            return;
        }

        string[] live;
        lock (_sync)
        {
            live = [.. _live];
            _live.Clear();
        }

        using CancellationTokenSource unsubscribe = new(_shutdownFlushTimeout);
        foreach (string contractId in live)
        {
            try
            {
                await _hub.UnsubscribeFromPriceUpdatesAsync(contractId, unsubscribe.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "A latest-quote unsubscribe did not finish.");
                _telemetry.QuoteFailure(QuoteWriteOutcome.UnsubscribeFailed, ex.GetType().Name);
            }
        }
    }

    private async Task ShutdownFlushAsync()
    {
        using CancellationTokenSource shutdown = new(_shutdownFlushTimeout);
        try
        {
            await FlushAsync(_coalescer.TakeAll(_clock.GetUtcNow()), shutdown.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The shutdown flush of latest quotes did not finish.");
            _telemetry.QuoteFailure(QuoteWriteOutcome.ShutdownFailed, ex.GetType().Name);
        }
    }

    private static DateTimeOffset? VenueTime(PriceUpdate update)
    {
        DateTime raw = update.Timestamp.Year >= 2000
            ? update.Timestamp
            : update.LastUpdated.Year >= 2000
                ? update.LastUpdated
                : default;
        return raw.Year >= 2000 ? ProjectXMapping.ToUtc(raw) : null;
    }
}
