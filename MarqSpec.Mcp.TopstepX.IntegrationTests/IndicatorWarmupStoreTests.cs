using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;

// TODO(gh#387): `BackgroundServiceTestSupport` is `internal` to MarqSpec.Mcp.TopstepX.Tests and is NOT
// compiled into this assembly yet, so this file does not build until it is. It is needed in BOTH tiers now
// that gh#387 has split IndicatorWarmupTests across them. Link it the way `CountingGateway.cs` is linked in
// this project's .csproj — ONE source compiled into both assemblies — rather than copying it: the whole
// point of gh#407 (PR #417) was that nine call sites share one bound, and a second copy is a second bound
// free to drift back into the fixed 2-second budget that flaked across three sessions. Do NOT reintroduce a
// wall-clock budget here.
using MarqSpec.Mcp.TopstepX.Tests.MarketData;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// The half of startup warmup that <b>writes</b> — HTTP with the switch on — and the claim that what it
/// wrote is what a subsequent <c>rebuild-indicators</c> would write.
/// </summary>
/// <remarks>
/// <para>
/// <b>Split out of <c>IndicatorWarmupTests</c> by gh#387.</b> The cases that stayed in the unit tier are the
/// ones that must NOT replay — stdio, HTTP with the switch off, a warmup that throws, an unreachable store —
/// and each of them asserts an empty <c>IndicatorValues</c> and an unfaulted <c>ExecuteTask</c>. Nothing
/// there reaches a store, so the in-memory provider is still honest about them. The two below serve the
/// replay for real: <c>IndicatorRebuilder</c> goes out as <c>UpsertValuesSql</c>, an
/// <c>ON CONFLICT … DO UPDATE</c>, inside the one <c>RepeatableRead</c> transaction
/// <c>SeriesUnitOfWork</c> now always opens.
/// </para>
/// <para>
/// <b>Why they could not stay.</b> That write used to have a second implementation —
/// <c>IndicatorProjector.WriteInMemory</c> — carried in product code purely so the unit tier's provider
/// could pretend. No production process executed it, and it disagreed with the real path about what a write
/// count means: it returned <c>pending.Count</c> where the store returns rows actually affected. That is
/// exactly the quantity <c>ValuesChanged</c> and <c>SeriesRewritten</c> are read off below, so the empty-diff
/// claim was being made against the wrong number. The stand-in is gone and the projector now throws
/// unconditionally without a transaction, so these run against a real Postgres or they do not run.
/// </para>
/// <para>
/// The claims themselves are unchanged. A warmup writes the series, and the numbers it writes are the same
/// numbers a confirming rebuild would write — a confirming rebuild is an empty diff (<c>R-2.2</c>), not a
/// heal. A faulted <c>ExecuteTask</c> is what <c>Program.AnyFaulted</c> reads, and would turn an ordinary
/// stdio EOF into a crash (gh#76).
/// </para>
/// </remarks>
[Collection(SeriesStoreCollection.Name)]
public sealed class IndicatorWarmupStoreTests(SeriesStoreFixture fixture) : IAsyncLifetime
{
    private const string Venue = "test";
    private const int Resolution = 5;
    private const int SeededBars = 40;

    private readonly SeriesStoreFixture _fixture = fixture;
    private readonly TopstepXDbContext _database = fixture.CreateContext();

    /// <inheritdoc />
    public Task InitializeAsync() => _fixture.ResetAsync();

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        _database.Dispose();
        return Task.CompletedTask;
    }

    private static DateTimeOffset SessionStart =>
        MarketClock.FromMarket(new DateOnly(2026, 8, 18), new TimeOnly(9, 0)).ToUniversalTime();

    [Fact]
    public async Task WarmupWritesTheSeries_WhenHttpAndTheSwitchAreOn()
    {
        (IndicatorWarmup warmup, TopstepXDbContext database, ServiceProvider services) =
            Build(McpTransport.Http, warmIndicators: true, seedBars: true);

        await using (services)
        await using (database)
        {
            await warmup.StartAsync(CancellationToken.None);

            // ExecuteTask completion is a real, awaitable signal — no poll needed, and bounded
            // so a hang still fails fast rather than taking the test host down (gh#407).
            await BackgroundServiceTestSupport.AwaitCompletionAsync(warmup.ExecuteTask!);

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

            // ExecuteTask completion is a real, awaitable signal — no poll needed, and bounded
            // so a hang still fails fast rather than taking the test host down (gh#407).
            await BackgroundServiceTestSupport.AwaitCompletionAsync(warmup.ExecuteTask!);
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

    /// <summary>
    /// Composes the warmup, its scope factory and the store it warms, over the shared container.
    /// </summary>
    /// <param name="transport">The configured transport.</param>
    /// <param name="warmIndicators">The configured warmup switch.</param>
    /// <param name="seedBars">Whether to seed the bars the warmup has something to replay over.</param>
    /// <returns>The warmup, the store handle the cases read through, and the provider.</returns>
    /// <remarks>
    /// <para>
    /// <b>The scoped context is the fixture's, not a private in-memory name.</b> The unit-tier copy of this
    /// builder handed every scope a <c>UseInMemoryDatabase(Guid.NewGuid())</c> store, which is what gave each
    /// case a store nobody else had written to. Here that isolation comes from
    /// <see cref="SeriesStoreFixture.ResetAsync"/> between tests instead, and every scope the warmup opens
    /// reaches the one container.
    /// </para>
    /// <para>
    /// <b>Trimmed to what these two cases need.</b> The unit-tier builder also takes a capturing logger, a
    /// pre-set <c>StoreAvailabilityHolder</c> and a throwing rebuilder factory; all three belong to the cases
    /// that must not replay, and those stayed behind with them.
    /// </para>
    /// </remarks>
    private (IndicatorWarmup Warmup, TopstepXDbContext Database, ServiceProvider Services) Build(
        McpTransport transport,
        bool warmIndicators,
        bool seedBars)
    {
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
        StoreAvailabilityHolder holder = new();

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
        services.AddScoped(_ => _fixture.CreateContext());
        services.AddScoped<IndicatorProjector>();
        services.AddScoped<IndicatorRebuilder>();

        ServiceProvider provider = services.BuildServiceProvider();
        if (seedBars)
        {
            Seed(_database, "ES", SeededBars);
        }

        IndicatorWarmup warmup = new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(market),
            Options.Create(mcp),
            holder,
            NullLogger<IndicatorWarmup>.Instance);

        return (warmup, _database, provider);
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
}
