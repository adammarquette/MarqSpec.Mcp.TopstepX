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

namespace MarqSpec.Mcp.TopstepX.Tests.Tools;

/// <summary>
/// <c>get_key_levels</c> binds the tape-derived profile around volume-* Detect (gh#319).
/// </summary>
public sealed class VolumeKeyLevelPlumbingTests : IDisposable
{
    private const string Contract = "CON.F.US.EP.U26";
    private const int FiveMinutes = 5;

    private static readonly DateTimeOffset _start = new(2026, 8, 18, 14, 10, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _cellBucket = new(2026, 8, 18, 14, 30, 0, TimeSpan.Zero);

    private readonly TopstepXDbContext _database = new(
        new DbContextOptionsBuilder<TopstepXDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics
                .InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task VolumePoc_UsesTheTapeProfile_NotBarVolume()
    {
        SeedBars();
        SeedCoverage(_start, _start.AddHours(1));
        SeedCells();

        ToolPayloads.LevelSet levels = await Tools().GetKeyLevels(
            "ES", FiveMinutes, 5, methods: "volume-poc", cancellationToken: CancellationToken.None);

        ToolPayloads.LevelMethodResult poc = levels.Methods!.Single(m => m.Name == "volume-poc");
        poc.Family.Should().Be(VolumeLevels.FamilyName);
        poc.AbsentReason.Should().BeNull();
        poc.Levels.Should().ContainSingle().Which.Midpoint.Should().Be(5000.00m);
    }

    [Fact]
    public async Task VolumePoc_WithoutTape_IsNamedAbsent_NotABarSpreadPoc()
    {
        SeedBars();

        ToolPayloads.LevelSet levels = await Tools().GetKeyLevels(
            "ES", FiveMinutes, 5, methods: "volume-poc", cancellationToken: CancellationToken.None);

        ToolPayloads.LevelMethodResult poc = levels.Methods!.Single(m => m.Name == "volume-poc");
        poc.Levels.Should().BeEmpty();
        poc.AbsentReason.Should().Be(VolumeLevels.NoTapeReason);
        levels.Confluence!.Absent.Should().Contain(a => a.Method == "volume-poc" && a.Reason == VolumeLevels.NoTapeReason);
    }

    [Fact]
    public async Task VolumePoc_WhenTheCoveredTapeIsNarrowerThanTheAsk_IsNamedAbsent_NotAConfinedPoc()
    {
        // Bars cover 14:10–14:35. Tape only listened from 14:25. Confine reports Narrowed;
        // the profile is a POC of that late run. Returning it with detectedOverBars == 5
        // would dress the confined print as a POC of the whole key-levels window (gh#221).
        SeedBars();
        SeedCoverage(_start.AddMinutes(15), _start.AddHours(1));
        SeedCells();

        ToolPayloads.LevelSet levels = await Tools().GetKeyLevels(
            "ES",
            FiveMinutes,
            5,
            methods: "volume-poc,volume-vah,volume-val,volume-traded",
            cancellationToken: CancellationToken.None);

        levels.DetectedOverBars.Should().Be(5, "the bar series is one contract and complete; the lie is the tape");

        foreach (string name in new[] { "volume-poc", "volume-vah", "volume-val", "volume-traded" })
        {
            ToolPayloads.LevelMethodResult method = levels.Methods!.Single(m => m.Name == name);
            method.Levels.Should().BeEmpty(
                name + " must not report a zone from a narrowed tape as if it covered the asked window");
            method.AbsentReason.Should().Be(VolumeLevels.NarrowedReason);
        }

        levels.Confluence!.Absent.Select(a => a.Method).Should().BeEquivalentTo(
            ["volume-poc", "volume-vah", "volume-val", "volume-traded"]);
        levels.Confluence.Absent.Should().OnlyContain(a => a.Reason == VolumeLevels.NarrowedReason);
        levels.Levels.Should().BeEmpty();
    }

    private MarketDataTools Tools()
    {
        IOptions<MarketDataOptions> market = Options.Create(new MarketDataOptions
        {
            Instruments = "ES,NQ",
            MaxRows = 5_000,
            SessionCloseCentral = "16:00",
        });

        BarSessionCalendar calendar = BarSessionCalendar.Parse("16:00", []);
        IndicatorCatalog indicators = new(
            Options.Create(new IndicatorOptions { AtrPeriod = 3 }), calendar);
        CountingGateway gateway = new([]);
        FakeTimeProvider clock = new(_start.AddHours(2));
        IndicatorProjector projector = new(_database, indicators, NullLogger<IndicatorProjector>.Instance);
        BarCacheService cache = new(
            _database, gateway, calendar, projector, clock, NullLogger<BarCacheService>.Instance);

        return new MarketDataTools(
            cache,
            _database,
            new InstrumentRegistry(market),
            indicators,
            new IndicatorCacheService(
                _database, indicators, projector, clock, NullLogger<IndicatorCacheService>.Instance),
            new LevelMethodCatalog(calendar),
            gateway,
            new ToolGuards(market),
            new StoreAvailabilityHolder(),
            clock,
            Options.Create(new KeyLevelDetectionOptions
            {
                Source = PivotSource.HighLow,
                PivotLookback = 2,
                PivotRightLookback = 2,
                ZoneAtrMultiple = 0.5m,
                MinSignificance = 0m,
                MaxZoneWidthPercent = 100m,
                MaxLevels = 1_000,
            }),
            new VolumeProfileService(_database),
            new TapeAvailabilityHolder(),
            new TapeVolumeFrontService(_database, gateway, calendar),
            new FootprintCacheService(
                _database,
                new FootprintProjector(_database, NullLogger<FootprintProjector>.Instance),
                clock,
                NullLogger<FootprintCacheService>.Instance));
    }

    private void SeedBars()
    {
        for (int i = 0; i < 5; i++)
        {
            DateTimeOffset bucket = _start.AddMinutes(5 * i);
            _database.Bars.Add(new BarRecord
            {
                Venue = "test",
                Instrument = "ES",
                ResolutionMinutes = FiveMinutes,
                BucketStart = bucket,
                Open = 4999m,
                High = 5005m,
                Low = 4995m,
                Close = 5001m,
                Volume = 1_000_000,
                ContractId = Contract,
                RecordedAt = _start,
            });
        }

        _database.SaveChanges();
    }

    private void SeedCoverage(DateTimeOffset start, DateTimeOffset end)
    {
        _database.TapeCoverage.Add(new TapeCoverageRecord
        {
            Venue = "test",
            Instrument = "ES",
            ContractId = Contract,
            RangeStart = start,
            RangeEnd = end,
            RecordedAt = _start,
        });
        _database.SaveChanges();
    }

    private void SeedCells()
    {
        _database.FootprintCells.AddRange(
            Cell(4999.75m, 2, 1),
            Cell(5000.00m, 8, 2),
            Cell(5000.25m, 4, 0));
        _database.SaveChanges();
    }

    private static FootprintCellRecord Cell(decimal price, long buy, long sell) => new()
    {
        Venue = "test",
        Instrument = "ES",
        ResolutionMinutes = FiveMinutes,
        BucketStart = _cellBucket,
        Price = price,
        BuyVolume = buy,
        SellVolume = sell,
        RecordedAt = _start,
    };
}
