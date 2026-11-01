using System.Runtime.CompilerServices;
using FluentAssertions;
using MarqSpec.Client.ProjectX.Api.Models;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// The writer rides the hub the recorder already connected. It does not open one.
/// </summary>
public sealed class LatestQuoteWriterTests
{
    private const string EsContract = "CON.F.US.EP.Z26";

    private static readonly HostTelemetry _telemetry = new();

    [Theory]
    [InlineData(McpTransport.Stdio, true, true)]
    [InlineData(McpTransport.Http, false, true)]
    [InlineData(McpTransport.Http, true, false)]
    public async Task TheWriterDoesNotSubscribe_UnlessHttpTapeAndQuotesAreAllOn(
        McpTransport transport,
        bool recordTape,
        bool recordQuotes)
    {
        RecordingStore store = new();
        (LatestQuoteWriter writer, FakeMarketHub hub) = Build(transport, recordTape, recordQuotes, store);

        await writer.StartAsync(CancellationToken.None);

        hub.MarketConnects.Should().Be(0);
        hub.PriceSubscriptions.Should().Be(0);
        store.Batches.Should().BeEmpty();

        await writer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ADisconnectedHub_IsNotConnectedByThisWriter()
    {
        RecordingStore store = new();
        (LatestQuoteWriter writer, FakeMarketHub hub) = Build(
            McpTransport.Http, recordTape: true, recordQuotes: true, store);

        await writer.StartAsync(CancellationToken.None);
        await Task.Delay(30);

        hub.MarketConnects.Should().Be(0);
        hub.PriceSubscriptionIds.Should().BeEmpty();

        await writer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task QuotesOnAConnectedHub_FlushAsTheLatestRow_WithoutASecondConnect()
    {
        RecordingStore store = new();
        (LatestQuoteWriter writer, FakeMarketHub hub, FakeTimeProvider clock) = BuildClock(
            McpTransport.Http, recordTape: true, recordQuotes: true, store);
        hub.SimulateMarketReconnect();

        await writer.StartAsync(CancellationToken.None);
        await WaitUntil(() => hub.PriceSubscriptionIds.Contains(EsContract));

        hub.RaiseQuote(Quote(EsContract, bid: 10m));
        hub.RaiseQuote(Quote(EsContract, bid: 11m));
        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitUntil(() => store.Batches.Count > 0);

        hub.MarketConnects.Should().Be(0);
        store.Batches.Should().ContainSingle();
        store.Batches[0].Should().ContainSingle().Which.Bid.Should().Be(11m);

        await writer.StopAsync(CancellationToken.None);
    }

    private static (LatestQuoteWriter Writer, FakeMarketHub Hub) Build(
        McpTransport transport,
        bool recordTape,
        bool recordQuotes,
        RecordingStore store)
    {
        (LatestQuoteWriter writer, FakeMarketHub hub, _) =
            BuildClock(transport, recordTape, recordQuotes, store);
        return (writer, hub);
    }

    private static (LatestQuoteWriter Writer, FakeMarketHub Hub, FakeTimeProvider Clock) BuildClock(
        McpTransport transport,
        bool recordTape,
        bool recordQuotes,
        RecordingStore store)
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 11, 1, 15, 0, 0, TimeSpan.Zero));
        FakeMarketHub hub = new();
        MarketDataOptions options = new()
        {
            Instruments = "ES",
            SessionCloseCentral = "16:00",
            RecordTape = recordTape,
            RecordLatestQuotes = recordQuotes,
            LatestQuoteFlush = TimeSpan.FromSeconds(1),
        };
        LatestQuoteWriter writer = new(
            hub,
            new InstrumentRegistry(Microsoft.Extensions.Options.Options.Create(options)),
            Microsoft.Extensions.Options.Options.Create(options),
            transport,
            clock,
            NullLogger<LatestQuoteWriter>.Instance,
            _telemetry,
            store,
            (instrument, _) => Task.FromResult<string?>(
                instrument.Symbol == "ES" ? EsContract : null),
            "projectx");
        return (writer, hub, clock);
    }

    private static PriceUpdate Quote(string contractId, decimal bid) =>
        new()
        {
            ContractId = contractId,
            Symbol = "F.US.EP",
            BestBid = bid,
            BestAsk = bid + 0.25m,
            LastPrice = bid,
            Volume = 4m,
            Timestamp = new DateTime(2026, 11, 1, 15, 0, 0, DateTimeKind.Utc),
        };

    private static async Task WaitUntil(
        Func<bool> condition,
        [CallerArgumentExpression(nameof(condition))] string? conditionExpression = null)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
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

    private sealed class RecordingStore : ILatestQuoteStore
    {
        public List<IReadOnlyList<LatestQuoteDraft>> Batches { get; } = [];

        public Task<int> UpsertAsync(
            IReadOnlyList<LatestQuoteDraft> quotes,
            CancellationToken cancellationToken)
        {
            Batches.Add(quotes);
            return Task.FromResult(quotes.Count);
        }

        public Task<LatestQuoteDraft?> FindAsync(
            string venue,
            string contractId,
            CancellationToken cancellationToken) =>
            Task.FromResult<LatestQuoteDraft?>(null);
    }
}
