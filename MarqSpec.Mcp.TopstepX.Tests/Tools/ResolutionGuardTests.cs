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
/// A non-positive <c>resolutionMinutes</c> is refused by every tool that takes one (gh#69).
/// </summary>
/// <remarks>
/// <para>
/// The rule already existed — inside <c>ToolGuards.ValidateWindow</c>, which only the windowed tools call. The
/// four tools that build their own window never reached it, so a <c>0</c> ran on into
/// <c>BarGapDetector.AlignDown</c> and crossed the tool boundary as a raw
/// <see cref="ArgumentOutOfRangeException"/>: a stack-shaped failure with no statement of what the caller did
/// wrong, for a mistake this server can name exactly. Two of the four did not even fail — they matched no row
/// and answered "cannot measure", which is a wrong question rendered as an answer.
/// </para>
/// <para>
/// The last test is the one that matters most. It walks the surface by reflection rather than naming tools, so
/// the guard cannot quietly fall off a tool added tomorrow — the exact failure <c>ToolGuards</c> was written
/// to prevent, and then suffered itself.
/// </para>
/// <para>
/// <b>That sweep keys on the parameter <i>name</i>, so name yours <c>resolutionMinutes</c>.</b> A rename on one
/// of today's six trips the count floor and fails loudly; a <i>new</i> tool spelling the same concept
/// <c>timeframeMinutes</c> or <c>barSizeMinutes</c> leaves the count at six and is silently uncovered. It is
/// the one door left open, and the only part of this fixture that fails quietly — <c>Instance</c> and
/// <c>Blank</c> both throw and say what to add. A marker attribute would close it properly, and costs more
/// than it buys while the surface is six methods.
/// </para>
/// </remarks>
public sealed class ResolutionGuardTests : IDisposable
{
    private const string Contract = "CON.F.US.EP.Z26";
    private const int SeededBars = 40;

    private readonly TopstepXDbContext _database;
    private readonly CountingGateway _gateway;
    private readonly MarketDataTools _marketData;
    private readonly SnapshotTools _snapshot;
    private readonly BarCacheService _cache;
    private readonly IndicatorCatalog _catalog;
    private readonly FakeTimeProvider _clock;

    public ResolutionGuardTests()
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

        IOptions<MarketDataOptions> options = Options.Create(new MarketDataOptions
        {
            Instruments = "ES,NQ",
            MaxRows = 5_000,
            SessionCloseCentral = "16:00",
        });

        BarSessionCalendar calendar = BarSessionCalendar.Parse("16:00", []);
        _catalog = new IndicatorCatalog(
            Options.Create(new IndicatorOptions { AtrPeriod = 3, RsiPeriod = 3 }), calendar);
        _clock = new FakeTimeProvider(Bucket(SeededBars).AddHours(2));

        _gateway = new CountingGateway([]);

        IndicatorProjector projector = new(_database, _catalog, NullLogger<IndicatorProjector>.Instance);
        _cache = new BarCacheService(
            _database, _gateway, calendar, projector, _clock, NullLogger<BarCacheService>.Instance);

        _marketData = new MarketDataTools(
            _cache,
            _database,
            new InstrumentRegistry(options),
            _catalog,
            new IndicatorCacheService(
                _database,
                _catalog,
                new IndicatorProjector(_database, _catalog, NullLogger<IndicatorProjector>.Instance),
                _clock,
                NullLogger<IndicatorCacheService>.Instance),
            new LevelMethodCatalog(calendar),
            _gateway,
            new ToolGuards(options),
            new StoreAvailabilityHolder(),
            _clock,
            Options.Create(new KeyLevelDetectionOptions()),
            new VolumeProfileService(_database));

        _snapshot = new SnapshotTools(
            _marketData,
            new ReferenceTools(new InstrumentRegistry(options), calendar, _gateway, options, _clock),
            new IndicatorCatalogNames(_catalog),
            _clock);
    }

    public void Dispose() => _database.Dispose();

    private static DateTimeOffset SessionStart =>
        MarketClock.FromMarket(new DateOnly(2026, 8, 18), new TimeOnly(9, 0)).ToUniversalTime();

    private static DateTimeOffset Bucket(int index) => SessionStart.AddMinutes(5 * index);

    // ── The rule itself, with no window in sight ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ANonPositiveResolution_IsRefused_WithNoWindowToValidateItAgainst(int resolutionMinutes)
    {
        // The rule's new home. Inside ValidateWindow it was reachable only when a window was ALSO being
        // validated, which is why four tools never met it.
        Action validate = () => ToolGuards.ValidateResolution(resolutionMinutes);

        validate.Should().Throw<McpException>()
            .WithMessage("*resolutionMinutes*")
            .WithMessage("*" + resolutionMinutes.ToString(CultureInfo.InvariantCulture) + "*");
    }

    [Fact]
    public void APositiveResolution_PassesThroughUnchanged()
    {
        ToolGuards.ValidateResolution(5).Should().Be(5);
    }

    [Fact]
    public void TheWindowedRule_StillRefusesToo()
    {
        // Moving the check must not lose it. ValidateWindow still rejects, by delegating rather than by
        // carrying its own copy.
        ToolGuards guards = new(Options.Create(new MarketDataOptions
        {
            Instruments = "ES",
            MaxRows = 5_000,
            SessionCloseCentral = "16:00",
        }));

        Action validate = () => guards.ValidateWindow(Bucket(0), Bucket(8), 0);

        validate.Should().Throw<McpException>().WithMessage("*resolutionMinutes*");
    }

    // ── The four tools that build their own window ───────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task GetLatestBars_RefusesANonPositiveResolution(int resolutionMinutes)
    {
        // The reported symptom. TimeSpan.FromMinutes(0) reached BarGapDetector.AlignDown and left the tool
        // boundary as an ArgumentOutOfRangeException -- an unhandled fault where a tool error belongs.
        Func<Task> call = () =>
            _marketData.GetLatestBars("ES", resolutionMinutes, 10, CancellationToken.None);

        (await call.Should().ThrowAsync<McpException>()).WithMessage("*resolutionMinutes*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task GetIndicatorAt_RefusesANonPositiveResolution(int resolutionMinutes)
    {
        // Worse than a crash: this one never threw. The query matched no row and the tool answered
        // { value: null } -- "cannot measure", which is what a genuine warm-up gap says. An impossible
        // timeframe and an honest absence must not be the same reply.
        Func<Task> call = () => _marketData.GetIndicatorAt(
            "ES", resolutionMinutes, "atr", Bucket(SeededBars), CancellationToken.None);

        (await call.Should().ThrowAsync<McpException>()).WithMessage("*resolutionMinutes*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task GetKeyLevels_RefusesANonPositiveResolution(int resolutionMinutes)
    {
        // The same silence in a different shape: no bars matched, so it returned an empty level set. Nothing
        // in "no levels here" tells the caller the timeframe it asked for cannot exist.
        Func<Task> call = () =>
            _marketData.GetKeyLevels("ES", resolutionMinutes, 100, cancellationToken: CancellationToken.None);

        (await call.Should().ThrowAsync<McpException>()).WithMessage("*resolutionMinutes*");
    }

    // ── The composed read, which fails as a set or not at all ────────────────────────────────────────

    [Fact]
    public void ResolveResolutions_RefusesTheWholeSet_WhenOneMemberIsNotPositive()
    {
        // The named home for resolution policy is where the set is judged. Pinned here as a pure function,
        // because that is where the whole-set property lives -- no store, no venue, no ordering.
        Action resolve = () => SnapshotTools.ResolveResolutions([5, 0, 60]);

        resolve.Should().Throw<McpException>()
            .WithMessage("*resolutionMinutes*")
            .WithMessage("*0*");
    }

    [Fact]
    public void ResolveResolutions_StillReturnsAValidSet()
    {
        // The guard must not change what a good set resolves to.
        SnapshotTools.ResolveResolutions([15, 60]).Should().Equal(15, 60);
        SnapshotTools.ResolveResolutions(null).Should().Equal(5, 60);
    }

    [Fact]
    public async Task GetMarketSnapshot_RefusesAMixedSet_WithoutFetchingTheGoodResolutionsFirst()
    {
        // The case that separates a real fix from four patched call sites. Judging each resolution as its
        // turn came round would let [5, 0, 60] fetch a whole five-minute slice and then throw -- the caller
        // holds half a snapshot AND an error, which is worse than either on its own.
        Func<Task> call = () =>
            _snapshot.GetMarketSnapshot("ES", [5, 0, 60], 10, CancellationToken.None);

        (await call.Should().ThrowAsync<McpException>()).WithMessage("*resolutionMinutes*");

        _gateway.BarRequests.Should().Be(0, "the set is judged before the first slice is read");
        _gateway.ContractRequests.Should().Be(0, "and before the contract behind it is resolved");
    }

    [Fact]
    public async Task AValidResolution_StillAnswers()
    {
        // The other half of the acceptance criterion: nothing changes for a resolution that is fine.
        ToolPayloads.BarSeries series =
            await _marketData.GetLatestBars("ES", 5, 10, CancellationToken.None);

        series.ResolutionMinutes.Should().Be(5);
        series.Bars.Should().HaveCount(10, "forty five-minute bars were seeded and ten were asked for");
    }

    // ── The other end of the same axis (gh#81) ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(ToolGuards.MaxResolutionMinutes + 1)]
    public void AResolutionPastTheCeiling_IsRefused(int resolutionMinutes)
    {
        // The rule's other direction. gh#69 gave it a floor and left the wording exhaustive; it was not.
        Action validate = () => ToolGuards.ValidateResolution(resolutionMinutes);

        validate.Should().Throw<McpException>()
            .WithMessage("*resolutionMinutes*")
            .WithMessage("*" + resolutionMinutes.ToString(CultureInfo.InvariantCulture) + "*");
    }

    [Fact]
    public void AResolutionAtTheCeiling_PassesThroughUnchanged()
    {
        // A guard that is red on correct input is not a guard, it is an outage. The ceiling is inclusive.
        ToolGuards.ValidateResolution(ToolGuards.MaxResolutionMinutes)
            .Should().Be(ToolGuards.MaxResolutionMinutes);
    }

    [Fact]
    public void TheLookbackReach_IsUnchangedForAnOrdinaryRequest()
    {
        // The arithmetic moved into ToolGuards, so pin what it produces rather than trusting the move. Four
        // bar spans per bar wanted, plus four days -- hand-computed, not read back from the implementation.
        DateTimeOffset end = Bucket(SeededBars);

        BarRange window = ToolGuards.LookbackWindow(end, 5, 10);

        window.End.Should().Be(end);
        window.Start.Should().Be(end - TimeSpan.FromMinutes(5 * 10 * 4) - TimeSpan.FromDays(4));
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(10_081)]
    public async Task GetLatestBars_RefusesAResolutionPastTheCeiling(int resolutionMinutes)
    {
        // The reported symptom. `barSize.Ticks * wanted * 4` is long arithmetic in an UNCHECKED context: at
        // int.MaxValue minutes barSize.Ticks is already ~1.3e18, so the product wraps and `end - reach` lands
        // outside DateTime's range -- a raw ArgumentOutOfRangeException where a tool error belongs. It
        // survives gh#69's guard for the one reason that guard cannot help with: the value is positive.
        Func<Task> call = () =>
            _marketData.GetLatestBars("ES", resolutionMinutes, 10, CancellationToken.None);

        (await call.Should().ThrowAsync<McpException>()).WithMessage("*resolutionMinutes*");
    }

    [Fact]
    public async Task AResolutionAtTheCeiling_StillAnswers()
    {
        // The boundary from the servable side. A ceiling that also refuses the coarsest bar it claims to
        // serve is a ceiling one minute lower, and nothing in the error would say so.
        Func<Task> call = () => _marketData.GetLatestBars("ES", 10_080, 10, CancellationToken.None);

        await call.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ACountThatWouldReachBeforeTheCalendar_IsRefused_NotFaulted()
    {
        // The ceiling alone does NOT close this bug, and this is the proof. MaxRows is operator
        // configuration -- [Range(1, 1_000_000)] on MarketDataOptions -- and the reach is FOUR bar spans per
        // bar asked for. At a weekly bar, exactly at the ceiling, 62,500 bars SPAN about 1,200 years, so they
        // REACH about 4,800: past year one, and `end - reach` throws exactly the way int.MaxValue did. The 4x
        // is the whole finding -- it is what carries a pair that is legal on both axes past a calendar neither
        // axis knows about, and it puts the real boundary near 26,400 weekly bars rather than 62,500. Nothing
        // about this request is out of range on either axis taken alone.
        MarketDataTools capped = WithRowCap(1_000_000);

        Func<Task> call = () => capped.GetLatestBars("ES", 10_080, 62_500, CancellationToken.None);

        (await call.Should().ThrowAsync<McpException>()).WithMessage("*resolutionMinutes*");

        // Refused, not quietly shortened. Clamping the window to year one would answer with however many
        // bars the store happened to hold -- a series indistinguishable from a complete one, which is the
        // failure mode ValidateWindow already refuses to commit for an over-cap window.
        _gateway.BarRequests.Should().Be(0, "the reach is judged before the first page is read");
    }

    [Fact]
    public void ANegativeCount_IsRefused_RatherThanWrappingBackThroughTheNarrowingCast()
    {
        // The guard bounds only the UPPER side of the reach, and the widened Int128 is narrowed back with an
        // UNCHECKED cast. A negative count makes the product negative, so `reach > end.UtcTicks` is false and
        // `(long)reach` wraps -- reintroducing, inside the new guard, the raw fault this guard exists to
        // remove. Not reachable through a tool today because ValidateCount runs first, but LookbackWindow is
        // public static and its only stated defence is a <param> comment saying "already validated".
        Action size = () => ToolGuards.LookbackWindow(Bucket(SeededBars), 10_080, -1_000_000);

        size.Should().Throw<McpException>()
            .WithMessage("*count*")
            .WithMessage("*-1000000*");
    }

    /// <summary>Rebuilds the market-data tools against a different row cap.</summary>
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
            new LevelMethodCatalog(BarSessionCalendar.Parse("16:00", [])),
            _gateway,
            new ToolGuards(capped),
            new StoreAvailabilityHolder(),
            _clock,
            Options.Create(new KeyLevelDetectionOptions()),
            new VolumeProfileService(_database));
    }

    // ── The drift guard ──────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(ToolGuards.MaxResolutionMinutes + 1)]
    [InlineData(int.MaxValue)]
    public async Task NoToolTakingAResolution_LetsAnUnservableOneThrough(int bad)
    {
        // "A cap enforced in three of four places is not a cap; it is a cap plus one tool that quietly
        // returns everything." -- ToolGuards' own XML docs, and exactly what happened to this rule. So the
        // sweep walks the surface by reflection rather than naming tools: a tool added tomorrow is covered
        // without anyone remembering to come back here.
        //
        // It walks the whole AXIS as well as the whole surface (gh#81). Driven by 0 alone it stayed green
        // through a ceiling that did not exist, because every tool refused the value it was handed -- the
        // sweep proved the floor and read as though it proved the parameter.
        const BindingFlags Surface = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;

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

            Func<Task> call = () => Invoke(tool, Instance(tool.DeclaringType!), bad);

            // The EXCEPTION TYPE is the assertion, not merely that something threw. An
            // ArgumentOutOfRangeException out of the arithmetic also "throws", and it is exactly the failure
            // this fixture exists to keep off the tool boundary.
            (await call.Should().ThrowAsync<McpException>(
                tool.Name + " accepts a resolutionMinutes and does not refuse "
                + bad.ToString(CultureInfo.InvariantCulture) + ", so a caller mistake reaches it as a fault "
                + "or as a plausible-looking empty answer."))
                .WithMessage("*resolutionMinutes*");

            // Refusing is only half of it, and the half that a per-call-site patch also satisfies. Deleting
            // the whole-set loop from ResolveResolutions still leaves every tool throwing an McpException --
            // GetLatestBars' own guard catches the bad member one slice late -- so the throw assertion alone
            // cannot tell the two designs apart. These two can: a tool that reached the venue before refusing
            // has already spent the request it was refusing to justify.
            _gateway.BarRequests.Should().Be(
                0, tool.Name + " read bars from the venue before refusing the resolution");
            _gateway.ContractRequests.Should().Be(
                0, tool.Name + " resolved a contract at the venue before refusing the resolution");
        }
    }

    /// <summary>Builds the tool type under test, or says what has to be added here.</summary>
    /// <param name="type">The tool type the sweep found.</param>
    /// <returns>An instance to invoke.</returns>
    private object Instance(Type type) =>
        type == typeof(MarketDataTools) ? _marketData
        : type == typeof(SnapshotTools) ? _snapshot
        : throw new InvalidOperationException(
            type.Name + " takes a resolutionMinutes and this fixture cannot build it. Add it here rather "
            + "than narrowing the sweep -- the sweep is the point.");

    /// <summary>Invokes a tool with a bad resolution and every other argument filled plausibly.</summary>
    /// <param name="tool">The tool method.</param>
    /// <param name="instance">The tool instance.</param>
    /// <param name="bad">The unservable resolution to drive it with.</param>
    /// <returns>The completed call.</returns>
    private static async Task Invoke(MethodInfo tool, object instance, int bad)
    {
        object?[] arguments = [.. tool.GetParameters().Select(p => Filler(p, bad))];

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

    /// <summary>A plausible value for one tool argument — and a deliberately bad resolution.</summary>
    /// <param name="parameter">The parameter to fill.</param>
    /// <param name="bad">The unservable resolution to drive it with.</param>
    /// <returns>The value to pass.</returns>
    private static object? Filler(ParameterInfo parameter, int bad) => parameter.Name switch
    {
        // A MIXED set, not a bare [0]. On its own that proves less than it looks: a per-slice check still
        // throws an McpException, only one slice later, so the throw assertion cannot tell the two designs
        // apart either way. It is the BarRequests == 0 assertion in the sweep that separates them, and the
        // mixed set is what gives that assertion something to catch -- a bare [0] would refuse on the first
        // member under both designs and spend nothing under either.
        "resolutionMinutes" when parameter.ParameterType == typeof(int[]) => new[] { 5, bad },
        "resolutionMinutes" => bad,
        "indicator" => "atr",
        "symbol" => "ES",
        _ => Blank(parameter),
    };

    /// <summary>A value for an argument the sweep has no opinion about.</summary>
    /// <param name="parameter">The parameter to fill.</param>
    /// <returns>The value to pass.</returns>
    private static object? Blank(ParameterInfo parameter)
    {
        Type type = parameter.ParameterType;

        return type == typeof(CancellationToken) ? CancellationToken.None
            : type == typeof(int) ? 10
            : type == typeof(bool) ? true
            : type == typeof(string) ? "ES"
            : type == typeof(DateTimeOffset) ? (parameter.Name == "toUtc" ? Bucket(8) : Bucket(0))
            : Nullable.GetUnderlyingType(type) is not null || !type.IsValueType ? null
            : throw new InvalidOperationException(
                "No filler for " + type.Name + " " + parameter.Name + ". Add one rather than skipping the "
                + "tool: an unfilled argument is a tool the sweep silently stops covering.");
    }
}
