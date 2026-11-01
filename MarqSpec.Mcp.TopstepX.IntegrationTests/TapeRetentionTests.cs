using System.Text;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// The recorder's tape prune against Postgres (gh#763). Rows older than the window go, the row at
/// the cutoff stays, a second run deletes nothing, and a small batch size takes more than one
/// statement.
/// </summary>
[Collection(SeriesStoreCollection.Name)]
public sealed class TapeRetentionTests : IAsyncLifetime
{
    private const string Venue = "test";

    private const string Instrument = "ES";

    private const string Contract = "CON.F.US.EP.Z26";

    private static readonly DateTimeOffset _now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly SeriesStoreFixture _fixture;

    private TopstepXDbContext _database = null!;

    /// <param name="fixture">The shared container.</param>
    public TapeRetentionTests(SeriesStoreFixture fixture) => _fixture = fixture;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await _fixture.ResetAsync();
        _database = _fixture.CreateContext();
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _database.DisposeAsync();

    [Fact]
    public async Task Prune_DeletesRowsOlderThanTheWindow_AndKeepsTheBoundary()
    {
        DateTimeOffset cutoff = _now - TimeSpan.FromDays(TapeRetentionOptions.DefaultDays);
        await InsertAsync(cutoff.AddDays(-30), sequence: 1);
        await InsertAsync(cutoff.AddSeconds(-1), sequence: 2);
        await InsertAsync(cutoff, sequence: 3);
        await InsertAsync(cutoff.AddSeconds(1), sequence: 4);

        using HostTelemetry telemetry = new();
        using MetricCollector<long> pruned = new(
            telemetry, HostTelemetry.Name, HostTelemetry.TapePrunedInstrument);
        CapturingLogger<TapeRetentionPass> logs = new();

        TapeRetentionRun run = await Pass(telemetry, logs, batchSize: TapeRetentionPass.DefaultBatchSize)
            .PruneAsync(CancellationToken.None);

        run.Rows.Should().Be(2);
        run.CutoffUtc.Should().Be(cutoff);
        (await SequencesAsync()).Should().Equal(3L, 4L);
        logs.Messages.Should().ContainSingle()
            .Which.Should().Contain("removed 2 Trades rows")
            .And.Contain("90 days");
        pruned.LastMeasurement!.Value.Should().Be(2);
    }

    [Fact]
    public async Task Prune_IsIdempotent()
    {
        DateTimeOffset cutoff = _now - TimeSpan.FromDays(TapeRetentionOptions.DefaultDays);
        await InsertAsync(cutoff.AddMinutes(-1), sequence: 1);
        await InsertAsync(cutoff, sequence: 2);

        using HostTelemetry telemetry = new();
        TapeRetentionPass pass = Pass(telemetry, new CapturingLogger<TapeRetentionPass>(), batchSize: 10);

        TapeRetentionRun first = await pass.PruneAsync(CancellationToken.None);
        TapeRetentionRun second = await pass.PruneAsync(CancellationToken.None);

        first.Rows.Should().Be(1);
        second.Rows.Should().Be(0);
        second.Batches.Should().Be(0);
        (await SequencesAsync()).Should().Equal(2L);
    }

    [Fact]
    public async Task Prune_DeletesInBatches_AndTheSelectUsesTheInstrumentTimeIndex()
    {
        DateTimeOffset cutoff = _now - TimeSpan.FromDays(TapeRetentionOptions.DefaultDays);

        // Minutes apart, so the old rows share the cutoff's chunk. drop_chunks leaves that chunk,
        // and the delete has to walk it in batches. The boundary row is the one at the cutoff.
        for (long sequence = 1; sequence <= 5; sequence++)
        {
            await InsertAsync(cutoff.AddMinutes(-sequence), sequence);
        }

        await InsertAsync(cutoff, sequence: 6);

        using HostTelemetry telemetry = new();
        TapeRetentionRun run = await Pass(telemetry, new CapturingLogger<TapeRetentionPass>(), batchSize: 2)
            .PruneAsync(CancellationToken.None);

        run.Rows.Should().Be(5);
        run.Batches.Should().Be(3);
        (await SequencesAsync()).Should().Equal(6L);

        string plan = await ExplainDeleteSelectAsync(cutoff);
        plan.Should().Contain(TapeRetentionPass.DeleteIndex);
    }

    private TapeRetentionPass Pass(
        HostTelemetry telemetry,
        CapturingLogger<TapeRetentionPass> logs,
        int batchSize) =>
        new(
            _database,
            Options.Create(new TapeRetentionOptions()),
            new FakeTimeProvider(_now),
            logs,
            telemetry,
            batchSize);

    private async Task InsertAsync(DateTimeOffset time, long sequence)
    {
        await _database.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "Trades"
                ("Venue", "Instrument", "ContractId", "TradeTimeUtc", "Sequence",
                 "Price", "Size", "Direction", "RecordedAt")
            VALUES
                ({Venue}, {Instrument}, {Contract}, {time}, {sequence},
                 {5000m}, {1L}, {1}, {time})
            """);
    }

    private async Task<List<long>> SequencesAsync() =>
        await _database.Database
            .SqlQuery<long>($"""SELECT "Sequence" AS "Value" FROM "Trades" ORDER BY "Sequence" """)
            .ToListAsync();

    private async Task<string> ExplainDeleteSelectAsync(DateTimeOffset cutoff)
    {
        // A new connection sees the committed rows. SET LOCAL dies with the transaction, so the
        // pool does not keep seqscan off for the next test.
        await using NpgsqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await using (NpgsqlCommand settings = new("SET LOCAL enable_seqscan = off", connection, transaction))
        {
            await settings.ExecuteNonQueryAsync();
        }

        await using NpgsqlCommand command = new(
            """
            EXPLAIN SELECT "TradeTimeUtc"
            FROM "Trades"
            WHERE "Instrument" = @instrument AND "TradeTimeUtc" < @cutoff
            ORDER BY "TradeTimeUtc"
            LIMIT 2
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("instrument", Instrument);
        command.Parameters.AddWithValue("cutoff", cutoff);

        StringBuilder plan = new();
        await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                plan.AppendLine(reader.GetString(0));
            }
        }

        await transaction.RollbackAsync();
        return plan.ToString();
    }
}
