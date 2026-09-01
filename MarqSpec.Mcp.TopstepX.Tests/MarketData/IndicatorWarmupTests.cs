using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// Startup warmup is HTTP and an explicit switch, both — the same shape as the tape recorder.
/// </summary>
/// <remarks>
/// <para>
/// A Cowork stdio child against a large store must not stall the handshake (gh#350). Choosing HTTP
/// is not consent to pay the 8.3 s at boot. A faulted <c>ExecuteTask</c> is what
/// <c>Program.AnyFaulted</c> reads, and would turn an ordinary stdio EOF into a crash (gh#76).
/// </para>
/// <para>
/// The numbers a warmup writes are the same numbers a subsequent <c>rebuild-indicators</c> would
/// write: a confirming rebuild is an empty diff (<c>R-2.2</c>).
/// </para>
/// </remarks>
public sealed class IndicatorWarmupTests
{
    private const string Venue = "test";
    private const int Resolution = 5;
    private const int SeededBars = 40;

    private static DateTimeOffset SessionStart =>
        MarketClock.FromMarket(new DateOnly(2026, 8, 18), new TimeOnly(9, 0)).ToUniversalTime();

    [Theory]
    [InlineData(McpTransport.Stdio, true)]
    [InlineData(McpTransport.Http, false)]
    [InlineData(McpTransport.Stdio, false)]
    public async Task WarmupDoesNotRun_WhenTheTransportIsStdioOrTheSwitchIsOff(
        McpTransport transport,
        bool warmIndicators)
    {
        (IndicatorWarmup warmup, TopstepXDbContext database, ServiceProvider services) =
            Build(transport, warmIndicators, seedBars: true);

        await using (services)
        await using (database)
        {
            await warmup.StartAsync(CancellationToken.None);

            // ExecuteTask completion is a real, awaitable signal — no poll needed (gh#407).
            // Task.WhenAny (not a bare await) so a fault is not rethrown here; every fixture in
            // this file separately asserts IsFaulted itself.
            await Task.WhenAny(warmup.ExecuteTask!);

            database.IndicatorValues.Should().BeEmpty(
                "stdio, or HTTP with the switch off, must not replay — a Cowork child would stall");
            warmup.ExecuteTask.Should().NotBeNull();
            warmup.ExecuteTask!.IsFaulted.Should().BeFalse();

            await warmup.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WarmupWritesTheSeries_WhenHttpAndTheSwitchAreOn()
    {
        (IndicatorWarmup warmup, TopstepXDbContext database, ServiceProvider services) =
            Build(McpTransport.Http, warmIndicators: true, seedBars: true);

        await using (services)
        await using (database)
        {
            await warmup.StartAsync(CancellationToken.None);

            // ExecuteTask completion is a real, awaitable signal — no poll needed (gh#407).
            // Task.WhenAny (not a bare await) so a fault is not rethrown here; every fixture in
            // this file separately asserts IsFaulted itself.
            await Task.WhenAny(warmup.ExecuteTask!);

            database.IndicatorValues.Count(v => v.Instrument == "ES").Should().BeGreaterThan(0);
            warmup.ExecuteTask.Should().NotBeNull();
            warmup.ExecuteTask!.IsFaulted.Should().BeFalse();

            await warmup.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ValuesAfterWarmup_MatchASubsequentRebuild_EmptyDiff()
    {
        (IndicatorWarmup warmup, TopstepXDbContext database, ServiceProvider services) =
            Build(McpTransport.Http, warmIndicators: true, seedBars: true);

        await using (services)
        await using (database)
        {
            await warmup.StartAsync(CancellationToken.None);

            // ExecuteTask completion is a real, awaitable signal — no poll needed (gh#407).
            // Task.WhenAny (not a bare await) so a fault is not rethrown here; every fixture in
            // this file separately asserts IsFaulted itself.
            await Task.WhenAny(warmup.ExecuteTask!);
            await warmup.StopAsync(CancellationToken.None);

            database.IndicatorValues.Should().NotBeEmpty("warmup must have written before the confirming rebuild");

            using IServiceScope scope = services.CreateScope();
            IndicatorRebuildResult second = await scope.ServiceProvider
                .GetRequiredService<IndicatorRebuilder>()
                .RebuildAsync(null, CancellationToken.None);

            second.ValuesChanged.Should().Be(0, "the same bars justify the same values (R-2.2)");
            second.SeriesRewritten.Should().Be(0, "a confirming rebuild is not a heal");
        }
    }

    [Fact]
    public async Task AFailureToWarm_DoesNotFaultExecuteTask()
    {
        CollectingLogger logger = new();
        InvalidOperationException refused = new("the store refused the warmup replay");
        (IndicatorWarmup warmup, TopstepXDbContext database, ServiceProvider services) =
            Build(
                McpTransport.Http,
                warmIndicators: true,
                seedBars: true,
                logger: logger,
                rebuilderFactory: _ => throw refused);

        await using (services)
        await using (database)
        {
            await warmup.StartAsync(CancellationToken.None);

            // ExecuteTask completion is a real, awaitable signal — no poll needed (gh#407).
            // Task.WhenAny (not a bare await) so a fault is not rethrown here; every fixture in
            // this file separately asserts IsFaulted itself.
            await Task.WhenAny(warmup.ExecuteTask!);

            warmup.ExecuteTask.Should().NotBeNull();
            warmup.ExecuteTask!.IsFaulted.Should().BeFalse(
                "a faulted ExecuteTask is what Program.AnyFaulted reads, and would turn a stdio EOF into a crash");
            database.IndicatorValues.Should().BeEmpty();
            logger.Errors.Should().Contain(entry =>
                entry.Exception == refused
                && entry.Message.Contains("warmup", StringComparison.OrdinalIgnoreCase));

            await warmup.StopAsync(CancellationToken.None);
            warmup.ExecuteTask.IsFaulted.Should().BeFalse();
        }
    }

    [Fact]
    public async Task AnUnreachableStore_DoesNotFaultTheHost()
    {
        StoreAvailabilityHolder store = new();
        store.Set(StoreAvailability.Unavailable("Nothing answered on the configured connection string."));

        (IndicatorWarmup warmup, TopstepXDbContext database, ServiceProvider services) =
            Build(McpTransport.Http, warmIndicators: true, seedBars: true, store: store);

        await using (services)
        await using (database)
        {
            await warmup.StartAsync(CancellationToken.None);

            // ExecuteTask completion is a real, awaitable signal — no poll needed (gh#407).
            // Task.WhenAny (not a bare await) so a fault is not rethrown here; every fixture in
            // this file separately asserts IsFaulted itself.
            await Task.WhenAny(warmup.ExecuteTask!);

            warmup.ExecuteTask.Should().NotBeNull();
            warmup.ExecuteTask!.IsFaulted.Should().BeFalse(
                "a missing database degrades; it must not take the host down");
            database.IndicatorValues.Should().BeEmpty();

            await warmup.StopAsync(CancellationToken.None);
        }
    }

    private static (
        IndicatorWarmup Warmup,
        TopstepXDbContext Database,
        ServiceProvider Services)
        Build(
            McpTransport transport,
            bool warmIndicators,
            bool seedBars,
            ILogger<IndicatorWarmup>? logger = null,
            StoreAvailabilityHolder? store = null,
            Func<IServiceProvider, IndicatorRebuilder>? rebuilderFactory = null)
    {
        string databaseName = Guid.NewGuid().ToString();
        DbContextOptions<TopstepXDbContext> options = new DbContextOptionsBuilder<TopstepXDbContext>()
            .UseInMemoryDatabase(databaseName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        MarketDataOptions market = new()
        {
            Instruments = "ES,NQ",
            MaxRows = 5_000,
            SessionCloseCentral = "16:00",
            WarmIndicators = warmIndicators,
        };
        McpOptions mcp = new()
        {
            Transport = transport,
            HttpBearerToken = transport == McpTransport.Http ? "a-token" : string.Empty,
        };
        IndicatorOptions indicators = new() { AtrPeriod = 3, RsiPeriod = 3 };
        FakeTimeProvider clock = new(SessionStart.AddDays(1));
        StoreAvailabilityHolder holder = store ?? new();

        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IOptions<MarketDataOptions>>(Options.Create(market));
        services.AddSingleton<IOptions<McpOptions>>(Options.Create(mcp));
        services.AddSingleton<IOptions<IndicatorOptions>>(Options.Create(indicators));
        services.AddSingleton(new InstrumentRegistry(Options.Create(market)));
        services.AddSingleton(BarSessionCalendar.Parse("16:00", []));
        services.AddSingleton<IndicatorCatalog>();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton(holder);
        services.AddScoped(_ => new TopstepXDbContext(options));
        services.AddScoped<IndicatorProjector>();
        if (rebuilderFactory is null)
        {
            services.AddScoped<IndicatorRebuilder>();
        }
        else
        {
            services.AddScoped(rebuilderFactory);
        }

        ServiceProvider provider = services.BuildServiceProvider();
        TopstepXDbContext database = new(options);
        if (seedBars)
        {
            Seed(database, "ES", SeededBars);
        }

        IndicatorWarmup warmup = new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(market),
            Options.Create(mcp),
            holder,
            logger ?? NullLogger<IndicatorWarmup>.Instance);

        return (warmup, database, provider);
    }

    private static void Seed(TopstepXDbContext database, string instrument, int bars)
    {
        for (int i = 0; i < bars; i++)
        {
            decimal drift = i % 3 == 0 ? 1.37m : i % 3 == 1 ? -0.91m : 2.13m;
            decimal close = 5_000m + (i * drift);
            database.Bars.Add(new BarRecord
            {
                Venue = Venue,
                Instrument = instrument,
                ResolutionMinutes = Resolution,
                BucketStart = SessionStart.AddMinutes(Resolution * i),
                Open = close,
                High = close + 1.25m,
                Low = close - 0.75m,
                Close = close,
                Volume = 1_000 + i,
                ContractId = "CON.F.US.EP.Z26",
                RecordedAt = SessionStart,
            });
        }

        database.SaveChanges();
    }

    /// <summary>Captures <see cref="LogLevel.Error"/> so a swallowed warmup failure is visible.</summary>
    private sealed class CollectingLogger : ILogger<IndicatorWarmup>
    {
        public List<(Exception? Exception, string Message)> Errors { get; } = [];

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
            if (logLevel == LogLevel.Error)
            {
                Errors.Add((exception, formatter(state, exception)));
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
