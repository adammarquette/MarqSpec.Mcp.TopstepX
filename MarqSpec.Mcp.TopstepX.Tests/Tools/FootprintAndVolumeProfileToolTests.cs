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
/// Live tape health is omitted on purpose (gh#218 is still blocked).
/// </remarks>
public sealed class FootprintAndVolumeProfileToolTests : IDisposable
{
    private const string Venue = "test";
    private const string Front = "CON.F.US.EP.Z26";
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
            new VolumeProfileService(_database));
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
        Func<Task> footprint = () => _tools.GetFootprint(
            "ES", FiveMinutes, _ten, _sixteen, CancellationToken.None);
        Func<Task> profile = () => _tools.GetVolumeProfile(
            "ES", FiveMinutes, _ten, _sixteen, CancellationToken.None);

        await footprint.Should().ThrowAsync<McpException>().WithMessage("*no tape*");
        await profile.Should().ThrowAsync<McpException>().WithMessage("*no tape*");
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
        }
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
