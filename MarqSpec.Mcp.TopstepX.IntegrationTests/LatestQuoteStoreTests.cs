using System.Diagnostics.Metrics;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// The latest-quote table: one row per contract, replaced in place, aged from the receipt.
/// </summary>
[Collection(SeriesStoreCollection.Name)]
public sealed class LatestQuoteStoreTests(SeriesStoreFixture fixture) : IAsyncLifetime
{
    private readonly SeriesStoreFixture _fixture = fixture;

    private static readonly DateTimeOffset _received =
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

        int second = await store.UpsertAsync([Draft(bid: 11m)], CancellationToken.None);
        second.Should().Be(1);

        await using TopstepXDbContext reading = _fixture.CreateContext();
        List<Data.Entities.LatestQuoteRecord> rows = await reading.LatestQuotes
            .AsNoTracking()
            .OrderBy(row => row.ContractId)
            .ToListAsync();

        rows.Should().HaveCount(2);
        rows.Single(row => row.ContractId == "CON.F.US.EP.Z26").Bid.Should().Be(11m);
        rows.Single(row => row.ContractId == "CON.F.US.ENQ.Z26").Bid.Should().Be(20m);
    }

    [Fact]
    public async Task ARead_ReportsAgeAndStale_AndDoesNotFailTheStaleRow()
    {
        await using TopstepXDbContext database = _fixture.CreateContext();
        await new LatestQuoteStore(database).UpsertAsync([Draft(bid: 5m)], CancellationToken.None);

        FakeTimeProvider clock = new(_received.AddSeconds(20));
        using HostTelemetry telemetry = new();
        using MetricCollector<long> reads = new(
            telemetry, HostTelemetry.Name, HostTelemetry.QuoteReadsInstrument);
        LatestQuoteReader reader = Reader(database, clock, telemetry, staleAfter: TimeSpan.FromSeconds(15));

        LatestQuoteReading? reading = await reader.ReadAsync(
            "projectx", "CON.F.US.EP.Z26", CancellationToken.None);

        reading.Should().NotBeNull();
        reading!.Stale.Should().BeTrue();
        reading.Age.Should().Be(TimeSpan.FromSeconds(20));
        reading.Quote.Bid.Should().Be(5m);
        reads.LastMeasurement!.Tags[HostTelemetry.OutcomeTag].Should().Be(QuoteReadOutcome.Stale);
    }

    [Fact]
    public async Task AFreshReceipt_IsNotStale()
    {
        await using TopstepXDbContext database = _fixture.CreateContext();
        await new LatestQuoteStore(database).UpsertAsync([Draft(bid: 5m)], CancellationToken.None);

        FakeTimeProvider clock = new(_received.AddSeconds(5));
        using HostTelemetry telemetry = new();
        using MetricCollector<long> reads = new(
            telemetry, HostTelemetry.Name, HostTelemetry.QuoteReadsInstrument);
        LatestQuoteReader reader = Reader(database, clock, telemetry, staleAfter: TimeSpan.FromSeconds(15));

        LatestQuoteReading? reading = await reader.ReadAsync(
            "projectx", "CON.F.US.EP.Z26", CancellationToken.None);

        reading!.Stale.Should().BeFalse();
        reading.Age.Should().Be(TimeSpan.FromSeconds(5));
        reads.LastMeasurement!.Tags[HostTelemetry.OutcomeTag].Should().Be(QuoteReadOutcome.Fresh);
    }

    [Fact]
    public async Task AMissingContract_IsAMiss_NotAnException()
    {
        await using TopstepXDbContext database = _fixture.CreateContext();
        using HostTelemetry telemetry = new();
        using MetricCollector<long> reads = new(
            telemetry, HostTelemetry.Name, HostTelemetry.QuoteReadsInstrument);
        LatestQuoteReader reader = Reader(
            database, new FakeTimeProvider(_received), telemetry, TimeSpan.FromSeconds(15));

        LatestQuoteReading? reading = await reader.ReadAsync(
            "projectx", "CON.F.US.EP.H27", CancellationToken.None);

        reading.Should().BeNull();
        reads.LastMeasurement!.Tags[HostTelemetry.OutcomeTag].Should().Be(QuoteReadOutcome.Miss);
    }

    [Fact]
    public async Task ABlankContract_IsRefused()
    {
        await using TopstepXDbContext database = _fixture.CreateContext();
        using HostTelemetry telemetry = new();
        using MetricCollector<long> reads = new(
            telemetry, HostTelemetry.Name, HostTelemetry.QuoteReadsInstrument);
        LatestQuoteReader reader = Reader(
            database, new FakeTimeProvider(_received), telemetry, TimeSpan.FromSeconds(15));

        Func<Task> read = () => reader.ReadAsync("projectx", "  ", CancellationToken.None);

        await read.Should().ThrowAsync<ArgumentException>();
        reads.LastMeasurement!.Tags[HostTelemetry.OutcomeTag].Should().Be(QuoteReadOutcome.Refused);
    }

    [Fact]
    public async Task ConcurrentWritersAndReaders_LeaveOneRow()
    {
        await using TopstepXDbContext left = _fixture.CreateContext();
        await using TopstepXDbContext right = _fixture.CreateContext();
        await using TopstepXDbContext readerContext = _fixture.CreateContext();
        LatestQuoteStore leftStore = new(left);
        LatestQuoteStore rightStore = new(right);
        LatestQuoteReader reader = Reader(
            readerContext,
            new FakeTimeProvider(_received),
            new HostTelemetry(),
            TimeSpan.FromSeconds(15));

        await Task.WhenAll(
            leftStore.UpsertAsync([Draft(bid: 1m)], CancellationToken.None),
            rightStore.UpsertAsync([Draft(bid: 2m)], CancellationToken.None),
            reader.ReadAsync("projectx", "CON.F.US.EP.Z26", CancellationToken.None));

        await using TopstepXDbContext check = _fixture.CreateContext();
        List<Data.Entities.LatestQuoteRecord> rows = await check.LatestQuotes.AsNoTracking().ToListAsync();
        rows.Should().ContainSingle();
        rows[0].Bid.Should().BeOneOf(1m, 2m);

        LatestQuoteReading? after = await Reader(
                check,
                new FakeTimeProvider(_received),
                new HostTelemetry(),
                TimeSpan.FromSeconds(15))
            .ReadAsync("projectx", "CON.F.US.EP.Z26", CancellationToken.None);
        after.Should().NotBeNull();
        after!.Quote.Bid.Should().Be(rows[0].Bid);
    }

    private static LatestQuoteReader Reader(
        TopstepXDbContext database,
        TimeProvider clock,
        HostTelemetry telemetry,
        TimeSpan staleAfter) =>
        new(
            new LatestQuoteStore(database),
            Microsoft.Extensions.Options.Options.Create(new MarketDataOptions
            {
                Instruments = "ES",
                SessionCloseCentral = "16:00",
                LatestQuoteStaleAfter = staleAfter,
            }),
            clock,
            telemetry);

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
            3m,
            _received,
            _received);

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
