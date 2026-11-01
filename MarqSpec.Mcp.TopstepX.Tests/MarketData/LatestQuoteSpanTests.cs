using System.Diagnostics;
using System.Runtime.CompilerServices;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>Flush and subscribe each open a span. The read span is on the store.</summary>
[Collection(HostTelemetryCollection.Name)]
public sealed class LatestQuoteSpanTests
{
    private const string EsContract = "CON.F.US.EP.Z26";

    [Fact]
    public async Task AFlushAndASubscribe_EachOpenASpan()
    {
        List<Activity> stopped = [];
        using HostTelemetry telemetry = new();
        using ActivityListener listener = Listen(stopped, telemetry.Activities);
        RecordingStore store = new();
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
            "projectx");
        hub.SimulateMarketReconnect();

        await writer.StartAsync(CancellationToken.None);
        await WaitUntil(() => stopped.Exists(activity => activity.OperationName == "quote.subscribe"));

        hub.RaiseQuote(new()
        {
            ContractId = EsContract,
            Symbol = "F.US.EP",
            BestBid = 10m,
            BestAsk = 10.25m,
            LastPrice = 10m,
            Volume = 1m,
            Timestamp = new DateTime(2026, 11, 1, 15, 0, 0, DateTimeKind.Utc),
        });
        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitUntil(() => stopped.Exists(activity => activity.OperationName == "quote.flush"));

        await writer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void TheReadFlushAndSubscribeSpans_AreNamed()
    {
        List<Activity> stopped = [];
        using HostTelemetry telemetry = new();
        using ActivityListener listener = Listen(stopped, telemetry.Activities);

        using (telemetry.StartQuoteRead())
        {
        }

        using (telemetry.StartQuoteFlush())
        {
        }

        using (telemetry.StartQuoteSubscribe())
        {
        }

        stopped.Select(activity => activity.OperationName).Should().Equal(
            "quote.read",
            "quote.flush",
            "quote.subscribe");
    }

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

        public Task<LatestQuoteRow?> FindAsync(
            string venue,
            string contractId,
            CancellationToken cancellationToken) =>
            Task.FromResult<LatestQuoteRow?>(null);
    }
}
