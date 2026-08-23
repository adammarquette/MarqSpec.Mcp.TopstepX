using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// What the rebuild verb leaves in the change tracker as it walks the store.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IndicatorRebuilder"/> reuses one context across every series, and the projector reads each
/// series' whole bar history. Tracked, those bars accumulate for the length of the run — and EF's change
/// detection is superlinear in the tracked count, so every subsequent series' save gets slower. On the
/// ~70,000-bars-per-instrument figure this repository already quotes, a fifty-series rebuild tracks millions
/// of entities it will never write.
/// </para>
/// <para>
/// Not a correctness bug, which is why it is pinned here rather than argued about: it is the <b>repair</b>
/// verb, run over the whole store, degrading worst exactly where the store is largest.
/// </para>
/// </remarks>
public sealed class IndicatorRebuilderTrackingTests : IDisposable
{
    private const string Venue = "test";

    private readonly TopstepXDbContext _database;

    public IndicatorRebuilderTrackingTests() =>
        _database = new TopstepXDbContext(
            new DbContextOptionsBuilder<TopstepXDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options);

    public void Dispose() => _database.Dispose();

    private static DateTimeOffset SessionStart =>
        MarketClock.FromMarket(new DateOnly(2026, 8, 18), new TimeOnly(9, 0)).ToUniversalTime();

    private void Seed(string instrument, int resolutionMinutes, int bars)
    {
        for (int i = 0; i < bars; i++)
        {
            _database.Bars.Add(new BarRecord
            {
                Venue = Venue,
                Instrument = instrument,
                ResolutionMinutes = resolutionMinutes,
                BucketStart = SessionStart.AddMinutes(resolutionMinutes * i),
                Open = 100m + i,
                High = 101m + i,
                Low = 99m + i,
                Close = 100m + i,
                Volume = 1_000,
                ContractId = "CON.F.US.EP.Z26",
                RecordedAt = SessionStart,
            });
        }

        _database.SaveChanges();
    }

    private IndicatorRebuilder Rebuilder()
    {
        BarSessionCalendar calendar = BarSessionCalendar.Parse("16:00", []);
        IndicatorCatalog catalog = new(
            Options.Create(new IndicatorOptions { AtrPeriod = 3, RsiPeriod = 3 }), calendar);

        IOptions<MarketDataOptions> options = Options.Create(new MarketDataOptions
        {
            Instruments = "ES,NQ",
            MaxRows = 5_000,
            SessionCloseCentral = "16:00",
        });

        return new IndicatorRebuilder(
            _database,
            new IndicatorProjector(_database, catalog, NullLogger<IndicatorProjector>.Instance),
            new InstrumentRegistry(options),
            new FakeTimeProvider(SessionStart.AddDays(1)),
            NullLogger<IndicatorRebuilder>.Instance);
    }

    [Fact]
    public async Task ARebuild_DoesNotAccumulateOneSeriesBarsWhileWalkingTheNext()
    {
        // Two series, so "cleared between series" is distinguishable from "cleared at the end". The assertion
        // is on BarRecord specifically: bars are the volume, they are read-only to the projector, and there is
        // no reason for a single one to be tracked once its series is written.
        Seed("ES", 5, 40);
        Seed("NQ", 5, 40);

        await Rebuilder().RebuildAsync(null, CancellationToken.None);

        _database.ChangeTracker.Entries<BarRecord>().Should().BeEmpty(
            "the projector never mutates a bar, so tracking them costs change detection and buys nothing");
    }

    [Fact]
    public async Task ARebuild_StillWritesTheValuesItShould()
    {
        // The other half. Detaching the wrong thing would make the rebuild quietly stop writing, and an
        // emptier change tracker is exactly what that failure would look like from the test above.
        Seed("ES", 5, 40);

        int changed = await Rebuilder().RebuildAsync("ES", CancellationToken.None);

        changed.Should().BeGreaterThan(0);
        _database.IndicatorValues.Count(v => v.Instrument == "ES").Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ASecondRebuild_IsAConfirmationAndWritesNothing()
    {
        // Idempotence, asserted after the tracking change rather than assumed to survive it. A rebuild that
        // re-reads untracked bars must still recognise the values already standing over them.
        Seed("ES", 5, 40);

        await Rebuilder().RebuildAsync("ES", CancellationToken.None);
        int second = await Rebuilder().RebuildAsync("ES", CancellationToken.None);

        second.Should().Be(0, "the same bars justify the same values, so the second pass changes nothing");
    }
}
