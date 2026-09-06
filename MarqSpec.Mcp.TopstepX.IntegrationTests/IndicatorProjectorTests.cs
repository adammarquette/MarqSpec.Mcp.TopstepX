using System.Data;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

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
/// <para>
/// <b>This tier, because proving any of that now means executing the real write.</b> The suite used to run in
/// the unit tier, where the projection took a second implementation that merged its values through the change
/// tracker instead of sending <c>UpsertValuesSql</c>'s <c>ON CONFLICT … DO UPDATE</c>. So the column whose
/// scale every claim here turns on did not exist while the suite ran: nothing rounded to
/// <c>numeric(18,8)</c>, and a value could not fail to compare equal to itself after a round trip because it
/// never made one. That stand-in was deleted in gh#387, and the only store left is this one.
/// </para>
/// <para>
/// <see cref="IndicatorProjector.ProjectAsync"/> also refuses outside a transaction now, which the stand-in's
/// provider had no way to give it — see <see cref="ProjectOnePassAsync"/>.
/// </para>
/// </remarks>
[Collection(SeriesStoreCollection.Name)]
public sealed class IndicatorProjectorTests : IAsyncLifetime
{
    private const string Venue = "test";
    private static readonly InstrumentId _es = new("ES");

    private readonly SeriesStoreFixture _fixture;
    private readonly TopstepXDbContext _database;

    /// <param name="fixture">The shared container.</param>
    public IndicatorProjectorTests(SeriesStoreFixture fixture)
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

    private IndicatorProjector Projector()
    {
        BarSessionCalendar calendar = BarSessionCalendar.Parse("16:00", []);
        IndicatorCatalog catalog = new(
            Options.Create(new IndicatorOptions { AtrPeriod = 3, RsiPeriod = 3 }), calendar);

        return new IndicatorProjector(_database, catalog, NullLogger<IndicatorProjector>.Instance);
    }

    /// <summary>Runs one projection pass the way every call site in the product runs one.</summary>
    /// <param name="projector">The projection.</param>
    /// <param name="now">The instant the pass runs at, stamped on the rows it changes.</param>
    /// <returns>How many rows the pass changed.</returns>
    /// <remarks>
    /// <para>
    /// A pass writes its values with a statement the store executes as it is sent, and removes the ones the
    /// bars no longer justify through the change tracker, which waits for the caller's <c>SaveChanges</c>.
    /// Outside a transaction the first commits on its own and the second does not, so
    /// <see cref="IndicatorProjector.ProjectAsync"/> refuses rather than half-committing.
    /// <c>SeriesUnitOfWork</c> is the shape both product call sites use: one
    /// <see cref="IsolationLevel.RepeatableRead"/> transaction around the pass and the save, committed
    /// together. This is that shape and nothing more.
    /// </para>
    /// <para>
    /// The tests below called straight into the pass with no transaction at all, which the tier they used to
    /// run in silently permitted (gh#387) — so the unit of work every one of them describes was never the one
    /// any of them exercised.
    /// </para>
    /// </remarks>
    private async Task<int> ProjectOnePassAsync(IndicatorProjector projector, DateTimeOffset now)
    {
        await using IDbContextTransaction transaction = await _database.Database
            .BeginTransactionAsync(IsolationLevel.RepeatableRead, CancellationToken.None);

        int changed = await projector.ProjectAsync(Venue, _es, 5, now, CancellationToken.None);

        await _database.SaveChangesAsync();
        await transaction.CommitAsync();

        return changed;
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

        int first = await ProjectOnePassAsync(projector, SessionStart);

        int second = await ProjectOnePassAsync(projector, SessionStart.AddHours(1));

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

        await ProjectOnePassAsync(projector, SessionStart);

        await ProjectOnePassAsync(projector, SessionStart.AddDays(1));

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

        await ProjectOnePassAsync(Projector(), SessionStart);

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

        await ProjectOnePassAsync(projector, SessionStart);

        BarRecord last = await _database.Bars.OrderBy(b => b.BucketStart).LastAsync();
        last.Close += 25m;
        last.High += 25m;
        await _database.SaveChangesAsync();

        int written = await ProjectOnePassAsync(projector, SessionStart.AddHours(1));

        written.Should().BeGreaterThan(0, "a moved close changes the indicators derived from it");
    }

    [Fact]
    public async Task IndicatorsAreProjectedPerContract_NeverAcrossARoll()
    {
        // gh#42. Eight contiguous 5-minute buckets under the symbol ES: four from the expiring contract at
        // 100 with a 2-point range, four from the new one at 140 with a 4-point range. Nothing in the bucket
        // sequence marks the roll — only the contract id does.
        //
        // Hand-checked, not captured. Every true range inside a segment is that segment's own H-L, so the
        // Wilder seed at the fourth bar is the mean of three identical values: 2 on the old contract and 4 on
        // the new one. Both are exact in decimal.
        //
        // Across the splice, true range at the first new-contract bar is
        // max(142-138, |142-100|, |138-100|) = 42, so a spliced ATR(3) reads (2·2 + 42)/3 ≈ 15.33 — the roll
        // gap reported as volatility. The assertion that there is NO row at that bucket is the assertion that
        // it was never computed.
        await SeedRolledBarsAsync();

        await ProjectOnePassAsync(Projector(), SessionStart);

        Dictionary<DateTimeOffset, decimal> atr = await _database.IndicatorValues
            .Where(v => v.Indicator == "atr")
            .ToDictionaryAsync(v => v.BucketStart, v => v.Value);

        atr.Should().ContainKey(Bucket(3)).WhoseValue.Should().Be(2m, "the expiring contract's own range");
        atr.Should().ContainKey(Bucket(7)).WhoseValue.Should().Be(4m, "the new contract's own range");

        atr.Should().NotContainKey(Bucket(4),
            "the new contract's warm-up restarts at the roll — a value here could only have come from "
            + "smoothing the roll gap forward");
        atr.Should().NotContainKey(Bucket(5));
        atr.Should().NotContainKey(Bucket(6));
    }

    [Fact]
    public async Task AConfirmingRebuild_AcrossARoll_StillWritesNothing()
    {
        // Segmenting must not cost reproducibility. The segment boundaries are a function of the stored bars,
        // so a second pass over the same rows has to produce the same numbers and an empty diff (ADR-0006).
        await SeedRolledBarsAsync();
        IndicatorProjector projector = Projector();

        int first = await ProjectOnePassAsync(projector, SessionStart);

        int second = await ProjectOnePassAsync(projector, SessionStart.AddHours(1));

        first.Should().BeGreaterThan(0);
        second.Should().Be(0);
    }

    [Fact]
    public async Task AValueTheBarsNoLongerJustify_IsRemoved_NotLeftBehind()
    {
        // gh#42 review, finding 1. Until segmenting arrived a bucket could only move from NOT COMPUTABLE to
        // COMPUTABLE, so an upsert-only projection was safe. Segmenting introduces the first move in the other
        // direction, and a row nothing rewrites is a row that stays.
        //
        // This is the remedy ADR-0011 itself prescribes, run end to end:
        //
        //   1. A legacy series with no provenance on any row. ATR(3) at the seam is (2 + 2 + 42) / 3 =
        //      15.33333333 -- the roll gap read as volatility, and the exact number the record exists to stop.
        //   2. The operator stamps the real contract ids.
        //   3. Re-project. The seam bucket is now the first bar of the new run, so the projection correctly
        //      produces NOTHING there.
        //
        // Without reconciliation step 3 leaves the 15.33333333 in place and get_indicators keeps serving it,
        // beside contracts.span: SpansRoll -- which ADR-0011 rejects by name as a fix.
        await SeedRolledBarsAsync(withProvenance: false);
        IndicatorProjector projector = Projector();

        await ProjectOnePassAsync(projector, SessionStart);

        (await AtrAsync()).Should().ContainKey(Bucket(4))
            .WhoseValue.Should().Be(15.33333333m, "the legacy series really does splice across the roll");

        foreach (BarRecord row in await _database.Bars.ToListAsync())
        {
            row.ContractId = row.BucketStart >= Bucket(4) ? "CON.F.US.EP.Z26" : "CON.F.US.EP.U26";
        }

        await _database.SaveChangesAsync();

        await ProjectOnePassAsync(projector, SessionStart.AddHours(1));

        Dictionary<DateTimeOffset, decimal> atr = await AtrAsync();

        atr.Should().NotContainKey(Bucket(4),
            "the current bars cannot justify a value here, so the old one must be gone rather than merely "
            + "not rewritten");
        atr.Should().NotContainKey(Bucket(5));
        atr.Should().NotContainKey(Bucket(6));
        atr.Should().ContainKey(Bucket(3)).WhoseValue.Should().Be(2m);
        atr.Should().ContainKey(Bucket(7)).WhoseValue.Should().Be(4m);
    }

    [Fact]
    public async Task Reconciling_LeavesAnotherPeriodsRowsAlone()
    {
        // The over-delete guard. The storage key is (Indicator, Period), and ATR(14) and ATR(3) are different
        // numbers under different keys -- a projection configured for one has no standing to delete the
        // other's rows. Deleting "everything this pass did not write" would quietly erase a series the
        // operator changed a period away from, which is a data-loss bug wearing a cleanup's clothes.
        await SeedRolledBarsAsync(withProvenance: true);

        _database.IndicatorValues.Add(new IndicatorValueRecord
        {
            Venue = Venue,
            Instrument = _es.Symbol,
            ResolutionMinutes = 5,
            Indicator = "atr",
            Period = 99,                    // a period this catalogue does not compute
            BucketStart = Bucket(4),
            Value = 42m,
            RecordedAt = SessionStart,
        });

        await _database.SaveChangesAsync();

        await ProjectOnePassAsync(Projector(), SessionStart);

        IndicatorValueRecord? survivor = await _database.IndicatorValues
            .FirstOrDefaultAsync(v => v.Indicator == "atr" && v.Period == 99);

        survivor.Should().NotBeNull();
        survivor!.Value.Should().Be(42m);
    }

    [Theory]
    [InlineData("nq", 5, "test")]      // another instrument
    [InlineData("ES", 60, "test")]     // another resolution
    [InlineData("ES", 5, "other")]     // another venue
    public async Task Reconciling_ReachesOnlyTheSeriesItProjected(
        string instrument,
        int resolutionMinutes,
        string venue)
    {
        // gh#42 review round two, finding 2. The delete is scoped on FOUR dimensions -- venue, instrument,
        // resolution (in the query) and (indicator, period) (in memory) -- and only the last had a test.
        // Dropping any of the other three predicates left all 228 green, so three quarters of the guard on a
        // destructive operation was resting on nothing.
        //
        // Each row below is planted at the seam bucket with the indicator and period this catalogue DOES
        // compute, so it is a row reconciliation would delete if it could reach it. Surviving is the assertion.
        await SeedRolledBarsAsync(withProvenance: true);

        _database.IndicatorValues.Add(new IndicatorValueRecord
        {
            Venue = venue,
            Instrument = instrument,
            ResolutionMinutes = resolutionMinutes,
            Indicator = "atr",
            Period = 3,
            BucketStart = Bucket(4),
            Value = 42m,
            RecordedAt = SessionStart,
        });

        await _database.SaveChangesAsync();

        await ProjectOnePassAsync(Projector(), SessionStart);

        IndicatorValueRecord? survivor = await _database.IndicatorValues.FirstOrDefaultAsync(
            v => v.Venue == venue
                && v.Instrument == instrument
                && v.ResolutionMinutes == resolutionMinutes
                && v.Indicator == "atr"
                && v.Period == 3
                && v.BucketStart == Bucket(4));

        survivor.Should().NotBeNull(
            "a projection of (test, ES, 5) has no standing over another series' rows");
        survivor!.Value.Should().Be(42m);
    }

    [Fact]
    public async Task Reconciling_RemovesEveryValue_WhenTheBarsAreAllGone()
    {
        // The empty-series case, which is where the early return used to sit. Values standing over a series
        // with no bars left are the purest form of a number nothing justifies, so the pass must reach them
        // rather than return early.
        await SeedRolledBarsAsync(withProvenance: true);
        await ProjectOnePassAsync(Projector(), SessionStart);

        (await AtrAsync()).Should().NotBeEmpty("the fixture has to produce something for this to be a test");

        _database.Bars.RemoveRange(await _database.Bars.ToListAsync());
        await _database.SaveChangesAsync();

        await ProjectOnePassAsync(Projector(), SessionStart.AddHours(1));

        (await AtrAsync()).Should().BeEmpty();
    }

    /// <summary>Every stored ATR(3) value for the series, by bucket.</summary>
    /// <returns>The values.</returns>
    /// <remarks>
    /// <b><c>AsNoTracking</c>, and it is load-bearing rather than tidy (gh#387).</b> A projection pass reads
    /// the stored values untracked and hands them to <c>Remove</c>, which attaches each as <c>Deleted</c>. If
    /// this read had tracked the same rows first, the pass would meet a second instance of a key the identity
    /// map already holds and throw. The in-memory branch this suite used to run on read them <i>tracked</i>,
    /// so the two instances were one and the collision could not arise.
    /// </remarks>
    private async Task<Dictionary<DateTimeOffset, decimal>> AtrAsync() =>
        await _database.IndicatorValues
            .AsNoTracking()
            .Where(v => v.Indicator == "atr" && v.Period == 3)
            .ToDictionaryAsync(v => v.BucketStart, v => v.Value);

    private static DateTimeOffset Bucket(int index) => SessionStart.AddMinutes(5 * index);

    /// <summary>Four bars of the expiring contract, then four of the new one, under the same symbol.</summary>
    /// <param name="withProvenance">
    /// <see langword="false"/> writes the bars with no contract id at all — the state every row written before
    /// the column existed is in, and the one a legacy store re-projects from.
    /// </param>
    private async Task SeedRolledBarsAsync(bool withProvenance = true)
    {
        for (int i = 0; i < 8; i++)
        {
            bool rolled = i >= 4;

            _database.Bars.Add(new BarRecord
            {
                Venue = Venue,
                Instrument = _es.Symbol,
                ResolutionMinutes = 5,
                BucketStart = Bucket(i),
                Open = rolled ? 140m : 100m,
                High = rolled ? 142m : 101m,
                Low = rolled ? 138m : 99m,
                Close = rolled ? 140m : 100m,
                Volume = 1_000,
                ContractId = withProvenance ? (rolled ? "CON.F.US.EP.Z26" : "CON.F.US.EP.U26") : null,
                RecordedAt = SessionStart,
            });
        }

        await _database.SaveChangesAsync();
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
