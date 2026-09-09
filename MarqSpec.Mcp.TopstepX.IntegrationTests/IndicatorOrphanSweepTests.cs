using System.Data;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// A stored indicator value the bars cannot account for is swept, whichever way it was orphaned (gh#571).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the "wrong looks right" class.</b> Nothing here throws and nothing returns an empty series. An
/// orphaned value reads back as an ordinary number — the right shape, the right scale, in the right column —
/// and it was computed over bars that are gone or under a window the operator stopped maintaining. ADR-0006
/// says every stored value is reproducible from §1 of the store; a row that is not is the one row a replay
/// cannot confirm or correct, and <c>rebuild-indicators</c> reported an empty diff over it.
/// </para>
/// <para>
/// <b>Two ways in, and they failed for two different reasons.</b> A value under a
/// <c>(Indicator, Period)</c> pair the catalogue no longer computes was skipped by the reconcile's own scope
/// filter — deliberately, as the guard against a period change sweeping the previous window's rows away. And
/// a value over a series whose bars are <i>all</i> gone was never reached at all, because
/// <see cref="IndicatorRebuilder"/> enumerated the series to replay from <c>Bars</c>: a series with no bars
/// left is not in that list, so nothing ever visited it again.
/// </para>
/// <para>
/// <b>A partial bar delete was already handled and stays that way.</b> The reconcile removes every value the
/// pass did not produce for a pair it does compute, so an orphaned tail goes on the next pass —
/// <c>IndicatorReconcileConcurrencyTests</c> drives exactly that. What is new here is the pair the catalogue
/// dropped, and the series that lost its last bar.
/// </para>
/// </remarks>
[Collection(SeriesStoreCollection.Name)]
public sealed class IndicatorOrphanSweepTests : IAsyncLifetime
{
    private const string Venue = "test";
    private const int Resolution = 5;
    private const int Bars = 40;

    private static readonly InstrumentId _es = new("ES");

    private readonly SeriesStoreFixture _fixture;
    private readonly TopstepXDbContext _database;
    private readonly HostTelemetry _telemetry = new();

    /// <param name="fixture">The shared container.</param>
    public IndicatorOrphanSweepTests(SeriesStoreFixture fixture)
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
        _telemetry.Dispose();
        return Task.CompletedTask;
    }

    private static DateTimeOffset SessionStart =>
        MarketClock.FromMarket(new DateOnly(2026, 8, 18), new TimeOnly(9, 0)).ToUniversalTime();

    private static DateTimeOffset Bucket(int index) => SessionStart.AddMinutes(Resolution * index);

    [Fact]
    public async Task AValueUnderARetiredPeriod_IsRemovedByTheNextPass()
    {
        // The operator configured ATR(99), the store wrote it, and the operator has since configured ATR(3).
        // The row sits at a bucket that still HAS a bar, so nothing about the bars is wrong -- what is wrong
        // is that no pass will ever recompute this number, so no replay can confirm it and none can correct
        // it. ADR-0006 calls that unreproducible; the store must not hold it.
        await SeedBarsAsync();

        _database.IndicatorValues.Add(RetiredRow(Bucket(10)));
        await _database.SaveChangesAsync();

        // The pass reads its values UNTRACKED and hands them to Remove, which attaches each as Deleted. A row
        // still tracked from the seed above would be a second instance of a key the identity map already
        // holds, and the pass would throw rather than sweep (the QA contract's seeding note, gh#387).
        _database.ChangeTracker.Clear();

        await ProjectOnePassAsync(Projector(), SessionStart);

        (await RetiredRowsAsync()).Should().BeEmpty(
            "no pass recomputes ATR(99), so the row is a number the store can neither confirm nor correct");
    }

    [Fact]
    public async Task AValueWhoseBarWasDeleted_IsRemovedByTheNextPass()
    {
        // Every bar of the series is deleted -- a base revision, or reselect-bars replacing a window that
        // turns out to be the whole of what was stored. There is no foreign key (ADR-0011), so the values
        // stand.
        //
        // The reconcile would remove them if it ran. It never ran: rebuild-indicators enumerated the series
        // to replay from `Bars`, and a series with no bars left is not in that list. So the verb an operator
        // reaches for to repair the store reported an EMPTY DIFF over exactly the rows that needed repairing.
        await SeedBarsAsync();

        await Rebuilder().RebuildAsync("ES", CancellationToken.None);
        (await StoredValueCountAsync()).Should().BeGreaterThan(0, "the fixture must produce something");

        _database.Bars.RemoveRange(await _database.Bars.ToListAsync());
        await _database.SaveChangesAsync();

        IndicatorRebuildResult healed = await Rebuilder().RebuildAsync("ES", CancellationToken.None);

        (await StoredValueCountAsync()).Should().Be(
            0, "a value over a series with no bars is the purest form of a number nothing justifies");
        healed.ValuesChanged.Should().BeGreaterThan(0, "the sweep is a change, and it is counted as one");
        healed.SeriesRewritten.Should().Be(1, "the series it swept is a series it rewrote");
    }

    [Fact]
    public async Task TheSweep_CountsTheTwoKindsOfOrphanSeparately()
    {
        // "Both counted separately and logged, never silent." A destructive pass that says only "removed 2"
        // cannot tell an operator whether their period change or their bar delete did it -- and those call
        // for different follow-ups.
        await SeedBarsAsync();

        _database.IndicatorValues.Add(RetiredRow(Bucket(10)));
        _database.IndicatorValues.Add(new IndicatorValueRecord
        {
            Venue = Venue,
            Instrument = _es.Symbol,
            ResolutionMinutes = Resolution,
            Indicator = "atr",
            Period = 3,                    // a pair the catalogue DOES compute...
            BucketStart = Bucket(Bars + 500),   // ...at a bucket that has no bar
            Value = 42m,
            RecordedAt = SessionStart,
        });

        await _database.SaveChangesAsync();
        _database.ChangeTracker.Clear();

        CapturingLogger<IndicatorProjector> log = new();

        await ProjectOnePassAsync(Projector(log), SessionStart);

        log.Messages.Should().ContainSingle(m => m.Contains("no longer computes", StringComparison.Ordinal))
            .Which.Should().Contain("1 stored indicator value").And.Contain("1 standing at a bucket");
    }

    [Fact]
    public async Task AConfirmingRebuild_OverACleanStore_StillReportsNothing()
    {
        // The sweep must not become a source of churn. A store with no orphans in it has nothing to sweep,
        // so the second rebuild is still the empty diff ADR-0006 turns on -- including now that the rebuild
        // walks the series it finds in IndicatorValues as well as the ones it finds in Bars.
        await SeedBarsAsync();

        await Rebuilder().RebuildAsync(null, CancellationToken.None);
        IndicatorRebuildResult second = await Rebuilder().RebuildAsync(null, CancellationToken.None);

        second.ValuesChanged.Should().Be(0);
        second.SeriesRewritten.Should().Be(0);
    }

    // ── Scaffolding ──────────────────────────────────────────────────────────────────────────────────

    private static IndicatorCatalog Catalog()
    {
        BarSessionCalendar calendar = BarSessionCalendar.Parse("16:00", []);
        return new IndicatorCatalog(
            Options.Create(new IndicatorOptions { AtrPeriod = 3, RsiPeriod = 3 }), calendar);
    }

    private IndicatorProjector Projector(ILogger<IndicatorProjector>? log = null) =>
        new(_database, Catalog(), log ?? NullLogger<IndicatorProjector>.Instance, _telemetry);

    private IndicatorRebuilder Rebuilder()
    {
        IOptions<MarketDataOptions> options = Options.Create(new MarketDataOptions
        {
            Instruments = "ES,NQ",
            MaxRows = 5_000,
            SessionCloseCentral = "16:00",
        });

        return new IndicatorRebuilder(
            _database,
            Projector(),
            new InstrumentRegistry(options),
            new FakeTimeProvider(SessionStart.AddDays(1)),
            NullLogger<IndicatorRebuilder>.Instance);
    }

    /// <summary>A row under a period this catalogue does not compute.</summary>
    /// <param name="bucket">Where to plant it.</param>
    /// <returns>The row.</returns>
    private static IndicatorValueRecord RetiredRow(DateTimeOffset bucket) => new()
    {
        Venue = Venue,
        Instrument = _es.Symbol,
        ResolutionMinutes = Resolution,
        Indicator = "atr",
        Period = 99,
        BucketStart = bucket,
        Value = 42m,
        RecordedAt = SessionStart,
    };

    /// <summary>Runs one projection pass the way every call site in the product runs one.</summary>
    /// <param name="projector">The projection.</param>
    /// <param name="now">The instant the pass runs at.</param>
    /// <returns>How many rows the pass changed.</returns>
    private async Task<int> ProjectOnePassAsync(IndicatorProjector projector, DateTimeOffset now)
    {
        await using IDbContextTransaction transaction = await _database.Database
            .BeginTransactionAsync(IsolationLevel.RepeatableRead, CancellationToken.None);

        int changed = await projector.ProjectAsync(Venue, _es, Resolution, now, CancellationToken.None);

        await _database.SaveChangesAsync();
        await transaction.CommitAsync();

        return changed;
    }

    private async Task<List<IndicatorValueRecord>> RetiredRowsAsync() =>
        await _database.IndicatorValues
            .AsNoTracking()
            .Where(v => v.Indicator == "atr" && v.Period == 99)
            .ToListAsync();

    private async Task<int> StoredValueCountAsync() =>
        await _database.IndicatorValues.AsNoTracking().CountAsync();

    /// <summary>Bars that drift irregularly, so the indicators land on values with real precision.</summary>
    private async Task SeedBarsAsync()
    {
        for (int i = 0; i < Bars; i++)
        {
            decimal drift = i % 3 == 0 ? 1.37m : i % 3 == 1 ? -0.91m : 2.13m;
            decimal close = 5_000m + (i * drift);

            _database.Bars.Add(new BarRecord
            {
                Venue = Venue,
                Instrument = _es.Symbol,
                ResolutionMinutes = Resolution,
                BucketStart = Bucket(i),
                Open = close,
                High = close + 1.25m,
                Low = close - 0.75m,
                Close = close,
                Volume = 1_000 + i,
                ContractId = "CON.F.US.EP.Z26",
                RecordedAt = SessionStart,
            });
        }

        await _database.SaveChangesAsync();
    }
}
