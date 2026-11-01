using System.Diagnostics;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Telemetry;
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
        using HostTelemetry telemetry = new();
        LatestQuoteStore store = new(writing, telemetry);

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
    public async Task Find_AgesOnTheDatabaseClock()
    {
        await using TopstepXDbContext database = _fixture.CreateContext();
        using HostTelemetry telemetry = new();
        List<Activity> stopped = [];
        using ActivityListener listener = Listen(stopped, telemetry.Activities);
        LatestQuoteStore store = new(database, telemetry);
        await store.UpsertAsync([Draft(bid: 5m)], CancellationToken.None);

        LatestQuoteRow? row = await store.FindAsync("projectx", "CON.F.US.EP.Z26", CancellationToken.None);

        row.Should().NotBeNull();
        row!.Bid.Should().Be(5m);
        row.AgeSeconds.Should().BeGreaterThanOrEqualTo(0);
        row.AgeSeconds.Should().BeLessThan(60);
        row.ReceivedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2));
        stopped.Should().Contain(activity => activity.OperationName == "quote.read");
    }

    [Fact]
    public async Task AMissingContract_IsNull()
    {
        await using TopstepXDbContext database = _fixture.CreateContext();
        using HostTelemetry telemetry = new();
        LatestQuoteStore store = new(database, telemetry);

        LatestQuoteRow? row = await store.FindAsync("projectx", "CON.F.US.EP.H27", CancellationToken.None);

        row.Should().BeNull();
    }

    [Fact]
    public async Task AnOlderReceipt_DoesNotOverwriteANewerOne()
    {
        await using TopstepXDbContext database = _fixture.CreateContext();
        using HostTelemetry telemetry = new();
        LatestQuoteStore store = new(database, telemetry);
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
        LatestQuoteRow? aged = await new LatestQuoteStore(held, telemetry).FindAsync(
            "projectx", "CON.F.US.EP.Z26", CancellationToken.None);
        aged!.AgeSeconds.Should().Be(0);

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
        using HostTelemetry telemetry = new();
        LatestQuoteStore leftStore = new(left, telemetry);
        LatestQuoteStore rightStore = new(right, telemetry);

        await Task.WhenAll(
            leftStore.UpsertAsync([Draft(bid: 1m)], CancellationToken.None),
            rightStore.UpsertAsync([Draft(bid: 2m)], CancellationToken.None));

        await using TopstepXDbContext check = _fixture.CreateContext();
        List<Data.Entities.LatestQuoteRecord> rows = await check.LatestQuotes.AsNoTracking().ToListAsync();
        rows.Should().ContainSingle();
        rows[0].Bid.Should().BeOneOf(1m, 2m);

        LatestQuoteRow? after = await new LatestQuoteStore(check, telemetry).FindAsync(
            "projectx", "CON.F.US.EP.Z26", CancellationToken.None);
        after.Should().NotBeNull();
        after!.Bid.Should().Be(rows[0].Bid);
    }

    private static LatestQuoteDraft Draft(
        decimal bid,
        string contract = "CON.F.US.EP.Z26",
        string instrument = "ES") =>
        new(
            "projectx",
            instrument,
            contract,
            bid,
            bid + 0.25m,
            bid,
            3L,
            _quoteTime,
            _quoteTime);

    private static ActivityListener Listen(List<Activity> into, ActivitySource source)
    {
        ActivityListener listener = new()
        {
            ShouldListenTo = candidate => ReferenceEquals(candidate, source),
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = into.Add,
        };

        ActivitySource.AddActivityListener(listener);
        return listener;
    }

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
