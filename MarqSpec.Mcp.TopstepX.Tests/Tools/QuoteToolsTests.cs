using System.Diagnostics;
using System.Reflection;
using FluentAssertions;
using MarqSpec.Client.ProjectX.WebSocket;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Telemetry;
using MarqSpec.Mcp.TopstepX.Tests.Configuration;
using MarqSpec.Mcp.TopstepX.Tests.MarketData;
using MarqSpec.Mcp.TopstepX.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace MarqSpec.Mcp.TopstepX.Tests.Tools;

/// <summary>
/// get_quote reads the stored row. The server does not register a market hub for that read.
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
        quote.Reason.Should().BeNull();
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
        quote.ContractId.Should().BeNull();
        quote.Bid.Should().BeNull();
        quote.Stale.Should().BeFalse();
        quote.Reason.Should().BeNull();
        reads.LastMeasurement!.Tags[HostTelemetry.OutcomeTag].Should().Be(QuoteReadOutcome.Miss);
    }

    [Fact]
    public async Task AFrontWithNoRow_IsAMiss_AndNamesTheContract()
    {
        using HostTelemetry telemetry = new();
        QuoteTools tool = Tool(telemetry, new FrontWithoutRowStore());

        ToolPayloads.Quote quote = await tool.GetQuote("ES", CancellationToken.None);

        quote.Status.Should().Be(LatestQuoteStatus.Missing);
        quote.ContractId.Should().Be(Contract);
        quote.Bid.Should().BeNull();
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
        store.Fronts.Should().Be(0);
        store.Finds.Should().Be(0);
        reads.LastMeasurement!.Tags[HostTelemetry.OutcomeTag].Should().Be(QuoteReadOutcome.Unknown);
    }

    [Fact]
    public async Task AFrontLookupThatThrows_IsFailed_NotAMiss()
    {
        await AssertFailed(
            new ThrowingFrontStore(),
            QuoteReadFailure.FrontLookupFailed,
            contractId: null);
    }

    [Fact]
    public async Task ARowReadThatThrows_IsFailed_NotAMiss()
    {
        await AssertFailed(
            new ThrowingRowStore(),
            QuoteReadFailure.StoreFailed,
            contractId: Contract);
    }

    [Fact]
    public async Task TheServerRead_DoesNotOpenTheHub()
    {
        FakeMarketHub hub = new();
        int hubResolves = 0;
        MemoryStore store = new(Row(ageSeconds: 1));
        WebApplicationBuilder builder = ServerBuilder(getQuoteEnabled: true);

        builder.Services.Should().NotContain(
            descriptor => descriptor.ServiceType == typeof(IProjectXWebSocketClient),
            "an unconfigured server does not register a market hub");
        builder.Services.AddSingleton<IProjectXWebSocketClient>(_ =>
        {
            hubResolves++;
            return hub;
        });
        builder.Services.RemoveAll<ILatestQuoteStore>();
        builder.Services.AddScoped<ILatestQuoteStore>(_ => store);

        using ServiceProvider provider = builder.Services.BuildServiceProvider();
        McpServerOptions listed = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;
        listed.ToolCollection.Should().Contain(tool => tool.ProtocolTool.Name == "get_quote");

        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        QuoteTools tool = scope.ServiceProvider.GetRequiredService<QuoteTools>();
        ToolPayloads.Quote quote = await tool.GetQuote("ES", CancellationToken.None);

        quote.Status.Should().Be(LatestQuoteStatus.Ok);
        quote.Bid.Should().Be(10m);
        hubResolves.Should().Be(0);
        hub.MarketConnects.Should().Be(0);
        hub.PriceSubscriptions.Should().Be(0);
        typeof(QuoteTools).GetCustomAttribute<McpServerToolTypeAttribute>().Should().BeNull();
        typeof(LatestQuoteReader).GetConstructors().Single().GetParameters()
            .Should().NotContain(parameter => parameter.ParameterType == typeof(IProjectXWebSocketClient));
    }

    [Fact]
    public void GetQuote_IsAbsent_WhenTheSwitchIsOff()
    {
        WebApplicationBuilder builder = ServerBuilder(getQuoteEnabled: false);
        using ServiceProvider provider = builder.Services.BuildServiceProvider();

        provider.GetService<QuoteTools>().Should().BeNull();
        McpServerOptions listed = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;
        listed.ToolCollection.Should().NotContain(tool => tool.ProtocolTool.Name == "get_quote");
        typeof(QuoteTools).GetCustomAttribute<McpServerToolTypeAttribute>().Should().BeNull();
    }

    private static async Task AssertFailed(ILatestQuoteStore store, string reason, string? contractId)
    {
        List<Activity> stopped = [];
        using HostTelemetry telemetry = new();
        using ActivityListener listener = Listen(stopped, telemetry.Activities);
        using MetricCollector<long> reads = new(
            telemetry, HostTelemetry.Name, HostTelemetry.QuoteReadsInstrument);
        CaptureLogger logger = new();
        QuoteTools tool = Tool(telemetry, store, logger);

        ToolPayloads.Quote quote = await tool.GetQuote("ES", CancellationToken.None);

        quote.Status.Should().Be(LatestQuoteStatus.Failed);
        quote.Reason.Should().Be(reason);
        quote.ContractId.Should().Be(contractId);
        quote.Bid.Should().BeNull();
        quote.Stale.Should().BeFalse();
        logger.Exceptions.Should().ContainSingle()
            .Which.Should().BeOfType<InvalidOperationException>();
        reads.LastMeasurement!.Tags[HostTelemetry.OutcomeTag].Should().Be(QuoteReadOutcome.ReadFailed);
        reads.LastMeasurement.Tags[HostTelemetry.ReasonTag].Should().Be(reason);
        reads.LastMeasurement.Tags[HostTelemetry.ExceptionTag]
            .Should().Be(nameof(InvalidOperationException));
        Activity span = stopped.Should()
            .ContainSingle(activity => activity.OperationName == "quote.read").Subject;
        span.Status.Should().Be(ActivityStatusCode.Error);
        span.GetTagItem(HostTelemetry.OutcomeTag).Should().Be(QuoteReadOutcome.ReadFailed);
        span.GetTagItem(HostTelemetry.ReasonTag).Should().Be(reason);
        span.GetTagItem(HostTelemetry.ExceptionTag).Should().Be(nameof(InvalidOperationException));
    }

    private static WebApplicationBuilder ServerBuilder(bool getQuoteEnabled)
    {
        WebApplicationBuilder builder = CompositionRootTestHost.CreateIsolatedBuilder();
        Dictionary<string, string?> settings = new(CompositionRootTestHost.BaseSettings)
        {
            ["Mcp:Transport"] = nameof(McpTransport.Http),
            ["Mcp:HttpBearerToken"] = "test-token",
            ["MarketData:RecordTape"] = "false",
            ["MarketData:RecordLatestQuotes"] = "false",
            ["MarketData:GetQuoteEnabled"] = getQuoteEnabled ? "true" : "false",
        };
        builder.Configuration.AddInMemoryCollection(settings);
        Program.ConfigureServices(
            builder,
            new McpOptions { Transport = McpTransport.Http, HttpBearerToken = "test-token" });
        return builder;
    }

    private static QuoteTools Tool(HostTelemetry telemetry, LatestQuoteRow? row) =>
        Tool(telemetry, new MemoryStore(row), NullLogger<LatestQuoteReader>.Instance);

    private static QuoteTools Tool(HostTelemetry telemetry, ILatestQuoteStore store) =>
        Tool(telemetry, store, NullLogger<LatestQuoteReader>.Instance);

    private static QuoteTools Tool(
        HostTelemetry telemetry,
        ILatestQuoteStore store,
        ILogger<LatestQuoteReader> logger)
    {
        MarketDataOptions options = QuoteOptions();
        LatestQuoteReader reader = new(
            store,
            new InstrumentRegistry(Options.Create(options)),
            Options.Create(options),
            telemetry,
            logger);
        return new QuoteTools(reader);
    }

    private static MarketDataOptions QuoteOptions() => new()
    {
        Instruments = "ES",
        SessionCloseCentral = "16:00",
        RecordTape = false,
        RecordLatestQuotes = false,
        GetQuoteEnabled = true,
        LatestQuoteStaleAfter = TimeSpan.FromSeconds(15),
    };

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
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = into.Add,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private sealed class MemoryStore(LatestQuoteRow? row) : ILatestQuoteStore
    {
        public int Fronts { get; private set; }

        public int Finds { get; private set; }

        public Task<int> UpsertAsync(
            IReadOnlyList<LatestQuoteDraft> quotes,
            CancellationToken cancellationToken) =>
            Task.FromResult(0);

        public Task<LatestQuoteFront?> FindFrontAsync(
            string instrument,
            CancellationToken cancellationToken)
        {
            Fronts++;
            if (row is null || !string.Equals(row.Instrument, instrument, StringComparison.Ordinal))
            {
                return Task.FromResult<LatestQuoteFront?>(null);
            }

            return Task.FromResult<LatestQuoteFront?>(new LatestQuoteFront(row.Venue, row.ContractId));
        }

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

    private sealed class FrontWithoutRowStore : ILatestQuoteStore
    {
        public Task<int> UpsertAsync(
            IReadOnlyList<LatestQuoteDraft> quotes,
            CancellationToken cancellationToken) =>
            Task.FromResult(0);

        public Task<LatestQuoteFront?> FindFrontAsync(
            string instrument,
            CancellationToken cancellationToken) =>
            Task.FromResult<LatestQuoteFront?>(new LatestQuoteFront("test", Contract));

        public Task<LatestQuoteRow?> FindAsync(
            string venue,
            string contractId,
            CancellationToken cancellationToken) =>
            Task.FromResult<LatestQuoteRow?>(null);
    }

    private sealed class ThrowingFrontStore : ILatestQuoteStore
    {
        public Task<int> UpsertAsync(
            IReadOnlyList<LatestQuoteDraft> quotes,
            CancellationToken cancellationToken) =>
            Task.FromResult(0);

        public Task<LatestQuoteFront?> FindFrontAsync(
            string instrument,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("front down");

        public Task<LatestQuoteRow?> FindAsync(
            string venue,
            string contractId,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("row should not be read");
    }

    private sealed class ThrowingRowStore : ILatestQuoteStore
    {
        public Task<int> UpsertAsync(
            IReadOnlyList<LatestQuoteDraft> quotes,
            CancellationToken cancellationToken) =>
            Task.FromResult(0);

        public Task<LatestQuoteFront?> FindFrontAsync(
            string instrument,
            CancellationToken cancellationToken) =>
            Task.FromResult<LatestQuoteFront?>(new LatestQuoteFront("test", Contract));

        public Task<LatestQuoteRow?> FindAsync(
            string venue,
            string contractId,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("row down");
    }

    private sealed class CaptureLogger : ILogger<LatestQuoteReader>
    {
        public List<Exception> Exceptions { get; } = [];

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
            if (exception is not null)
            {
                Exceptions.Add(exception);
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
