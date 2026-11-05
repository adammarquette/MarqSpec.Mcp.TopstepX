using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// The latest-quote table: one row per contract, replaced in place, aged on the database clock.
/// An older receipt cannot overwrite a newer one.
/// </summary>
[Collection(SeriesStoreCollection.Name)]
public sealed class LatestQuoteStoreTests(SeriesStoreFixture fixture) : IAsyncLifetime
{
    private readonly SeriesStoreFixture _fixture = fixture;

    private static readonly DateTimeOffset _quoteTime =
        new(2026, 11, 1, 15, 0, 0, TimeSpan.Zero);

    /// <inheritdoc />
    public Task InitializeAsync() => _fixture.ResetAsync();

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Upsert_IsOneConflictStatement_AndASecondWriteReplacesTheRow()
    {
        CountingCommands commands = new();
        await using TopstepXDbContext writing = _fixture.CreateContext(commands);
        LatestQuoteStore store = new(writing);

        int first = await store.UpsertAsync(
            [Draft(bid: 10m), Draft(bid: 20m, contract: "CON.F.US.ENQ.Z26", instrument: "NQ")],
            CancellationToken.None);

        first.Should().Be(2);
        commands.NonQueries.Should().Be(1);
        commands.LastText.Should().Contain("ON CONFLICT (\"Venue\", \"ContractId\") DO UPDATE");
        commands.LastText.Should().Contain("\"ReceivedAt\" <= excluded.\"ReceivedAt\"");

        int second = await store.UpsertAsync([Draft(bid: 11m)], CancellationToken.None);
        second.Should().Be(1);

        await using TopstepXDbContext reading = _fixture.CreateContext();
        List<Data.Entities.LatestQuoteRecord> rows = await reading.LatestQuotes
            .AsNoTracking()
            .OrderBy(row => row.ContractId)
            .ToListAsync();

        rows.Should().HaveCount(2);
        Data.Entities.LatestQuoteRecord es = rows.Single(row => row.ContractId == "CON.F.US.EP.Z26");
        es.Bid.Should().Be(11m);
        es.Volume.Should().Be(3L);
        rows.Single(row => row.ContractId == "CON.F.US.ENQ.Z26").Bid.Should().Be(20m);
    }

    [Fact]
    public async Task Find_ReturnsTheRowAndItsDatabaseAge()
    {
        await using TopstepXDbContext database = _fixture.CreateContext();
        LatestQuoteStore store = new(database);
        await store.UpsertAsync([Draft(bid: 5m)], CancellationToken.None);

        LatestQuoteRow? row = await store.FindAsync("projectx", "CON.F.US.EP.Z26", CancellationToken.None);

        row.Should().NotBeNull();
        row!.Bid.Should().Be(5m);
        row.Volume.Should().Be(3L);
        row.AgeSeconds.Should().BeGreaterThanOrEqualTo(0);
        row.AgeSeconds.Should().BeLessThan(60);
        (await store.FindAsync("projectx", "CON.F.US.EP.H27", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Find_AgesAPlantedOldReceipt()
    {
        await using TopstepXDbContext database = _fixture.CreateContext();
        LatestQuoteStore store = new(database);
        await store.UpsertAsync([Draft(bid: 5m)], CancellationToken.None);
        await database.Database.ExecuteSqlRawAsync(
            """
            UPDATE "LatestQuotes"
            SET "ReceivedAt" = clock_timestamp() - interval '1 minute'
            WHERE "ContractId" = {0}
            """,
            "CON.F.US.EP.Z26");

        LatestQuoteRow? row = await store.FindAsync("projectx", "CON.F.US.EP.Z26", CancellationToken.None);

        row!.AgeSeconds.Should().BeGreaterThan(30);
    }

    [Fact]
    public async Task FindFront_ReturnsTheNewestRowForTheInstrument()
    {
        await using TopstepXDbContext database = _fixture.CreateContext();
        LatestQuoteStore store = new(database);
        await store.UpsertAsync(
            [
                Draft(bid: 1m, contract: "CON.F.US.EP.U26"),
                Draft(bid: 2m, contract: "CON.F.US.EP.Z26"),
            ],
            CancellationToken.None);
        await database.Database.ExecuteSqlRawAsync(
            """
            UPDATE "LatestQuotes"
            SET "ReceivedAt" = clock_timestamp() - interval '1 hour'
            WHERE "ContractId" = {0}
            """,
            "CON.F.US.EP.U26");

        LatestQuoteFront? front = await store.FindFrontAsync("ES", CancellationToken.None);

        front.Should().NotBeNull();
        front!.Venue.Should().Be("projectx");
        front.ContractId.Should().Be("CON.F.US.EP.Z26");
        (await store.FindFrontAsync("NQ", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task TheStamp_IsTheDatabaseClock()
    {
        await using TopstepXDbContext database = _fixture.CreateContext();
        await new LatestQuoteStore(database).UpsertAsync([Draft(bid: 5m)], CancellationToken.None);

        await using TopstepXDbContext reading = _fixture.CreateContext();
        Data.Entities.LatestQuoteRecord row = await reading.LatestQuotes.AsNoTracking().SingleAsync();
        row.Bid.Should().Be(5m);
        row.ReceivedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2));
        row.ReceivedAt.Should().NotBe(_quoteTime);
        double age = await AgeSeconds(reading, "CON.F.US.EP.Z26");
        age.Should().BeGreaterThanOrEqualTo(0);
        age.Should().BeLessThan(60);
    }

    [Fact]
    public async Task AnOlderReceipt_DoesNotOverwriteANewerOne()
    {
        await using TopstepXDbContext database = _fixture.CreateContext();
        LatestQuoteStore store = new(database);
        await store.UpsertAsync([Draft(bid: 1m)], CancellationToken.None);
        await database.Database.ExecuteSqlRawAsync(
            """
            UPDATE "LatestQuotes"
            SET "ReceivedAt" = clock_timestamp() + interval '1 day', "Bid" = 99
            WHERE "ContractId" = {0}
            """,
            "CON.F.US.EP.Z26");

        int skipped = await store.UpsertAsync([Draft(bid: 1m)], CancellationToken.None);

        skipped.Should().Be(0);
        await using TopstepXDbContext held = _fixture.CreateContext();
        Data.Entities.LatestQuoteRecord kept = await held.LatestQuotes.AsNoTracking().SingleAsync();
        kept.Bid.Should().Be(99m);
        (await AgeSeconds(held, "CON.F.US.EP.Z26")).Should().Be(0);

        await database.Database.ExecuteSqlRawAsync(
            """
            UPDATE "LatestQuotes"
            SET "ReceivedAt" = clock_timestamp() - interval '1 day'
            WHERE "ContractId" = {0}
            """,
            "CON.F.US.EP.Z26");
        int replaced = await store.UpsertAsync([Draft(bid: 7m)], CancellationToken.None);

        replaced.Should().Be(1);
        await using TopstepXDbContext after = _fixture.CreateContext();
        (await after.LatestQuotes.AsNoTracking().SingleAsync()).Bid.Should().Be(7m);
    }

    [Fact]
    public async Task ConcurrentWriters_LeaveOneRow()
    {
        await using TopstepXDbContext left = _fixture.CreateContext();
        await using TopstepXDbContext right = _fixture.CreateContext();
        LatestQuoteStore leftStore = new(left);
        LatestQuoteStore rightStore = new(right);

        await Task.WhenAll(
            leftStore.UpsertAsync([Draft(bid: 1m)], CancellationToken.None),
            rightStore.UpsertAsync([Draft(bid: 2m)], CancellationToken.None));

        await using TopstepXDbContext check = _fixture.CreateContext();
        List<Data.Entities.LatestQuoteRecord> rows = await check.LatestQuotes.AsNoTracking().ToListAsync();
        rows.Should().ContainSingle();
        rows[0].Bid.Should().BeOneOf(1m, 2m);
    }

    [Fact]
    public async Task AZeroLast_KeepsAFreshSameSessionLast_AndDropsItAcrossTheSessionOrAge()
    {
        await using TopstepXDbContext database = _fixture.CreateContext();
        LatestQuoteStore store = new(database);
        DateTimeOffset seen = DateTimeOffset.UtcNow;
        DateOnly session = new(2026, 10, 30);

        await store.UpsertAsync(
            [Draft(bid: 10m, last: 7650.5m, volume: 100, lastAt: seen, volumeAt: seen, tradeDate: session)],
            CancellationToken.None);

        await store.UpsertAsync(
            [Draft(bid: 11m, last: 0m, volume: 0, quoteTime: seen.AddSeconds(2), tradeDate: session)],
            CancellationToken.None);

        await using TopstepXDbContext kept = _fixture.CreateContext();
        Data.Entities.LatestQuoteRecord fresh = await kept.LatestQuotes.AsNoTracking().SingleAsync();
        fresh.Bid.Should().Be(11m);
        fresh.Last.Should().Be(7650.5m);
        fresh.Volume.Should().Be(100L);
        fresh.LastAt.Should().BeCloseTo(seen, TimeSpan.FromSeconds(1));
        fresh.QuoteTimeUtc.Should().BeCloseTo(seen.AddSeconds(2), TimeSpan.FromSeconds(1));

        int older = await store.UpsertAsync(
            [Draft(
                bid: 12m,
                last: 1m,
                volume: 1,
                lastAt: seen.AddMinutes(-5),
                volumeAt: seen.AddMinutes(-5),
                quoteTime: seen.AddMinutes(-5),
                tradeDate: session)],
            CancellationToken.None);
        older.Should().Be(1);
        await using TopstepXDbContext still = _fixture.CreateContext();
        Data.Entities.LatestQuoteRecord held = await still.LatestQuotes.AsNoTracking().SingleAsync();
        held.Bid.Should().Be(12m);
        held.Last.Should().Be(7650.5m);
        held.Volume.Should().Be(100L);
        held.QuoteTimeUtc.Should().BeCloseTo(seen.AddSeconds(2), TimeSpan.FromSeconds(1));

        await store.UpsertAsync(
            [Draft(bid: 13m, last: 0m, volume: 0, tradeDate: session.AddDays(3))],
            CancellationToken.None);
        await using TopstepXDbContext nextSession = _fixture.CreateContext();
        Data.Entities.LatestQuoteRecord cleared = await nextSession.LatestQuotes.AsNoTracking().SingleAsync();
        cleared.Bid.Should().Be(13m);
        cleared.Last.Should().Be(0m);
        cleared.Volume.Should().Be(0L);
        cleared.LastAt.Should().BeNull();

        await store.UpsertAsync(
            [Draft(bid: 14m, last: 20m, volume: 8, lastAt: seen, volumeAt: seen, tradeDate: session)],
            CancellationToken.None);
        int staleSeconds = (int)LatestQuoteCarry.MaxAge.TotalSeconds + 60;
        await database.Database.ExecuteSqlRawAsync(
            """
            UPDATE "LatestQuotes"
            SET "LastAt" = clock_timestamp() - make_interval(secs => {0}),
                "VolumeAt" = clock_timestamp() - make_interval(secs => {0}),
                "TradeDate" = {1}
            WHERE "ContractId" = {2}
            """,
            staleSeconds,
            session,
            "CON.F.US.EP.Z26");
        await store.UpsertAsync(
            [Draft(bid: 15m, last: 0m, volume: 0, tradeDate: session)],
            CancellationToken.None);

        await using TopstepXDbContext aged = _fixture.CreateContext();
        Data.Entities.LatestQuoteRecord expired = await aged.LatestQuotes.AsNoTracking().SingleAsync();
        expired.Bid.Should().Be(15m);
        expired.Last.Should().Be(0m);
        expired.Volume.Should().Be(0L);
        ((int)LatestQuoteCarry.MaxAge.TotalSeconds).Should().Be(1_800);
    }

    [Fact]
    public async Task AnOlderLastOnTheNextSession_DoesNotKeepThePreviousLast()
    {
        await using TopstepXDbContext database = _fixture.CreateContext();
        LatestQuoteStore store = new(database);
        DateTimeOffset seen = DateTimeOffset.UtcNow;
        DateOnly first = new(2026, 10, 30);
        DateOnly next = first.AddDays(3);

        await store.UpsertAsync(
            [Draft(
                bid: 10m,
                last: 100m,
                volume: 50,
                lastAt: seen,
                volumeAt: seen,
                tradeDate: first)],
            CancellationToken.None);

        await store.UpsertAsync(
            [Draft(
                bid: 11m,
                last: 1m,
                volume: 1,
                lastAt: seen.AddMinutes(-5),
                volumeAt: seen.AddMinutes(-5),
                quoteTime: seen.AddMinutes(-5),
                tradeDate: next)],
            CancellationToken.None);

        await using TopstepXDbContext check = _fixture.CreateContext();
        Data.Entities.LatestQuoteRecord row = await check.LatestQuotes.AsNoTracking().SingleAsync();
        row.Bid.Should().Be(11m);
        row.Last.Should().Be(0m);
        row.LastAt.Should().BeNull();
        row.Volume.Should().Be(0L);
        row.VolumeAt.Should().BeNull();
        row.TradeDate.Should().Be(next);
    }

    private static async Task<double> AgeSeconds(TopstepXDbContext database, string contractId)
    {
        List<double> ages = await database.Database.SqlQueryRaw<double>(
                """
                SELECT GREATEST(EXTRACT(EPOCH FROM (clock_timestamp() - "ReceivedAt")), 0)::float8
                    AS "Value"
                FROM "LatestQuotes"
                WHERE "ContractId" = {0}
                """,
                contractId)
            .ToListAsync();
        return ages.Single();
    }

    private static LatestQuoteDraft Draft(
        decimal bid,
        string contract = "CON.F.US.EP.Z26",
        string instrument = "ES",
        decimal? last = null,
        long volume = 3,
        DateTimeOffset? lastAt = null,
        DateTimeOffset? volumeAt = null,
        DateTimeOffset? quoteTime = null,
        DateOnly? tradeDate = null) =>
        new(
            "projectx",
            instrument,
            contract,
            bid,
            bid + 0.25m,
            last ?? bid,
            volume,
            quoteTime ?? _quoteTime,
            _quoteTime,
            lastAt,
            volumeAt,
            BookKnown: true,
            tradeDate);

    private sealed class CountingCommands : DbCommandInterceptor
    {
        public int NonQueries { get; private set; }

        public string? LastText { get; private set; }

        public override InterceptionResult<int> NonQueryExecuting(
            System.Data.Common.DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            Note(command);
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            System.Data.Common.DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Note(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Note(System.Data.Common.DbCommand command)
        {
            if (command.CommandText.Contains("LatestQuotes", StringComparison.Ordinal))
            {
                NonQueries++;
                LastText = command.CommandText;
            }
        }
    }
}
