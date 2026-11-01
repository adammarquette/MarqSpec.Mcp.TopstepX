using System.Runtime.CompilerServices;
using FluentAssertions;
using MarqSpec.Client.ProjectX.Api.Models;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Telemetry;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// The writer rides the hub the recorder already connected. It does not open one, and a failed
/// flush does not stop it.
/// </summary>
public sealed class LatestQuoteWriterTests
{
    private const string EsContract = "CON.F.US.EP.Z26";

    private const string NextContract = "CON.F.US.EP.H27";

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
        using HostTelemetry telemetry = new();
        using MetricCollector<long> writes = new(
            telemetry, HostTelemetry.Name, HostTelemetry.QuoteWritesInstrument);
        (LatestQuoteWriter writer, FakeMarketHub hub, FakeTimeProvider clock) = BuildClock(
            McpTransport.Http, recordTape: true, recordQuotes: true, store, telemetry);
        hub.SimulateMarketReconnect();

        await writer.StartAsync(CancellationToken.None);
        await WaitUntil(() => hub.PriceSubscriptionIds.Contains(EsContract));

        hub.RaiseQuote(Quote(EsContract, bid: 10m, volume: 4.9m));
        hub.RaiseQuote(Quote(EsContract, bid: 11m, volume: 4.9m));
        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitUntil(() => store.Batches.Count > 0);

        hub.MarketConnects.Should().Be(0);
        store.Batches.Should().ContainSingle();
        LatestQuoteDraft row = store.Batches[0].Should().ContainSingle().Subject;
        row.Bid.Should().Be(11m);
        row.Volume.Should().Be(4L);
        Counted(writes, QuoteWriteOutcome.Written).Should().Be(1);

        await writer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AThrowingStore_IsLoggedAndRetried_AndTheWriterStaysRunning()
    {
        ThrowingStore store = new();
        WarningLogger logger = new();
        using HostTelemetry telemetry = new();
        using MetricCollector<long> writes = new(
            telemetry, HostTelemetry.Name, HostTelemetry.QuoteWritesInstrument);
        using MetricCollector<long> failures = new(
            telemetry, HostTelemetry.Name, HostTelemetry.QuoteFailuresInstrument);
        (LatestQuoteWriter writer, FakeMarketHub hub, FakeTimeProvider clock) = BuildClock(
            McpTransport.Http,
            recordTape: true,
            recordQuotes: true,
            store,
            telemetry,
            logger: logger);
        hub.SimulateMarketReconnect();

        await writer.StartAsync(CancellationToken.None);
        await WaitUntil(() => hub.PriceSubscriptionIds.Contains(EsContract));

        hub.RaiseQuote(Quote(EsContract, bid: 10m));
        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitUntil(() => store.Calls >= 1);

        writer.ExecuteTask!.IsFaulted.Should().BeFalse();
        store.Batches.Should().BeEmpty();
        Counted(writes, QuoteWriteOutcome.Failed).Should().Be(1);
        Counted(failures, QuoteWriteOutcome.Failed).Should().Be(1);
        logger.Warnings.Should().Contain(warning =>
            warning.Text.Contains("flush failed", StringComparison.Ordinal)
            && warning.Exception == null);

        hub.RaiseQuote(Quote(EsContract, bid: 12m));
        clock.Advance(TimeSpan.FromSeconds(2));
        await WaitUntil(() => store.Batches.Count > 0);

        writer.ExecuteTask!.IsFaulted.Should().BeFalse();
        store.Batches.Should().ContainSingle().Which.Should().ContainSingle().Which.Bid.Should().Be(12m);
        Counted(writes, QuoteWriteOutcome.Written).Should().Be(1);

        await writer.StopAsync(CancellationToken.None);
        writer.ExecuteTask!.IsFaulted.Should().BeFalse();
    }

    [Fact]
    public async Task AFrontRoll_UnsubscribesTheOldContract_WithoutReconnecting()
    {
        RecordingStore store = new();
        int calls = 0;
        (LatestQuoteWriter writer, FakeMarketHub hub, FakeTimeProvider clock) = BuildClock(
            McpTransport.Http,
            recordTape: true,
            recordQuotes: true,
            store,
            resolveFront: (instrument, _) =>
            {
                if (instrument.Symbol != "ES")
                {
                    return Task.FromResult<string?>(null);
                }

                int n = Interlocked.Increment(ref calls);
                return Task.FromResult<string?>(n == 1 ? EsContract : NextContract);
            },
            frontRefresh: TimeSpan.FromSeconds(1));
        hub.SimulateMarketReconnect();

        await writer.StartAsync(CancellationToken.None);
        await WaitUntil(() => hub.PriceSubscriptionIds.Contains(EsContract));

        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitUntil(() =>
            hub.PriceSubscriptionIds.Contains(NextContract)
            && !hub.PriceSubscriptionIds.Contains(EsContract));

        hub.PriceUnsubscribes.Should().BeGreaterThan(0);
        hub.MarketConnects.Should().Be(0);
        hub.TransitionsIntoConnected.Should().Be(1);

        await writer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ARefusedSubscribe_IsRetriedWithoutWaitingForReconnect()
    {
        RecordingStore store = new();
        using HostTelemetry telemetry = new();
        using MetricCollector<long> failures = new(
            telemetry, HostTelemetry.Name, HostTelemetry.QuoteFailuresInstrument);
        (LatestQuoteWriter writer, FakeMarketHub hub, FakeTimeProvider clock) = BuildClock(
            McpTransport.Http, recordTape: true, recordQuotes: true, store, telemetry);
        hub.PriceSubscribeThrows = new InvalidOperationException("refused");
        hub.SimulateMarketReconnect();

        await writer.StartAsync(CancellationToken.None);
        await WaitUntil(() => hub.PriceSubscribeAttempts >= 1);

        hub.PriceSubscriptionIds.Should().BeEmpty();
        hub.TransitionsIntoConnected.Should().Be(1);
        Counted(failures, QuoteWriteOutcome.SubscribeRefused).Should().Be(1);

        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitUntil(() => hub.PriceSubscriptionIds.Contains(EsContract));

        hub.TransitionsIntoConnected.Should().Be(1);
        hub.MarketConnects.Should().Be(0);

        await writer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AnUnresolvedFront_IsCounted_AndDoesNotSubscribe()
    {
        RecordingStore store = new();
        using HostTelemetry telemetry = new();
        using MetricCollector<long> failures = new(
            telemetry, HostTelemetry.Name, HostTelemetry.QuoteFailuresInstrument);
        (LatestQuoteWriter writer, FakeMarketHub hub, _) = BuildClock(
            McpTransport.Http,
            recordTape: true,
            recordQuotes: true,
            store,
            telemetry,
            resolveFront: (_, _) => Task.FromResult<string?>(null));
        hub.SimulateMarketReconnect();

        await writer.StartAsync(CancellationToken.None);
        await WaitUntil(() => Counted(failures, QuoteWriteOutcome.FrontUnresolved) >= 1);

        hub.PriceSubscriptionIds.Should().BeEmpty();
        hub.MarketConnects.Should().Be(0);

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
        ILatestQuoteStore store,
        HostTelemetry? telemetry = null,
        Func<InstrumentId, CancellationToken, Task<string?>>? resolveFront = null,
        TimeSpan? frontRefresh = null,
        ILogger<LatestQuoteWriter>? logger = null)
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
            LatestQuoteFrontRefresh = frontRefresh ?? MarketDataOptions.DefaultLatestQuoteFrontRefresh,
        };
        LatestQuoteWriter writer = new(
            hub,
            new InstrumentRegistry(Options.Create(options)),
            Options.Create(options),
            transport,
            clock,
            logger ?? NullLogger<LatestQuoteWriter>.Instance,
            telemetry ?? _telemetry,
            store,
            resolveFront ?? ((instrument, _) => Task.FromResult<string?>(
                instrument.Symbol == "ES" ? EsContract : null)),
            "projectx");
        return (writer, hub, clock);
    }

    private static PriceUpdate Quote(string contractId, decimal bid, decimal volume = 4m) =>
        new()
        {
            ContractId = contractId,
            Symbol = "F.US.EP",
            BestBid = bid,
            BestAsk = bid + 0.25m,
            LastPrice = bid,
            Volume = volume,
            Timestamp = new DateTime(2026, 11, 1, 15, 0, 0, DateTimeKind.Utc),
        };

    private static long Counted(MetricCollector<long> collector, string value) =>
        collector.GetMeasurementSnapshot()
            .Where(measurement => measurement.Tags.Values.Contains(value))
            .Sum(measurement => measurement.Value);

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

        public Task<LatestQuoteRow?> FindAsync(
            string venue,
            string contractId,
            CancellationToken cancellationToken) =>
            Task.FromResult<LatestQuoteRow?>(null);
    }

    private sealed class ThrowingStore : ILatestQuoteStore
    {
        private int _calls;

        public int Calls => _calls;

        public List<IReadOnlyList<LatestQuoteDraft>> Batches { get; } = [];

        public Task<int> UpsertAsync(
            IReadOnlyList<LatestQuoteDraft> quotes,
            CancellationToken cancellationToken)
        {
            int n = Interlocked.Increment(ref _calls);
            if (n == 1)
            {
                throw new InvalidOperationException("db down");
            }

            Batches.Add(quotes);
            return Task.FromResult(quotes.Count);
        }

        public Task<LatestQuoteRow?> FindAsync(
            string venue,
            string contractId,
            CancellationToken cancellationToken) =>
            Task.FromResult<LatestQuoteRow?>(null);
    }

    private sealed record Warning(string Text, Exception? Exception);

    private sealed class WarningLogger : ILogger<LatestQuoteWriter>
    {
        public List<Warning> Warnings { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(new Warning(formatter(state, exception), exception));
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
