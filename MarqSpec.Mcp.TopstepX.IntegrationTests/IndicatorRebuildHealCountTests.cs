using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// <c>rebuild-indicators</c> reports how many series it rewrote — ADR-0012's cheap side channel (gh#348).
/// </summary>
/// <remarks>
/// <para>
/// A fill cannot increment "I left a seam": the pass that suffers adjacent-fill write-skew cannot see that it
/// did. The next deliberate heal — this verb — is what knows a value actually changed. That is a <b>heal
/// count</b>, not a skew count, and a confirming rebuild (same bars, same numbers) must not move it.
/// </para>
/// <para>
/// The seam fixture below plants the two scars <c>AdjacentFillWriteSkewTests</c> names: ATR absent at the
/// join, VWAP present and wrong. Planted rather than raced, which is what keeps the two suites distinct —
/// that one drives the race and shows the scars are what a race really leaves, and this one asks only what
/// the heal count reports once they are there.
/// </para>
/// <para>
/// <b>This tier since gh#387.</b> The suite used to run in the unit tier, where the projection took a second
/// write that merged its values through the change tracker rather than sending <c>UpsertValuesSql</c>'s
/// <c>ON CONFLICT … DO UPDATE</c>, and the rebuild's per-series transaction was skipped for want of a
/// provider that had one. That stand-in has been deleted, so the count this verb returns is only observable
/// by running the verb against a real store — the store it counts rows in.
/// </para>
/// </remarks>
[Collection(SeriesStoreCollection.Name)]
public sealed class IndicatorRebuildHealCountTests : IAsyncLifetime
{
    /// <summary>The bucket the two adjacent fills meet at, matching <c>AdjacentFillWriteSkewTests</c>.</summary>
    private const int Seam = 20;

    /// <summary>One past the last bucket those two fills cover between them.</summary>
    private const int End = 40;

    private const string Venue = "test";

    private readonly SeriesStoreFixture _fixture;
    private readonly TopstepXDbContext _database;

    /// <param name="fixture">The shared container.</param>
    public IndicatorRebuildHealCountTests(SeriesStoreFixture fixture)
    {
        _fixture = fixture;
        _database = fixture.CreateContext();
    }

    /// <inheritdoc />
    public Task InitializeAsync() => _fixture.ResetAsync();

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        _database.Dispose();
        return Task.CompletedTask;
    }

    private static DateTimeOffset SessionStart =>
        MarketClock.FromMarket(new DateOnly(2026, 8, 18), new TimeOnly(9, 0)).ToUniversalTime();

    private void Seed(string instrument, int bars)
    {
        for (int i = 0; i < bars; i++)
        {
            decimal drift = i % 3 == 0 ? 1.37m : i % 3 == 1 ? -0.91m : 2.13m;
            decimal close = 5_000m + (i * drift);

            _database.Bars.Add(new BarRecord
            {
                Venue = Venue,
                Instrument = instrument,
                ResolutionMinutes = 5,
                BucketStart = SessionStart.AddMinutes(5 * i),
                Open = close,
                High = close + 1.25m,
                Low = close - 0.75m,
                Close = close,
                Volume = 1_000 + i,
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
    public async Task AConfirmingRebuild_DoesNotCountTheSeriesAsRewritten()
    {
        Seed("ES", End);

        await Rebuilder().RebuildAsync("ES", CancellationToken.None);
        IndicatorRebuildResult second = await Rebuilder().RebuildAsync("ES", CancellationToken.None);

        second.ValuesChanged.Should().Be(0, "the same bars justify the same values");
        second.SeriesRewritten.Should().Be(
            0,
            "a confirming rebuild is not a heal — counting it would make every scheduled run look like write-skew");
    }

    [Fact]
    public async Task ARebuildThatChangesAStoredValue_CountsTheSeriesAsRewritten()
    {
        Seed("ES", End);

        IndicatorRebuildResult first = await Rebuilder().RebuildAsync("ES", CancellationToken.None);

        first.ValuesChanged.Should().BeGreaterThan(0);
        first.SeriesRewritten.Should().Be(1, "the empty series was written, so it was rewritten");
    }

    [Fact]
    public async Task ARebuildOfTwoSeries_CountsOnlyTheSeriesWhoseValuesChanged()
    {
        Seed("ES", End);
        Seed("NQ", End);

        await Rebuilder().RebuildAsync(null, CancellationToken.None);

        IndicatorValueRecord row = _database.IndicatorValues.First(v => v.Instrument == "ES");
        row.Value += 1m;
        _database.SaveChanges();

        IndicatorRebuildResult result = await Rebuilder().RebuildAsync(null, CancellationToken.None);

        result.SeriesRewritten.Should().Be(
            1,
            "NQ confirmed; only ES changed — the count is series rewritten, not series walked");
        result.ValuesChanged.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task TheAdjacentFillSeamScars_IncrementTheRewroteCount_WhenRebuildHealsThem()
    {
        // The two scars AdjacentFillWriteSkewTests names on one bucket: a smoothed indicator goes absent at
        // the join, and a session-anchored one stays present and wrong. Planted rather than raced -- driving
        // the race is that suite's job, and this one is about the count the heal returns rather than about
        // how the seam came to be there.
        Seed("ES", End);

        await Rebuilder().RebuildAsync("ES", CancellationToken.None);

        DateTimeOffset seam = SessionStart.AddMinutes(5 * Seam);

        IndicatorValueRecord atrAtTheSeam = _database.IndicatorValues.Single(v =>
            v.Instrument == "ES"
            && v.Indicator == "atr"
            && v.Period == 3
            && v.BucketStart == seam);
        _database.IndicatorValues.Remove(atrAtTheSeam);

        IndicatorValueRecord vwapAtTheSeam = _database.IndicatorValues.Single(v =>
            v.Instrument == "ES"
            && v.Indicator == "vwap"
            && v.Period == 0
            && v.BucketStart == seam);
        vwapAtTheSeam.Value += 1m;
        _database.SaveChanges();

        IndicatorRebuildResult healed = await Rebuilder().RebuildAsync("ES", CancellationToken.None);

        healed.SeriesRewritten.Should().Be(
            1,
            "the rebuild rewrote the raced series — that is the heal count ADR-0012 said to take first");
        healed.ValuesChanged.Should().BeGreaterThan(
            0,
            "ATR returns at the seam and VWAP is corrected; a confirming rebuild would have been empty");
    }
}
