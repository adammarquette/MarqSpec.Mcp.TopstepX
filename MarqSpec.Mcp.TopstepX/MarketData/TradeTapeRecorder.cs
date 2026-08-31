using System.Threading.Channels;
using MarqSpec.Client.ProjectX.Api.Models;
using MarqSpec.Client.ProjectX.WebSocket;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// The first first-party <see cref="BackgroundService"/>: subscribe to the market hub for every
/// configured instrument's front contract, write prints to <c>Trades</c>, and write
/// <c>TapeCoverage</c> from subscription lifecycle — a still-open listen is stored when
/// subscribe is confirmed, and replaced with the exclusive end when the range closes.
/// </summary>
/// <remarks>
/// <para>
/// <b>HTTP and an explicit switch, both.</b> Choosing the HTTP transport is not consent to record.
/// A Cowork stdio child against the same store a deployed HTTP instance already writes would
/// double every volume (ADR-0016). The loop returns immediately unless the transport is HTTP
/// and <see cref="MarketDataOptions.RecordTape"/> is on.
/// </para>
/// <para>
/// <b>No captive dependency.</b> <c>IProjectXApiClient</c> is registered scoped; this service is a
/// singleton. Every venue or store operation opens a scope through
/// <see cref="IServiceScopeFactory"/>. Consuming the client from the constructor is the crash
/// that only appears when credentials <i>are</i> configured.
/// </para>
/// <para>
/// <b>Events are handed off, never handled on SignalR's loop.</b> The hub raises synchronously.
/// A slow persist must not back-pressure the connection. Writes go through a bounded
/// <see cref="Channel{T}"/>; when the channel is full the drop is recorded, not discarded
/// silently. Connection transitions enqueue restore or coverage-close work on a second
/// channel; <see cref="ConnectionState.Connected"/> is not treated as listening.
/// </para>
/// <para>
/// <b><see cref="ExecuteAsync"/> catches rather than faulting the host.</b> A faulted
/// <see cref="BackgroundService.ExecuteTask"/> is what <c>Program.AnyFaulted</c> reads, and
/// would turn an ordinary stdio EOF into a crash (gh#76). A refused subscribe is logged
/// and leaves the coverage range closed. A store fault after a confirmed subscribe drops
/// that subscription instead of claiming the venue refused it (gh#376). Neither faults the host.
/// </para>
/// <para>
/// <b>Re-subscribe on every transition into <see cref="ConnectionState.Connected"/>.</b>
/// The intended set is held here and restored on that event, including the first connect,
/// so one path covers both. Client#87 already restores in 3.0.0; this service still defends,
/// because a missed print cannot be backfilled (gh#217, ADR-0016).
/// </para>
/// <para>
/// <b>The hub handle outlives the resolve scope, as it did on gh#216.</b> The scoped
/// resolve is only how the singleton finds the client. Events and re-subscribe have to
/// target the instance that actually reconnected; a new scope per <c>Connected</c> would
/// be a different client if the registration is scoped, and would miss the transition.
/// </para>
/// <para>
/// <b>Live tape health.</b> This service writes <see cref="TapeAvailabilityHolder"/> as the
/// hub drops and restores. That holder is the opposite of <see cref="StoreAvailabilityHolder"/>:
/// it is mutable and read at the point of use, because a tape's subscriptions change mid-session.
/// This card does not pick the front month by volume — that is gh#219.
/// </para>
/// </remarks>
public sealed class TradeTapeRecorder : BackgroundService
{
    /// <summary>How many prints the bounded channel holds before a drop is recorded.</summary>
    public const int DefaultChannelCapacity = 4_096;

    private readonly IServiceScopeFactory _scopes;
    private readonly MarketDataOptions _market;
    private readonly McpOptions _mcp;
    private readonly InstrumentRegistry _registry;
    private readonly TimeProvider _clock;
    private readonly ILogger<TradeTapeRecorder> _logger;
    private readonly TapeAvailabilityHolder _tape;
    private readonly Channel<PendingPrint> _channel;
    private readonly Channel<LifecycleWork> _lifecycle;
    private readonly Dictionary<string, Attribution> _attribution = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Venue, string Instrument, string Contract), long> _sequences = [];

    /// <summary>
    /// The <c>TapeCoverage</c> state machine. This service owns the hub and the print pipeline and
    /// calls the ledger; the ledger owns the open ranges, the queued closes, the print-suppression
    /// boundary and the store gate, and takes no hub (gh#390).
    /// </summary>
    private readonly TapeCoverageLedger _ledger;

    private IProjectXWebSocketClient? _hub;
    private long _recorded;
    private long _dropped;

    /// <summary>Creates the recorder with the default channel capacity.</summary>
    [ActivatorUtilitiesConstructor]
    public TradeTapeRecorder(
        IServiceScopeFactory scopes,
        IOptions<MarketDataOptions> market,
        IOptions<McpOptions> mcp,
        InstrumentRegistry registry,
        TimeProvider clock,
        ILogger<TradeTapeRecorder> logger,
        TapeAvailabilityHolder tape)
        : this(scopes, market, mcp, registry, clock, logger, tape, DefaultChannelCapacity)
    {
    }

    /// <summary>Creates the recorder.</summary>
    /// <param name="scopes">Per-operation scopes — the client and the store are both scoped.</param>
    /// <param name="market">The served instruments and the <c>RecordTape</c> switch.</param>
    /// <param name="mcp">The transport. Recording starts only under HTTP.</param>
    /// <param name="registry">The configured instruments.</param>
    /// <param name="clock">The clock. Receipt time is taken here, not at persist.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="tape">Live subscription health, written from this lifecycle.</param>
    /// <param name="channelCapacity">
    /// How many prints may wait. Tests pass 1 so a drop is reachable without a live tape.
    /// </param>
    public TradeTapeRecorder(
        IServiceScopeFactory scopes,
        IOptions<MarketDataOptions> market,
        IOptions<McpOptions> mcp,
        InstrumentRegistry registry,
        TimeProvider clock,
        ILogger<TradeTapeRecorder> logger,
        TapeAvailabilityHolder tape,
        int channelCapacity)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(market);
        ArgumentNullException.ThrowIfNull(mcp);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(tape);
        ArgumentOutOfRangeException.ThrowIfLessThan(channelCapacity, 1);

        _scopes = scopes;
        _market = market.Value;
        _mcp = mcp.Value;
        _registry = registry;
        _clock = clock;
        _logger = logger;
        _tape = tape;
        _ledger = new TapeCoverageLedger(scopes, clock);
        _channel = Channel.CreateBounded<PendingPrint>(new BoundedChannelOptions(channelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
        _lifecycle = Channel.CreateUnbounded<LifecycleWork>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    }

    /// <summary>Prints that landed in <c>Trades</c> during this process.</summary>
    public long RecordedPrints => Interlocked.Read(ref _recorded);

    /// <summary>Prints the bounded channel refused because it was full.</summary>
    public long DroppedPrints => Interlocked.Read(ref _dropped);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (_mcp.Transport != McpTransport.Http || !_market.RecordTape)
            {
                _tape.Set(_mcp.Transport != McpTransport.Http
                    ? TapeAvailability.NeverStartedBecauseStdio()
                    : TapeAvailability.NeverStartedBecauseSwitchOff());
                _logger.LogInformation(
                    "Tape recording is off (transport {Transport}, RecordTape {RecordTape}).",
                    _mcp.Transport,
                    _market.RecordTape);
                return;
            }

            bool drain = false;
            using (IServiceScope scope = _scopes.CreateScope())
            {
                IProjectXWebSocketClient? resolved = scope.ServiceProvider.GetService<IProjectXWebSocketClient>();
                if (resolved is null)
                {
                    _tape.Set(TapeAvailability.NeverStartedBecauseNoVenueClient());
                    _logger.LogWarning(
                        "RecordTape is on but the venue client is not registered. Set ProjectX credentials "
                        + "and a data tier, then restart. Nothing will be recorded until then.");
                    return;
                }

                _hub = resolved;

                IMarketDataGateway gateway = scope.ServiceProvider.GetRequiredService<IMarketDataGateway>();

                try
                {
                    foreach (InstrumentId instrument in _registry.Instruments)
                    {
                        IReadOnlyList<VenueContract> contracts = await gateway
                            .ResolveContractsAsync(instrument, stoppingToken)
                            .ConfigureAwait(false);

                        if (contracts.Count == 0)
                        {
                            _logger.LogWarning(
                                "The venue returned no contracts for {Instrument}, so it will not be recorded. "
                                + "If this instrument is listed, check ProjectX__DataTier.",
                                instrument.Symbol);
                            continue;
                        }

                        VenueContract front = contracts[0];
                        _attribution[front.ContractId] = new Attribution(gateway.VenueId, instrument.Symbol);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger.LogError(
                        exception,
                        "The trade-tape recorder could not finish resolving every contract. "
                        + "Contracts that did resolve will still be restored on Connected.");
                }

                // Crash leftovers of this process's own instruments only, and so after the
                // resolve above: the discard set is what this start is about to subscribe.
                // A stdio or switch-off start still serves tools against the same store and
                // must not delete a live HTTP listen (gh#378); a second recorder must not
                // delete a listen it does not own (gh#382).
                await _ledger
                    .DiscardAbandonedOpenRangesAsync(
                        gateway.VenueId, InstrumentsResolvedAt(gateway.VenueId), stoppingToken)
                    .ConfigureAwait(false);

                // Hook BEFORE the first await that can yield after connect, so a print cannot
                // land between Subscribe returning and the handler being attached, and so the
                // first Connected uses the same restore path as a reconnect.
                _hub.TradeUpdateReceived += OnTrade;
                _hub.ConnectionStatusChanged += OnConnectionStatusChanged;
                try
                {
                    await _hub.ConnectMarketHubAsync(stoppingToken).ConfigureAwait(false);
                    drain = true;
                }
                catch
                {
                    Unhook();
                    _channel.Writer.TryComplete();
                    _lifecycle.Writer.TryComplete();
                    throw;
                }
            }

            if (drain)
            {
                try
                {
                    await Task.WhenAll(DrainAsync(stoppingToken), ProcessLifecycleAsync(stoppingToken))
                        .ConfigureAwait(false);
                }
                finally
                {
                    _ledger.CloseOpenRangesAt(_clock.GetUtcNow());
                    try
                    {
                        await _ledger.PersistPendingClosesAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        _logger.LogError(exception, "The trade-tape recorder could not close the last coverage range.");
                    }

                    Unhook();
                    _channel.Writer.TryComplete();
                    _lifecycle.Writer.TryComplete();
                    _tape.Set(TapeAvailability.Stopped());
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown. Not a fault.
            if (_tape.Value.Reason != TapeUnavailableReason.NeverStarted)
            {
                _tape.Set(TapeAvailability.Stopped());
            }
        }
        catch (Exception exception)
        {
            _tape.Set(TapeAvailability.Stopped());
            _logger.LogError(
                exception,
                "The trade-tape recorder stopped after a fault. Prints will not be recorded until "
                + "the process restarts.");
        }
    }

    private void OnConnectionStatusChanged(object? sender, ConnectionStatusChange change)
    {
        IProjectXWebSocketClient? hub = _hub;
        if (hub is null)
        {
            return;
        }

        // The nupkg XML says this event fires for either hub. User hub is out of scope;
        // ignore a transition that is not the market hub we opened.
        if (hub.MarketHubState != change.CurrentState)
        {
            return;
        }

        if (change.CurrentState == ConnectionState.Connected
            && change.PreviousState != ConnectionState.Connected)
        {
            // Connected is not listening. Tools must refuse until restore completes.
            _tape.Set(TapeAvailability.ConnectedButNotSubscribed());
            _lifecycle.Writer.TryWrite(LifecycleWork.RestoreSubscriptions);
            return;
        }

        if (change.PreviousState == ConnectionState.Connected
            && change.CurrentState != ConnectionState.Connected)
        {
            _tape.Set(TapeAvailability.Reconnecting());
            _ledger.CloseOpenRangesAt(_clock.GetUtcNow());
            _lifecycle.Writer.TryWrite(LifecycleWork.PersistCloses);
        }
    }

    private void OnTrade(object? sender, TradeUpdate update)
    {
        PendingPrint pending = new(update, _clock.GetUtcNow());
        if (_channel.Writer.TryWrite(pending))
        {
            return;
        }

        Interlocked.Increment(ref _dropped);
        _logger.LogWarning(
            "The tape channel is full; a print on {Contract} at {TradeTime} was dropped.",
            update.ContractId,
            update.Timestamp);
    }

    private async Task ProcessLifecycleAsync(CancellationToken stoppingToken)
    {
        await foreach (LifecycleWork work in _lifecycle.Reader.ReadAllAsync(stoppingToken)
            .ConfigureAwait(false))
        {
            try
            {
                switch (work)
                {
                    case LifecycleWork.RestoreSubscriptions:
                        await RestoreSubscriptionsAsync(stoppingToken).ConfigureAwait(false);
                        break;
                    case LifecycleWork.PersistCloses:
                        await _ledger.PersistPendingClosesAsync(stoppingToken).ConfigureAwait(false);
                        break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "The trade-tape recorder could not finish a connection-lifecycle step.");
            }
        }
    }

    private async Task RestoreSubscriptionsAsync(CancellationToken cancellationToken)
    {
        IProjectXWebSocketClient? hub = _hub;
        if (hub is null)
        {
            return;
        }

        int restored = 0;
        foreach ((string contractId, Attribution attribution) in _attribution)
        {
            bool subscribed = false;

            // Recorded before the call, not after it. The venue can print as soon as it accepts the
            // subscribe, so a print queued while the RPC is still in flight belongs to this listen
            // and must not be stored unless this listen's open reaches the store (gh#376). The
            // coverage range still starts at the confirm, so nothing claims the in-flight window.
            DateTimeOffset attempt = _clock.GetUtcNow();
            DateTimeOffset start = attempt;
            _ledger.RememberSubscribeAttempt(contractId, attempt);
            try
            {
                await hub.SubscribeToTradeUpdatesAsync(contractId, cancellationToken)
                    .ConfigureAwait(false);
                subscribed = true;
                start = _clock.GetUtcNow();
                _ledger.ClaimOpenRange(contractId, attribution.Venue, attribution.Instrument, start);

                await _ledger
                    .PersistOpenRangeAsync(
                        attribution.Venue, attribution.Instrument, contractId, start, cancellationToken)
                    .ConfigureAwait(false);

                _tape.Set(attribution.Instrument, TapeAvailability.Listening());
                restored++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (subscribed)
            {
                // The venue accepted the subscribe. A store fault after that side effect is
                // not "the venue refused" (R-5.7). Drop the subscription so prints cannot
                // land without a ledger row. A hub drop between the assignment and here has
                // already snapshotted this listen into the ledger's queued closes; discard that,
                // because a listen that never reached the store is a hole, not a range (gh#376).
                _logger.LogError(
                    exception,
                    "The trade-tape recorder subscribed {Contract} but could not persist the open "
                    + "coverage range. The venue subscription is being dropped.",
                    contractId);

                try
                {
                    await hub.UnsubscribeFromTradeUpdatesAsync(contractId, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception unsubscribeException)
                {
                    _logger.LogError(
                        unsubscribeException,
                        "The trade-tape recorder could not drop {Contract} after a failed open persist. "
                        + "The store outcome is unknown; this contract is not listening.",
                        contractId);
                }

                _ledger.DiscardFailedOpen(contractId, start);

                _tape.Set(attribution.Instrument, TapeAvailability.ConnectedButNotSubscribed());
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "The trade-tape recorder could not re-subscribe {Contract}. That contract is not listening.",
                    contractId);
                _tape.Set(attribution.Instrument, TapeAvailability.ConnectedButNotSubscribed());
                _ledger.CloseOpenRangeAt(contractId, _clock.GetUtcNow());

                await _ledger.PersistPendingClosesAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        if (restored > 0)
        {
            _logger.LogInformation(
                "Restored trade subscriptions for {Count} contract(s). Connected is not listening.",
                restored);
        }
    }

    private async Task DrainAsync(CancellationToken stoppingToken)
    {
        await foreach (PendingPrint pending in _channel.Reader.ReadAllAsync(stoppingToken)
            .ConfigureAwait(false))
        {
            try
            {
                await PersistAsync(pending, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "A print on {Contract} could not be stored.",
                    pending.Update.ContractId);
            }
        }
    }

    private async Task PersistAsync(PendingPrint pending, CancellationToken cancellationToken)
    {
        TradeUpdate update = pending.Update;
        if (string.IsNullOrWhiteSpace(update.ContractId))
        {
            _logger.LogWarning("A print arrived with no contract id and was not stored.");
            return;
        }

        if (!_attribution.TryGetValue(update.ContractId, out Attribution attribution))
        {
            _logger.LogWarning(
                "A print on {Contract} is not one this recorder subscribed to.",
                update.ContractId);
            return;
        }

        // The ledger's gate, not a second one: a print write and a coverage write must not race
        // the same store, and the suppression answer must be read under it.
        using (await _ledger.EnterStoreAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_ledger.ShouldSuppressPrint(update.ContractId, pending.ReceivedAt))
            {
                _logger.LogWarning(
                    "A print on {Contract} arrived without a persisted coverage open and was not stored.",
                    update.ContractId);
                return;
            }

            using IServiceScope scope = _scopes.CreateScope();
            TopstepXDbContext database = scope.ServiceProvider.GetRequiredService<TopstepXDbContext>();

            database.Trades.Add(new TradeRecord
            {
                Venue = attribution.Venue,
                Instrument = attribution.Instrument,
                ContractId = update.ContractId,
                TradeTimeUtc = ProjectXMapping.ToUtc(update.Timestamp),
                Sequence = NextSequence(database, attribution, update.ContractId),
                Price = update.Price,
                Size = decimal.ToInt64(decimal.Truncate(update.Volume)),
                Direction = ProjectXMapping.ToTradeDirection(update.Type),
                RecordedAt = pending.ReceivedAt,
            });

            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _recorded);
        }
    }

    /// <summary>
    /// The instruments this start resolved a front contract for at <paramref name="venue"/> — the
    /// scope of the ledger's abandoned-row discard, and the reason that discard is not store-wide
    /// (gh#382).
    /// </summary>
    private List<string> InstrumentsResolvedAt(string venue) =>
        [.. _attribution.Values
            .Where(attribution => string.Equals(attribution.Venue, venue, StringComparison.Ordinal))
            .Select(attribution => attribution.Instrument)];

    private long NextSequence(TopstepXDbContext database, Attribution attribution, string contractId)
    {
        (string Venue, string Instrument, string Contract) key =
            (attribution.Venue, attribution.Instrument, contractId);

        if (!_sequences.TryGetValue(key, out long current))
        {
            // Max() over an empty set throws; DefaultIfEmpty does not translate on every
            // provider. A nullable Max is empty-safe on in-memory and on Postgres.
            current = database.Trades
                .Where(trade =>
                    trade.Venue == attribution.Venue
                    && trade.Instrument == attribution.Instrument
                    && trade.ContractId == contractId)
                .Select(trade => (long?)trade.Sequence)
                .Max() ?? 0;
        }

        long next = current + 1;
        _sequences[key] = next;
        return next;
    }

    private void Unhook()
    {
        if (_hub is null)
        {
            return;
        }

        _hub.TradeUpdateReceived -= OnTrade;
        _hub.ConnectionStatusChanged -= OnConnectionStatusChanged;
    }

    private readonly record struct PendingPrint(TradeUpdate Update, DateTimeOffset ReceivedAt);

    private readonly record struct Attribution(string Venue, string Instrument);

    private enum LifecycleWork
    {
        RestoreSubscriptions,
        PersistCloses,
    }
}
