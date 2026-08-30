using MarqSpec.Client.ProjectX.Api.Models;
using MarqSpec.Client.ProjectX.WebSocket;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// A market-hub double that records subscribe calls and raises prints on demand.
/// </summary>
/// <remarks>
/// The published 3.0.0 nupkg ships no fake. This is the second-seam stand-in ADR-0016 describes:
/// it counts subscriptions, not REST bar requests. <see cref="CountingGateway"/> stays the
/// request/response meter.
/// <para>
/// A test can force disconnect → <see cref="ConnectionState.Connected"/> and watch whether
/// subscribe ran again. Disconnecting drops the server-side set (prints stop) the way a real
/// SignalR reconnect loses the subscription; <see cref="TransitionsIntoConnected"/> is the
/// proof the transition ran, not an inspection of the recorder.
/// </para>
/// </remarks>
public sealed class FakeMarketHub : IProjectXWebSocketClient
{
    public int MarketConnects { get; private set; }

    public int UserConnects { get; private set; }

    public int PriceSubscriptions { get; private set; }

    public int OrderBookSubscriptions { get; private set; }

    public List<string> TradeSubscriptions { get; } = [];

    public Exception? ConnectThrows { get; set; }

    /// <summary>Thrown on the second and later <see cref="SubscribeToTradeUpdatesAsync"/> call.</summary>
    public Exception? SubscribeThrowsAfterFirst { get; set; }

    public int SubscribeAttempts { get; private set; }

    /// <summary>How many times the market hub transitioned into <see cref="ConnectionState.Connected"/>.</summary>
    public int TransitionsIntoConnected { get; private set; }

    public ConnectionState MarketHubState { get; set; } = ConnectionState.Disconnected;

    public ConnectionState UserHubState { get; set; } = ConnectionState.Disconnected;

    public MarketHubSubscriptions MarketSubscriptions { get; } = new();

    public UserHubSubscriptions UserSubscriptions { get; } = new();

    public event EventHandler<ConnectionStatusChange>? ConnectionStatusChanged;

#pragma warning disable CS0067 // The interface requires these; this double never raises them.
    public event EventHandler<PriceUpdate>? PriceUpdateReceived;

    public event EventHandler<OrderBookUpdate>? OrderBookUpdateReceived;

    public event EventHandler<TradeUpdate>? TradeUpdateReceived;

    public event EventHandler<AccountUpdate>? AccountUpdateReceived;

    public event EventHandler<OrderUpdate>? OrderUpdateReceived;

    public event EventHandler<PositionUpdate>? PositionUpdateReceived;

    public event EventHandler<TradeNotification>? TradeNotificationReceived;

    public event EventHandler<WebSocketMessageFailedEventArgs>? MessageSendFailed;
#pragma warning restore CS0067

    public Task ConnectMarketHubAsync(CancellationToken cancellationToken = default)
    {
        if (ConnectThrows is not null)
        {
            throw ConnectThrows;
        }

        MarketConnects++;
        EnterMarketState(ConnectionState.Connected);
        return Task.CompletedTask;
    }

    public Task ConnectUserHubAsync(CancellationToken cancellationToken = default)
    {
        UserConnects++;
        UserHubState = ConnectionState.Connected;
        return Task.CompletedTask;
    }

    public Task DisconnectMarketHubAsync(CancellationToken cancellationToken = default)
    {
        EnterMarketState(ConnectionState.Disconnected);
        return Task.CompletedTask;
    }

    public Task DisconnectUserHubAsync(CancellationToken cancellationToken = default)
    {
        UserHubState = ConnectionState.Disconnected;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Drops the market hub the way a transport fault does: server-side trade
    /// subscriptions are gone, and <see cref="ConnectionStatusChanged"/> reports it.
    /// </summary>
    public void SimulateMarketDisconnect()
    {
        TradeSubscriptions.Clear();
        EnterMarketState(ConnectionState.Disconnected);
    }

    /// <summary>
    /// Completes a reconnect: <see cref="ConnectionState.Connected"/> with no
    /// subscriptions. The recorder must subscribe again or prints stay silent.
    /// </summary>
    public void SimulateMarketReconnect() => EnterMarketState(ConnectionState.Connected);

    public Task SubscribeToPriceUpdatesAsync(string contractId, CancellationToken cancellationToken = default)
    {
        PriceSubscriptions++;
        return Task.CompletedTask;
    }

    public Task UnsubscribeFromPriceUpdatesAsync(string contractId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task SubscribeToOrderBookUpdatesAsync(string contractId, CancellationToken cancellationToken = default)
    {
        OrderBookSubscriptions++;
        return Task.CompletedTask;
    }

    public Task UnsubscribeFromOrderBookUpdatesAsync(
        string contractId,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task SubscribeToTradeUpdatesAsync(string contractId, CancellationToken cancellationToken = default)
    {
        SubscribeAttempts++;
        if (SubscribeAttempts > 1 && SubscribeThrowsAfterFirst is not null)
        {
            throw SubscribeThrowsAfterFirst;
        }

        TradeSubscriptions.Add(contractId);
        return Task.CompletedTask;
    }

    public Task UnsubscribeFromTradeUpdatesAsync(string contractId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task SubscribeToAccountUpdatesAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task UnsubscribeFromAccountUpdatesAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task SubscribeToOrderUpdatesAsync(int accountId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task UnsubscribeFromOrderUpdatesAsync(int accountId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task SubscribeToPositionUpdatesAsync(int accountId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task UnsubscribeFromPositionUpdatesAsync(int accountId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task SubscribeToTradeNotificationsAsync(int accountId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task UnsubscribeFromTradeNotificationsAsync(int accountId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Prints that passed the live-subscription check and were raised to listeners.</summary>
    public int RaisedToListeners { get; private set; }

    public void Raise(TradeUpdate update)
    {
        if (string.IsNullOrWhiteSpace(update.ContractId)
            || !TradeSubscriptions.Contains(update.ContractId))
        {
            return;
        }

        RaisedToListeners++;
        TradeUpdateReceived?.Invoke(this, update);
    }

    private void EnterMarketState(ConnectionState current)
    {
        ConnectionState previous = MarketHubState;
        if (previous == current)
        {
            return;
        }

        MarketHubState = current;
        if (current == ConnectionState.Connected)
        {
            TransitionsIntoConnected++;
        }

        ConnectionStatusChanged?.Invoke(this, new ConnectionStatusChange
        {
            PreviousState = previous,
            CurrentState = current,
            Timestamp = DateTime.UtcNow,
        });
    }
}
