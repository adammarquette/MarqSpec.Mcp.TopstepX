using System.Reflection;
using System.Runtime.ExceptionServices;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Tests.MarketData;
using MarqSpec.Mcp.TopstepX.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// The served half of the calendar-end guard suite — the windows it lets <b>through</b> (gh#387).
/// </summary>
/// <remarks>
/// <para>
/// <c>MarqSpec.Mcp.TopstepX.Tests.Tools.CalendarEndGuardTests</c> is mostly a suite about refusal: a window
/// ending past the last representable instant, one tick past the bound, an instant past the calendar horizon.
/// None of those reaches a store — the window is judged at the tool boundary, before the first venue page and
/// before the first write — so they stay in the unit tier, container-free. The four cases here are the
/// opposite claim, that a window the rule <i>allows</i> is genuinely servable, and the only way to make it is
/// to serve one.
/// </para>
/// <para>
/// Serving one executes the write path: the <c>ON CONFLICT … DO UPDATE</c> bar upsert (<c>UpsertBarsSql</c>)
/// and the coverage ledger write (<c>RecordCoverageSql</c>), inside the unit of work's <c>RepeatableRead</c>
/// transaction. An empty venue answer is not an exception to that — it is recorded as coverage, which is
/// itself a write. The unit tier used to run all of it against <c>Microsoft.EntityFrameworkCore.InMemory</c>,
/// which has neither <c>ON CONFLICT</c> nor transactions, at the price of a <i>second</i> implementation of
/// every write kept in product code solely to serve that provider. Those are deleted, so these four now have
/// exactly one store to run against — see <see cref="SeriesStoreFixture"/>.
/// </para>
/// <para>
/// <b>The drift sweep came down with them for the same reason.</b> It is not itself a served-read test: it
/// asserts that no tool <i>faults</i> at the end of the calendar, and expressly allows a tool to answer. The
/// tools that answer are the ones that anchor on the clock instead of the window, and answering is what puts
/// them through the fill — so the sweep only proves anything where the fill can actually run.
/// </para>
/// <para>
/// <b>The two halves are one suite and read as one.</b> A bound proven only to refuse is a bound nobody has
/// checked for over-reach, so the refusing class upstairs names this one and this one names it back rather
/// than letting the servable side look deleted.
/// </para>
/// </remarks>
[Collection(SeriesStoreCollection.Name)]
public sealed class CalendarEndGuardServedReadTests : IAsyncLifetime
{
    private const string Contract = "CON.F.US.EP.Z26";
    private const int SeededBars = 40;

    /// <summary>
    /// The last instant a one-minute window may end at.
    /// </summary>
    /// <remarks>
    /// Worked out by hand from the rule, not read back from the guard: <see cref="DateTimeOffset.MaxValue"/>
    /// is <c>9999-12-31T23:59:59.9999999Z</c>; less three days for the calendar's reach is
    /// <c>9999-12-28T23:59:59.9999999Z</c>; less two one-minute spans is <c>23:57:59.9999999</c> on that day.
    /// </remarks>
    private static readonly DateTimeOffset _lastServableEndAtOneMinute =
        new DateTimeOffset(9999, 12, 28, 23, 57, 59, TimeSpan.Zero).AddTicks(9_999_999);

    private readonly SeriesStoreFixture _fixture;
    private readonly TopstepXDbContext _database;
    private readonly CountingGateway _gateway;
    private readonly BarCacheService _cache;
    private readonly IndicatorCatalog _catalog;
    private readonly BarSessionCalendar _calendar;
    private readonly FakeTimeProvider _clock;

    /// <summary>Builds the store context and the pieces every tool here is composed from.</summary>
    /// <param name="fixture">The shared container.</param>
    public CalendarEndGuardServedReadTests(SeriesStoreFixture fixture)
    {
        _fixture = fixture;
        _database = fixture.CreateContext();

        _calendar = BarSessionCalendar.Parse("16:00", []);
        _catalog = new IndicatorCatalog(
            Options.Create(new IndicatorOptions { AtrPeriod = 3, RsiPeriod = 3 }), _calendar);
        _clock = new FakeTimeProvider(Bucket(SeededBars).AddHours(2));
        _gateway = new CountingGateway([]);

        IndicatorProjector projector = new(_database, _catalog, NullLogger<IndicatorProjector>.Instance);
        _cache = new BarCacheService(
            _database, _gateway, _calendar, projector, _clock, NullLogger<BarCacheService>.Instance);
    }

    private static DateTimeOffset SessionStart =>
        MarketClock.FromMarket(new DateOnly(2026, 8, 18), new TimeOnly(9, 0)).ToUniversalTime();

    private static DateTimeOffset Bucket(int index) => SessionStart.AddMinutes(5 * index);

    /// <inheritdoc />
    /// <remarks>
    /// <b>Empty the store first, then seed it.</b> The unit-tier fixture seeded in its constructor because
    /// <c>UseInMemoryDatabase(Guid.NewGuid())</c> handed it a store nobody else could have written to. Here
    /// the container is shared, so the emptying has to happen first — and xUnit runs the constructor before
    /// this, so a seed left up there would be truncated away before the test ever saw it.
    /// </remarks>
    public async Task InitializeAsync()
    {
        await _fixture.ResetAsync();

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

        await _database.SaveChangesAsync();
    }

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        _database.Dispose();
        return Task.CompletedTask;
    }

    // ── The reproduction that is served rather than refused ──────────────────────────────────────────

    [Fact]
    public async Task GetBars_ServesAWindowWithinOneVenuePageOfTheEndOfTheCalendar()
    {
        // Reproduction 2, and it is NOT the same bug as the first: this window is comfortably inside the
        // representability bound, so refusing it would be wrong. The fault is one layer down, in
        // BarCacheService.FetchAsync's page walk -- `to = from + page` is computed BEFORE being clamped to
        // range.End, and a page at 60-minute bars is 1,000 hours, roughly forty-two days. A window nine days
        // from the end of the calendar therefore overflows on the add, for a range four hours long.
        //
        // 9999-12-22 is a Wednesday and 12:00-16:00 Central is inside its session, so the detector really
        // does hand FetchAsync an outstanding range here rather than an empty list.
        BarTools tools = Tools();
        DateTimeOffset from = new(9999, 12, 22, 18, 0, 0, TimeSpan.Zero);

        ToolPayloads.BarSeries series =
            await tools.GetBars("ES", 60, from, from.AddHours(4), CancellationToken.None);

        _gateway.BarRequests.Should().Be(
            1, "the four-hour range is shorter than a page, so it is fetched as exactly one slice");
        series.Bars.Should().BeEmpty("the venue holds nothing in year 9999");
    }

    // ── The boundary from the servable side ──────────────────────────────────────────────────────────

    [Fact]
    public async Task AWindowEndingExactlyAtTheLastServableInstant_IsServed()
    {
        // A guard that is red on correct input is not a guard, it is an outage. This window ends on the last
        // instant the rule allows and is genuinely servable: 9999-12-28 is a Tuesday, and the window covers
        // 14:00 Central through the close and on into the evening leg -- which is the leg that makes the
        // calendar map a bucket onto the NEXT trade date, so the three-day term is exercised rather than
        // assumed.
        BarTools tools = Tools();
        DateTimeOffset from = new(9999, 12, 28, 20, 0, 0, TimeSpan.Zero);

        ToolPayloads.BarSeries series =
            await tools.GetBars("ES", 1, from, _lastServableEndAtOneMinute, CancellationToken.None);

        _gateway.BarRequests.Should().BeGreaterThan(
            0, "the window holds expected buckets, so the read reaches the venue for them");
        series.Bars.Should().BeEmpty("the venue holds nothing in year 9999");
    }

    [Fact]
    public async Task AnOrdinaryReadStillAnswers()
    {
        // The other half of the acceptance criterion: nothing changes for a request that was always fine.
        BarTools tools = Tools();

        ToolPayloads.BarSeries series = await tools.GetLatestBars("ES", 5, 10, CancellationToken.None);

        series.Bars.Should().HaveCount(10, "forty five-minute bars were seeded and ten were asked for");
    }

    // ── The drift guard ──────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(ToolGuards.MaxResolutionMinutes)]
    public async Task NoToolFaults_ForAWindowAtTheVeryEndOfTheCalendar(int resolutionMinutes)
    {
        // The criterion this card is measured against, driven rather than argued: no raw
        // ArgumentOutOfRangeException escapes ANY tool at the end of the calendar, at any servable
        // resolution, at the DEFAULT MaxRows. Both ends of the resolution range are swept, because the bound
        // moves with the bar size, and the surface is walked by reflection rather than named, so a tool added
        // tomorrow is covered without anyone remembering this file.
        //
        // THE FILTER IS EVERY TOOL THAT TAKES AN INSTANT, not only those that take a resolution, and that
        // width earned itself immediately: get_market_session takes an atUtc and no window at all, and no
        // window guard was ever going to reach it. A resolution-shaped filter would have swept past it.
        //
        // gh#69, gh#81 and gh#96 each believed this criterion met and each left one axis open. A sweep that
        // permitted "threw something" would have been green through every one of them.
        const BindingFlags Surface = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        Family family = Compose();
        ReferenceTools reference = Reference();
        AccountTools accounts = new(_gateway, Guards());

        List<MethodInfo> takingAnInstant =
        [
            .. typeof(BarTools).Assembly.GetTypes()
                .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
                .SelectMany(t => t.GetMethods(Surface))
                .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
                .Where(m => m.GetParameters().Any(p =>
                    p.ParameterType == typeof(DateTimeOffset)
                    || p.ParameterType == typeof(DateTimeOffset?)
                    || p.Name == "resolutionMinutes")),
        ];

        takingAnInstant.Should().HaveCountGreaterThanOrEqualTo(
            9, "the reflection filter must actually match the surface it is guarding");

        foreach (MethodInfo tool in takingAnInstant)
        {
            _gateway.ResetCounters();

            object instance = tool.DeclaringType! == typeof(ReferenceTools) ? reference
                : tool.DeclaringType! == typeof(AccountTools) ? accounts
                : family.Instance(tool.DeclaringType!);

            Exception? thrown = await Capture(() => Invoke(tool, instance, resolutionMinutes));

            // A tool may legitimately answer here -- get_latest_bars and get_key_levels take no window at
            // all, and anchor on the clock instead. What none of them may do is FAULT: the assertion is on
            // the TYPE, because an ArgumentOutOfRangeException is also "an exception" and is the exact shape
            // this boundary must never show a caller.
            if (thrown is not null)
            {
                thrown.Should().BeOfType<McpException>(
                    tool.Name + " let a caller's mistake past the boundary as a fault rather than naming it: "
                    + thrown.GetType().Name + ": " + thrown.Message);

                _gateway.BarRequests.Should().Be(
                    0, tool.Name + " refused the request after spending a venue bar call on it");
                _gateway.ContractRequests.Should().Be(
                    0, tool.Name + " refused the request after resolving a contract at the venue");
            }
        }
    }

    /// <summary>The options every fixture here builds against — the DEFAULT row cap, deliberately.</summary>
    /// <returns>The options.</returns>
    private static IOptions<MarketDataOptions> Defaults() => Options.Create(new MarketDataOptions
    {
        Instruments = "ES,NQ",
        SessionCloseCentral = "16:00",
    });

    /// <summary>The guards, at the default row cap.</summary>
    /// <returns>The guards.</returns>
    private static ToolGuards Guards() => new(Defaults());

    /// <summary>Builds the bar tools at the default row cap.</summary>
    /// <returns>The tools.</returns>
    private BarTools Tools() => Compose().Bars;

    /// <summary>Builds the reference tools — the ones that take an instant and no window.</summary>
    /// <returns>The reference tools.</returns>
    private ReferenceTools Reference() =>
        new(new InstrumentRegistry(Defaults()), _calendar, _gateway, Defaults(), _clock);

    /// <summary>
    /// Builds every market-data tool type the reflection sweep can land on, at the default row cap.
    /// </summary>
    /// <returns>The family.</returns>
    /// <remarks>
    /// Five types now, not one (gh#414). The sweep maps a declaring type to an instance, so every one of
    /// them has to be buildable here — <see cref="Family.Instance"/> throws by name for a type that is not,
    /// rather than letting it drop out of the sweep.
    /// </remarks>
    private Family Compose()
    {
        InstrumentResolver resolver = new(new InstrumentRegistry(Defaults()), new StoreAvailabilityHolder());
        ToolGuards guards = Guards();
        VolumeFrontReader front = new(new TapeVolumeFrontService(_database, _gateway, _calendar));

        BarTools bars = new(resolver, _cache, guards, _clock);

        IndicatorTools indicators = new(
            resolver,
            _database,
            _catalog,
            new IndicatorCacheService(
                _database,
                _catalog,
                new IndicatorProjector(_database, _catalog, NullLogger<IndicatorProjector>.Instance),
                _clock,
                NullLogger<IndicatorCacheService>.Instance),
            _gateway,
            guards);

        KeyLevelTools keyLevels = new(
            resolver,
            _database,
            _catalog,
            new LevelMethodCatalog(_calendar),
            _gateway,
            guards,
            new VolumeProfileService(_database),
            Options.Create(new KeyLevelDetectionOptions()));

        TapeTools tape = new(
            resolver,
            _database,
            _gateway,
            guards,
            new TapeAvailabilityHolder(),
            new VolumeProfileService(_database),
            front,
            new FootprintCacheService(
                _database,
                new FootprintProjector(_database, NullLogger<FootprintProjector>.Instance),
                _clock,
                NullLogger<FootprintCacheService>.Instance));

        ContractRollTools roll = new(
            resolver, _database, _gateway, new LevelMethodCatalog(_calendar), front, _clock);

        SnapshotTools snapshot = new(
            bars, indicators, keyLevels, Reference(), new IndicatorCatalogNames(_catalog), _clock);

        return new Family(bars, indicators, keyLevels, tape, roll, snapshot);
    }

    /// <summary>Every market-data tool type this fixture can hand the sweep.</summary>
    /// <param name="Bars">The bar tools.</param>
    /// <param name="Indicators">The indicator tools.</param>
    /// <param name="KeyLevels">The key-level tools.</param>
    /// <param name="Tape">The tape tools.</param>
    /// <param name="Roll">The contract-roll tools.</param>
    /// <param name="Snapshot">The composed snapshot tool.</param>
    private sealed record Family(
        BarTools Bars,
        IndicatorTools Indicators,
        KeyLevelTools KeyLevels,
        TapeTools Tape,
        ContractRollTools Roll,
        SnapshotTools Snapshot)
    {
        /// <summary>Hands back the instance for a declaring type, or says what has to be added here.</summary>
        /// <param name="type">The tool type the sweep found.</param>
        /// <returns>An instance to invoke.</returns>
        public object Instance(Type type) =>
            type == typeof(BarTools) ? Bars
            : type == typeof(IndicatorTools) ? Indicators
            : type == typeof(KeyLevelTools) ? KeyLevels
            : type == typeof(TapeTools) ? Tape
            : type == typeof(ContractRollTools) ? Roll
            : type == typeof(SnapshotTools) ? Snapshot
            : throw new InvalidOperationException(
                type.Name + " takes an instant and this fixture cannot build it. "
                + "Add it here rather than narrowing the sweep -- the sweep is the point.");
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

    /// <summary>Invokes a tool with every instant argument at the very end of the calendar.</summary>
    /// <param name="tool">The tool method.</param>
    /// <param name="instance">The tool instance.</param>
    /// <param name="resolutionMinutes">The resolution to sweep at.</param>
    /// <returns>The completed call.</returns>
    private static async Task Invoke(MethodInfo tool, object instance, int resolutionMinutes)
    {
        object?[] arguments = [.. tool.GetParameters().Select(p => Filler(p, resolutionMinutes))];

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

    /// <summary>A value for one tool argument, chosen to sit at the very end of the calendar.</summary>
    /// <param name="parameter">The parameter to fill.</param>
    /// <param name="resolutionMinutes">The resolution to sweep at.</param>
    /// <returns>The value to pass.</returns>
    private static object? Filler(ParameterInfo parameter, int resolutionMinutes) => parameter.Name switch
    {
        "resolutionMinutes" when parameter.ParameterType == typeof(int[]) => new[] { resolutionMinutes },
        "resolutionMinutes" => resolutionMinutes,
        "indicator" => "atr",
        "symbol" => "ES",

        // One tick wide, at the very end. That is the window that spans ZERO buckets and so clears every cap
        // this boundary had before this card -- the whole point of the sweep.
        "fromUtc" => DateTimeOffset.MaxValue.AddTicks(-1),
        "toUtc" => DateTimeOffset.MaxValue,
        "asOfUtc" => DateTimeOffset.MaxValue,
        "atUtc" => DateTimeOffset.MaxValue,

        // FALSE, so get_orders takes its windowed branch. Left true it ignores fromUtc and toUtc entirely,
        // and the sweep would cover that tool by not exercising it.
        "openOnly" => false,
        _ => Blank(parameter),
    };

    /// <summary>A value for an argument the sweep has no opinion about.</summary>
    /// <param name="parameter">The parameter to fill.</param>
    /// <returns>The value to pass.</returns>
    private static object? Blank(ParameterInfo parameter)
    {
        Type type = parameter.ParameterType;

        // Counts are left small and legal on purpose. A count over the row cap would be refused on the count
        // and the tool would never reach the arithmetic this sweep is about.
        return type == typeof(CancellationToken) ? CancellationToken.None
            : type == typeof(int) ? 10
            : type == typeof(bool) ? true
            : type == typeof(string) ? "ES"
            : type == typeof(DateTimeOffset) ? DateTimeOffset.MaxValue
            : Nullable.GetUnderlyingType(type) is not null || !type.IsValueType ? null
            : throw new InvalidOperationException(
                "No filler for " + type.Name + " " + parameter.Name + ". Add one rather than skipping the "
                + "tool: an unfilled argument is a tool the sweep silently stops covering.");
    }
}
