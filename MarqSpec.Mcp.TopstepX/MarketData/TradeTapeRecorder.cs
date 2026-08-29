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
/// configured instrument's front contract, and write prints to <c>Trades</c>.
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
/// silently.
/// </para>
/// <para>
/// <b><see cref="ExecuteAsync"/> catches rather than faulting the host.</b> A faulted
/// <see cref="BackgroundService.ExecuteTask"/> is what <c>Program.AnyFaulted</c> reads, and
/// would turn an ordinary stdio EOF into a crash (gh#76).
/// </para>
/// <para>
/// This card does not aggregate, write <c>TapeCoverage</c>, re-subscribe after reconnect, or
/// pick the front month by volume. Those are gh#217 / #218 / #219.
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
    private readonly Channel<PendingPrint> _channel;
    private readonly Dictionary<string, Attribution> _attribution = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Venue, string Instrument, string Contract), long> _sequences = [];

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
        ILogger<TradeTapeRecorder> logger)
        : this(scopes, market, mcp, registry, clock, logger, DefaultChannelCapacity)
    {
    }

    /// <summary>Creates the recorder.</summary>
    /// <param name="scopes">Per-operation scopes — the client and the store are both scoped.</param>
    /// <param name="market">The served instruments and the <c>RecordTape</c> switch.</param>
    /// <param name="mcp">The transport. Recording starts only under HTTP.</param>
    /// <param name="registry">The configured instruments.</param>
    /// <param name="clock">The clock. Receipt time is taken here, not at persist.</param>
    /// <param name="logger">The logger.</param>
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
        int channelCapacity)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(market);
        ArgumentNullException.ThrowIfNull(mcp);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfLessThan(channelCapacity, 1);

        _scopes = scopes;
        _market = market.Value;
        _mcp = mcp.Value;
        _registry = registry;
        _clock = clock;
        _logger = logger;
        _channel = Channel.CreateBounded<PendingPrint>(new BoundedChannelOptions(channelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
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
                _logger.LogInformation(
                    "Tape recording is off (transport {Transport}, RecordTape {RecordTape}).",
                    _mcp.Transport,
                    _market.RecordTape);
                return;
            }

            IProjectXWebSocketClient hub;
            bool drain = false;
            using (IServiceScope scope = _scopes.CreateScope())
            {
                IProjectXWebSocketClient? resolved = scope.ServiceProvider.GetService<IProjectXWebSocketClient>();
                if (resolved is null)
                {
                    _logger.LogWarning(
                        "RecordTape is on but the venue client is not registered. Set ProjectX credentials "
                        + "and a data tier, then restart. Nothing will be recorded until then.");
                    return;
                }

                hub = resolved;

                IMarketDataGateway gateway = scope.ServiceProvider.GetRequiredService<IMarketDataGateway>();

                // Hook BEFORE the first await that can yield, so a print cannot land
                // between Subscribe returning and the handler being attached.
                hub.TradeUpdateReceived += OnTrade;
                try
                {
                    await hub.ConnectMarketHubAsync(stoppingToken).ConfigureAwait(false);

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
                            await hub.SubscribeToTradeUpdatesAsync(front.ContractId, stoppingToken)
                                .ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        // A later instrument's resolve/subscribe must not skip drain: the first
                        // subscription is already live, and OnTrade writes a channel nobody would
                        // read. Catch here so ExecuteTask stays clean (Program.AnyFaulted).
                        _logger.LogError(
                            exception,
                            "The trade-tape recorder could not finish every subscribe. Prints on "
                            + "contracts that did subscribe will still be recorded.");
                    }

                    drain = true;
                }
                catch
                {
                    hub.TradeUpdateReceived -= OnTrade;
                    _channel.Writer.TryComplete();
                    throw;
                }
            }

            if (drain)
            {
                try
                {
                    await DrainAsync(stoppingToken).ConfigureAwait(false);
                }
                finally
                {
                    hub.TradeUpdateReceived -= OnTrade;
                    _channel.Writer.TryComplete();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown. Not a fault.
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "The trade-tape recorder stopped after a fault. Prints will not be recorded until "
                + "the process restarts.");
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

    private readonly record struct PendingPrint(TradeUpdate Update, DateTimeOffset ReceivedAt);

    private readonly record struct Attribution(string Venue, string Instrument);
}
