using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
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
/// The dedicated roll-event tool: tape changeover plus both front-month answers (gh#349).
/// </summary>
/// <remarks>
/// The recorder is not built. Prints and bars are written by the test. A fixture that never
/// seeds two contracts cannot prove a roll or a disagreement.
/// </remarks>
public sealed class ContractRollToolTests : IDisposable
{
    private const string Venue = "test";
    private const string Expiring = "CON.F.US.EP.U26";
    private const string Next = "CON.F.US.EP.Z26";
    private const string GatewaySelected = "CON.F.US.TEST.Z26";
    private const int OneMinute = 1;
    private const int FiveMinutes = 5;

    private static readonly InstrumentId _es = new("ES");
    private static readonly DateOnly _wednesday = new(2026, 8, 19);
    private static readonly DateTimeOffset _now = new(2026, 8, 20, 16, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _recorded = new(2026, 8, 18, 16, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions _wire = McpJsonUtilities.DefaultOptions;

    private readonly TopstepXDbContext _database = new(
        new DbContextOptionsBuilder<TopstepXDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private readonly FakeTimeProvider _clock = new(_now);
    private readonly ContractRollTools _tools;

    public ContractRollToolTests()
    {
        IOptions<MarketDataOptions> options = Options.Create(new MarketDataOptions
        {
            Instruments = "ES,NQ",
            MaxRows = 5_000,
            SessionCloseCentral = "16:00",
        });

        BarSessionCalendar calendar = BarSessionCalendar.Parse("16:00", []);
        CountingGateway gateway = new([]);

        _tools = new ContractRollTools(
            new InstrumentResolver(new InstrumentRegistry(options), new StoreAvailabilityHolder()),
            _database,
            gateway,
            new LevelMethodCatalog(calendar),
            new VolumeFrontReader(new TapeVolumeFrontService(_database, gateway, calendar)),
            _clock);
    }

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task AnUnknownInstrument_IsAnError_NotAnEmptyRoll()
    {
        Func<Task> call = () => _tools.GetContractRoll("EXX", asOfUtc: null, CancellationToken.None);

        (await call.Should().ThrowAsync<McpException>())
            .WithMessage("*EXX*")
            .WithMessage("*ES, NQ*");
    }

    [Fact]
    public async Task ASymbolWithNoChangeover_OmitsTheChangeover_RatherThanGuessingADate()
    {
        await SeedTradesAsync(
            Trade(Central(2026, 8, 18, 10, 0), 1, Expiring, 40, TradeDirection.Buy));

        ToolPayloads.ContractRollInfo payload =
            await _tools.GetContractRoll("ES", asOfUtc: null, CancellationToken.None);

        payload.Symbol.Should().Be("ES");
        payload.AsOfUtc.Should().Be(_now);
        payload.Front.Changeover.Should().BeNull();
        payload.Contracts.Should().BeNull();

        JsonElement wire = Wire(payload);
        wire.GetProperty("front").TryGetProperty("changeover", out _).Should().BeFalse();
        wire.TryGetProperty("contracts", out _).Should().BeFalse();
        wire.TryGetProperty("why", out _).Should().BeFalse();
        wire.GetProperty("front").TryGetProperty("why", out _).Should().BeFalse();
    }

    [Fact]
    public async Task WhenTapeAndGatewayDisagree_BothAreNamed_AndUsedIsNotTheGateway()
    {
        await SeedRolledTapeAsync();

        ToolPayloads.ContractRollInfo payload =
            await _tools.GetContractRoll("ES", asOfUtc: null, CancellationToken.None);

        payload.Front.Used.Should().Be(TapeVolumeFrontRead.UsedTapeVolume);
        payload.Front.Used.Should().NotBe(GatewaySelected);
        payload.Front.Agree.Should().BeFalse();
        payload.Front.TapeContractId.Should().Be(Next);
        payload.Front.TapeSessionDate.Should().Be(_wednesday);
        payload.Front.GatewayContractId.Should().Be(GatewaySelected);
        payload.Front.Changeover.Should().NotBeNull();
        payload.Front.Changeover!.SessionDate.Should().Be(_wednesday);
        payload.Front.Changeover.FromContractId.Should().Be(Expiring);
        payload.Front.Changeover.ToContractId.Should().Be(Next);
        payload.Front.Changeover.FlippedAtUtc.Should().Be(Central(2026, 8, 19, 10, 1));
    }

    [Fact]
    public async Task AsOfUtc_PastTheSessionRulesHorizon_Refuses_NamingBothInstants()
    {
        Func<Task> call = () => _tools.GetContractRoll(
            "ES", DateTimeOffset.MaxValue, CancellationToken.None);

        (await call.Should().ThrowAsync<McpException>())
            .WithMessage("*9999-12-31T23:59:59.9999999*")
            .WithMessage("*9999-12-28T23:59:59.9999999*");
    }

    [Fact]
    public async Task AsOfUtc_BeforeTheFlip_DoesNotReportTheLaterChangeover()
    {
        await SeedRolledTapeAsync();

        ToolPayloads.ContractRollInfo payload = await _tools.GetContractRoll(
            "ES", Central(2026, 8, 18, 15, 0), CancellationToken.None);

        payload.Front.TapeContractId.Should().Be(Expiring);
        payload.Front.Changeover.Should().BeNull();
        payload.Contracts.Should().BeNull();
    }

    [Fact]
    public async Task HistoricalAsOf_OmitsTheLiveGatewayPick_AndDoesNotReportAgree()
    {
        // The 19 Aug flip is on the tape. asOfUtc is the 18th. The live gateway
        // selects CON.F.US.TEST.Z26 — today's pick. Mixing them makes agree false
        // as if they already disagreed on the 18th. That comparison never happened.
        await SeedRolledTapeAsync();

        DateTimeOffset eighteenth = new(2026, 8, 18, 20, 0, 0, TimeSpan.Zero);
        ToolPayloads.ContractRollInfo payload =
            await _tools.GetContractRoll("ES", eighteenth, CancellationToken.None);

        payload.AsOfUtc.Should().Be(eighteenth);
        payload.Front.TapeContractId.Should().Be(Expiring);
        payload.Front.Changeover.Should().BeNull();
        payload.Front.Used.Should().Be(TapeVolumeFrontRead.UsedTapeVolume);
        payload.Front.GatewayContractId.Should().BeNull();
        payload.Front.Agree.Should().BeNull();

        JsonElement front = Wire(payload).GetProperty("front");
        front.GetProperty("tapeContractId").GetString().Should().Be(Expiring);
        front.TryGetProperty("gatewayContractId", out _).Should().BeFalse();
        front.TryGetProperty("agree", out _).Should().BeFalse();
    }

    [Fact]
    public async Task BarSideSeam_AroundTheChangeover_ReportsSpansRoll()
    {
        await SeedRolledTapeAsync();
        await SeedBarsAroundFlipAsync(knownProvenance: true);

        ToolPayloads.ContractRollInfo payload =
            await _tools.GetContractRoll("ES", asOfUtc: null, CancellationToken.None);

        payload.Contracts.Should().NotBeNull();
        payload.Contracts!.Span.Should().Be(ToolPayloads.ContractSpan.SpansRoll);
        payload.Contracts.Segments.Should().HaveCount(2);
        payload.Contracts.Segments[0].ContractId.Should().Be(Expiring);
        payload.Contracts.Segments[1].ContractId.Should().Be(Next);
    }

    [Fact]
    public async Task BarSideSeam_FinestIsSingleContract_ButCoarserSpans_ReportsSpansRoll()
    {
        // 5-minute series already holds U26 then Z26 across the flip (SpansRoll).
        // 1-minute series after the flip is Z26 only. Finest-only coverage would
        // report SingleContract and hide the 5-minute seam this tool exists to name.
        await SeedRolledTapeAsync();
        await SeedBarsAroundFlipAsync(knownProvenance: true);
        await SeedOneMinuteBarsAfterFlipAsync(Next);

        ToolPayloads.ContractRollInfo payload =
            await _tools.GetContractRoll("ES", asOfUtc: null, CancellationToken.None);

        payload.Front.Changeover.Should().NotBeNull();
        payload.Front.Changeover!.FromContractId.Should().Be(Expiring);
        payload.Front.Changeover.ToContractId.Should().Be(Next);
        payload.Contracts.Should().NotBeNull();
        payload.Contracts!.Span.Should().Be(ToolPayloads.ContractSpan.SpansRoll);
        payload.Contracts.Segments.Should().HaveCount(2);
        payload.Contracts.Segments[0].ContractId.Should().Be(Expiring);
        payload.Contracts.Segments[1].ContractId.Should().Be(Next);
    }

    [Fact]
    public async Task BarSideSeam_TwoContractsOnDifferentResolutions_ReportsSpansRoll()
    {
        // 5-minute U26 only before the flip; 1-minute Z26 only after. No single
        // series crosses, so picking one resolution reports SingleContract.
        // The store around the flip has two contracts — that is SpansRoll.
        await SeedRolledTapeAsync();
        await SeedFiveMinuteBarsBeforeFlipAsync(Expiring);
        await SeedOneMinuteBarsAfterFlipAsync(Next);

        ToolPayloads.ContractRollInfo payload =
            await _tools.GetContractRoll("ES", asOfUtc: null, CancellationToken.None);

        payload.Front.Changeover.Should().NotBeNull();
        payload.Front.Changeover!.FromContractId.Should().Be(Expiring);
        payload.Front.Changeover.ToContractId.Should().Be(Next);
        payload.Contracts.Should().NotBeNull();
        payload.Contracts!.Span.Should().Be(ToolPayloads.ContractSpan.SpansRoll);
        payload.Contracts.Segments.Select(s => s.ContractId).Should().Equal(Expiring, Next);
    }

    [Fact]
    public async Task BarSideSeam_WithUnrecordedProvenance_IsUnknown_NotAGuessedSingleContract()
    {
        await SeedRolledTapeAsync();
        await SeedBarsAroundFlipAsync(knownProvenance: false);

        ToolPayloads.ContractRollInfo payload =
            await _tools.GetContractRoll("ES", asOfUtc: null, CancellationToken.None);

        payload.Contracts.Should().NotBeNull();
        payload.Contracts!.Span.Should().Be(ToolPayloads.ContractSpan.Unknown);
        payload.Contracts.Segments.Should().ContainSingle()
            .Which.ContractId.Should().BeNull();
    }

    [Fact]
    public void TheTool_IsRegistered_AndItsDescriptionStatesTheForwardOnlyLimit()
    {
        MethodInfo method = typeof(ContractRollTools).GetMethod(nameof(ContractRollTools.GetContractRoll))!;

        method.GetCustomAttribute<McpServerToolAttribute>().Should().NotBeNull();
        method.GetCustomAttribute<McpServerToolAttribute>()!.ReadOnly.Should().BeTrue();

        string description = method.GetCustomAttribute<DescriptionAttribute>()!.Description;
        description.Should().MatchRegex("(?i)no historical tape|before recording");
        description.Should().MatchRegex("(?i)omitted|absent");
        description.Should().NotMatchRegex("(?i)\\bwhy\\b");
    }

    private async Task SeedRolledTapeAsync()
    {
        await SeedTradesAsync(
            Trade(Central(2026, 8, 18, 10, 0), 1, Expiring, 100, TradeDirection.Buy),
            Trade(Central(2026, 8, 18, 11, 0), 2, Next, 20, TradeDirection.Sell),
            Trade(Central(2026, 8, 19, 10, 0), 3, Expiring, 10, TradeDirection.Buy),
            Trade(Central(2026, 8, 19, 10, 1), 4, Next, 80, TradeDirection.Buy));
    }

    private async Task SeedBarsAroundFlipAsync(bool knownProvenance)
    {
        DateTimeOffset flip = Central(2026, 8, 19, 10, 1);
        _database.Bars.AddRange(
            Bar(flip.AddMinutes(-10), knownProvenance ? Expiring : null),
            Bar(flip.AddMinutes(-5), knownProvenance ? Expiring : null),
            Bar(flip, knownProvenance ? Next : null),
            Bar(flip.AddMinutes(5), knownProvenance ? Next : null));
        await _database.SaveChangesAsync();
    }

    private async Task SeedFiveMinuteBarsBeforeFlipAsync(string contractId)
    {
        DateTimeOffset flip = Central(2026, 8, 19, 10, 1);
        _database.Bars.AddRange(
            Bar(flip.AddMinutes(-10), contractId, FiveMinutes),
            Bar(flip.AddMinutes(-5), contractId, FiveMinutes));
        await _database.SaveChangesAsync();
    }

    private async Task SeedOneMinuteBarsAfterFlipAsync(string contractId)
    {
        DateTimeOffset flip = Central(2026, 8, 19, 10, 1);
        _database.Bars.AddRange(
            Bar(flip, contractId, OneMinute),
            Bar(flip.AddMinutes(1), contractId, OneMinute),
            Bar(flip.AddMinutes(2), contractId, OneMinute));
        await _database.SaveChangesAsync();
    }

    private async Task SeedTradesAsync(params TradeRecord[] trades)
    {
        _database.Trades.AddRange(trades);
        await _database.SaveChangesAsync();
    }

    private static DateTimeOffset Central(int year, int month, int day, int hour, int minute) =>
        MarketClock.FromMarket(new DateOnly(year, month, day), new TimeOnly(hour, minute));

    private static TradeRecord Trade(
        DateTimeOffset when,
        long sequence,
        string contractId,
        long size,
        TradeDirection direction) => new()
        {
            Venue = Venue,
            Instrument = _es.Symbol,
            ContractId = contractId,
            TradeTimeUtc = when,
            Sequence = sequence,
            Price = 5000m,
            Size = size,
            Direction = direction,
            RecordedAt = _recorded,
        };

    private static BarRecord Bar(DateTimeOffset bucket, string? contractId, int resolutionMinutes = FiveMinutes) => new()
    {
        Venue = Venue,
        Instrument = _es.Symbol,
        ResolutionMinutes = resolutionMinutes,
        BucketStart = bucket,
        Open = 100m,
        High = 101m,
        Low = 99m,
        Close = 100m,
        Volume = 1_000,
        ContractId = contractId,
        RecordedAt = _recorded,
    };

    private static JsonElement Wire<T>(T payload) =>
        JsonDocument.Parse(JsonSerializer.Serialize(payload, _wire)).RootElement.Clone();
}
