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
/// </remarks>
public sealed class FakeMarketHub : IProjectXWebSocketClient
{
    public int MarketConnects { get; private set; }

    public int UserConnects { get; private set; }

    public int PriceSubscriptions { get; private set; }

    public int OrderBookSubscriptions { get; private set; }

    public List<string> TradeSubscriptions { get; } = [];

    public Exception? ConnectThrows { get; set; }

    public ConnectionState MarketHubState { get; set; } = ConnectionState.Disconnected;

    public ConnectionState UserHubState { get; set; } = ConnectionState.Disconnected;

    public MarketHubSubscriptions MarketSubscriptions { get; } = new();

    public UserHubSubscriptions UserSubscriptions { get; } = new();

#pragma warning disable CS0067 // The interface requires these; this double never raises them.
    public event EventHandler<ConnectionStatusChange>? ConnectionStatusChanged;

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
        MarketHubState = ConnectionState.Connected;
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
        MarketHubState = ConnectionState.Disconnected;
        return Task.CompletedTask;
    }

    public Task DisconnectUserHubAsync(CancellationToken cancellationToken = default)
    {
        UserHubState = ConnectionState.Disconnected;
        return Task.CompletedTask;
    }

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

    public void Raise(TradeUpdate update) => TradeUpdateReceived?.Invoke(this, update);
}
