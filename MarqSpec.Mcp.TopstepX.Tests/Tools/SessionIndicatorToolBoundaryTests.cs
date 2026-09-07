using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Telemetry;
using MarqSpec.Mcp.TopstepX.Tests.MarketData;
using MarqSpec.Mcp.TopstepX.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol;

namespace MarqSpec.Mcp.TopstepX.Tests.Tools;

/// <summary>
/// What <c>get_session_indicators</c> and <c>get_session_indicator_at</c> refuse, and that they refuse it
/// before they spend anything (gh#501).
/// </summary>
/// <remarks>
/// <para>
/// Two closed vocabularies meet on this surface and both are refused by name rather than answered with no
/// rows: the SESSION name, which <c>SessionBarTools</c> already closes, and the INDICATOR name, which is
/// narrower here than on <c>get_indicators</c> — a session series carries no session-anchored <c>vwap</c>,
/// because a session that IS one bar has no intra-session distribution to weight (ADR-0022). An empty series
/// is indistinguishable from a market that produced none, which is why neither miss is served quietly.
/// </para>
/// <para>
/// <b>No container, and none needed (gh#387).</b> Every case here refuses before the read reaches a store,
/// and the two counter assertions say what they measure and no more: the VENUE was not reached. That is a
/// statement worth making on this surface even though the route holds no gateway at all — the indicator read
/// is <i>defined</i> as one the vendor never serves (ADR-0022 §7), and a counter is how a test says so about
/// behaviour rather than about a constructor.
/// </para>
/// <para>
/// The served half, where a request the rules allow is genuinely answered against a real store, is
/// <c>MarqSpec.Mcp.TopstepX.IntegrationTests.SessionIndicatorProjectionTests</c>: a boundary proven only to
/// refuse is a boundary nobody has checked for over-reach.
/// </para>
/// </remarks>
public sealed class SessionIndicatorToolBoundaryTests : IDisposable
{
    private readonly TopstepXDbContext _database;
    private readonly CountingGateway _gateway;
    private readonly SessionIndicatorTools _tools;
    private readonly HostTelemetry _telemetry = new();

    public SessionIndicatorToolBoundaryTests()
    {
        _database = new TopstepXDbContext(
            new DbContextOptionsBuilder<TopstepXDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics
                    .InMemoryEventId.TransactionIgnoredWarning))
                .Options);

        _gateway = new CountingGateway([]);
        _tools = Tools();
    }

    /// <summary>A Monday in August 2026, at midnight UTC — before that day's <c>rth</c> session opens.</summary>
    private static DateTimeOffset MondayStart => new(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The Monday a fortnight later, at midnight UTC.</summary>
    private static DateTimeOffset FortnightLater => new(2026, 8, 17, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A clock instant well after every session named here has closed.</summary>
    private static DateTimeOffset SettledNow => new(2026, 8, 25, 0, 0, 0, TimeSpan.Zero);

    public void Dispose() => _database.Dispose();

    [Theory]
    [InlineData(Route.Series)]
    [InlineData(Route.AsOf)]
    public async Task AnUnknownSession_IsAnError_NamingTheConfiguredOnes(Route route)
    {
        // The same closed vocabulary get_session_bars refuses on, refused in the same place: a typo answered
        // with an empty series reads as a session that produced no values.
        Func<Task> call = Read(route, "rht", "atr");

        (await call.Should().ThrowAsync<McpException>())
            .WithMessage("*rht*", "the refusal quotes what was asked for")
            .WithMessage(
                "*asia, europe, full, rth*",
                "and lists every configured name, in the catalogue's ordinal order");

        NothingWasSpent();
    }

    [Theory]
    [InlineData(Route.Series)]
    [InlineData(Route.AsOf)]
    public async Task VwapOnASession_IsRefused_NamingVwapRolling(Route route)
    {
        // ADR-0022: "No 'vwap' on a session series, and the tool surface says so rather than omitting it
        // silently." Nothing ever writes a vwap row for a session series, so a read allowed through would
        // answer with no values at all -- the exact absence-that-looks-like-an-answer the closed vocabulary
        // exists to stop. The refusal names the alternative that IS computed here.
        Func<Task> call = Read(route, "rth", "vwap");

        (await call.Should().ThrowAsync<McpException>())
            .WithMessage("*anchors on the session*", "the refusal says why this series cannot carry it")
            .WithMessage("*vwap-rolling*", "and names the window-based one that a session series does carry");

        NothingWasSpent();
    }

    [Theory]
    [InlineData(Route.Series)]
    [InlineData(Route.AsOf)]
    public async Task AnUnconfiguredPeriod_IsRefused_ListingTheConfiguredOnes(Route route)
    {
        // gh#495's rule, unchanged on this surface: a period SELECTS among what the operator configured and
        // never asks for a new one. Refused before the store, because this read would otherwise answer an
        // unconfigured period with an honest-looking empty series.
        Func<Task> call = Read(route, "rth", "rsi", period: 99);

        (await call.Should().ThrowAsync<McpException>())
            .WithMessage("*99*", "the refusal names the period that was asked for")
            .WithMessage("*Configured periods for rsi*", "and the ones that would have answered");

        NothingWasSpent();
    }

    [Fact]
    public async Task AnEmptyWindow_IsRefused()
    {
        // SessionWindows.TradeDatesIn answers an empty window with an empty list, which on this surface reads
        // as "no session traded then" rather than "you asked for nothing".
        //
        // NOT SWEPT OVER BOTH ROUTES, unlike the three vocabulary refusals above, and it is the one case
        // where that is right: get_session_indicator_at takes a single moment and no window at all, so there
        // is no empty window for it to refuse. A theory row driving it here would exercise a guard the route
        // does not have and pass by asserting nothing.
        Func<Task> call = () => _tools.GetSessionIndicators(
            "ES", "rth", "atr", MondayStart, MondayStart, cancellationToken: CancellationToken.None);

        (await call.Should().ThrowAsync<McpException>())
            .WithMessage(
                "*empty or inverted*",
                "the window is named as the fault, rather than answered with no rows");

        NothingWasSpent();
    }

    /// <summary>Which of the two reads a refusal case is being driven through.</summary>
    /// <remarks>
    /// <b>Both, for every refusal either of them can reach.</b> The two tools resolve the same session name,
    /// the same indicator name and the same period, and each does it in its own method body — so a suite
    /// that only ever drove the window read would let the as-of read stop refusing without going red. That
    /// is the worse half to lose: <c>get_session_indicators</c> answering an unresolved name would at least
    /// hand back an empty list, while <c>get_session_indicator_at</c> hands back <c>{}</c>, which a caller
    /// is told to read as CANNOT MEASURE. A typo would arrive as a measurement that could not be taken.
    /// </remarks>
    public enum Route
    {
        /// <summary><c>get_session_indicators</c>, over a window.</summary>
        Series,

        /// <summary><c>get_session_indicator_at</c>, as of a moment.</summary>
        AsOf,
    }

    /// <summary>Drives one refusal case through one of the two reads.</summary>
    /// <param name="route">Which read.</param>
    /// <param name="session">The session name to ask for.</param>
    /// <param name="indicator">The indicator name to ask for.</param>
    /// <param name="period">The period to ask for, or <see langword="null"/> for the primary.</param>
    /// <returns>The call, unstarted.</returns>
    /// <remarks>
    /// The window and the moment are both well inside what the guards allow, so the only thing either call
    /// can refuse is the vocabulary the case is about.
    /// </remarks>
    private Func<Task> Read(Route route, string session, string indicator, int? period = null)
    {
        if (route == Route.Series)
        {
            return () => _tools.GetSessionIndicators(
                "ES", session, indicator, MondayStart, FortnightLater, period, CancellationToken.None);
        }

        return () => _tools.GetSessionIndicatorAt(
            "ES", session, indicator, FortnightLater, period, CancellationToken.None);
    }

    /// <summary>Asserts the refusal landed before the venue was touched at all.</summary>
    private void NothingWasSpent()
    {
        // An indicator route reaches no venue by construction -- this tool reads the gateway once for its
        // venue id and keeps no client -- so these two can only ever confirm that. They are asserted anyway,
        // because the claim a caller cares about is about the READ, and a constructor argument is not one.
        _gateway.BarRequests.Should().Be(0, "an indicator read is served from stored bars, never fetched");
        _gateway.ContractRequests.Should().Be(0, "and resolving a contract is a vendor call too");
    }

    /// <summary>Builds the tool type over an in-memory context it never reaches.</summary>
    /// <returns>The tools.</returns>
    private SessionIndicatorTools Tools()
    {
        IOptions<MarketDataOptions> options = Options.Create(new MarketDataOptions
        {
            Instruments = "ES,NQ",
            MaxRows = 5_000,
            SessionCloseCentral = "16:00",
        });

        BarSessionCalendar calendar = BarSessionCalendar.Parse("16:00", []);
        FakeTimeProvider clock = new(SettledNow);

        IndicatorCatalog catalog = new(
            Options.Create(new IndicatorOptions { AtrPeriod = 3, RsiPeriod = 3 }), calendar);

        IndicatorProjector projector = new(_database, catalog, NullLogger<IndicatorProjector>.Instance, _telemetry);

        return new SessionIndicatorTools(
            new InstrumentResolver(new InstrumentRegistry(options), new StoreAvailabilityHolder()),
            _database,
            catalog,
            new IndicatorCacheService(
                _database, catalog, projector, clock, NullLogger<IndicatorCacheService>.Instance, _telemetry),
            new SessionCatalog(options, calendar),
            calendar,
            _gateway,
            new ToolGuards(options));
    }
}
