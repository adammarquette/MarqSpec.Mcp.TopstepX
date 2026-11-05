using System.Diagnostics;
using System.Runtime.CompilerServices;
using FluentAssertions;
using MarqSpec.Client.ProjectX.Api.Models;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// Flush and subscribe spans carry an outcome, and a failure sets the error status.
/// </summary>
[Collection(HostTelemetryCollection.Name)]
public sealed class LatestQuoteSpanTests
{
    private const string EsContract = "CON.F.US.EP.Z26";

    [Fact]
    public async Task AFlushAndASubscribe_RecordTheOutcome()
    {
        List<Activity> stopped = [];
        using HostTelemetry telemetry = new();
        using ActivityListener listener = Listen(stopped, telemetry.Activities);
        (LatestQuoteWriter writer, FakeMarketHub hub, FakeTimeProvider clock) =
            Writer(telemetry, new RecordingStore());
        hub.SimulateMarketReconnect();

        await writer.StartAsync(CancellationToken.None);
        await WaitUntil(() => Span(stopped, "quote.subscribe") is not null);

        Activity subscribe = Span(stopped, "quote.subscribe")!;
        subscribe.GetTagItem(HostTelemetry.OutcomeTag).Should().Be(QuoteWriteOutcome.Subscribed);
        subscribe.Status.Should().NotBe(ActivityStatusCode.Error);

        hub.RaiseQuote(Quote(10m));
        await WaitForFlush(stopped, clock);

        Activity flush = Span(stopped, "quote.flush")!;
        flush.GetTagItem(HostTelemetry.OutcomeTag).Should().Be(QuoteWriteOutcome.Written);
        flush.Status.Should().NotBe(ActivityStatusCode.Error);

        await writer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AFailedFlush_SetsTheErrorStatusAndTheExceptionType()
    {
        List<Activity> stopped = [];
        using HostTelemetry telemetry = new();
        using ActivityListener listener = Listen(stopped, telemetry.Activities);
        ThrowingStore store = new();
        (LatestQuoteWriter writer, FakeMarketHub hub, FakeTimeProvider clock) = Writer(telemetry, store);
        hub.SimulateMarketReconnect();

        await writer.StartAsync(CancellationToken.None);
        await WaitUntil(() => hub.PriceSubscriptionIds.Contains(EsContract));
        hub.RaiseQuote(Quote(10m));
        await WaitForFlush(stopped, clock);

        Activity flush = Span(stopped, "quote.flush")!;
        flush.GetTagItem(HostTelemetry.OutcomeTag).Should().Be(QuoteWriteOutcome.Failed);
        flush.Status.Should().Be(ActivityStatusCode.Error);
        flush.GetTagItem(HostTelemetry.ExceptionTag).Should().Be(nameof(InvalidOperationException));

        await writer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ARefusedSubscribe_SetsTheErrorStatusAndTheExceptionType()
    {
        List<Activity> stopped = [];
        using HostTelemetry telemetry = new();
        using ActivityListener listener = Listen(stopped, telemetry.Activities);
        (LatestQuoteWriter writer, FakeMarketHub hub, _) = Writer(telemetry, new RecordingStore());
        hub.PriceSubscribeThrows = new InvalidOperationException("refused");
        hub.SimulateMarketReconnect();

        await writer.StartAsync(CancellationToken.None);
        await WaitUntil(() => Span(stopped, "quote.subscribe") is not null);

        Activity subscribe = Span(stopped, "quote.subscribe")!;
        subscribe.GetTagItem(HostTelemetry.OutcomeTag).Should().Be(QuoteWriteOutcome.SubscribeRefused);
        subscribe.Status.Should().Be(ActivityStatusCode.Error);
        subscribe.GetTagItem(HostTelemetry.ExceptionTag).Should().Be(nameof(InvalidOperationException));

        await writer.StopAsync(CancellationToken.None);
    }

    private static Activity? Span(List<Activity> stopped, string name) =>
        stopped.Find(activity => activity.OperationName == name);

    private static (LatestQuoteWriter Writer, FakeMarketHub Hub, FakeTimeProvider Clock) Writer(
        HostTelemetry telemetry,
        ILatestQuoteStore store)
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 11, 1, 15, 0, 0, TimeSpan.Zero));
        FakeMarketHub hub = new();
        MarketDataOptions options = new()
        {
            Instruments = "ES",
            SessionCloseCentral = "16:00",
            RecordTape = true,
            RecordLatestQuotes = true,
            LatestQuoteFlush = TimeSpan.FromSeconds(1),
        };
        LatestQuoteWriter writer = new(
            hub,
            new InstrumentRegistry(Options.Create(options)),
            Options.Create(options),
            McpTransport.Http,
            clock,
            NullLogger<LatestQuoteWriter>.Instance,
            telemetry,
            store,
            (instrument, _) => Task.FromResult<string?>(instrument.Symbol == "ES" ? EsContract : null),
            "projectx",
            BarSessionCalendar.Parse(options.SessionCloseCentral, []));
        return (writer, hub, clock);
    }

    private static PriceUpdate Quote(decimal bid) =>
        new()
        {
            ContractId = EsContract,
            Symbol = "F.US.EP",
            BestBid = bid,
            BestAsk = bid + 0.25m,
            LastPrice = bid,
            Volume = 1m,
            Timestamp = new DateTime(2026, 11, 1, 15, 0, 0, DateTimeKind.Utc),
        };

    private static ActivityListener Listen(List<Activity> into, ActivitySource source)
    {
        ActivityListener listener = new()
        {
            ShouldListenTo = candidate => ReferenceEquals(candidate, source),
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = into.Add,
        };

        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    /// <summary>
    /// Advances the fake clock until a flush span exists. One advance can land before the
    /// writer has started its delay, and then the delay waits for a second advance.
    /// </summary>
    private static async Task WaitForFlush(List<Activity> stopped, FakeTimeProvider clock)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        try
        {
            while (Span(stopped, "quote.flush") is null)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                await Task.Delay(10, timeout.Token);
            }
        }
        catch (OperationCanceledException exception) when (exception.CancellationToken == timeout.Token)
        {
            throw new InvalidOperationException("Timed out waiting for a quote.flush span.");
        }
    }

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
        public Task<int> UpsertAsync(
            IReadOnlyList<LatestQuoteDraft> quotes,
            CancellationToken cancellationToken) =>
            Task.FromResult(quotes.Count);

        public Task<LatestQuoteFront?> FindFrontAsync(
            string instrument,
            CancellationToken cancellationToken) =>
            Task.FromResult<LatestQuoteFront?>(null);

        public Task<LatestQuoteRow?> FindAsync(
            string venue,
            string contractId,
            CancellationToken cancellationToken) =>
            Task.FromResult<LatestQuoteRow?>(null);
    }

    private sealed class ThrowingStore : ILatestQuoteStore
    {
        public Task<int> UpsertAsync(
            IReadOnlyList<LatestQuoteDraft> quotes,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("db down");

        public Task<LatestQuoteFront?> FindFrontAsync(
            string instrument,
            CancellationToken cancellationToken) =>
            Task.FromResult<LatestQuoteFront?>(null);

        public Task<LatestQuoteRow?> FindAsync(
            string venue,
            string contractId,
            CancellationToken cancellationToken) =>
            Task.FromResult<LatestQuoteRow?>(null);
    }
}
