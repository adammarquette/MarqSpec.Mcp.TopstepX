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
/// <para>
/// <b>The servable side of the bound is not in this file, and the drift sweep is not either.</b> What is left
/// here refuses, and a refusal never reaches a store — which is what lets these keep running on the in-memory
/// provider with no container. The four cases that <i>serve</i> a window the rule allows, the sweep among
/// them, now run the real <c>ON CONFLICT … DO UPDATE</c> bar and coverage writes, so they moved down to
/// <c>MarqSpec.Mcp.TopstepX.IntegrationTests.CalendarEndGuardServedReadTests</c> (gh#387). Read the two
/// together: a bound proven only to refuse is a bound nobody has checked for over-reach.
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

    // ── The reproduction that is refused ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetBars_RefusesAWindowAtTheEndOfTheCalendar_AtTheDefaultRowCap()
    {
        // Reproduction 1, and it fires at the DEFAULT MaxRows, so it is not a misconfiguration. The window is
        // under one bar wide, so it spans ZERO buckets and clears both caps -- and BarGapDetector.AlignUp
        // then rounds its start up to the next one-minute boundary on the fixed UTC grid, which is past the
        // end of year 9999. The DateTimeOffset that alignment builds is what threw.
        BarTools tools = Tools();
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

    // ── The boundary from the refusing side ──────────────────────────────────────────────────────────

    [Fact]
    public async Task AWindowEndingOneTickPastTheLastServableInstant_IsRefused()
    {
        BarTools tools = Tools();
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
    /// rather than letting it drop out of the sweep. The sweep itself moved to the served-read companion in
    /// the integration tier (gh#387), and the shape is kept in step with it on both sides.
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
}
