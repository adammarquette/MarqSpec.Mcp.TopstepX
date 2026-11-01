using System.Globalization;
using System.Threading.Channels;
using MarqSpec.Client.ProjectX.Api.Models;
using MarqSpec.Client.ProjectX.WebSocket;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Tools;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ProjectXWebSocketOptions = MarqSpec.Client.ProjectX.Configuration.WebSocketOptions;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// Caches the latest market-hub quote and answers <c>get_quote</c> from it.
/// </summary>
/// <remarks>
/// <para>
/// The hub client is the one the tape recorder uses. When <c>RecordTape</c> is on, this
/// service does not call <c>ConnectMarketHubAsync</c>. When it is off, this process connects
/// only while a fresh token is already held, and it does not call <c>loginKey</c>. Stdio
/// never subscribes. A missing quote is a refusal, not a bar close (gh#351).
/// </para>
/// <para>
/// Configured instruments stay subscribed. A contract that is not one of those fronts is
/// subscribed for the read and then unsubscribed. A hub drop clears the live set. The last
/// quote stays, marked stale, until a newer one arrives.
/// </para>
/// </remarks>
public sealed class LiveQuoteService : BackgroundService
{
    private const string NoBarClose = " get_quote does not answer from a bar close.";

    private readonly IServiceScopeFactory? _scopes;
    private readonly InstrumentRegistry _registry;
    private readonly MarketDataOptions _market;
    private readonly McpTransport _transport;
    private readonly TimeProvider _clock;
    private readonly ILogger<LiveQuoteService> _logger;
    private Func<InstrumentId, CancellationToken, Task<string?>> _resolveFront;
    private readonly bool _allowConnectWithoutToken;

    private readonly object _sync = new();
    private readonly Dictionary<string, string> _frontBySymbol = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _symbolByContract = new(StringComparer.Ordinal);
    private readonly HashSet<string> _held = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _transient = new(StringComparer.Ordinal);
    private readonly HashSet<string> _live = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _subscribing = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CachedQuote> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<TaskCompletionSource<bool>>> _waiters =
        new(StringComparer.Ordinal);
    private readonly Channel<WorkKind> _work = Channel.CreateUnbounded<WorkKind>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private IProjectXWebSocketClient? _hub;
    private HeldSession? _heldSession;
    private SessionLoginGate? _loginGate;
    private ProjectXWebSocketOptions? _socketOptions;
    private int _hooked;
    private int _noClient;
    private int _sawConflict;
    private int _reconnectQueued;
    private int _reconnectAttempt;
    private int _loggedWaitingForToken;
    private int _connectionEpoch;
    private int _suppressedAutoReconnect;

    /// <summary>The production constructor. The hub is resolved when the service starts.</summary>
    /// <param name="scopes">Opens a scope for the hub client and the contract search.</param>
    /// <param name="registry">The configured instruments.</param>
    /// <param name="market">The quote switch, the wait, and the stale threshold.</param>
    /// <param name="mcp">Which transport this process is.</param>
    /// <param name="clock">The clock quote age is measured on.</param>
    /// <param name="logger">The logger. Vendor error text is not written.</param>
    public LiveQuoteService(
        IServiceScopeFactory scopes,
        InstrumentRegistry registry,
        IOptions<MarketDataOptions> market,
        IOptions<McpOptions> mcp,
        TimeProvider clock,
        ILogger<LiveQuoteService> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(market);
        ArgumentNullException.ThrowIfNull(mcp);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _scopes = scopes;
        _registry = registry;
        _market = market.Value;
        _transport = mcp.Value.Transport;
        _clock = clock;
        _logger = logger;
        _resolveFront = ResolveThroughGatewayAsync;
    }

    /// <summary>The test constructor. The hub is the double the test raises quotes on.</summary>
    /// <param name="hub">The market hub.</param>
    /// <param name="registry">The configured instruments.</param>
    /// <param name="market">The quote switch, the wait, and the stale threshold.</param>
    /// <param name="transport">Which transport the test is pretending to be.</param>
    /// <param name="clock">The clock quote age and reconnect backoff use.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="resolveFront">Returns the front contract id, or null when the venue has none.</param>
    /// <param name="heldSession">When set, connect waits for a fresh token and does not log in.</param>
    /// <param name="allowConnectWithoutToken">
    /// Test doubles have no login. Production leaves this false.
    /// </param>
    internal LiveQuoteService(
        IProjectXWebSocketClient hub,
        InstrumentRegistry registry,
        IOptions<MarketDataOptions> market,
        McpTransport transport,
        TimeProvider clock,
        ILogger<LiveQuoteService> logger,
        Func<InstrumentId, CancellationToken, Task<string?>> resolveFront,
        HeldSession? heldSession = null,
        bool allowConnectWithoutToken = false)
        : this(
            new EmptyScopeFactory(),
            registry,
            market,
            Microsoft.Extensions.Options.Options.Create(new McpOptions { Transport = transport }),
            clock,
            logger)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(resolveFront);
        _hub = hub;
        _resolveFront = resolveFront;
        _heldSession = heldSession;
        _allowConnectWithoutToken = allowConnectWithoutToken;
    }

    /// <summary>Reads the latest quote for a configured instrument.</summary>
    /// <param name="symbol">The caller's symbol.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The cached quote, including a stale one.</returns>
    public async Task<ToolPayloads.Quote> ReadAsync(string symbol, CancellationToken cancellationToken)
    {
        await _ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        RefuseIfNotServing();
        InstrumentId instrument = ResolveSymbol(symbol);
        string contractId = await FrontContractAsync(instrument, cancellationToken).ConfigureAwait(false);
        return await ReadContractAsync(instrument.Symbol, contractId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Subscribes an extra contract for one read, then unsubscribes it unless it is a held front.
    /// </summary>
    /// <param name="contractId">The contract to subscribe.</param>
    /// <param name="symbol">A configured symbol. The payload uses the normalised form.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The quote for that contract.</returns>
    internal async Task<ToolPayloads.Quote> ReadUnheldContractAsync(
        string contractId,
        string symbol,
        CancellationToken cancellationToken)
    {
        await _ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        RefuseIfNotServing();
        InstrumentId instrument = ResolveSymbol(symbol);
        if (string.IsNullOrWhiteSpace(contractId))
        {
            throw new McpException("A quote subscription needs a contract id." + NoBarClose);
        }

        EnterTransient(contractId, instrument.Symbol);
        try
        {
            return await ReadContractAsync(instrument.Symbol, contractId, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            await LeaveTransientAsync(contractId, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (!Serving)
            {
                _logger.LogInformation(
                    "Live quotes are off (transport {Transport}, ServeQuotes {ServeQuotes}).",
                    _transport,
                    _market.ServeQuotes);
                return;
            }

            Attach();
            if (_hub is null)
            {
                Interlocked.Exchange(ref _noClient, 1);
                _logger.LogWarning(
                    "ServeQuotes is on but the venue client is not registered. Set ProjectX credentials "
                    + "and a data tier, then restart. No quote will be served until then.");
                return;
            }

            Hook();
            Task processing = ProcessAsync(stoppingToken);
            try
            {
                await WarmAsync(stoppingToken).ConfigureAwait(false);
                if (OwnsHub)
                {
                    SuppressAutoReconnect();
                    await ConnectIfAllowedAsync(stoppingToken).ConfigureAwait(false);
                }
                else
                {
                    _logger.LogInformation(
                        "Live quotes will use the market hub the tape recorder connects.");
                }

                if (HubConnected)
                {
                    await ResubscribeAsync(stoppingToken).ConfigureAwait(false);
                }

                _ready.TrySetResult();
                await processing.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Host shutdown.
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown before the loop.
        }
        finally
        {
            _ready.TrySetResult();
            Unhook();
            await DropSubscriptionsAsync().ConfigureAwait(false);
            if (OwnsHub && _hub is not null && _hub.MarketHubState == ConnectionState.Connected)
            {
                try
                {
                    await _hub.DisconnectMarketHubAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    _logger.LogError(
                        "The quote service could not disconnect the market hub it opened.");
                }
            }

            RestoreAutoReconnect();
        }
    }

    private bool Serving => _transport == McpTransport.Http && _market.ServeQuotes;

    private bool OwnsHub => Serving && !_market.RecordTape;

    private bool HubConnected =>
        _hub is not null && _hub.MarketHubState == ConnectionState.Connected;

    private void Attach()
    {
        if (_hub is not null || _scopes is null || _scopes is EmptyScopeFactory)
        {
            return;
        }

        using IServiceScope scope = _scopes.CreateScope();
        _hub = scope.ServiceProvider.GetService<IProjectXWebSocketClient>();
        _heldSession = scope.ServiceProvider.GetService<HeldSession>();
        _loginGate = scope.ServiceProvider.GetService<SessionLoginGate>();
        _socketOptions = scope.ServiceProvider.GetService<IOptions<ProjectXWebSocketOptions>>()?.Value;
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

    private async Task WarmAsync(CancellationToken cancellationToken)
    {
        foreach (InstrumentId instrument in _registry.Instruments)
        {
            string? contractId = await TryResolveAsync(instrument, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(contractId))
            {
                continue;
            }

            Hold(contractId, instrument.Symbol);
            lock (_sync)
            {
                _frontBySymbol[instrument.Symbol] = contractId;
            }
        }
    }

    private async Task<string?> TryResolveAsync(InstrumentId instrument, CancellationToken cancellationToken)
    {
        try
        {
            return await _resolveFront(instrument, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (QuoteFrontFailedException)
        {
            return null;
        }
    }

    private async Task<string> FrontContractAsync(
        InstrumentId instrument,
        CancellationToken cancellationToken)
    {
        string symbol = instrument.Symbol;
        string? previous;
        lock (_sync)
        {
            _frontBySymbol.TryGetValue(symbol, out previous);
        }

        if (previous is not null && HaveFreshCache(previous))
        {
            return previous;
        }

        string? resolved;
        try
        {
            resolved = await _resolveFront(instrument, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (QuoteFrontFailedException)
        {
            throw new McpException(
                "The venue could not resolve a contract for " + symbol + "." + NoBarClose);
        }

        if (string.IsNullOrWhiteSpace(resolved))
        {
            throw new McpException(
                "The venue returned no contract for " + symbol + ", so there is no live quote." + NoBarClose);
        }

        if (previous is not null && !string.Equals(previous, resolved, StringComparison.Ordinal))
        {
            await ReleaseHoldAsync(previous, cancellationToken).ConfigureAwait(false);
        }

        Hold(resolved, symbol);
        lock (_sync)
        {
            _frontBySymbol[symbol] = resolved;
        }

        return resolved;
    }

    private async Task<string?> ResolveThroughGatewayAsync(
        InstrumentId instrument,
        CancellationToken cancellationToken)
    {
        if (_scopes is null || _scopes is EmptyScopeFactory)
        {
            return null;
        }

        using IServiceScope scope = _scopes.CreateScope();
        IMarketDataGateway? gateway = scope.ServiceProvider.GetService<IMarketDataGateway>();
        if (gateway is null)
        {
            return null;
        }

        try
        {
            IReadOnlyList<VenueContract> contracts = await gateway
                .ResolveContractsAsync(instrument, cancellationToken)
                .ConfigureAwait(false);
            return contracts.Count == 0 ? null : contracts[0].ContractId;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            _logger.LogError(
                "Quote front resolution failed for {Symbol}. The venue error is not logged.",
                instrument.Symbol);
            throw new QuoteFrontFailedException();
        }
    }

    private async Task<ToolPayloads.Quote> ReadContractAsync(
        string symbol,
        string contractId,
        CancellationToken cancellationToken)
    {
        if (TryCache(contractId, out _))
        {
            return ToQuote(symbol, contractId);
        }

        if (!HubConnected)
        {
            throw NotConnected(symbol);
        }

        await EnsureSubscribedAsync(contractId, cancellationToken).ConfigureAwait(false);
        if (TryCache(contractId, out _))
        {
            return ToQuote(symbol, contractId);
        }

        if (!HubConnected)
        {
            throw NotConnected(symbol);
        }

        TaskCompletionSource<bool> waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
        AddWaiter(contractId, waiter);
        try
        {
            if (TryCache(contractId, out _))
            {
                return ToQuote(symbol, contractId);
            }

            if (!HubConnected)
            {
                throw NotConnected(symbol);
            }

            if (_market.QuoteWait > TimeSpan.Zero)
            {
                Task delay = Task.Delay(_market.QuoteWait, _clock, cancellationToken);
                await Task.WhenAny(waiter.Task, delay).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            RemoveWaiter(contractId, waiter);
        }

        if (TryCache(contractId, out _))
        {
            return ToQuote(symbol, contractId);
        }

        if (!HubConnected || Volatile.Read(ref _sawConflict) != 0)
        {
            throw NotConnected(symbol);
        }

        throw new McpException(
            "No live quote arrived for " + symbol + " within "
            + _market.QuoteWait.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)
            + " seconds." + NoBarClose);
    }

    private Task EnsureSubscribedAsync(string contractId, CancellationToken cancellationToken)
    {
        if (!HubConnected)
        {
            return Task.CompletedTask;
        }

        lock (_sync)
        {
            if (_live.Contains(contractId))
            {
                return Task.CompletedTask;
            }

            if (_subscribing.TryGetValue(contractId, out Task? existing))
            {
                return existing;
            }

            // SubscribeNewAsync can finish before this assignment: the hub double returns a
            // completed task, so the finally removes the entry and THEN we would store the
            // completed task. A later reconnect would join that task and never subscribe again.
            Task created = SubscribeNewAsync(contractId, cancellationToken);
            if (!created.IsCompleted)
            {
                _subscribing[contractId] = created;
            }

            return created;
        }
    }

    private async Task SubscribeNewAsync(string contractId, CancellationToken cancellationToken)
    {
        int epoch;
        lock (_sync)
        {
            epoch = _connectionEpoch;
        }

        try
        {
            if (_hub is null || !HubConnected)
            {
                return;
            }

            await _hub.SubscribeToPriceUpdatesAsync(contractId, cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                // A disconnect during the subscribe clears the server-side set and bumps the
                // epoch. Adding the id after that would claim a subscription the hub already
                // dropped, and the next reconnect would not subscribe again.
                if (epoch == _connectionEpoch && HubConnected)
                {
                    _live.Add(contractId);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            _logger.LogError(
                "The quote subscription was refused for {ContractId}. The venue error is not logged.",
                contractId);
            throw new McpException(
                "The market hub refused the quote subscription for " + SymbolFor(contractId) + "."
                + NoBarClose);
        }
        finally
        {
            lock (_sync)
            {
                _subscribing.Remove(contractId);
            }
        }
    }

    private async Task ResubscribeAsync(CancellationToken cancellationToken)
    {
        foreach (string contractId in Desired())
        {
            try
            {
                await EnsureSubscribedAsync(contractId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                _logger.LogError(
                    "The quote service could not subscribe {ContractId}. The venue error is not logged.",
                    contractId);
            }
        }
    }

    private async Task ReleaseHoldAsync(string contractId, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            _held.Remove(contractId);
        }

        await UnsubscribeIfIdleAsync(contractId, cancellationToken).ConfigureAwait(false);
    }

    private void EnterTransient(string contractId, string symbol)
    {
        lock (_sync)
        {
            _transient[contractId] = _transient.TryGetValue(contractId, out int count) ? count + 1 : 1;
            _symbolByContract[contractId] = symbol;
        }
    }

    private async Task LeaveTransientAsync(string contractId, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (!_transient.TryGetValue(contractId, out int count))
            {
                return;
            }

            if (count <= 1)
            {
                _transient.Remove(contractId);
            }
            else
            {
                _transient[contractId] = count - 1;
            }
        }

        await UnsubscribeIfIdleAsync(contractId, cancellationToken).ConfigureAwait(false);
    }

    private async Task UnsubscribeIfIdleAsync(string contractId, CancellationToken cancellationToken)
    {
        bool unsubscribe;
        lock (_sync)
        {
            unsubscribe = !_held.Contains(contractId)
                && !_transient.ContainsKey(contractId)
                && _live.Remove(contractId);
        }

        if (!unsubscribe || _hub is null || _hub.MarketHubState != ConnectionState.Connected)
        {
            return;
        }

        try
        {
            await _hub.UnsubscribeFromPriceUpdatesAsync(contractId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            _logger.LogError(
                "The quote service could not unsubscribe {ContractId}. The venue error is not logged.",
                contractId);
        }
    }

    private async Task DropSubscriptionsAsync()
    {
        List<string> live;
        lock (_sync)
        {
            live = [.. _live];
            _live.Clear();
            _held.Clear();
            _transient.Clear();
        }

        if (_hub is null || _hub.MarketHubState != ConnectionState.Connected)
        {
            return;
        }

        foreach (string contractId in live)
        {
            try
            {
                await _hub.UnsubscribeFromPriceUpdatesAsync(contractId, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                _logger.LogError(
                    "The quote service could not unsubscribe {ContractId} while stopping.",
                    contractId);
            }
        }
    }

    private void OnPrice(object? sender, PriceUpdate update)
    {
        if (string.IsNullOrWhiteSpace(update.ContractId) || !IsDesired(update.ContractId))
        {
            return;
        }

        CachedQuote cached = new(
            update.BestBid,
            update.BestAsk,
            update.LastPrice,
            update.Volume,
            VenueTime(update),
            _clock.GetUtcNow());
        List<TaskCompletionSource<bool>>? waiters = null;
        lock (_sync)
        {
            _cache[update.ContractId] = cached;
            if (_waiters.Remove(update.ContractId, out List<TaskCompletionSource<bool>>? found))
            {
                waiters = found;
            }
        }

        if (waiters is null)
        {
            return;
        }

        foreach (TaskCompletionSource<bool> waiter in waiters)
        {
            waiter.TrySetResult(true);
        }
    }

    private void OnConnectionStatusChanged(object? sender, ConnectionStatusChange change)
    {
        if (change.CurrentState == ConnectionState.Connected)
        {
            Interlocked.Exchange(ref _sawConflict, 0);
            _work.Writer.TryWrite(WorkKind.Resubscribe);
            return;
        }

        if (change.CurrentState is not (ConnectionState.Disconnected or ConnectionState.Failed
            or ConnectionState.Reconnecting))
        {
            return;
        }

        NoteHubDown();
        bool conflict = SessionConflictSignals.IsHubConflict(change.ErrorMessage, change.Exception?.Message);
        if (conflict)
        {
            Interlocked.Exchange(ref _sawConflict, 1);
        }

        if (!OwnsHub || change.CurrentState == ConnectionState.Reconnecting)
        {
            return;
        }

        if (conflict)
        {
            _loginGate?.LatchStop();
            _logger.LogWarning(
                "Live quotes stopped because the market hub named a session conflict. "
                + "This process will not call loginKey.");
            return;
        }

        EnqueueReconnect();
    }

    private void NoteHubDown()
    {
        List<TaskCompletionSource<bool>> waiters = [];
        lock (_sync)
        {
            _connectionEpoch++;
            _live.Clear();
            _subscribing.Clear();
            foreach (List<TaskCompletionSource<bool>> list in _waiters.Values)
            {
                waiters.AddRange(list);
            }

            _waiters.Clear();
        }

        foreach (TaskCompletionSource<bool> waiter in waiters)
        {
            waiter.TrySetResult(false);
        }
    }

    private async Task ProcessAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            WorkKind kind = await _work.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (kind == WorkKind.Resubscribe)
            {
                await ResubscribeAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            await ReconnectAfterBackoffAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ConnectIfAllowedAsync(CancellationToken cancellationToken)
    {
        if (!MayConnect())
        {
            NoteWaitingForToken();
            if (OwnsHub && Volatile.Read(ref _sawConflict) == 0 && _loginGate?.StopLatched != true)
            {
                EnqueueReconnect();
            }

            return;
        }

        if (_hub is null
            || _hub.MarketHubState is ConnectionState.Connected
                or ConnectionState.Connecting
                or ConnectionState.Reconnecting)
        {
            return;
        }

        try
        {
            await _hub.ConnectMarketHubAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Exchange(ref _reconnectAttempt, 0);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            _logger.LogError(
                "The quote service could not connect the market hub. It will try again "
                + "and will not call loginKey.");
            EnqueueReconnect();
        }
    }

    private async Task ReconnectAfterBackoffAsync(CancellationToken cancellationToken)
    {
        bool again = false;
        try
        {
            int attempt = Volatile.Read(ref _reconnectAttempt);
            await Task.Delay(SessionReprobe.DelayForAttempt(attempt), _clock, cancellationToken)
                .ConfigureAwait(false);
            if (!MayConnect())
            {
                NoteWaitingForToken();
                if (Volatile.Read(ref _sawConflict) == 0 && _loginGate?.StopLatched != true)
                {
                    again = true;
                }

                return;
            }

            if (_hub is null
                || _hub.MarketHubState is ConnectionState.Connected
                    or ConnectionState.Connecting
                    or ConnectionState.Reconnecting)
            {
                return;
            }

            await _hub.ConnectMarketHubAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Exchange(ref _reconnectAttempt, 0);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            again = true;
            int attempt = Volatile.Read(ref _reconnectAttempt);
            Interlocked.Exchange(ref _reconnectAttempt, attempt == int.MaxValue ? attempt : attempt + 1);
            _logger.LogError(
                "The quote service could not connect the market hub. It will try again "
                + "and will not call loginKey.");
        }
        finally
        {
            Interlocked.Exchange(ref _reconnectQueued, 0);
        }

        if (again)
        {
            EnqueueReconnect();
        }
    }

    private bool MayConnect()
    {
        if (!OwnsHub || Volatile.Read(ref _sawConflict) != 0 || _loginGate?.StopLatched == true)
        {
            return false;
        }

        if (_heldSession is null)
        {
            return _allowConnectWithoutToken;
        }

        return _heldSession.TryReadFresh(_clock, out _);
    }

    private void NoteWaitingForToken()
    {
        if (Interlocked.Exchange(ref _loggedWaitingForToken, 1) != 0)
        {
            return;
        }

        _logger.LogInformation(
            "Live quotes will not connect the market hub without a fresh token, and will not call loginKey.");
    }

    private void EnqueueReconnect()
    {
        if (Interlocked.CompareExchange(ref _reconnectQueued, 1, 0) == 0)
        {
            _work.Writer.TryWrite(WorkKind.Reconnect);
        }
    }

    private void SuppressAutoReconnect()
    {
        if (_socketOptions is null || _loginGate is null)
        {
            return;
        }

        _loginGate.SuppressAutomaticRetry(_socketOptions);
        Interlocked.Exchange(ref _suppressedAutoReconnect, 1);
    }

    private void RestoreAutoReconnect()
    {
        if (_socketOptions is null
            || _loginGate is null
            || Interlocked.Exchange(ref _suppressedAutoReconnect, 0) == 0
            || Volatile.Read(ref _sawConflict) != 0
            || _loginGate.StopLatched)
        {
            return;
        }

        _loginGate.RestorePriorAutoReconnect(_socketOptions);
    }

    private void Hold(string contractId, string symbol)
    {
        lock (_sync)
        {
            _held.Add(contractId);
            _symbolByContract[contractId] = symbol;
        }
    }

    private bool IsDesired(string contractId)
    {
        lock (_sync)
        {
            return _held.Contains(contractId) || _transient.ContainsKey(contractId);
        }
    }

    private List<string> Desired()
    {
        lock (_sync)
        {
            List<string> ids = [.. _held];
            foreach (string contractId in _transient.Keys)
            {
                if (!_held.Contains(contractId))
                {
                    ids.Add(contractId);
                }
            }

            return ids;
        }
    }

    private bool HaveFreshCache(string contractId)
    {
        if (!HubConnected || !TryCache(contractId, out CachedQuote cached))
        {
            return false;
        }

        return Age(cached) <= _market.QuoteStaleAfter;
    }

    private bool TryCache(string contractId, out CachedQuote cached)
    {
        lock (_sync)
        {
            return _cache.TryGetValue(contractId, out cached);
        }
    }

    private void AddWaiter(string contractId, TaskCompletionSource<bool> waiter)
    {
        lock (_sync)
        {
            if (!_waiters.TryGetValue(contractId, out List<TaskCompletionSource<bool>>? list))
            {
                list = [];
                _waiters[contractId] = list;
            }

            list.Add(waiter);
        }
    }

    private void RemoveWaiter(string contractId, TaskCompletionSource<bool> waiter)
    {
        lock (_sync)
        {
            if (!_waiters.TryGetValue(contractId, out List<TaskCompletionSource<bool>>? list))
            {
                return;
            }

            list.Remove(waiter);
            if (list.Count == 0)
            {
                _waiters.Remove(contractId);
            }
        }
    }

    private ToolPayloads.Quote ToQuote(string symbol, string contractId)
    {
        if (!TryCache(contractId, out CachedQuote cached))
        {
            throw new McpException("No live quote is cached for " + symbol + "." + NoBarClose);
        }

        bool hub = HubConnected;
        TimeSpan age = Age(cached);
        bool stale = !hub || age > _market.QuoteStaleAfter || Volatile.Read(ref _sawConflict) != 0;
        return new ToolPayloads.Quote(
            symbol,
            contractId,
            cached.Bid,
            cached.Ask,
            cached.Last,
            null,
            null,
            cached.Volume,
            cached.VenueTimestamp,
            age.TotalSeconds,
            stale,
            hub);
    }

    private TimeSpan Age(CachedQuote cached)
    {
        DateTimeOffset basis = cached.VenueTimestamp ?? cached.ReceivedAt;
        TimeSpan age = _clock.GetUtcNow() - basis;
        return age < TimeSpan.Zero ? TimeSpan.Zero : age;
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

    private InstrumentId ResolveSymbol(string symbol)
    {
        try
        {
            return _registry.Resolve(symbol);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or ArgumentException)
        {
            throw new McpException(ex.Message);
        }
    }

    private void RefuseIfNotServing()
    {
        if (_transport != McpTransport.Http)
        {
            throw new McpException(
                "Live quotes are not served on the stdio transport. Run the HTTP transport with "
                + "MarketData__ServeQuotes=true." + NoBarClose);
        }

        if (!_market.ServeQuotes)
        {
            throw new McpException(
                "MarketData__ServeQuotes is off, so this process is not subscribed to quotes. "
                + "Set MarketData__ServeQuotes=true on the HTTP process that holds the market hub."
                + NoBarClose);
        }

        if (Volatile.Read(ref _noClient) != 0 || _hub is null)
        {
            throw new McpException(
                "ServeQuotes is on but the venue client is not registered. Set ProjectX credentials "
                + "and a data tier, then restart." + NoBarClose);
        }
    }

    private McpException NotConnected(string symbol)
    {
        if (Volatile.Read(ref _sawConflict) != 0)
        {
            return new McpException(
                "The market hub stopped for a session conflict, so no live quote is available for "
                + symbol + ". This process will not call loginKey." + NoBarClose);
        }

        if (OwnsHub && _heldSession is not null && !_heldSession.TryReadFresh(_clock, out _))
        {
            return new McpException(
                "The market hub is not connected and this process will not call loginKey to open it. "
                + "Live quotes share the hub the tape recorder connects when MarketData__RecordTape is on. "
                + "No live quote is available for " + symbol + "." + NoBarClose);
        }

        return new McpException(
            "The market hub is not connected, so no live quote is available for " + symbol + "."
            + NoBarClose);
    }

    private string SymbolFor(string contractId)
    {
        lock (_sync)
        {
            return _symbolByContract.TryGetValue(contractId, out string? symbol) ? symbol : contractId;
        }
    }

    private readonly record struct CachedQuote(
        decimal Bid,
        decimal Ask,
        decimal Last,
        decimal Volume,
        DateTimeOffset? VenueTimestamp,
        DateTimeOffset ReceivedAt);

    private enum WorkKind
    {
        Resubscribe = 0,
        Reconnect = 1,
    }

    /// <summary>A failed front search. The venue's own text stays off the tool result.</summary>
    private sealed class QuoteFrontFailedException : Exception;

    /// <summary>
    /// Stands in for a scope factory on the test constructor so the production constructor
    /// can require one. Attach treats it as "the hub was supplied".
    /// </summary>
    private sealed class EmptyScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() =>
            throw new InvalidOperationException("The test quote service does not open a scope.");
    }
}
