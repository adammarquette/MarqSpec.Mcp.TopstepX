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

    /// <summary>
    /// Contracts this process resolved but does not hold the claim for — refused at start, or
    /// stood down from later. Re-attempted on every renewal tick, because a refusal that never
    /// retried would turn a rolling redeploy into nothing recording at all, and a tape gap has no
    /// backfill (gh#404).
    /// </summary>
    private readonly Dictionary<string, Attribution> _refused = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Venue, string Instrument, string Contract), long> _sequences = [];

    /// <summary>
    /// The <c>TapeCoverage</c> state machine. This service owns the hub and the print pipeline and
    /// calls the ledger; the ledger owns the open ranges, the queued closes, the print-suppression
    /// boundary and the store gate, and takes no hub (gh#390).
    /// </summary>
    private readonly TapeCoverageLedger _ledger;

    /// <summary>
    /// This process's claim on the instruments it records. Nothing but this stops two recorders
    /// subscribing to one tape, which doubles every volume (ADR-0016, gh#404).
    /// </summary>
    private readonly TapeLease _lease;

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
        : this(
            scopes,
            market,
            mcp,
            registry,
            clock,
            logger,
            tape,
            DefaultChannelCapacity,
            TapeLease.DefaultTimeToLive)
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
    /// <param name="leaseTimeToLive">
    /// How long this process's tape claim stands unrenewed. Tests pass a short one so a takeover
    /// is reachable without waiting out the real value.
    /// </param>
    public TradeTapeRecorder(
        IServiceScopeFactory scopes,
        IOptions<MarketDataOptions> market,
        IOptions<McpOptions> mcp,
        InstrumentRegistry registry,
        TimeProvider clock,
        ILogger<TradeTapeRecorder> logger,
        TapeAvailabilityHolder tape,
        int channelCapacity,
        TimeSpan leaseTimeToLive)
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
        _lease = new TapeLease(scopes, clock, leaseTimeToLive);
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

                // The claim comes before the discard, and before any subscribe. A recorder that
                // does not hold an instrument must not delete a coverage row the holder is still
                // writing under, and must not put a second subscriber on the tape (ADR-0016,
                // gh#404). Refused instruments leave _attribution here, so everything downstream
                // — the discard scope, the restore set, print attribution — narrows with it.
                TapeAvailability? refusedEverything = await ClaimInstrumentsAsync(
                    gateway.VenueId, stoppingToken).ConfigureAwait(false);
                if (refusedEverything is not null)
                {
                    // Refused everything, and it still stays up. Quitting here is what turns a
                    // rolling redeploy into a silent stop: the new container is refused, exits,
                    // and then the old one drains and releases its rows, leaving nothing
                    // recording — permanently, and with no backfill to repair the gap. That is a
                    // worse outcome than the double-recording this card exists to prevent, so the
                    // recorder holds its connection and retries instead (gh#404).
                    _tape.Set(refusedEverything);
                    _logger.LogWarning(
                        "Every configured instrument's tape is claimed by another recorder, so this "
                        + "start subscribed to nothing. It stays up and re-attempts every {Interval}, "
                        + "taking over as soon as a claim is released or lapses.",
                        _lease.RenewInterval);
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
                    await Task.WhenAll(
                            DrainAsync(stoppingToken),
                            ProcessLifecycleAsync(stoppingToken),
                            RenewClaimsAsync(stoppingToken))
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
        finally
        {
            // Every exit, not just the clean one: a claim this process is no longer recording
            // under would otherwise make the next start wait out the whole expiry for nothing.
            try
            {
                await _lease.ReleaseAllAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "The trade-tape recorder could not release its tape claim. The next start will "
                    + "wait out the claim's expiry before recording.");
            }
        }
    }

    /// <summary>
    /// Takes this process's claim on every instrument it resolved a front contract for, and drops
    /// the ones another recorder already holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Per instrument, because the split deployment is legal.</b> Two recorders partitioned by
    /// <c>MarketData__Instruments</c> are supported and gh#382 protects them; only the overlap that
    /// doubles volume is refused.
    /// </para>
    /// <para>
    /// <b>A claim that cannot be read refuses too.</b> An unreadable store is not an empty one, and
    /// recording on the assumption that silence means nobody is there is the doubled tape this
    /// exists to prevent.
    /// </para>
    /// </remarks>
    /// <param name="venue">The venue this start resolved its contracts through.</param>
    /// <param name="cancellationToken">The stopping token.</param>
    /// <returns>
    /// The process-wide refusal to report when every instrument was refused and there is nothing
    /// left to record, or <see langword="null"/> when this start still holds something.
    /// </returns>
    private async Task<TapeAvailability?> ClaimInstrumentsAsync(
        string venue,
        CancellationToken cancellationToken)
    {
        TapeAvailability? firstRefusal = null;

        foreach (string contractId in _attribution.Keys.ToList())
        {
            Attribution attribution = _attribution[contractId];
            TapeLeaseOutcome outcome;
            try
            {
                outcome = await _lease
                    .TryAcquireAsync(venue, attribution.Instrument, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "The trade-tape recorder could not read the tape claim for {Instrument}. It "
                    + "will not record that instrument: an unreadable claim is not a free one.",
                    attribution.Instrument);
                outcome = TapeLeaseOutcome.Unreadable();
            }

            if (outcome.IsGranted)
            {
                _refused.Remove(contractId);
                _tape.ClearUnclaimed(attribution.Instrument);
                continue;
            }

            _attribution.Remove(contractId);
            _refused[contractId] = attribution;

            TapeAvailability refusal = outcome.HolderId is null
                ? TapeAvailability.NeverStartedBecauseTheClaimIsUnreadable()
                : TapeAvailability.HeldByAnotherRecorder(
                    outcome.HolderId, outcome.HolderExpiresAt!.Value);
            firstRefusal ??= refusal;

            _tape.SetUnclaimed(attribution.Instrument, refusal);
            _logger.LogWarning(
                "{Instrument} is already claimed by another recorder ({Holder}), so this one will "
                + "not subscribe to it. Two recorders on one instrument double every volume.",
                attribution.Instrument,
                outcome.HolderId ?? "owner unknown");
        }

        return _attribution.Count == 0 ? firstRefusal : null;
    }

    /// <summary>
    /// Renews this process's claims while it records, and stands down from any it has lost.
    /// </summary>
    /// <remarks>
    /// A holder paused past its expiry — a long stall, a store outage — can be taken over while it
    /// is still subscribed. This is where it finds out. Standing down, rather than the expiry
    /// itself, is what keeps a reclaim from producing the two writers the claim exists to refuse.
    /// A renewal the store <i>refused</i> is not a loss: it is retried on the next interval, and
    /// the interval is a third of the expiry so two in a row can fail before anything lapses.
    /// </remarks>
    /// <param name="stoppingToken">The stopping token.</param>
    /// <returns>A task that runs until the host stops.</returns>
    private async Task RenewClaimsAsync(CancellationToken stoppingToken)
    {
        while (true)
        {
            await Task.Delay(_lease.RenewInterval, _clock, stoppingToken).ConfigureAwait(false);

            await RenewHeldClaimsAsync(stoppingToken).ConfigureAwait(false);
            await RetryRefusedClaimsAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task RenewHeldClaimsAsync(CancellationToken stoppingToken)
    {
        foreach ((string venue, string instrument) in _lease.Held)
        {
            // Read before renewing: a renewal that loses the claim clears it, and this is the
            // instant after which this process could no longer prove it was the holder.
            DateTimeOffset? term = _lease.ExpiryOf(venue, instrument);

            TapeLeaseRenewal renewal;
            try
            {
                renewal = await _lease.TryRenewAsync(venue, instrument, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                if (term is not null && _clock.GetUtcNow() >= term)
                {
                    // The term ran out and the store will not say who holds it now. Another start
                    // may already have taken it, so "probably still mine" is exactly the
                    // assumption that produces two writers. Give it up and re-attempt.
                    _logger.LogError(
                        exception,
                        "The trade-tape recorder could not renew its tape claim on {Instrument} "
                        + "before it expired, so it is giving the instrument up rather than "
                        + "recording without a claim. It will re-attempt.",
                        instrument);
                    _lease.Forfeit(venue, instrument);
                    await StandDownAsync(venue, instrument, term.Value, stoppingToken)
                        .ConfigureAwait(false);
                    continue;
                }

                _logger.LogError(
                    exception,
                    "The trade-tape recorder could not renew its tape claim on {Instrument}. "
                    + "It is still the holder until the claim expires, and will try again.",
                    instrument);
                continue;
            }

            if (!renewal.Kept)
            {
                // Close at the handover, never at the notice. The window between them was written
                // by two processes, and a range ending at notice would report it as ordinary
                // covered volume — the doubled delta ADR-0016 exists to prevent.
                DateTimeOffset closeAt = renewal.ReclaimedAt ?? term ?? _clock.GetUtcNow();
                await StandDownAsync(venue, instrument, closeAt, stoppingToken)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Re-attempts every instrument this process resolved but does not hold — refused at start, or
    /// stood down from since.
    /// </summary>
    /// <remarks>
    /// This is what keeps a refusal from being permanent. A redeploy whose old container has not
    /// drained yet, a restart seconds after a crash, an operator stopping the duplicate: each is a
    /// claim that becomes takeable within one term, and nothing else would ever take it. The
    /// recorder does not try to recognise its own predecessor to shortcut that wait — see
    /// <see cref="TapeLease.OwnerId"/> for why no identity is safe enough to key it on.
    /// </remarks>
    /// <param name="stoppingToken">The stopping token.</param>
    /// <returns>A task that completes when every refused instrument has been re-attempted.</returns>
    private async Task RetryRefusedClaimsAsync(CancellationToken stoppingToken)
    {
        if (_refused.Count == 0)
        {
            return;
        }

        foreach ((string contractId, Attribution attribution) in _refused.ToList())
        {
            TapeLeaseOutcome outcome;
            try
            {
                outcome = await _lease
                    .TryAcquireAsync(attribution.Venue, attribution.Instrument, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "The trade-tape recorder could not re-attempt the tape claim for {Instrument}.",
                    attribution.Instrument);
                continue;
            }

            if (!outcome.IsGranted)
            {
                continue;
            }

            _refused.Remove(contractId);
            _attribution[contractId] = attribution;
            _tape.ClearUnclaimed(attribution.Instrument);

            _logger.LogInformation(
                "The trade-tape recorder took the tape claim for {Instrument} and is starting to "
                + "record it.",
                attribution.Instrument);

            // The claim is new, so any still-open row under it is a leftover this process now
            // supersedes — the same discard a start does, scoped the same way (gh#382).
            try
            {
                await _ledger
                    .DiscardAbandonedOpenRangesAsync(
                        attribution.Venue, [attribution.Instrument], stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "The trade-tape recorder could not discard abandoned coverage for {Instrument} "
                    + "after taking its claim.",
                    attribution.Instrument);
            }

            IProjectXWebSocketClient? hub = _hub;
            if (hub is not null && hub.MarketHubState == ConnectionState.Connected)
            {
                await SubscribeOneAsync(hub, contractId, attribution, stoppingToken)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Gives up an instrument whose claim this process no longer holds: drop the subscription,
    /// close its coverage range, and say so at the point of use.
    /// </summary>
    /// <param name="venue">The venue.</param>
    /// <param name="instrument">The instrument this process is no longer the holder of.</param>
    /// <param name="closeAt">
    /// When this process stopped being the holder — the replacement's acquisition, or this
    /// process's own expiry. Never the instant it found out: a range ending there would claim a
    /// window the replacement also claims.
    /// </param>
    /// <param name="cancellationToken">The stopping token.</param>
    /// <returns>A task that completes when the subscription is dropped and the range closed.</returns>
    private async Task StandDownAsync(
        string venue,
        string instrument,
        DateTimeOffset closeAt,
        CancellationToken cancellationToken)
    {
        _logger.LogError(
            "Another recorder took this one's tape claim on {Instrument}. Dropping the subscription "
            + "rather than leaving two writers on one tape.",
            instrument);

        IProjectXWebSocketClient? hub = _hub;
        List<string> contracts =
            [.. _attribution
                .Where(pair =>
                    string.Equals(pair.Value.Venue, venue, StringComparison.Ordinal)
                    && string.Equals(pair.Value.Instrument, instrument, StringComparison.Ordinal))
                .Select(pair => pair.Key)];

        foreach (string contractId in contracts)
        {
            // Out of the intended set first, so a reconnect racing this cannot restore it, and
            // into the retry set, so the instrument comes back if the claim frees up again.
            _attribution.Remove(contractId);
            _refused[contractId] = new Attribution(venue, instrument);

            if (hub is not null)
            {
                try
                {
                    await hub.UnsubscribeFromTradeUpdatesAsync(contractId, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger.LogError(
                        exception,
                        "The trade-tape recorder could not drop {Contract} after losing its claim. "
                        + "Prints on it are no longer attributed and will not be stored.",
                        contractId);
                }
            }

            _ledger.CloseOpenRangeAt(contractId, closeAt);
        }

        try
        {
            await _ledger.PersistPendingClosesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "The trade-tape recorder could not close the coverage range for {Instrument} after "
                + "losing its claim. The still-open row stands until the next start discards it.",
                instrument);
        }

        _tape.SetUnclaimed(instrument, TapeAvailability.ClaimTakenOver());
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
        foreach ((string contractId, Attribution attribution) in _attribution.ToList())
        {
            if (await SubscribeOneAsync(hub, contractId, attribution, cancellationToken)
                .ConfigureAwait(false))
            {
                restored++;
            }
        }

        if (restored > 0)
        {
            _logger.LogInformation(
                "Restored trade subscriptions for {Count} contract(s). Connected is not listening.",
                restored);
        }
    }

    /// <summary>
    /// Subscribes one contract and opens its coverage range. Shared by the restore-on-connect path
    /// and by the retry that picks an instrument up after its claim frees.
    /// </summary>
    /// <param name="hub">The connected market hub.</param>
    /// <param name="contractId">The contract to subscribe.</param>
    /// <param name="attribution">Its venue and instrument.</param>
    /// <param name="cancellationToken">The stopping token.</param>
    /// <returns>Whether this contract is now listening.</returns>
    private async Task<bool> SubscribeOneAsync(
        IProjectXWebSocketClient hub,
        string contractId,
        Attribution attribution,
        CancellationToken cancellationToken)
    {
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
                return true;
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

        return false;
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

            // The fence. A renewal only reports a lost claim at the next tick, so between the
            // expiry and that tick this process would otherwise keep writing prints a replacement
            // is already writing too — and Sequence is a per-process counter, so the duplicate
            // takes a different key and lands as a second row rather than collapsing. A footprint
            // then reports doubled volume and a doubled delta as an ordinary answer, which is
            // ADR-0016's failure arriving through the mechanism meant to stop it. A print received
            // at or after this process's own term is therefore not stored (gh#404).
            if (!_lease.MayWrite(attribution.Venue, attribution.Instrument, pending.ReceivedAt))
            {
                _logger.LogWarning(
                    "A print on {Contract} was received after this recorder's tape claim expired "
                    + "and was not stored. Another process may already hold that tape.",
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
