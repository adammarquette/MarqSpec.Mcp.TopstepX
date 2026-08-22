using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Tests.MarketData;
using MarqSpec.Mcp.TopstepX.Tools;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol;

namespace MarqSpec.Mcp.TopstepX.Tests.Tools;

/// <summary>
/// The rules at the tool boundary — the ones that decide whether a wrong question looks like a quiet market.
/// </summary>
/// <remarks>
/// None of these need a venue. They are the parts of the surface that are pure policy, and they are exactly
/// the parts whose failure modes are silent: an empty series where a symbol was misspelled, or a truncated one
/// where a window was too wide.
/// </remarks>
public sealed class ToolSurfaceTests
{
    private static readonly DateTimeOffset _tuesdayMidSession =
        MarketClock.FromMarket(new DateOnly(2026, 8, 18), new TimeOnly(9, 30)).ToUniversalTime();

    private static MarketDataOptions Options(string instruments = "ES,NQ", int maxRows = 5_000) =>
        new() { Instruments = instruments, MaxRows = maxRows, SessionCloseCentral = "16:00" };

    private static ReferenceTools Reference(DateTimeOffset now, MarketDataOptions? options = null)
    {
        MarketDataOptions o = options ?? Options();
        IOptions<MarketDataOptions> wrapped = Microsoft.Extensions.Options.Options.Create(o);
        return new ReferenceTools(
            new InstrumentRegistry(wrapped),
            BarSessionCalendar.Parse(o.SessionCloseCentral, o.HolidayList()),
            new CountingGateway([]),
            wrapped,
            new FakeTimeProvider(now));
    }

    // ── The closed instrument list ───────────────────────────────────────────────────────────────────

    [Fact]
    public void AnUnknownInstrument_IsAnError_AndNamesTheValidOnes()
    {
        // R-5.3. An empty series here would be indistinguishable from a market that produced nothing, and an
        // agent would reason about the silence instead of the typo.
        Action session = () => Reference(_tuesdayMidSession).GetMarketSession("EXX");

        session.Should().Throw<McpException>()
            .WithMessage("*EXX*")
            .WithMessage("*ES, NQ*");
    }

    [Fact]
    public void ABlankInstrument_IsAnError()
    {
        Action session = () => Reference(_tuesdayMidSession).GetMarketSession("   ");
        session.Should().Throw<McpException>();
    }

    [Fact]
    public void AnInstrumentIsResolvedRegardlessOfCasingAndPadding()
    {
        // Normalisation happens at construction, so a row written under one casing is found under another.
        ToolPayloads.SessionState state = Reference(_tuesdayMidSession).GetMarketSession("  es  ");
        state.Symbol.Should().Be("ES");
    }

    [Fact]
    public void ListInstruments_ReportsMoneyPerPoint_NotPerTick()
    {
        // The venue publishes money per TICK, and the two differ by exactly the tick size. Conflating them is
        // wrong by a plausible-looking constant factor, which is the hardest kind of wrong to notice.
        IReadOnlyList<ToolPayloads.InstrumentInfo> instruments = Reference(_tuesdayMidSession).ListInstruments();

        ToolPayloads.InstrumentInfo es = instruments.Single(i => i.Symbol == "ES");
        es.TickSize.Should().Be(0.25m);
        es.PointValue.Should().Be(50m);
        es.TickValue.Should().Be(12.50m);
    }

    [Fact]
    public void AConfiguredInstrumentWithNoKnownSpec_FailsAtStartup()
    {
        // Failing here is the point. The alternative is discovering it at the first tool call, from an agent,
        // mid-question -- and a substituted default would make every money figure quietly wrong.
        Action build = () => new InstrumentRegistry(
            Microsoft.Extensions.Options.Options.Create(Options(instruments: "ES,ZZZZ")));

        build.Should().Throw<InvalidOperationException>().WithMessage("*ZZZZ*");
    }

    // ── Session state ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GetMarketSession_ReportsAnOpenMarketAndItsClose()
    {
        ToolPayloads.SessionState state = Reference(_tuesdayMidSession).GetMarketSession("ES");

        state.IsOpen.Should().BeTrue();
        state.TradeDate.Should().Be(new DateOnly(2026, 8, 18));
        state.MinutesToClose.Should().Be(390); // 09:30 to 16:00 Central
        state.NextOpenUtc.Should().BeNull();
    }

    [Fact]
    public void GetMarketSession_ReportsTheNextOpen_WhenTheMarketIsShut()
    {
        // Saturday. The next open is Sunday 17:00 Central, and finding it walks the calendar forward rather
        // than re-deriving the rules -- a second implementation of them is a second place to be wrong.
        DateTimeOffset saturday =
            MarketClock.FromMarket(new DateOnly(2026, 8, 22), new TimeOnly(10, 0)).ToUniversalTime();

        ToolPayloads.SessionState state = Reference(saturday).GetMarketSession("ES");

        state.IsOpen.Should().BeFalse();
        state.TradeDate.Should().BeNull();
        state.NextOpenUtc.Should().Be(
            MarketClock.FromMarket(new DateOnly(2026, 8, 23), new TimeOnly(17, 0)).ToUniversalTime());
    }

    [Fact]
    public void GetMarketSession_ReportsTheSessionsActualOpen_NotWhereTheScanLanded()
    {
        // Regression. The scan steps in 15-minute increments from "now", and it used to report the probe
        // instant -- so a query at 10:09:06 on a Saturday answered "opens at 17:09", nine minutes late for a
        // session that opens at 17:00. Wrong in exactly the way an agent acts on without noticing.
        DateTimeOffset saturday =
            MarketClock.FromMarket(new DateOnly(2026, 8, 22), new TimeOnly(10, 9))
                .AddSeconds(6).AddMilliseconds(319).ToUniversalTime();

        ToolPayloads.SessionState state = Reference(saturday).GetMarketSession("ES");

        DateTimeOffset expected =
            MarketClock.FromMarket(new DateOnly(2026, 8, 23), new TimeOnly(17, 0)).ToUniversalTime();

        state.NextOpenUtc.Should().Be(expected);
        state.NextOpenUtc!.Value.Second.Should().Be(0);
        state.NextOpenUtc!.Value.Minute.Should().Be(0);
    }

    [Fact]
    public void GetMarketSession_FlagsADeclaredHoliday()
    {
        MarketDataOptions options = new()
        {
            Instruments = "ES",
            SessionCloseCentral = "16:00",
            Holidays = "2026-08-19",
            MaxRows = 5_000,
        };
        DateTimeOffset holiday =
            MarketClock.FromMarket(new DateOnly(2026, 8, 19), new TimeOnly(10, 0)).ToUniversalTime();

        ToolPayloads.SessionState state = Reference(holiday, options).GetMarketSession("ES");

        state.IsHoliday.Should().BeTrue();
        state.IsOpen.Should().BeFalse();
    }

    // ── The read caps ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnOverCapWindow_IsRefusedWithTheRealCount()
    {
        // R-5.4. It refuses rather than truncating: a shortened series arrives looking exactly like a
        // complete one, and the part cut off is the part the caller was reaching for.
        ToolGuards guards = new(Microsoft.Extensions.Options.Options.Create(Options(maxRows: 100)));

        Action validate = () => guards.ValidateWindow(
            _tuesdayMidSession, _tuesdayMidSession.AddDays(1), 5);

        validate.Should().Throw<McpException>()
            .WithMessage("*288*")   // buckets in a day at 5 minutes
            .WithMessage("*100*");  // the cap
    }

    [Fact]
    public void AWindowInsideTheCap_IsAccepted()
    {
        ToolGuards guards = new(Microsoft.Extensions.Options.Options.Create(Options(maxRows: 100)));

        BarRange window = guards.ValidateWindow(
            _tuesdayMidSession, _tuesdayMidSession.AddHours(1), 5);

        window.Start.Should().Be(_tuesdayMidSession);
        window.Duration.Should().Be(TimeSpan.FromHours(1));
    }

    [Fact]
    public void AnInvertedWindow_IsRefused()
    {
        ToolGuards guards = new(Microsoft.Extensions.Options.Options.Create(Options()));

        Action validate = () => guards.ValidateWindow(
            _tuesdayMidSession.AddHours(1), _tuesdayMidSession, 5);

        validate.Should().Throw<McpException>().WithMessage("*inverted*");
    }

    [Fact]
    public void ANonPositiveResolution_IsRefused()
    {
        ToolGuards guards = new(Microsoft.Extensions.Options.Options.Create(Options()));

        Action validate = () => guards.ValidateWindow(
            _tuesdayMidSession, _tuesdayMidSession.AddHours(1), 0);

        validate.Should().Throw<McpException>().WithMessage("*resolutionMinutes*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(10_001)]
    public void AnOutOfRangeCount_IsRefused(int count)
    {
        ToolGuards guards = new(Microsoft.Extensions.Options.Options.Create(Options(maxRows: 10_000)));

        Action validate = () => guards.ValidateCount(count);

        validate.Should().Throw<McpException>();
    }

    // ── The closed indicator vocabulary ──────────────────────────────────────────────────────────────

    private static IndicatorCatalog Catalog() => new(
        Microsoft.Extensions.Options.Options.Create(new IndicatorOptions()),
        BarSessionCalendar.Parse("16:00", []));

    [Fact]
    public void AnUnknownIndicator_IsAnError_AndListsTheKnownOnes()
    {
        // A typo that returned no data would read as "no signal", which is a conclusion rather than a fault.
        Action resolve = () => Catalog().Resolve("stochastic");

        resolve.Should().Throw<KeyNotFoundException>()
            .WithMessage("*stochastic*")
            .WithMessage("*atr*");
    }

    [Fact]
    public void IndicatorNamesAreCaseInsensitiveOnInput()
    {
        Catalog().Resolve("  RSI ").Name.Should().Be("rsi");
    }

    [Fact]
    public void TheCatalogueCoversEveryIndicatorTheToolCatalogueAdvertises()
    {
        // One place declares the set, so the projection and the tool surface cannot disagree by construction.
        // An indicator the tools accept but the projection never writes reads back as an empty series.
        Catalog().KnownNames.Should().BeEquivalentTo(
        [
            "atr", "bb-lower", "bb-middle", "bb-upper", "ema",
            "macd", "macd-histogram", "macd-signal", "rsi", "sma", "vwap",
        ]);
    }

    [Fact]
    public void EveryCatalogueEntryComputesWithoutThrowing_OnAShortSeries()
    {
        // The warm-up path. Every indicator must return nulls rather than throw when handed fewer bars than
        // it needs -- a projection over a nearly-empty store is the normal case on a cold start.
        IReadOnlyList<Bar> tooShort =
        [
            new(_tuesdayMidSession, 100m, 101m, 99m, 100m, 10),
            new(_tuesdayMidSession.AddMinutes(5), 100m, 101m, 99m, 100m, 10),
        ];

        foreach (IIndicator indicator in Catalog().All)
        {
            Action compute = () => indicator.Compute(tooShort);
            compute.Should().NotThrow(indicator.Name);
        }
    }
}
