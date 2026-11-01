using System.Diagnostics;
using FluentAssertions;
using MarqSpec.Client.ProjectX.WebSocket;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Telemetry;
using MarqSpec.Mcp.TopstepX.Tests.MarketData;
using MarqSpec.Mcp.TopstepX.Tools;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.Tests.Tools;

/// <summary>
/// get_quote reads the stored row. It does not open a market hub.
/// </summary>
[Collection(HostTelemetryCollection.Name)]
public sealed class QuoteToolsTests
{
    private const string Contract = "CON.F.US.EP.Z26";

    [Fact]
    public async Task AFreshRow_IsReturnedWithItsAge()
    {
        List<Activity> stopped = [];
        using HostTelemetry telemetry = new();
        using ActivityListener listener = Listen(stopped, telemetry.Activities);
        using MetricCollector<long> reads = new(
            telemetry, HostTelemetry.Name, HostTelemetry.QuoteReadsInstrument);
        QuoteTools tool = Tool(telemetry, Row(ageSeconds: 2));

        ToolPayloads.Quote quote = await tool.GetQuote("es", CancellationToken.None);

        quote.Status.Should().Be(LatestQuoteStatus.Ok);
        quote.Symbol.Should().Be("ES");
        quote.ContractId.Should().Be(Contract);
        quote.Bid.Should().Be(10m);
        quote.Ask.Should().Be(10.25m);
        quote.Stale.Should().BeFalse();
        quote.AgeSeconds.Should().Be(2);
        reads.LastMeasurement!.Tags[HostTelemetry.OutcomeTag].Should().Be(QuoteReadOutcome.Fresh);
        stopped.Should().ContainSingle(activity => activity.OperationName == "quote.read")
            .Which.GetTagItem(HostTelemetry.OutcomeTag).Should().Be(QuoteReadOutcome.Fresh);
    }

    [Fact]
    public async Task AStaleRow_IsStillReturned()
    {
        using HostTelemetry telemetry = new();
        using MetricCollector<long> reads = new(
            telemetry, HostTelemetry.Name, HostTelemetry.QuoteReadsInstrument);
        QuoteTools tool = Tool(telemetry, Row(ageSeconds: 40));

        ToolPayloads.Quote quote = await tool.GetQuote("ES", CancellationToken.None);

        quote.Status.Should().Be(LatestQuoteStatus.Ok);
        quote.Stale.Should().BeTrue();
        quote.Bid.Should().Be(10m);
        reads.LastMeasurement!.Tags[HostTelemetry.OutcomeTag].Should().Be(QuoteReadOutcome.Stale);
    }

    [Fact]
    public async Task AMissingRow_IsAResult()
    {
        using HostTelemetry telemetry = new();
        using MetricCollector<long> reads = new(
            telemetry, HostTelemetry.Name, HostTelemetry.QuoteReadsInstrument);
        QuoteTools tool = Tool(telemetry, row: null);

        ToolPayloads.Quote quote = await tool.GetQuote("ES", CancellationToken.None);

        quote.Status.Should().Be(LatestQuoteStatus.Missing);
        quote.ContractId.Should().Be(Contract);
        quote.Bid.Should().BeNull();
        quote.Stale.Should().BeFalse();
        reads.LastMeasurement!.Tags[HostTelemetry.OutcomeTag].Should().Be(QuoteReadOutcome.Miss);
    }

    [Fact]
    public async Task AnUnknownSymbol_IsAResult_AndDoesNotReadTheStore()
    {
        using HostTelemetry telemetry = new();
        using MetricCollector<long> reads = new(
            telemetry, HostTelemetry.Name, HostTelemetry.QuoteReadsInstrument);
        MemoryStore store = new(Row(ageSeconds: 1));
        QuoteTools tool = Tool(telemetry, store);

        ToolPayloads.Quote quote = await tool.GetQuote("ZZ", CancellationToken.None);

        quote.Status.Should().Be(LatestQuoteStatus.UnknownSymbol);
        quote.Bid.Should().BeNull();
        store.Finds.Should().Be(0);
        reads.LastMeasurement!.Tags[HostTelemetry.OutcomeTag].Should().Be(QuoteReadOutcome.Unknown);
    }

    [Fact]
    public async Task TheServerRead_DoesNotOpenTheHub()
    {
        FakeMarketHub hub = new();
        MemoryStore store = new(Row(ageSeconds: 1));
        MarketDataOptions options = QuoteOptions();
        using HostTelemetry telemetry = new();
        LatestQuoteWriter writer = new(
            hub,
            new InstrumentRegistry(Options.Create(options)),
            Options.Create(options),
            McpTransport.Http,
            TimeProvider.System,
            NullLogger<LatestQuoteWriter>.Instance,
            telemetry,
            store,
            (_, _) => Task.FromResult<string?>(Contract),
            "test");
        QuoteTools tool = Tool(telemetry, store);

        await writer.StartAsync(CancellationToken.None);
        ToolPayloads.Quote quote = await tool.GetQuote("ES", CancellationToken.None);
        await writer.StopAsync(CancellationToken.None);

        quote.Status.Should().Be(LatestQuoteStatus.Ok);
        hub.MarketConnects.Should().Be(0);
        hub.PriceSubscriptions.Should().Be(0);
        typeof(QuoteTools).GetConstructors().Single().GetParameters()
            .Should().NotContain(parameter => parameter.ParameterType == typeof(IProjectXWebSocketClient));
        typeof(LatestQuoteReader).GetConstructors().Single().GetParameters()
            .Should().NotContain(parameter => parameter.ParameterType == typeof(IProjectXWebSocketClient));
    }

    private static QuoteTools Tool(HostTelemetry telemetry, LatestQuoteRow? row) =>
        Tool(telemetry, new MemoryStore(row));

    private static QuoteTools Tool(HostTelemetry telemetry, MemoryStore store)
    {
        MarketDataOptions options = QuoteOptions();
        LatestQuoteReader reader = new(
            store,
            Gateway(),
            new InstrumentRegistry(Options.Create(options)),
            Options.Create(options),
            telemetry,
            NullLogger<LatestQuoteReader>.Instance);
        return new QuoteTools(reader);
    }

    private static MarketDataOptions QuoteOptions() => new()
    {
        Instruments = "ES",
        SessionCloseCentral = "16:00",
        RecordTape = false,
        RecordLatestQuotes = false,
        LatestQuoteStaleAfter = TimeSpan.FromSeconds(15),
    };

    private static CountingGateway Gateway() =>
        new(
            new Dictionary<string, IEnumerable<Bar>>(StringComparer.Ordinal)
            {
                [Contract] = [],
            },
            Contract);

    private static LatestQuoteRow Row(double ageSeconds) =>
        new(
            "test",
            "ES",
            Contract,
            10m,
            10.25m,
            10m,
            4L,
            new DateTimeOffset(2026, 11, 1, 15, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 1, 15, 0, 0, TimeSpan.Zero),
            ageSeconds);

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

    private sealed class MemoryStore(LatestQuoteRow? row) : ILatestQuoteStore
    {
        public int Finds { get; private set; }

        public Task<int> UpsertAsync(
            IReadOnlyList<LatestQuoteDraft> quotes,
            CancellationToken cancellationToken) =>
            Task.FromResult(0);

        public Task<LatestQuoteRow?> FindAsync(
            string venue,
            string contractId,
            CancellationToken cancellationToken)
        {
            Finds++;
            if (row is null || row.Venue != venue || row.ContractId != contractId)
            {
                return Task.FromResult<LatestQuoteRow?>(null);
            }

            return Task.FromResult<LatestQuoteRow?>(row);
        }
    }
}
