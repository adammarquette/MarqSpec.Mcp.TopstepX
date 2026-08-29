using FluentAssertions;
using MarqSpec.Client.ProjectX.Api.Models;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// The first first-party background service: prints land, once, attributed and UTC, and it
/// cannot start under stdio.
/// </summary>
public sealed class TradeTapeRecorderTests
{
    private static readonly DateTimeOffset _receipt =
        new(2026, 8, 28, 14, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(McpTransport.Stdio, true)]
    [InlineData(McpTransport.Http, false)]
    [InlineData(McpTransport.Stdio, false)]
    public async Task TheRecorderDoesNotStart_WhenTheTransportIsStdioOrTheSwitchIsOff(
        McpTransport transport,
        bool recordTape)
    {
        (TradeTapeRecorder recorder, FakeMarketHub hub, TopstepXDbContext database, ServiceProvider services) =
            Build(transport, recordTape);

        await using (services)
        await using (database)
        {
            await recorder.StartAsync(CancellationToken.None);

            hub.MarketConnects.Should().Be(0);
            hub.TradeSubscriptions.Should().BeEmpty();
            database.Trades.Should().BeEmpty();

            await recorder.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task TheRecorderWritesAnAttributedUtcPrint_WhenHttpAndRecordTapeAreOn()
    {
        (TradeTapeRecorder recorder, FakeMarketHub hub, TopstepXDbContext database, ServiceProvider services) =
            Build(McpTransport.Http, recordTape: true);

        await using (services)
        await using (database)
        {
            await recorder.StartAsync(CancellationToken.None);
            await WaitUntil(() => hub.TradeSubscriptions.Count > 0);

            hub.Raise(Print(
                new DateTime(2026, 8, 28, 13, 45, 0, DateTimeKind.Unspecified),
                TradeLogType.Buy,
                price: 5000.25m));

            await WaitUntil(() => recorder.RecordedPrints == 1);

            TradeRecord row = database.Trades.Should().ContainSingle().Subject;
            row.Venue.Should().Be("test");
            row.Instrument.Should().Be("ES");
            row.ContractId.Should().Be("CON.F.US.TEST.Z26");
            row.TradeTimeUtc.Should().Be(new DateTimeOffset(2026, 8, 28, 13, 45, 0, TimeSpan.Zero));
            row.TradeTimeUtc.Offset.Should().Be(TimeSpan.Zero);
            row.RecordedAt.Should().Be(_receipt);
            row.Price.Should().Be(5000.25m);
            row.Size.Should().Be(3);
            row.Direction.Should().Be(TradeDirection.Buy);
            row.Sequence.Should().Be(1);

            hub.UserConnects.Should().Be(0, "the user hub is still out of scope (ADR-0016)");
            hub.PriceSubscriptions.Should().Be(0);
            hub.OrderBookSubscriptions.Should().Be(0);

            await recorder.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public async Task APrintTimestamp_IsStoredUtc_ForEveryKindTheVenueSends(DateTimeKind kind)
    {
        // ProjectXMapping.ToUtc already pins each Kind. This proves the recorder uses that path
        // rather than new DateTimeOffset(timestamp) which would treat Local as the machine offset
        // and Unspecified as local-by-inference.
        DateTime utcInstant = new(2026, 8, 28, 18, 0, 0, DateTimeKind.Utc);
        DateTime stamped = kind switch
        {
            DateTimeKind.Utc => utcInstant,
            DateTimeKind.Local => DateTime.SpecifyKind(utcInstant.ToLocalTime(), DateTimeKind.Local),
            _ => DateTime.SpecifyKind(utcInstant, DateTimeKind.Unspecified),
        };

        (TradeTapeRecorder recorder, FakeMarketHub hub, TopstepXDbContext database, ServiceProvider services) =
            Build(McpTransport.Http, recordTape: true);

        await using (services)
        await using (database)
        {
            await recorder.StartAsync(CancellationToken.None);
            await WaitUntil(() => hub.TradeSubscriptions.Count > 0);

            hub.Raise(Print(stamped, TradeLogType.Sell, price: 4999m));
            await WaitUntil(() => recorder.RecordedPrints == 1);

            TradeRecord row = database.Trades.Should().ContainSingle().Subject;
            row.TradeTimeUtc.Offset.Should().Be(TimeSpan.Zero);
            row.TradeTimeUtc.UtcDateTime.Should().Be(utcInstant);

            await recorder.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ANullTradeType_IsStoredAsUnknown_NotBuy()
    {
        (TradeTapeRecorder recorder, FakeMarketHub hub, TopstepXDbContext database, ServiceProvider services) =
            Build(McpTransport.Http, recordTape: true);

        await using (services)
        await using (database)
        {
            await recorder.StartAsync(CancellationToken.None);
            await WaitUntil(() => hub.TradeSubscriptions.Count > 0);

            hub.Raise(Print(
                new DateTime(2026, 8, 28, 13, 45, 0, DateTimeKind.Utc),
                type: null,
                price: 5001m));

            await WaitUntil(() => recorder.RecordedPrints == 1);

            database.Trades.Single().Direction.Should().Be(TradeDirection.Unknown);

            await recorder.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AnUnrecognisedTradeType_IsStoredAsUnknown_NotBuy()
    {
        (TradeTapeRecorder recorder, FakeMarketHub hub, TopstepXDbContext database, ServiceProvider services) =
            Build(McpTransport.Http, recordTape: true);

        await using (services)
        await using (database)
        {
            await recorder.StartAsync(CancellationToken.None);
            await WaitUntil(() => hub.TradeSubscriptions.Count > 0);

            hub.Raise(Print(
                new DateTime(2026, 8, 28, 13, 45, 0, DateTimeKind.Utc),
                (TradeLogType)99,
                price: 5002m));

            await WaitUntil(() => recorder.RecordedPrints == 1);

            database.Trades.Single().Direction.Should().Be(TradeDirection.Unknown);

            await recorder.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AFullChannel_RecordsTheDrop_RatherThanDiscardingSilently()
    {
        TaskCompletionSource hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource persistStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        (TradeTapeRecorder recorder, FakeMarketHub hub, TopstepXDbContext database, ServiceProvider services) =
            Build(
                McpTransport.Http,
                recordTape: true,
                channelCapacity: 1,
                persistHold: hold,
                persistStarted: persistStarted);

        await using (services)
        await using (database)
        {
            await recorder.StartAsync(CancellationToken.None);
            await WaitUntil(() => hub.TradeSubscriptions.Count > 0);

            hub.Raise(Print(Utc(13, 45, 0), TradeLogType.Buy, price: 1m));
            await persistStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            // First print is being persisted (held). Second fills the one-slot channel. Third drops.
            hub.Raise(Print(Utc(13, 45, 1), TradeLogType.Buy, price: 2m));
            hub.Raise(Print(Utc(13, 45, 2), TradeLogType.Buy, price: 3m));

            await WaitUntil(() => recorder.DroppedPrints == 1);

            recorder.DroppedPrints.Should().Be(1);
            recorder.RecordedPrints.Should().Be(0);

            hold.SetResult();
            await WaitUntil(() => recorder.RecordedPrints == 2);

            database.Trades.Select(t => t.Price).Should().BeEquivalentTo([1m, 2m]);

            await recorder.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task APrintOnTheFirstContract_IsStillRecorded_WhenALaterSubscribeThrows()
    {
        // Shipped default is ES,NQ. The connect-throws test never reaches subscribe, and the
        // rest of this suite configures ES alone, so a throw on instrument 1 used to leave the
        // ES subscription live with no drain: prints TryWrite into an unread channel, then
        // log as full-channel drops, and Trades stays empty while ExecuteTask looks clean.
        (TradeTapeRecorder recorder, FakeMarketHub hub, TopstepXDbContext database, ServiceProvider services) =
            Build(
                McpTransport.Http,
                recordTape: true,
                instruments: "ES,NQ",
                gateway: new PerInstrumentGateway());

        hub.SubscribeThrowsAfterFirst =
            new InvalidOperationException("the venue refused the NQ trade subscribe");

        await using (services)
        await using (database)
        {
            await recorder.StartAsync(CancellationToken.None);
            await WaitUntil(() => hub.SubscribeAttempts >= 2);

            recorder.ExecuteTask.Should().NotBeNull();
            recorder.ExecuteTask!.IsFaulted.Should().BeFalse(
                "a faulted ExecuteTask is what Program.AnyFaulted reads");

            hub.Raise(Print(
                new DateTime(2026, 8, 28, 13, 45, 0, DateTimeKind.Utc),
                TradeLogType.Buy,
                price: 5000.25m,
                contractId: "CON.F.US.EP.Z26"));

            await WaitUntil(() => recorder.RecordedPrints == 1);

            TradeRecord row = database.Trades.Should().ContainSingle().Subject;
            row.Instrument.Should().Be("ES");
            row.ContractId.Should().Be("CON.F.US.EP.Z26");
            hub.TradeSubscriptions.Should().Equal("CON.F.US.EP.Z26");

            await recorder.StopAsync(CancellationToken.None);

            recorder.ExecuteTask.IsFaulted.Should().BeFalse();
        }
    }

    [Fact]
    public async Task TheRecorderCompletesWithoutFaulting_WhenTheHubThrows()
    {
        (TradeTapeRecorder recorder, FakeMarketHub hub, TopstepXDbContext database, ServiceProvider services) =
            Build(McpTransport.Http, recordTape: true);

        hub.ConnectThrows = new InvalidOperationException("the market hub refused the handshake");

        await using (services)
        await using (database)
        {
            await recorder.StartAsync(CancellationToken.None);

            recorder.ExecuteTask.Should().NotBeNull();
            await recorder.ExecuteTask!;

            recorder.ExecuteTask.IsFaulted.Should().BeFalse(
                "a faulted ExecuteTask is what Program.AnyFaulted reads, and would turn a stdio EOF into a crash");
            hub.TradeSubscriptions.Should().BeEmpty();

            await recorder.StopAsync(CancellationToken.None);
        }
    }

    private static DateTime Utc(int hour, int minute, int second) =>
        new(2026, 8, 28, hour, minute, second, DateTimeKind.Utc);

    private static TradeUpdate Print(
        DateTime timestamp,
        TradeLogType? type,
        decimal price,
        string contractId = "CON.F.US.TEST.Z26") =>
        new()
        {
            ContractId = contractId,
            SymbolId = "F.US.EP",
            Price = price,
            Timestamp = timestamp,
            Type = type,
            Volume = 3m,
        };

    private static (
        TradeTapeRecorder Recorder,
        FakeMarketHub Hub,
        TopstepXDbContext Database,
        ServiceProvider Services)
        Build(
            McpTransport transport,
            bool recordTape,
            int channelCapacity = 16,
            TaskCompletionSource? persistHold = null,
            TaskCompletionSource? persistStarted = null,
            string instruments = "ES",
            IMarketDataGateway? gateway = null)
    {
        FakeMarketHub hub = new();
        FakeTimeProvider clock = new(_receipt);
        string databaseName = Guid.NewGuid().ToString();
        DbContextOptionsBuilder<TopstepXDbContext> builder = new DbContextOptionsBuilder<TopstepXDbContext>()
            .UseInMemoryDatabase(databaseName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning));
        if (persistHold is not null)
        {
            builder.AddInterceptors(new HoldingInterceptor(persistHold, persistStarted));
        }

        DbContextOptions<TopstepXDbContext> options = builder.Options;

        MarketDataOptions market = new() { Instruments = instruments, RecordTape = recordTape };
        McpOptions mcp = new()
        {
            Transport = transport,
            HttpBearerToken = transport == McpTransport.Http ? "a-token" : string.Empty,
        };

        ServiceCollection services = new();
        services.AddSingleton<IOptions<MarketDataOptions>>(Options.Create(market));
        services.AddSingleton<IOptions<McpOptions>>(Options.Create(mcp));
        services.AddSingleton(new InstrumentRegistry(Options.Create(market)));
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton(hub);
        services.AddSingleton<MarqSpec.Client.ProjectX.WebSocket.IProjectXWebSocketClient>(hub);
        services.AddScoped<IMarketDataGateway>(_ => gateway ?? new CountingGateway([]));
        services.AddScoped(_ => new TopstepXDbContext(options));

        ServiceProvider provider = services.BuildServiceProvider();
        TopstepXDbContext database = new(options);

        TradeTapeRecorder recorder = new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(market),
            Options.Create(mcp),
            provider.GetRequiredService<InstrumentRegistry>(),
            clock,
            NullLogger<TradeTapeRecorder>.Instance,
            channelCapacity);

        return (recorder, hub, database, provider);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    /// <summary>Resolves a distinct front contract per symbol so two instruments are two subscriptions.</summary>
    private sealed class PerInstrumentGateway : IMarketDataGateway
    {
        public string VenueId => "test";

        public Task<IReadOnlyList<VenueContract>> ResolveContractsAsync(
            InstrumentId instrument,
            CancellationToken cancellationToken)
        {
            string contract = instrument.Symbol switch
            {
                "ES" => "CON.F.US.EP.Z26",
                "NQ" => "CON.F.US.ENQ.Z26",
                _ => "CON.F.US.TEST.Z26",
            };

            return Task.FromResult<IReadOnlyList<VenueContract>>(
                [new VenueContract(contract, instrument, true, 0.25m, 12.50m)]);
        }

        public Task<IReadOnlyList<Bar>> GetBarsAsync(
            string contractId,
            BarRange window,
            TimeSpan barSize,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Bar>>([]);

        public Task<IReadOnlyList<VenueAccount>> GetAccountsAsync(
            bool onlyActive,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VenueAccount>>([]);

        public Task<IReadOnlyList<VenuePosition>> GetOpenPositionsAsync(
            int accountId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VenuePosition>>([]);

        public Task<IReadOnlyList<VenueOrder>> GetOrdersAsync(
            int accountId,
            BarRange? window,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VenueOrder>>([]);

        public Task<IReadOnlyList<VenueTrade>> GetTradesAsync(
            int accountId,
            BarRange window,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VenueTrade>>([]);
    }

    /// <summary>Holds persist so the bounded channel can fill in a test.</summary>
    private sealed class HoldingInterceptor(TaskCompletionSource hold, TaskCompletionSource? started)
        : SaveChangesInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            started?.TrySetResult();
            await hold.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await base.SavingChangesAsync(eventData, result, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
