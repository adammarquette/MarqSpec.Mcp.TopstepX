using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using MarqSpec.Client.ProjectX.Api.Models;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// <c>get_quote</c> reads the market hub the tape already uses. A missing quote refuses.
/// A bar close is never the answer (gh#351).
/// </summary>
public sealed class LiveQuoteServiceTests
{
    private const string EsContract = "CON.F.US.EP.Z26";

    private const string OtherContract = "CON.F.US.EP.H27";

    private const decimal BarCloseThatMustNotLeak = 4321.25m;

    private static readonly TimeSpan _poll = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task GetQuote_ReturnsBidAskLastAndAge_WhenTheHubDeliversAQuote()
    {
        await using Harness harness = await Harness.StartAsync(recordTape: true);
        await harness.ConnectRecorderHubAsync();

        Task<ToolPayloads.Quote> pending = harness.Service.ReadAsync("  es  ", CancellationToken.None);
        await harness.WaitUntilSubscribedAsync(EsContract);
        harness.Hub.RaiseQuoteUnfiltered(Quote(OtherContract, 1m, 2m, 3m, harness.Clock));
        harness.Hub.RaiseQuote(Quote(EsContract, 5100.25m, 5100.50m, 5100.25m, harness.Clock, volume: 42m));

        ToolPayloads.Quote quote = await pending;

        quote.Symbol.Should().Be("ES");
        quote.ContractId.Should().Be(EsContract);
        quote.Bid.Should().Be(5100.25m);
        quote.Ask.Should().Be(5100.50m);
        quote.Last.Should().Be(5100.25m);
        quote.Volume.Should().Be(42m);
        quote.BidSize.Should().BeNull();
        quote.AskSize.Should().BeNull();
        quote.AgeSeconds.Should().BeApproximately(0, 0.001);
        quote.Stale.Should().BeFalse();
        quote.HubConnected.Should().BeTrue();
        quote.Timestamp.Should().NotBeNull();
        harness.Hub.MarketConnects.Should().Be(0, "the recorder owns the hub");
    }

    [Fact]
    public async Task GetQuote_RefusesAndDoesNotSubstituteABarClose_WhenNoQuoteArrives()
    {
        await using Harness harness = await Harness.StartAsync(
            recordTape: true,
            quoteWait: TimeSpan.FromSeconds(1));
        await harness.ConnectRecorderHubAsync();

        Task<ToolPayloads.Quote> pending = harness.Service.ReadAsync("ES", CancellationToken.None);
        await harness.WaitUntilSubscribedAsync(EsContract);
        await harness.AdvanceUntilAsync(pending);

        Func<Task> wait = () => pending;
        (await wait.Should().ThrowAsync<McpException>()).Which.Message.Should()
            .Contain("bar close").And
            .Contain("ES").And
            .NotContain(BarCloseThatMustNotLeak.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task GetQuote_ReportsTheQuoteStale_WhenItIsOlderThanTheThreshold()
    {
        await using Harness harness = await Harness.StartAsync(
            recordTape: true,
            staleAfter: TimeSpan.FromSeconds(15));
        await harness.ConnectRecorderHubAsync();
        DateTime old = harness.Clock.GetUtcNow().UtcDateTime.AddSeconds(-20);
        harness.Hub.RaiseQuote(Quote(EsContract, 10m, 11m, 10.5m, old));

        ToolPayloads.Quote quote = await harness.Service.ReadAsync("ES", CancellationToken.None);

        quote.Stale.Should().BeTrue();
        quote.AgeSeconds.Should().BeApproximately(20, 0.001);
        quote.Bid.Should().Be(10m);
        quote.HubConnected.Should().BeTrue();
    }

    [Fact]
    public async Task GetQuote_ReturnsAFreshQuote_WhenTheAgeIsInsideTheThreshold()
    {
        await using Harness harness = await Harness.StartAsync(
            recordTape: true,
            staleAfter: TimeSpan.FromSeconds(15));
        await harness.ConnectRecorderHubAsync();
        harness.Hub.RaiseQuote(Quote(EsContract, 10m, 11m, 10.5m, harness.Clock));

        ToolPayloads.Quote quote = await harness.Service.ReadAsync("ES", CancellationToken.None);

        quote.Stale.Should().BeFalse();
        quote.AgeSeconds.Should().BeApproximately(0, 0.001);
    }

    [Fact]
    public async Task AnUnheldContract_IsSubscribedForTheRead_AndUnsubscribedAfterwards()
    {
        await using Harness harness = await Harness.StartAsync(recordTape: true);
        await harness.ConnectRecorderHubAsync();

        Task<ToolPayloads.Quote> pending = harness.Service.ReadUnheldContractAsync(
            OtherContract, "ES", CancellationToken.None);
        await harness.WaitUntilSubscribedAsync(OtherContract);
        harness.Hub.RaiseQuote(Quote(OtherContract, 8m, 9m, 8.5m, harness.Clock));

        ToolPayloads.Quote quote = await pending;
        await harness.WaitUntilAsync(() => !harness.Hub.PriceSubscriptionIds.Contains(OtherContract));

        quote.ContractId.Should().Be(OtherContract);
        quote.Bid.Should().Be(8m);
        harness.Hub.PriceSubscriptionIds.Should().Contain(EsContract);
        harness.Hub.PriceUnsubscribes.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task AReplacedFrontContract_IsUnsubscribed()
    {
        int calls = 0;
        await using Harness harness = await Harness.StartAsync(
            recordTape: true,
            staleAfter: TimeSpan.FromSeconds(15),
            resolve: (instrument, _) =>
            {
                if (instrument.Symbol != "ES")
                {
                    return Task.FromResult<string?>(null);
                }

                int n = Interlocked.Increment(ref calls);
                return Task.FromResult<string?>(n <= 1 ? EsContract : OtherContract);
            });
        await harness.ConnectRecorderHubAsync();
        harness.Hub.RaiseQuote(Quote(EsContract, 1m, 2m, 1.5m, harness.Clock));
        ToolPayloads.Quote first = await harness.Service.ReadAsync("ES", CancellationToken.None);
        first.ContractId.Should().Be(EsContract);

        harness.Clock.Advance(TimeSpan.FromSeconds(16));
        Task<ToolPayloads.Quote> pending = harness.Service.ReadAsync("ES", CancellationToken.None);
        await harness.WaitUntilSubscribedAsync(OtherContract);
        harness.Hub.RaiseQuote(Quote(OtherContract, 3m, 4m, 3.5m, harness.Clock));

        ToolPayloads.Quote second = await pending;

        second.ContractId.Should().Be(OtherContract);
        harness.Hub.PriceSubscriptionIds.Should().NotContain(EsContract);
        harness.Hub.PriceSubscriptionIds.Should().Contain(OtherContract);
    }

    [Fact]
    public async Task ACachedQuote_IsMarkedStale_WhenTheHubDisconnects()
    {
        await using Harness harness = await Harness.StartAsync(recordTape: true);
        await harness.ConnectRecorderHubAsync();
        harness.Hub.RaiseQuote(Quote(EsContract, 12m, 13m, 12.5m, harness.Clock));
        ToolPayloads.Quote live = await harness.Service.ReadAsync("ES", CancellationToken.None);
        live.HubConnected.Should().BeTrue();

        harness.Hub.SimulateMarketDisconnect();
        ToolPayloads.Quote stale = await harness.Service.ReadAsync("ES", CancellationToken.None);

        stale.Bid.Should().Be(12m);
        stale.HubConnected.Should().BeFalse();
        stale.Stale.Should().BeTrue();
    }

    [Fact]
    public async Task GetQuote_Refuses_WhenTheHubDropsWhileWaiting()
    {
        await using Harness harness = await Harness.StartAsync(
            recordTape: true,
            quoteWait: TimeSpan.FromSeconds(30));
        await harness.ConnectRecorderHubAsync();

        Task<ToolPayloads.Quote> pending = harness.Service.ReadAsync("ES", CancellationToken.None);
        await harness.WaitUntilSubscribedAsync(EsContract);
        harness.Hub.SimulateMarketDisconnect();

        Func<Task> wait = () => pending;
        (await wait.Should().ThrowAsync<McpException>()).Which.Message.Should()
            .Contain("not connected").And
            .Contain("bar close");
    }

    [Fact]
    public async Task GetQuote_Refuses_WhenTheHubIsDownAndNoQuoteHasArrived()
    {
        await using Harness harness = await Harness.StartAsync(recordTape: true);

        (await harness.Service.Awaiting(service => service.ReadAsync("ES", CancellationToken.None))
            .Should().ThrowAsync<McpException>()).Which.Message.Should()
            .Contain("not connected").And
            .Contain("bar close");
        harness.Hub.PriceSubscriptions.Should().Be(0);
    }

    [Fact]
    public async Task TheServiceDoesNotSubscribe_OnStdio()
    {
        await using Harness harness = await Harness.StartAsync(
            transport: McpTransport.Stdio,
            recordTape: false);

        harness.Hub.MarketConnects.Should().Be(0);
        harness.Hub.PriceSubscriptions.Should().Be(0);
        (await harness.Service.Awaiting(service => service.ReadAsync("ES", CancellationToken.None))
            .Should().ThrowAsync<McpException>()).Which.Message.Should()
            .Contain("stdio").And
            .Contain("bar close");
    }

    [Fact]
    public async Task TheServiceDoesNotSubscribe_WhenTheSwitchIsOff()
    {
        await using Harness harness = await Harness.StartAsync(serveQuotes: false, recordTape: true);

        harness.Hub.MarketConnects.Should().Be(0);
        harness.Hub.PriceSubscriptions.Should().Be(0);
        (await harness.Service.Awaiting(service => service.ReadAsync("ES", CancellationToken.None))
            .Should().ThrowAsync<McpException>()).Which.Message.Should()
            .Contain("ServeQuotes").And
            .Contain("bar close");
    }

    [Fact]
    public async Task TheServiceResubscribes_WhenTheRecorderReconnectsTheHub()
    {
        await using Harness harness = await Harness.StartAsync(recordTape: true);
        harness.Hub.MarketConnects.Should().Be(0);

        await harness.ConnectRecorderHubAsync();
        harness.Hub.PriceSubscriptionIds.Should().Contain(EsContract);

        harness.Hub.SimulateMarketDisconnect();
        await harness.WaitUntilAsync(() => !harness.Hub.PriceSubscriptionIds.Contains(EsContract));
        harness.Hub.SimulateMarketReconnect();
        await harness.WaitUntilSubscribedAsync(EsContract);

        harness.Hub.MarketConnects.Should().Be(0);
    }

    [Fact]
    public async Task TheServiceReconnectsTheHubItOwns_AfterATransportDrop()
    {
        await using Harness harness = await Harness.StartAsync(
            recordTape: false,
            allowConnectWithoutToken: true);
        await harness.WaitUntilAsync(() => harness.Hub.MarketConnects == 1);
        await harness.WaitUntilSubscribedAsync(EsContract);

        harness.Hub.SimulateMarketDisconnect();
        await harness.AdvanceUntilAsync(() => harness.Hub.MarketConnects >= 2, pushes: 8);
        await harness.WaitUntilSubscribedAsync(EsContract);

        harness.Hub.MarketConnects.Should().Be(2);
    }

    [Fact]
    public async Task TheServiceDoesNotReconnect_WhenTheHubNamesASessionConflict()
    {
        await using Harness harness = await Harness.StartAsync(
            recordTape: false,
            allowConnectWithoutToken: true);
        await harness.WaitUntilAsync(() => harness.Hub.MarketConnects == 1);

        harness.Hub.SimulateSessionConflict("InvalidSession");
        await harness.AdvanceUntilAsync(() => false, pushes: 4);

        harness.Hub.MarketConnects.Should().Be(1);
        (await harness.Service.Awaiting(service => service.ReadAsync("ES", CancellationToken.None))
            .Should().ThrowAsync<McpException>()).Which.Message.Should()
            .Contain("session conflict").And
            .Contain("loginKey").And
            .Contain("bar close");
    }

    [Fact]
    public async Task TheServiceDoesNotConnect_WhenItOwnsTheHubAndNoTokenIsFresh()
    {
        HeldSession held = new();
        await using Harness harness = await Harness.StartAsync(
            recordTape: false,
            allowConnectWithoutToken: false,
            held: held);

        await harness.AdvanceUntilAsync(() => false, pushes: 3);

        harness.Hub.MarketConnects.Should().Be(0);
        (await harness.Service.Awaiting(service => service.ReadAsync("ES", CancellationToken.None))
            .Should().ThrowAsync<McpException>()).Which.Message.Should()
            .Contain("loginKey").And
            .Contain("bar close");
    }

    [Fact]
    public async Task AnUnknownSymbol_IsRefusedAndNamesTheConfiguredList()
    {
        await using Harness harness = await Harness.StartAsync(recordTape: true);

        (await harness.Service.Awaiting(service => service.ReadAsync("EXX", CancellationToken.None))
            .Should().ThrowAsync<McpException>()).Which.Message.Should()
            .Contain("EXX").And
            .Contain("ES");
    }

    [Fact]
    public async Task AZeroBid_IsReturnedRatherThanTreatedAsMissing()
    {
        await using Harness harness = await Harness.StartAsync(recordTape: true);
        await harness.ConnectRecorderHubAsync();
        harness.Hub.RaiseQuote(Quote(EsContract, 0m, 0.25m, 0m, harness.Clock));

        ToolPayloads.Quote quote = await harness.Service.ReadAsync("ES", CancellationToken.None);

        quote.Bid.Should().Be(0m);
        quote.Ask.Should().Be(0.25m);
    }

    [Fact]
    public async Task ARefusedSubscription_DoesNotEchoTheVenueMessage()
    {
        await using Harness harness = await Harness.StartAsync(recordTape: true);
        await harness.ConnectRecorderHubAsync();
        harness.Hub.PriceSubscribeThrows = new InvalidOperationException("vendor says hello");

        (await harness.Service.Awaiting(service =>
                service.ReadUnheldContractAsync(OtherContract, "ES", CancellationToken.None))
            .Should().ThrowAsync<McpException>()).Which.Message.Should()
            .Contain("refused").And
            .Contain("bar close").And
            .NotContain("vendor says hello");
    }

    [Fact]
    public async Task GetQuote_OmitsSizeAndTimestamp_WhenTheQuoteCarriesNeither()
    {
        await using Harness harness = await Harness.StartAsync(recordTape: true);
        await harness.ConnectRecorderHubAsync();
        harness.Hub.RaiseQuote(new PriceUpdate
        {
            ContractId = EsContract,
            Symbol = "F.US.EP",
            BestBid = 1m,
            BestAsk = 2m,
            LastPrice = 1.5m,
            Volume = 0m,
        });

        ToolPayloads.Quote quote = await harness.Service.ReadAsync("ES", CancellationToken.None);
        quote.Timestamp.Should().BeNull();
        quote.BidSize.Should().BeNull();
        quote.Symbol.Should().Be("ES");

        JsonElement wire = JsonSerializer.SerializeToElement(quote, McpJsonUtilities.DefaultOptions);
        wire.TryGetProperty("bidSize", out _).Should().BeFalse();
        wire.TryGetProperty("askSize", out _).Should().BeFalse();
        wire.TryGetProperty("timestamp", out _).Should().BeFalse();
        wire.GetProperty("bid").GetDecimal().Should().Be(1m);
    }

    [Fact]
    public async Task TheToolReturnsTheCachedQuote()
    {
        await using Harness harness = await Harness.StartAsync(recordTape: true);
        await harness.ConnectRecorderHubAsync();
        harness.Hub.RaiseQuote(Quote(EsContract, 5m, 6m, 5.5m, harness.Clock));
        QuoteTools tools = new(harness.Service);

        ToolPayloads.Quote quote = await tools.GetQuote("ES", CancellationToken.None);

        quote.Bid.Should().Be(5m);
        quote.Ask.Should().Be(6m);
    }

    private static PriceUpdate Quote(
        string contractId,
        decimal bid,
        decimal ask,
        decimal last,
        FakeTimeProvider clock,
        decimal volume = 1m) =>
        Quote(contractId, bid, ask, last, clock.GetUtcNow().UtcDateTime, volume);

    private static PriceUpdate Quote(
        string contractId,
        decimal bid,
        decimal ask,
        decimal last,
        DateTime timestamp,
        decimal volume = 1m) =>
        new()
        {
            ContractId = contractId,
            Symbol = "F.US.EP",
            BestBid = bid,
            BestAsk = ask,
            LastPrice = last,
            Volume = volume,
            Timestamp = DateTime.SpecifyKind(timestamp, DateTimeKind.Utc),
        };

    private sealed class Harness : IAsyncDisposable
    {
        private Harness(LiveQuoteService service, FakeMarketHub hub, FakeTimeProvider clock)
        {
            Service = service;
            Hub = hub;
            Clock = clock;
        }

        public LiveQuoteService Service { get; }

        public FakeMarketHub Hub { get; }

        public FakeTimeProvider Clock { get; }

        public static async Task<Harness> StartAsync(
            McpTransport transport = McpTransport.Http,
            bool serveQuotes = true,
            bool recordTape = true,
            TimeSpan? quoteWait = null,
            TimeSpan? staleAfter = null,
            Func<InstrumentId, CancellationToken, Task<string?>>? resolve = null,
            HeldSession? held = null,
            bool allowConnectWithoutToken = false)
        {
            FakeTimeProvider clock = new(new DateTimeOffset(2026, 11, 2, 15, 0, 0, TimeSpan.Zero));
            FakeMarketHub hub = new();
            MarketDataOptions options = new()
            {
                Instruments = "ES,NQ",
                SessionCloseCentral = "16:00",
                ServeQuotes = serveQuotes,
                RecordTape = recordTape,
                QuoteWait = quoteWait ?? TimeSpan.FromSeconds(2),
                QuoteStaleAfter = staleAfter ?? TimeSpan.FromSeconds(15),
            };
            IOptions<MarketDataOptions> wrapped = Microsoft.Extensions.Options.Options.Create(options);
            LiveQuoteService service = new(
                hub,
                new InstrumentRegistry(wrapped),
                wrapped,
                transport,
                clock,
                NullLogger<LiveQuoteService>.Instance,
                resolve ?? DefaultResolve,
                held,
                allowConnectWithoutToken);
            await service.StartAsync(CancellationToken.None);
            return new Harness(service, hub, clock);
        }

        public async Task ConnectRecorderHubAsync()
        {
            Hub.SimulateMarketReconnect();
            await WaitUntilSubscribedAsync(EsContract);
        }

        public Task WaitUntilSubscribedAsync(string contractId) =>
            WaitUntilAsync(() => Hub.PriceSubscriptionIds.Contains(contractId));

        public async Task WaitUntilAsync(
            Func<bool> condition,
            [CallerArgumentExpression(nameof(condition))] string? conditionExpression = null)
        {
            using CancellationTokenSource timeout = new(_poll);
            try
            {
                while (!condition())
                {
                    await Task.Delay(10, timeout.Token);
                }
            }
            catch (OperationCanceledException exception) when (exception.CancellationToken == timeout.Token)
            {
                throw new InvalidOperationException("Timed out waiting for: " + conditionExpression);
            }
        }

        public async Task AdvanceUntilAsync(Task pending, int pushes = 8)
        {
            for (int i = 0; i < pushes && !pending.IsCompleted; i++)
            {
                Clock.Advance(TimeSpan.FromSeconds(1));
                await Task.Delay(20);
            }
        }

        public async Task AdvanceUntilAsync(Func<bool> condition, int pushes)
        {
            for (int i = 0; i < pushes && !condition(); i++)
            {
                Clock.Advance(TimeSpan.FromSeconds(1));
                await Task.Delay(20);
            }
        }

        public async ValueTask DisposeAsync() =>
            await Service.StopAsync(CancellationToken.None);

        private static Task<string?> DefaultResolve(
            InstrumentId instrument,
            CancellationToken cancellationToken) =>
            Task.FromResult<string?>(instrument.Symbol switch
            {
                "ES" => EsContract,
                "NQ" => "CON.F.US.ENQ.Z26",
                _ => null,
            });
    }
}
