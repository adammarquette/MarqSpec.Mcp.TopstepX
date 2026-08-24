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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MarqSpec.Mcp.TopstepX.Tests.Tools;

/// <summary>
/// A window the calendar cannot represent is refused at the tool boundary, not faulted below it (gh#110).
/// </summary>
/// <remarks>
/// <para>
/// This is a bound on <b>representability</b>, not on size, which is why it survived gh#69, gh#81 and gh#96.
/// Every one of those bounded how <i>much</i> a caller may ask for; none of them bounded <i>where</i>. A
/// window one tick wide at the end of year 9999 is inside <c>MaxRows</c> at its default of 5,000 and inside
/// <see cref="BarGapDetector.MaxBucketsPerPass"/> — it spans zero buckets — and the machinery that serves it
/// still reaches past <see cref="DateTimeOffset.MaxValue"/> and throws.
/// </para>
/// <para>
/// <b>The exception TYPE is the assertion throughout, never merely that something threw.</b> An
/// <see cref="ArgumentOutOfRangeException"/> also throws, and it is precisely what must stay off this
/// boundary — a fixture that asserted "threw" would have been green against both bugs below.
/// </para>
/// <para>
/// <b>Serving a window reaches past its end, and that reach is what the bound is made of.</b> The bucket grid
/// is aligned <i>up</i> from the window's start, so a window narrower than one bucket names a first bucket up
/// to a full span past its own end; the gap detector then tests one span beyond the last bucket it yields;
/// and <see cref="BarSessionCalendar"/> maps an evening bucket onto the <b>next</b> trade date and expresses
/// that date's close in Central wall-clock time. Two bar spans plus three days covers all of it.
/// </para>
/// </remarks>
public sealed class CalendarEndGuardTests : IDisposable
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

    private readonly TopstepXDbContext _database;
    private readonly CountingGateway _gateway;
    private readonly BarCacheService _cache;
    private readonly IndicatorCatalog _catalog;
    private readonly BarSessionCalendar _calendar;
    private readonly FakeTimeProvider _clock;

    public CalendarEndGuardTests()
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
    public async Task GetBars_RefusesAWindowAtTheEndOfTheCalendar_AtTheDefaultRowCap()
    {
        // Reproduction 1, and it fires at the DEFAULT MaxRows, so it is not a misconfiguration. The window is
        // under one bar wide, so it spans ZERO buckets and clears both caps -- and BarGapDetector.AlignUp
        // then rounds its start up to the next one-minute boundary on the fixed UTC grid, which is past the
        // end of year 9999. The DateTimeOffset that alignment builds is what threw.
        MarketDataTools tools = Tools();
        DateTimeOffset from = new(9999, 12, 31, 23, 59, 59, TimeSpan.Zero);
        DateTimeOffset to = from.AddTicks(9_999_999);

        Func<Task> call = () => tools.GetBars("ES", 1, from, to, CancellationToken.None);

        (await call.Should().ThrowAsync<McpException>())
            .WithMessage("*9999-12-31T23:59:59.9999999*", "the refusal names the toUtc the caller passed")
            .WithMessage(
                "*9999-12-28T23:57:59.9999999*",
                "and the last instant a one-minute window may end at, so the caller can move to it");

        // Refused, not moved back. A window quietly clamped to the last representable bucket answers with a
        // series short at one end and says so nowhere -- the failure ValidateWindow already refuses to commit.
        _gateway.BarRequests.Should().Be(0, "the window is judged before the first page is read");
        _gateway.ContractRequests.Should().Be(0, "and before the contract behind it is resolved");
    }

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
        MarketDataTools tools = Tools();
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
        MarketDataTools tools = Tools();
        DateTimeOffset from = new(9999, 12, 28, 20, 0, 0, TimeSpan.Zero);

        ToolPayloads.BarSeries series =
            await tools.GetBars("ES", 1, from, _lastServableEndAtOneMinute, CancellationToken.None);

        _gateway.BarRequests.Should().BeGreaterThan(
            0, "the window holds expected buckets, so the read reaches the venue for them");
        series.Bars.Should().BeEmpty("the venue holds nothing in year 9999");
    }

    [Fact]
    public async Task AWindowEndingOneTickPastTheLastServableInstant_IsRefused()
    {
        MarketDataTools tools = Tools();
        DateTimeOffset from = new(9999, 12, 28, 20, 0, 0, TimeSpan.Zero);

        Func<Task> call = () => tools.GetBars(
            "ES", 1, from, _lastServableEndAtOneMinute.AddTicks(1), CancellationToken.None);

        (await call.Should().ThrowAsync<McpException>())
            .WithMessage("*9999-12-28T23:58:00.0000000*", "the refusal names the toUtc that was asked for")
            .WithMessage("*9999-12-28T23:57:59.9999999*", "and the last one it would have accepted");

        _gateway.BarRequests.Should().Be(0);
        _gateway.ContractRequests.Should().Be(0);
    }

    [Fact]
    public void TheBoundMovesWithTheResolution()
    {
        // The headroom is two bar spans plus three days, so it is not a fixed instant: at the coarsest bar
        // this server serves -- one week -- two spans is a fortnight, and the last servable end is seventeen
        // days before the end of the calendar rather than three. Hand-computed: 9999-12-31T23:59:59.9999999Z
        // less 17 days is 9999-12-14T23:59:59.9999999Z.
        ToolGuards guards = Guards();
        DateTimeOffset last = new DateTimeOffset(9999, 12, 14, 23, 59, 59, TimeSpan.Zero)
            .AddTicks(9_999_999);

        BarRange window = guards.ValidateWindow(
            last - TimeSpan.FromDays(7), last, ToolGuards.MaxResolutionMinutes);

        window.End.Should().Be(last, "exactly at the bound is servable, as it is at every other cap here");

        Action past = () => guards.ValidateWindow(
            last - TimeSpan.FromDays(7), last.AddTicks(1), ToolGuards.MaxResolutionMinutes);

        past.Should().Throw<McpException>()
            .WithMessage("*9999-12-14T23:59:59.9999999*", "the refusal names the bound it moved past");
    }

    // ── The same axis on the instant-taking tool ─────────────────────────────────────────────────────

    [Fact]
    public void GetMarketSession_RefusesAnInstantPastTheCalendarHorizon()
    {
        // The third reproduction, found by sweeping rather than from the card: get_market_session takes an
        // INSTANT, not a window, so no window guard was ever going to reach it. BarSessionCalendar reads an
        // evening instant as belonging to the NEXT trade date, and on 9999-12-31 that DateOnly.AddDays(1)
        // threw -- the same raw ArgumentOutOfRangeException on the same axis, one tool along.
        ReferenceTools reference = Reference();

        Action call = () => reference.GetMarketSession("ES", DateTimeOffset.MaxValue);

        call.Should().Throw<McpException>()
            .WithMessage("*9999-12-31T23:59:59.9999999*", "the refusal names the atUtc the caller passed")
            .WithMessage("*9999-12-28T23:59:59.9999999*", "and the horizon it is past");
    }

    [Fact]
    public void GetMarketSession_AnswersWhileTheMarketIsShutAtTheHorizon()
    {
        // And the servable side of it, which is a different code path: 16:30 Central is inside the
        // maintenance window, so the market is shut and the forward scan for the next open runs. That scan
        // is bounded at a fortnight, and `from + 14 days` overflowed on its own -- for an instant three days
        // inside the horizon, which is an instant the session rules answer perfectly well. The fortnight is
        // a termination guard rather than something a caller asked for, so it is clamped, not refused.
        ReferenceTools reference = Reference();

        ToolPayloads.SessionState session =
            reference.GetMarketSession("ES", new DateTimeOffset(9999, 12, 28, 22, 30, 0, TimeSpan.Zero));

        session.IsOpen.Should().BeFalse("16:30 Central is inside the daily maintenance window");
        session.NextOpenUtc.Should().Be(
            new DateTimeOffset(9999, 12, 28, 23, 0, 0, TimeSpan.Zero),
            "the session reopens at 17:00 Central the same evening, which is 23:00Z at UTC-6");
    }

    [Fact]
    public async Task AnOrdinaryReadStillAnswers()
    {
        // The other half of the acceptance criterion: nothing changes for a request that was always fine.
        MarketDataTools tools = Tools();

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

        MarketDataTools marketData = Tools();
        SnapshotTools snapshot = SnapshotFor(marketData);
        ReferenceTools reference = Reference();
        AccountTools accounts = new(_gateway, Guards());

        List<MethodInfo> takingAnInstant =
        [
            .. typeof(MarketDataTools).Assembly.GetTypes()
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

            object instance = tool.DeclaringType! == typeof(MarketDataTools) ? marketData
                : tool.DeclaringType! == typeof(SnapshotTools) ? snapshot
                : tool.DeclaringType! == typeof(ReferenceTools) ? reference
                : tool.DeclaringType! == typeof(AccountTools) ? accounts
                : throw new InvalidOperationException(
                    tool.DeclaringType!.Name + " takes an instant and this fixture cannot build it. "
                    + "Add it here rather than narrowing the sweep -- the sweep is the point.");

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

    /// <summary>Builds the market-data tools at the default row cap.</summary>
    /// <returns>The tools.</returns>
    private MarketDataTools Tools() =>
        new(_cache,
            _database,
            new InstrumentRegistry(Defaults()),
            _catalog,
            _gateway,
            Guards(),
            new StoreAvailabilityHolder(),
            _clock);

    /// <summary>Builds the reference tools — the ones that take an instant and no window.</summary>
    /// <returns>The reference tools.</returns>
    private ReferenceTools Reference() =>
        new(new InstrumentRegistry(Defaults()), _calendar, _gateway, Defaults(), _clock);

    /// <summary>Builds the composed tool over the same options.</summary>
    /// <param name="marketData">The market-data tools it composes.</param>
    /// <returns>The snapshot tool.</returns>
    private SnapshotTools SnapshotFor(MarketDataTools marketData) =>
        new(marketData, Reference(), new IndicatorCatalogNames(_catalog));

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
