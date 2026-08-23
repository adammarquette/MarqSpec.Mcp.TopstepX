using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// The projection, and specifically the claim that a confirming rebuild changes nothing.
/// </summary>
/// <remarks>
/// <para>
/// That claim was false for the whole of Phase 2 and nothing noticed, because <b>no test projected twice</b>.
/// The column is <c>numeric(18,8)</c> and the computation carries full decimal precision, so a stored
/// <c>38.95895082</c> never equalled a recomputed <c>38.958950821743…</c> — the "skip unchanged" guard was
/// dead code, and every rebuild rewrote every row and moved every <c>RecordedAt</c>.
/// </para>
/// <para>
/// Found by running <c>rebuild-indicators</c> against a live container for the first time: 8,777 values
/// written, then 8,777 again with nothing changed in between.
/// </para>
/// </remarks>
public sealed class IndicatorProjectorTests : IDisposable
{
    private const string Venue = "test";
    private static readonly InstrumentId _es = new("ES");

    private readonly TopstepXDbContext _database;

    public IndicatorProjectorTests() =>
        _database = new TopstepXDbContext(
            new DbContextOptionsBuilder<TopstepXDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);

    public void Dispose() => _database.Dispose();

    private static DateTimeOffset SessionStart =>
        MarketClock.FromMarket(new DateOnly(2026, 8, 18), new TimeOnly(9, 0)).ToUniversalTime();

    private IndicatorProjector Projector()
    {
        BarSessionCalendar calendar = BarSessionCalendar.Parse("16:00", []);
        IndicatorCatalog catalog = new(
            Options.Create(new IndicatorOptions { AtrPeriod = 3, RsiPeriod = 3 }), calendar);

        return new IndicatorProjector(_database, catalog, NullLogger<IndicatorProjector>.Instance);
    }

    /// <summary>
    /// Seeds bars whose indicators land on values with more precision than the column keeps.
    /// </summary>
    /// <remarks>
    /// Deliberately irregular. A tidy ramp produces RSI values like 100 and 50 that survive rounding intact,
    /// and would have passed even with the defect present.
    /// </remarks>
    private async Task SeedBarsAsync(int count)
    {
        for (int i = 0; i < count; i++)
        {
            decimal drift = i % 3 == 0 ? 1.37m : i % 3 == 1 ? -0.91m : 2.13m;
            decimal close = 5000m + (i * drift);

            _database.Bars.Add(new BarRecord
            {
                Venue = Venue,
                Instrument = _es.Symbol,
                ResolutionMinutes = 5,
                BucketStart = SessionStart.AddMinutes(5 * i),
                Open = close,
                High = close + 1.25m,
                Low = close - 0.75m,
                Close = close,
                Volume = 1_000 + i,
                RecordedAt = SessionStart,
            });
        }

        await _database.SaveChangesAsync();
    }

    [Fact]
    public async Task AConfirmingRebuild_WritesNothing()
    {
        // THE regression. Two projections over identical bars: the second must be a no-op.
        await SeedBarsAsync(40);
        IndicatorProjector projector = Projector();

        int first = await projector.ProjectAsync(Venue, _es, 5, SessionStart, CancellationToken.None);
        await _database.SaveChangesAsync();

        int second = await projector.ProjectAsync(
            Venue, _es, 5, SessionStart.AddHours(1), CancellationToken.None);

        first.Should().BeGreaterThan(0, "the first pass has values to write");
        second.Should().Be(0, "nothing changed, so a rebuild must produce an empty diff");
    }

    [Fact]
    public async Task AConfirmingRebuild_LeavesEveryRecordedAtAlone()
    {
        // RecordedAt is documented as WHEN THIS VALUE LAST CHANGED. If a rebuild moves it, the field instead
        // records when a rebuild last ran -- a different fact, and the audit value it exists for is gone.
        await SeedBarsAsync(40);
        IndicatorProjector projector = Projector();

        await projector.ProjectAsync(Venue, _es, 5, SessionStart, CancellationToken.None);
        await _database.SaveChangesAsync();

        await projector.ProjectAsync(Venue, _es, 5, SessionStart.AddDays(1), CancellationToken.None);
        await _database.SaveChangesAsync();

        List<DateTimeOffset> stamps = await _database.IndicatorValues
            .Select(v => v.RecordedAt).Distinct().ToListAsync();

        stamps.Should().ContainSingle().Which.Should().Be(SessionStart);
    }

    [Fact]
    public async Task StoredValuesNeverExceedTheColumnScale()
    {
        // The root cause, pinned directly: a value written with more places than the column keeps is a value
        // that cannot compare equal to itself after a round trip.
        await SeedBarsAsync(40);

        await Projector().ProjectAsync(Venue, _es, 5, SessionStart, CancellationToken.None);
        await _database.SaveChangesAsync();

        List<decimal> values = await _database.IndicatorValues.Select(v => v.Value).ToListAsync();

        values.Should().NotBeEmpty();
        values.Should().AllSatisfy(v =>
            decimal.Round(v, TopstepXDbContext.PriceScale).Should().Be(v));
    }

    [Fact]
    public async Task AChangedBar_IsStillPickedUp()
    {
        // The other half. Rounding must not make the projector blind to a real change -- a guard that never
        // fires and a guard that always fires are equally useless, in opposite directions.
        await SeedBarsAsync(40);
        IndicatorProjector projector = Projector();

        await projector.ProjectAsync(Venue, _es, 5, SessionStart, CancellationToken.None);
        await _database.SaveChangesAsync();

        BarRecord last = await _database.Bars.OrderBy(b => b.BucketStart).LastAsync();
        last.Close += 25m;
        last.High += 25m;
        await _database.SaveChangesAsync();

        int written = await projector.ProjectAsync(
            Venue, _es, 5, SessionStart.AddHours(1), CancellationToken.None);

        written.Should().BeGreaterThan(0, "a moved close changes the indicators derived from it");
    }

    [Fact]
    public void TheScaleConstantAgreesWithTheColumnType()
    {
        // They cannot be derived from one another at compile time, so this is what keeps them honest. A
        // column widened without moving the constant reintroduces the defect silently.
        TopstepXDbContext.PriceColumnType
            .Should().EndWith("," + TopstepXDbContext.PriceScale.ToString(
                System.Globalization.CultureInfo.InvariantCulture) + ")");
    }
}
