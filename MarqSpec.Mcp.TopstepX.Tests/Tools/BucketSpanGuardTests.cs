using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Tests.MarketData;
using MarqSpec.Mcp.TopstepX.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MarqSpec.Mcp.TopstepX.Tests.Tools;

/// <summary>
/// A read is refused when its window spans more buckets than one gap-detection pass will enumerate (gh#96).
/// </summary>
/// <remarks>
/// <para>
/// <c>MarketDataOptions.MaxRows</c> and <c>BarGapDetector.MaxBucketsPerPass</c> are <b>two independent caps on
/// the same quantity</b>, and only one of them is an <see cref="McpException"/>. The row cap is operator
/// configuration ranging to 1,000,000; the detection cap is a fixed 250,000. Configure the first above the
/// second and a request that is legal on every axis the tool boundary checks still faults one layer down, and
/// leaves the boundary as a raw <see cref="ArgumentOutOfRangeException"/> — a stack-shaped failure for a
/// mistake this server can name exactly.
/// </para>
/// <para>
/// <b>The exception TYPE is the assertion throughout, never merely that something threw.</b> An
/// <see cref="ArgumentOutOfRangeException"/> also throws, and it is precisely what must stay off this
/// boundary — a fixture that asserted "threw" would have been green against the bug it was written for.
/// </para>
/// <para>
/// <b>The row cap is not the only way in.</b> <c>get_latest_bars</c> never validates a window: it sizes one
/// from a count, reaching <b>four bar spans per bar wanted</b> plus four days. A <c>MaxRows</c> comfortably
/// inside the detection cap therefore still names a window four times over it — which is why bounding the
/// configuration alone would have closed one of the two reproductions below and left the other exactly where
/// it was.
/// </para>
/// </remarks>
public sealed class BucketSpanGuardTests : IDisposable
{
    private const string Contract = "CON.F.US.EP.Z26";
    private const int SeededBars = 40;

    /// <summary>
    /// The largest look-back count at one-minute resolution that still fits one detection pass.
    /// </summary>
    /// <remarks>
    /// Hand-computed, not read back from the implementation: the reach is <c>4 × count</c> bar spans plus four
    /// days, so at one-minute bars it is <c>4 × 61,060 + 5,760 = 250,000</c> buckets — the cap exactly. One
    /// more bar asks for 250,004 and is refused.
    /// </remarks>
    private const int LookbackAtTheCap = 61_060;

    private readonly TopstepXDbContext _database;
    private readonly CountingGateway _gateway;
    private readonly BarCacheService _cache;
    private readonly IndicatorCatalog _catalog;
    private readonly BarSessionCalendar _calendar;
    private readonly FakeTimeProvider _clock;

    public BucketSpanGuardTests()
    {
        _database = new TopstepXDbContext(
            new DbContextOptionsBuilder<TopstepXDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics
                    .InMemoryEventId.TransactionIgnoredWarning))
                .Options);

        for (int i = 0; i < SeededBars; i++)
        {
            _database.Bars.Add(new BarRecord
            {
                Venue = "test",
                Instrument = "ES",
                ResolutionMinutes = 5,
                BucketStart = Bucket(i),
                Open = 100m,
                High = 101m,
                Low = 99m,
                Close = 100m,
                Volume = 1_000,
                ContractId = Contract,
                RecordedAt = SessionStart,
            });
        }

        _database.SaveChanges();

        _calendar = BarSessionCalendar.Parse("16:00", []);
        _catalog = new IndicatorCatalog(
            Options.Create(new IndicatorOptions { AtrPeriod = 3, RsiPeriod = 3 }), _calendar);
        _clock = new FakeTimeProvider(Bucket(SeededBars).AddHours(2));
        _gateway = new CountingGateway([]);

        IndicatorProjector projector = new(_database, _catalog, NullLogger<IndicatorProjector>.Instance);
        _cache = new BarCacheService(
            _database, _gateway, _calendar, projector, _clock, NullLogger<BarCacheService>.Instance);
    }

    public void Dispose() => _database.Dispose();

    private static DateTimeOffset SessionStart =>
        MarketClock.FromMarket(new DateOnly(2026, 8, 18), new TimeOnly(9, 0)).ToUniversalTime();

    private static DateTimeOffset Bucket(int index) => SessionStart.AddMinutes(5 * index);

    // ── The two reproductions ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetBars_RefusesAWindowPastTheDetectionCap_WhenTheRowCapIsConfiguredAboveIt()
    {
        // Reproduction 1. Legal on every axis the boundary checked: MaxRows is 300,000, its own [Range] tops
        // out at 1,000,000, and the window spans exactly 300,000 one-minute buckets -- so ValidateWindow's
        // `buckets > MaxRows` is false and the read went on to fault inside BarGapDetector.ExpectedBuckets.
        MarketDataTools tools = WithRowCap(300_000);
        DateTimeOffset from = SessionStart;

        Func<Task> call = () => tools.GetBars(
            "ES", 1, from, from.AddMinutes(300_000), CancellationToken.None);

        (await call.Should().ThrowAsync<McpException>())
            .WithMessage("*300000*", "the refusal names the bucket count that was asked for")
            .WithMessage("*250000*", "and the cap it is over, so the operator can see both numbers");

        // Refused, not truncated, and refused before anything was spent. A shortened series is
        // indistinguishable from a complete one -- the failure ValidateWindow already refuses to commit.
        _gateway.BarRequests.Should().Be(0, "the span is judged before the first page is read");
        _gateway.ContractRequests.Should().Be(0, "and before the contract behind it is resolved");
    }

    [Fact]
    public async Task GetLatestBars_RefusesACountWhoseReachIsPastTheDetectionCap()
    {
        // Reproduction 2, and the one that decides the design. MaxRows is 100,000 -- comfortably INSIDE the
        // 250,000 detection cap -- and the count is exactly at it. Nothing here is out of range on any axis,
        // and the window still needs 4 x 100,000 + 5,760 = 405,760 buckets, because the look-back reaches
        // four bar spans per bar wanted plus four days. Bounding MaxRows against MaxBucketsPerPass would
        // leave this exactly as it was.
        MarketDataTools tools = WithRowCap(100_000);

        Func<Task> call = () => tools.GetLatestBars("ES", 1, 100_000, CancellationToken.None);

        (await call.Should().ThrowAsync<McpException>())
            .WithMessage("*100000*", "the refusal names the count the caller passed")
            .WithMessage("*405760*", "and the buckets that count actually needs")
            .WithMessage("*250000*", "and the cap it is over");

        _gateway.BarRequests.Should().Be(0, "the reach is judged before the first page is read");
        _gateway.ContractRequests.Should().Be(0, "and before the contract behind it is resolved");
    }

    // ── The boundary from the servable side ──────────────────────────────────────────────────────────

    [Fact]
    public void AWindowExactlyAtTheDetectionCap_PassesThrough()
    {
        // A guard that is red on correct input is not a guard, it is an outage. The cap is inclusive on both
        // sides of the seam: ExpectedBuckets refuses `candidates > MaxBucketsPerPass`, so exactly the cap is
        // servable and this must agree with it rather than shaving a bucket off.
        ToolGuards guards = new(Options.Create(new MarketDataOptions
        {
            Instruments = "ES",
            MaxRows = 300_000,
            SessionCloseCentral = "16:00",
        }));

        DateTimeOffset from = SessionStart;
        BarRange window = guards.ValidateWindow(
            from, from.AddMinutes(BarGapDetector.MaxBucketsPerPass), 1);

        window.Start.Should().Be(from);
        window.End.Should().Be(from.AddMinutes(BarGapDetector.MaxBucketsPerPass));
    }

    [Fact]
    public void AWindowOneBucketPastTheDetectionCap_IsRefused()
    {
        ToolGuards guards = new(Options.Create(new MarketDataOptions
        {
            Instruments = "ES",
            MaxRows = 300_000,
            SessionCloseCentral = "16:00",
        }));

        DateTimeOffset from = SessionStart;

        Action validate = () => guards.ValidateWindow(
            from, from.AddMinutes(BarGapDetector.MaxBucketsPerPass + 1), 1);

        validate.Should().Throw<McpException>()
            .WithMessage("*250001*")
            .WithMessage("*250000*");
    }

    [Fact]
    public void TheTighterOfTheTwoCapsIsTheOneReported()
    {
        // Two caps on one quantity means two possible messages, and the useful one is the tighter. With the
        // default row cap of 5,000 a 300,000-bucket window is over BOTH; naming the detection cap would send
        // an operator to a constant they cannot change, past the one they configured.
        ToolGuards guards = new(Options.Create(new MarketDataOptions
        {
            Instruments = "ES",
            MaxRows = 5_000,
            SessionCloseCentral = "16:00",
        }));

        DateTimeOffset from = SessionStart;

        Action validate = () => guards.ValidateWindow(from, from.AddMinutes(300_000), 1);

        // The assertion is on wording only the row cap produces, AND on the absence of the other message.
        // A bare "*5000*" does not pin the ordering: WithMessage is a containment match, and the detection
        // cap's own refusal names 250000 -- which contains "5000" at characters 2 through 5. Reorder the two
        // checks in ValidateWindow and that pattern still matches, leaving the only test that holds the
        // ordering green through the exact regression it exists to catch.
        McpException refusal = validate.Should().Throw<McpException>()
            .WithMessage("*cap of 5000*", "the refusal is the row cap's, naming the knob the operator set")
            .Which;

        refusal.Message.Should().NotContain(
            "gap-detection pass",
            "naming the fixed cap would send them past the knob they configured");
    }

    [Fact]
    public void TheLookbackReach_ExactlyAtTheDetectionCap_PassesThrough()
    {
        // Hand-computed: 4 x 61,060 one-minute spans plus four days is 250,000 buckets exactly.
        DateTimeOffset end = Bucket(SeededBars);

        BarRange window = ToolGuards.LookbackWindow(end, 1, LookbackAtTheCap);

        window.End.Should().Be(end);
        (window.End - window.Start).Should().Be(
            TimeSpan.FromMinutes(BarGapDetector.MaxBucketsPerPass),
            "the reach at this count is the cap exactly, and the cap is inclusive");
    }

    [Fact]
    public void TheLookbackReach_OneBarPastTheDetectionCap_IsRefused()
    {
        DateTimeOffset end = Bucket(SeededBars);

        Action size = () => ToolGuards.LookbackWindow(end, 1, LookbackAtTheCap + 1);

        size.Should().Throw<McpException>()
            .WithMessage("*" + (LookbackAtTheCap + 1).ToString(CultureInfo.InvariantCulture) + "*")
            .WithMessage("*250004*")
            .WithMessage("*250000*");
    }

    [Fact]
    public async Task AnOrdinaryReadStillAnswers()
    {
        // The other half of the acceptance criterion: nothing changes for a request that was always fine.
        MarketDataTools tools = WithRowCap(5_000);

        ToolPayloads.BarSeries series = await tools.GetLatestBars("ES", 5, 10, CancellationToken.None);

        series.Bars.Should().HaveCount(10, "forty five-minute bars were seeded and ten were asked for");
    }

    // ── The drift guard ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoToolTakingAResolution_FaultsAtTheTopOfTheRowCapsDeclaredRange()
    {
        // The criterion this card is measured against, driven rather than argued: no raw
        // ArgumentOutOfRangeException escapes ANY tool for ANY MaxRows inside [Range(1, 1_000_000)], at any
        // servable resolution. So the sweep runs at the TOP of that range, at the FINEST resolution -- the
        // corner where the row cap is furthest above the detection cap -- and walks the surface by reflection
        // rather than naming tools, so a tool added tomorrow is covered without anyone remembering this file.
        //
        // gh#81's equivalent criterion was believed met and was not; the gap it left is this card. A sweep
        // that permits "threw something" would have been green through both.
        const BindingFlags Surface = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        MarketDataTools marketData = WithRowCap(1_000_000);
        SnapshotTools snapshot = SnapshotFor(marketData, 1_000_000);

        List<MethodInfo> takingAResolution =
        [
            .. typeof(MarketDataTools).Assembly.GetTypes()
                .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
                .SelectMany(t => t.GetMethods(Surface))
                .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
                .Where(m => m.GetParameters().Any(p => p.Name == "resolutionMinutes")),
        ];

        takingAResolution.Should().HaveCountGreaterThanOrEqualTo(
            6, "the reflection filter must actually match the surface it is guarding");

        foreach (MethodInfo tool in takingAResolution)
        {
            _gateway.ResetCounters();

            object instance = tool.DeclaringType! == typeof(MarketDataTools) ? marketData
                : tool.DeclaringType! == typeof(SnapshotTools) ? snapshot
                : throw new InvalidOperationException(
                    tool.DeclaringType!.Name + " takes a resolutionMinutes and this fixture cannot build it. "
                    + "Add it here rather than narrowing the sweep -- the sweep is the point.");

            Exception? thrown = await Capture(() => Invoke(tool, instance));

            // A tool may legitimately answer here -- get_indicator_at and get_key_levels read the store
            // directly and never size a window. What none of them may do is FAULT: the assertion is on the
            // TYPE, because an ArgumentOutOfRangeException is also "an exception" and is the exact shape this
            // boundary must never show a caller.
            if (thrown is not null)
            {
                thrown.Should().BeOfType<McpException>(
                    tool.Name + " let a caller's mistake past the boundary as a fault rather than naming it: "
                    + thrown.GetType().Name + ": " + thrown.Message);
            }

            _gateway.BarRequests.Should().Be(
                0, tool.Name + " read bars from the venue on a request it cannot serve");
            _gateway.ContractRequests.Should().Be(
                0, tool.Name + " resolved a contract at the venue on a request it cannot serve");
        }
    }

    /// <summary>Builds the market-data tools against a given row cap.</summary>
    /// <param name="maxRows">The cap to build against.</param>
    /// <returns>The tools.</returns>
    private MarketDataTools WithRowCap(int maxRows)
    {
        IOptions<MarketDataOptions> capped = Options.Create(new MarketDataOptions
        {
            Instruments = "ES,NQ",
            MaxRows = maxRows,
            SessionCloseCentral = "16:00",
        });

        return new MarketDataTools(
            _cache,
            _database,
            new InstrumentRegistry(capped),
            _catalog,
            new IndicatorCacheService(
                _database,
                _catalog,
                new IndicatorProjector(_database, _catalog, NullLogger<IndicatorProjector>.Instance),
                _clock,
                NullLogger<IndicatorCacheService>.Instance),
            new LevelMethodCatalog(),
            _gateway,
            new ToolGuards(capped),
            new StoreAvailabilityHolder(),
            _clock,
            Options.Create(new KeyLevelDetectionOptions()));
    }

    /// <summary>Builds the composed tool over the same row cap.</summary>
    /// <param name="marketData">The market-data tools it composes.</param>
    /// <param name="maxRows">The cap to build against.</param>
    /// <returns>The snapshot tool.</returns>
    private SnapshotTools SnapshotFor(MarketDataTools marketData, int maxRows)
    {
        IOptions<MarketDataOptions> capped = Options.Create(new MarketDataOptions
        {
            Instruments = "ES,NQ",
            MaxRows = maxRows,
            SessionCloseCentral = "16:00",
        });

        return new SnapshotTools(
            marketData,
            new ReferenceTools(new InstrumentRegistry(capped), _calendar, _gateway, capped, _clock),
            new IndicatorCatalogNames(_catalog));
    }

    /// <summary>Runs a call and hands back whatever it threw, if anything.</summary>
    /// <param name="call">The call.</param>
    /// <returns>The exception, or <see langword="null"/> when the call answered.</returns>
    private static async Task<Exception?> Capture(Func<Task> call)
    {
        try
        {
            await call();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>Invokes a tool at the finest resolution with every count argument at the row cap.</summary>
    /// <param name="tool">The tool method.</param>
    /// <param name="instance">The tool instance.</param>
    /// <returns>The completed call.</returns>
    private static async Task Invoke(MethodInfo tool, object instance)
    {
        object?[] arguments = [.. tool.GetParameters().Select(Filler)];

        try
        {
            if (tool.Invoke(instance, arguments) is Task running)
            {
                await running;
            }
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            // Reflection wraps whatever the tool threw. The wrapper is not the fact under test, and rethrowing
            // this way keeps the original stack.
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
        }
    }

    /// <summary>A value for one tool argument, chosen to sit at the top of every declared bound.</summary>
    /// <param name="parameter">The parameter to fill.</param>
    /// <returns>The value to pass.</returns>
    private static object? Filler(ParameterInfo parameter) => parameter.Name switch
    {
        "resolutionMinutes" when parameter.ParameterType == typeof(int[]) => new[] { 1 },
        "resolutionMinutes" => 1,
        "indicator" => "atr",
        "symbol" => "ES",

        // Exactly MaxRows, so every count passes ValidateCount and the window is exactly MaxRows buckets
        // wide -- the request that is legal on every axis the boundary checks, which is the whole point.
        "fromUtc" => SessionStart,
        "toUtc" => SessionStart.AddMinutes(1_000_000),
        _ => Blank(parameter),
    };

    /// <summary>A value for an argument the sweep has no opinion about.</summary>
    /// <param name="parameter">The parameter to fill.</param>
    /// <returns>The value to pass.</returns>
    private static object? Blank(ParameterInfo parameter)
    {
        Type type = parameter.ParameterType;

        return type == typeof(CancellationToken) ? CancellationToken.None
            : type == typeof(int) ? 1_000_000
            : type == typeof(bool) ? true
            : type == typeof(string) ? "ES"
            : type == typeof(DateTimeOffset) ? SessionStart
            : Nullable.GetUnderlyingType(type) is not null || !type.IsValueType ? null
            : throw new InvalidOperationException(
                "No filler for " + type.Name + " " + parameter.Name + ". Add one rather than skipping the "
                + "tool: an unfilled argument is a tool the sweep silently stops covering.");
    }
}
