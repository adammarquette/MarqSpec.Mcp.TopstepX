using System.ComponentModel;
using System.Globalization;
using System.Reflection;
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
/// The MCP surface over stored footprint cells and volume profile (gh#222).
/// </summary>
/// <remarks>
/// Coverage and cells are seeded the way gh#220 / gh#221 proved them — the recorder is not built.
/// Live tape health is a holder this fixture Sets: Listening for the stored-read path,
/// and not-listening for the gh#218 refusal.
/// </remarks>
public sealed class FootprintAndVolumeProfileToolTests : IDisposable
{
    private const string Venue = "test";
    private const string Front = "CON.F.US.EP.Z26";
    private const string Expiring = "CON.F.US.EP.U26";
    private const string GatewaySelected = "CON.F.US.TEST.Z26";
    private const int FiveMinutes = 5;

    private static readonly InstrumentId _es = new("ES");

    private static readonly DateTimeOffset _ten = new(2026, 8, 18, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _twelve = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _fourteen = new(2026, 8, 18, 14, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _sixteen = new(2026, 8, 18, 16, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _bucket1430 = new(2026, 8, 18, 14, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _recorded = new(2026, 8, 18, 16, 0, 0, TimeSpan.Zero);

    private readonly TopstepXDbContext _database = new(
        new DbContextOptionsBuilder<TopstepXDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private readonly TapeAvailabilityHolder _tape = new();
    private readonly MarketDataTools _tools;

    public FootprintAndVolumeProfileToolTests()
    {
        IOptions<MarketDataOptions> options = Options.Create(new MarketDataOptions
        {
            Instruments = "ES,NQ",
            MaxRows = 5_000,
            SessionCloseCentral = "16:00",
        });

        BarSessionCalendar calendar = BarSessionCalendar.Parse("16:00", []);
        IndicatorCatalog catalog = new(
            Options.Create(new IndicatorOptions { AtrPeriod = 3, RsiPeriod = 3 }), calendar);
        FakeTimeProvider clock = new(_sixteen);
        CountingGateway gateway = new([]);
        IndicatorProjector projector = new(_database, catalog, NullLogger<IndicatorProjector>.Instance);
        BarCacheService cache = new(
            _database, gateway, calendar, projector, clock, NullLogger<BarCacheService>.Instance);

        _tape.Set(TapeAvailability.Listening());
        _tools = new MarketDataTools(
            cache,
            _database,
            new InstrumentRegistry(options),
            catalog,
            new IndicatorCacheService(
                _database, catalog, projector, clock, NullLogger<IndicatorCacheService>.Instance),
            new LevelMethodCatalog(calendar),
            gateway,
            new ToolGuards(options),
            new StoreAvailabilityHolder(),
            clock,
            Options.Create(new KeyLevelDetectionOptions()),
            new VolumeProfileService(_database),
            _tape,
            new TapeVolumeFrontService(_database, gateway, calendar),
            new FootprintCacheService(
                _database,
                new FootprintProjector(_database, NullLogger<FootprintProjector>.Instance),
                clock,
                NullLogger<FootprintCacheService>.Instance));
    }

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task GetVolumeProfile_ReportsTheCoveredWindow_NotTheAsk_AndTheFrontContract()
    {
        await SeedCoverageAsync(Coverage(Front, _fourteen, _sixteen));
        await SeedCellsAsync(Cell(_bucket1430, 5000m, buy: 4, sell: 1));

        ToolPayloads.VolumeProfileSeries payload = await _tools.GetVolumeProfile(
            "ES", FiveMinutes, _ten, _sixteen, CancellationToken.None);

        payload.Covered.Start.Should().Be(_fourteen);
        payload.Covered.End.Should().Be(_sixteen);
        payload.Covered.Narrowed.Should().BeTrue();
        payload.PointOfControl.Should().Be(5000m);
        payload.TotalVolume.Should().Be(5);
        payload.Contracts.Span.Should().Be(ToolPayloads.ContractSpan.SingleContract);
        payload.Contracts.Segments.Should().ContainSingle()
            .Which.ContractId.Should().Be(Front);
    }

    [Fact]
    public async Task GetFootprint_ReturnsTheStoredCells_UnderTheCoveredWindow()
    {
        await SeedCoverageAsync(Coverage(Front, _fourteen, _sixteen));
        await SeedCellsAsync(
            Cell(_bucket1430, 5000.00m, buy: 5, sell: 0),
            Cell(_bucket1430, 5000.25m, buy: 0, sell: 4));

        ToolPayloads.FootprintSeries payload = await _tools.GetFootprint(
            "ES", FiveMinutes, _fourteen, _sixteen, CancellationToken.None);

        payload.Covered.Start.Should().Be(_fourteen);
        payload.Covered.End.Should().Be(_sixteen);
        payload.Covered.Narrowed.Should().BeFalse();
        payload.Cells.Should().BeEquivalentTo(
        [
            new ToolPayloads.FootprintCellPoint(_bucket1430, 5000.00m, 5, 0),
            new ToolPayloads.FootprintCellPoint(_bucket1430, 5000.25m, 0, 4),
        ]);
        payload.Contracts.Span.Should().Be(ToolPayloads.ContractSpan.SingleContract);
        payload.Contracts.Segments.Should().ContainSingle()
            .Which.ContractId.Should().Be(Front);
    }

    [Fact]
    public async Task GetFootprint_AtAnUnprojectedResolution_Refuses_RatherThanLookingQuiet()
    {
        // Reviewer fixture: TapeCoverage is not per-resolution. Coverage + 5-minute cells with
        // volume and no trades, then ask for 15-minute footprint. On-read has nothing to project
        // from; the 15m cell query stays empty. Returning { cells: [] } is the quiet-market shape.
        await SeedCoverageAsync(Coverage(Front, _fourteen, _sixteen));
        await SeedCellsAsync(Cell(_bucket1430, 5000m, buy: 4, sell: 1));

        Func<Task> footprint = () => _tools.GetFootprint(
            "ES", 15, _fourteen, _sixteen, CancellationToken.None);

        (await footprint.Should().ThrowAsync<McpException>())
            .WithMessage("*15*")
            .WithMessage("*quiet*");
    }

    [Fact]
    public async Task ContractsSegments_ReportBarOpenTimes_NotTheCoverageEnvelope()
    {
        // Coverage [14:00, 16:00), one 5-minute cell at 14:30. lastBucket is when the run's last
        // bar opened — 14:30 — not the exclusive coverage end 16:00. covered already carries the ledger.
        await SeedCoverageAsync(Coverage(Front, _fourteen, _sixteen));
        await SeedCellsAsync(Cell(_bucket1430, 5000m, buy: 4, sell: 1));

        ToolPayloads.FootprintSeries footprint = await _tools.GetFootprint(
            "ES", FiveMinutes, _fourteen, _sixteen, CancellationToken.None);
        ToolPayloads.VolumeProfileSeries profile = await _tools.GetVolumeProfile(
            "ES", FiveMinutes, _fourteen, _sixteen, CancellationToken.None);

        ToolPayloads.ContractSegmentInfo footprintSegment = footprint.Contracts.Segments.Should().ContainSingle().Subject;
        footprintSegment.FirstBucket.Should().Be(_bucket1430);
        footprintSegment.LastBucket.Should().Be(_bucket1430);
        footprintSegment.BarCount.Should().Be(1);
        footprintSegment.LastBucket.Should().NotBe(_sixteen);

        ToolPayloads.ContractSegmentInfo profileSegment = profile.Contracts.Segments.Should().ContainSingle().Subject;
        profileSegment.FirstBucket.Should().Be(_bucket1430);
        profileSegment.LastBucket.Should().Be(_bucket1430);
        profileSegment.BarCount.Should().Be(1);

        footprint.Covered.Start.Should().Be(_fourteen);
        footprint.Covered.End.Should().Be(_sixteen);
    }

    [Fact]
    public async Task AWindowBeforeRecordingBegan_Refuses_AndNamesTheEarliestCoveredTime()
    {
        await SeedCoverageAsync(Coverage(Front, _fourteen, _sixteen));
        await SeedCellsAsync(Cell(_bucket1430, 5000m, buy: 4, sell: 1));

        string earliest = _fourteen.ToString("O", CultureInfo.InvariantCulture);

        Func<Task> footprint = () => _tools.GetFootprint(
            "ES", FiveMinutes, _ten, _twelve, CancellationToken.None);
        Func<Task> profile = () => _tools.GetVolumeProfile(
            "ES", FiveMinutes, _ten, _twelve, CancellationToken.None);

        (await footprint.Should().ThrowAsync<McpException>())
            .WithMessage("*earliest*")
            .WithMessage("*" + earliest + "*");
        (await profile.Should().ThrowAsync<McpException>())
            .WithMessage("*earliest*")
            .WithMessage("*" + earliest + "*");
    }

    [Fact]
    public async Task AWindowWithNoTapeAtAll_Refuses_RatherThanReturningAnEmptyAnswer()
    {
        // Prints that would name a volume-front must not turn a no-tape window
        // into a payload. front is not a consolation prize (R-9.6, gh#346).
        await SeedTradesAsync(
            Trade(Central(2026, 8, 18, 10, 0), 1, Expiring, 80, TradeDirection.Buy));

        Func<Task> footprint = () => _tools.GetFootprint(
            "ES", FiveMinutes, _ten, _sixteen, CancellationToken.None);
        Func<Task> profile = () => _tools.GetVolumeProfile(
            "ES", FiveMinutes, _ten, _sixteen, CancellationToken.None);

        await footprint.Should().ThrowAsync<McpException>().WithMessage("*no tape*");
        await profile.Should().ThrowAsync<McpException>().WithMessage("*no tape*");
    }

    [Fact]
    public async Task Front_NamesBothAnswers_WhenTapeVolumeAndTheGatewayDisagree()
    {
        await SeedCoverageAsync(Coverage(Front, _fourteen, _sixteen));
        await SeedCellsAsync(Cell(_bucket1430, 5000m, buy: 4, sell: 1));
        await SeedTradesAsync(
            Trade(Central(2026, 8, 18, 10, 0), 1, Front, 20, TradeDirection.Buy),
            Trade(Central(2026, 8, 19, 10, 0), 2, Front, 10, TradeDirection.Sell),
            Trade(Central(2026, 8, 19, 10, 1), 3, Expiring, 80, TradeDirection.Unknown));

        ToolPayloads.FootprintSeries footprint = await _tools.GetFootprint(
            "ES", FiveMinutes, _fourteen, _sixteen, CancellationToken.None);
        ToolPayloads.VolumeProfileSeries profile = await _tools.GetVolumeProfile(
            "ES", FiveMinutes, _fourteen, _sixteen, CancellationToken.None);

        AssertDisagreement(footprint.Front);
        AssertDisagreement(profile.Front);
    }

    [Fact]
    public async Task Front_UsedIsNone_WhenTheTapeHasNoUniqueFront_AndTheGatewayIsNotSubstituted()
    {
        await SeedCoverageAsync(Coverage(Front, _fourteen, _sixteen));
        await SeedCellsAsync(Cell(_bucket1430, 5000m, buy: 4, sell: 1));

        ToolPayloads.FootprintSeries footprint = await _tools.GetFootprint(
            "ES", FiveMinutes, _fourteen, _sixteen, CancellationToken.None);
        ToolPayloads.VolumeProfileSeries profile = await _tools.GetVolumeProfile(
            "ES", FiveMinutes, _fourteen, _sixteen, CancellationToken.None);

        AssertUnusedGateway(footprint.Front);
        AssertUnusedGateway(profile.Front);
    }

    [Fact]
    public async Task ContractsSegments_StayTheListeningRun_WhenFrontTapeContractIdDiffers()
    {
        await SeedCoverageAsync(Coverage(Front, _fourteen, _sixteen));
        await SeedCellsAsync(Cell(_bucket1430, 5000m, buy: 4, sell: 1));
        // Counted prints justify the seeded cell. Expiring size is Unknown so it
        // moves tape-volume front without entering the footprint (data dictionary §7 / §9).
        await SeedTradesAsync(
            Trade(_bucket1430.AddMinutes(1), 1, Front, 4, TradeDirection.Buy),
            Trade(_bucket1430.AddMinutes(2), 2, Front, 1, TradeDirection.Sell),
            Trade(_bucket1430.AddMinutes(3), 3, Expiring, 80, TradeDirection.Unknown));

        ToolPayloads.FootprintSeries footprint = await _tools.GetFootprint(
            "ES", FiveMinutes, _fourteen, _sixteen, CancellationToken.None);
        ToolPayloads.VolumeProfileSeries profile = await _tools.GetVolumeProfile(
            "ES", FiveMinutes, _fourteen, _sixteen, CancellationToken.None);

        footprint.Front.TapeContractId.Should().Be(Expiring);
        profile.Front.TapeContractId.Should().Be(Expiring);
        footprint.Contracts.Segments.Should().ContainSingle()
            .Which.ContractId.Should().Be(Front);
        profile.Contracts.Segments.Should().ContainSingle()
            .Which.ContractId.Should().Be(Front);
        footprint.Contracts.Segments[0].ContractId.Should().NotBe(footprint.Front.TapeContractId);
        profile.Contracts.Segments[0].ContractId.Should().NotBe(profile.Front.TapeContractId);
    }

    [Fact]
    public async Task GetFootprintAndGetVolumeProfile_RefuseWithTheExplanation_WhenHealthIsNotListening()
    {
        // Cells in the store must not look like a live tape. An absent tape refuses;
        // it does not answer thinly.
        await SeedCoverageAsync(Coverage(Front, _fourteen, _sixteen));
        await SeedCellsAsync(Cell(_bucket1430, 5000m, buy: 4, sell: 1));

        _tape.Set(TapeAvailability.NeverStartedBecauseStdio());
        string explanation = _tape.Value.Explanation!;

        Func<Task<ToolPayloads.FootprintSeries>> footprint = () => _tools.GetFootprint(
            "ES", FiveMinutes, _fourteen, _sixteen, CancellationToken.None);
        Func<Task<ToolPayloads.VolumeProfileSeries>> profile = () => _tools.GetVolumeProfile(
            "ES", FiveMinutes, _fourteen, _sixteen, CancellationToken.None);

        McpException footprintError = (await footprint.Should().ThrowAsync<McpException>()).Which;
        footprintError.Message.Should().Be(explanation);
        footprintError.Message.Should().MatchRegex("(?i)stdio|http|recordtape");

        McpException profileError = (await profile.Should().ThrowAsync<McpException>()).Which;
        profileError.Message.Should().Be(explanation);
    }

    [Fact]
    public void BothTools_AreRegistered_AndTheirDescriptionsStateTheForwardOnlyLimitAndNullForms()
    {
        MethodInfo footprint = typeof(MarketDataTools).GetMethod(nameof(MarketDataTools.GetFootprint))!;
        MethodInfo profile = typeof(MarketDataTools).GetMethod(nameof(MarketDataTools.GetVolumeProfile))!;

        footprint.GetCustomAttribute<McpServerToolAttribute>().Should().NotBeNull();
        profile.GetCustomAttribute<McpServerToolAttribute>().Should().NotBeNull();

        string footprintDescription = footprint.GetCustomAttribute<DescriptionAttribute>()!.Description;
        string profileDescription = profile.GetCustomAttribute<DescriptionAttribute>()!.Description;

        foreach (string description in new[] { footprintDescription, profileDescription })
        {
            description.Should().MatchRegex("(?i)tape only goes forward|no historical footprint|before recording");
            description.Should().MatchRegex("(?i)omitted|present.*null|always present");
            description.Should().MatchRegex("(?i)refus");
            description.Should().MatchRegex("(?i)listen");
            description.Should().MatchRegex("(?i)tape-volume|volume-front");
        }
    }

    private static void AssertDisagreement(ToolPayloads.VolumeFrontInfo front)
    {
        front.Used.Should().Be(TapeVolumeFrontRead.UsedTapeVolume);
        front.Agree.Should().BeFalse();
        front.TapeContractId.Should().Be(Expiring);
        front.TapeSessionDate.Should().Be(new DateOnly(2026, 8, 19));
        front.GatewayContractId.Should().Be(GatewaySelected);
        front.Changeover.Should().NotBeNull();
        front.Changeover!.SessionDate.Should().Be(new DateOnly(2026, 8, 19));
        front.Changeover.FromContractId.Should().Be(Front);
        front.Changeover.ToContractId.Should().Be(Expiring);
    }

    private static void AssertUnusedGateway(ToolPayloads.VolumeFrontInfo front)
    {
        front.Used.Should().Be(TapeVolumeFrontRead.UsedNone);
        front.Used.Should().NotBe(TapeVolumeFrontRead.UsedTapeVolume);
        front.TapeContractId.Should().BeNull();
        front.GatewayContractId.Should().Be(GatewaySelected);
        front.Agree.Should().BeFalse();
    }

    private async Task SeedCoverageAsync(params TapeCoverageRecord[] rows)
    {
        _database.TapeCoverage.AddRange(rows);
        await _database.SaveChangesAsync();
    }

    private async Task SeedCellsAsync(params FootprintCellRecord[] cells)
    {
        _database.FootprintCells.AddRange(cells);
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

    private static TapeCoverageRecord Coverage(
        string contractId,
        DateTimeOffset start,
        DateTimeOffset end) => new()
        {
            Venue = Venue,
            Instrument = _es.Symbol,
            ContractId = contractId,
            RangeStart = start,
            RangeEnd = end,
            RecordedAt = _recorded,
        };

    private static FootprintCellRecord Cell(
        DateTimeOffset bucket,
        decimal price,
        long buy,
        long sell) => new()
        {
            Venue = Venue,
            Instrument = _es.Symbol,
            ResolutionMinutes = FiveMinutes,
            BucketStart = bucket,
            Price = price,
            BuyVolume = buy,
            SellVolume = sell,
            RecordedAt = _recorded,
        };
}
