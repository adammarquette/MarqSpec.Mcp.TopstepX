using System.Text;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Testcontainers.PostgreSql;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// Receipt-gated tape prune against Timescale (gh#763, ADR-0027). No uploaded trades receipt
/// means the rows stay. A receipt deletes that day. A partial set deletes only those days.
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
    public void DeleteBatchSql_NamesTradesAndNotLatestQuotes()
    {
        TapeRetentionPass.DeleteBatchSql.Should().Contain("\"Trades\"");
        TapeRetentionPass.DeleteBatchSql.Should().NotContain("LatestQuotes");
    }

    [Fact]
    public async Task Prune_WhenThereIsNoUploadedReceipt_KeepsTheRows()
    {
        DateOnly oldDay = new(2026, 7, 2);
        await InsertTradeAsync(DayStart(oldDay).AddHours(1), sequence: 1);
        await InsertReceiptAsync(oldDay, TapeExportReceipt.StateDriveDisabled);

        TapeRetentionRun run = await PruneAsync(batchSize: 10);

        run.Rows.Should().Be(0);
        run.DaysSkipped.Should().Be(1);
        (await SequencesAsync()).Should().Equal(1L);
    }

    [Fact]
    public async Task Prune_WhenTheDayHasAnUploadedReceipt_DeletesThatDayAndKeepsTheBoundary()
    {
        DateOnly oldDay = new(2026, 7, 2);
        DateOnly boundaryDay = new(2026, 7, 3);
        await InsertTradeAsync(DayStart(oldDay).AddHours(1), sequence: 1);
        await InsertTradeAsync(DayStart(boundaryDay).AddHours(1), sequence: 2);
        await InsertReceiptAsync(oldDay, TapeExportReceipt.StateUploaded, rowCount: 1);
        await InsertReceiptAsync(boundaryDay, TapeExportReceipt.StateUploaded, rowCount: 1);

        using HostTelemetry telemetry = new();
        using MetricCollector<long> pruned = new(
            telemetry, HostTelemetry.Name, HostTelemetry.TapePrunedInstrument);
        CapturingLogger<TapeRetentionPass> logs = new();

        TapeRetentionRun run = await PruneAsync(telemetry, logs, batchSize: 10);

        run.Rows.Should().Be(1);
        (await SequencesAsync()).Should().Equal(2L);
        logs.Messages.Should().Contain(message =>
            message.Contains("will delete about 1", StringComparison.Ordinal)
            && message.Contains("2026-07-02", StringComparison.Ordinal)
            && message.Contains(Instrument, StringComparison.Ordinal));
        pruned.LastMeasurement!.Value.Should().Be(1);
    }

    [Fact]
    public async Task Prune_WhenOnlySomeDaysHaveReceipts_DeletesThoseDays()
    {
        DateOnly exported = new(2026, 6, 1);
        DateOnly kept = new(2026, 6, 2);
        await InsertTradeAsync(DayStart(exported).AddHours(2), sequence: 1);
        await InsertTradeAsync(DayStart(kept).AddHours(2), sequence: 2);
        await InsertReceiptAsync(exported, TapeExportReceipt.StateUploaded);

        TapeRetentionRun run = await PruneAsync(batchSize: 10);

        run.Rows.Should().Be(1);
        run.DaysSkipped.Should().Be(1);
        (await SequencesAsync()).Should().Equal(2L);
    }

    [Fact]
    public async Task Prune_IsIdempotent()
    {
        DateOnly oldDay = new(2026, 7, 2);
        await InsertTradeAsync(DayStart(oldDay).AddHours(1), sequence: 1);
        await InsertReceiptAsync(oldDay, TapeExportReceipt.StateUploaded);
        TapeRetentionPass pass = CreatePass(
            new HostTelemetry(), new CapturingLogger<TapeRetentionPass>(), 10);

        TapeRetentionRun first = await pass.PruneAsync(static () => true, CancellationToken.None);
        TapeRetentionRun second = await pass.PruneAsync(static () => true, CancellationToken.None);

        first.Rows.Should().Be(1);
        second.Rows.Should().Be(0);
        second.Batches.Should().Be(0);
        (await SequencesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Prune_WhenTheLeaseIsLost_StopsBeforeTheNextBatch()
    {
        DateOnly oldDay = new(2026, 7, 2);
        await InsertTradeAsync(DayStart(oldDay).AddHours(1), sequence: 1, contract: "A");
        await InsertTradeAsync(DayStart(oldDay).AddHours(2), sequence: 2, contract: "A");
        await InsertTradeAsync(DayStart(oldDay).AddHours(3), sequence: 3, contract: "A");
        await InsertTradeAsync(DayStart(oldDay).AddHours(4), sequence: 4, contract: "B");
        await InsertReceiptAsync(oldDay, TapeExportReceipt.StateUploaded, contract: "A");

        TapeRetentionPass pass = CreatePass(
            new HostTelemetry(), new CapturingLogger<TapeRetentionPass>(), 1);
        TapeRetentionRun run = await pass.PruneAsync(() => CountTrades() > 3, CancellationToken.None);

        run.StoppedForLease.Should().BeTrue();
        run.Batches.Should().Be(1);
        (await SequencesAsync()).Should().BeEquivalentTo([2L, 3L, 4L]);
    }

    [Fact]
    public async Task Prune_WhenACompressedChunkIsOnlyPartlyExported_LeavesItCompressed()
    {
        DateTimeOffset first = new(2026, 1, 6, 15, 0, 0, TimeSpan.Zero);
        DateTimeOffset second = first.AddDays(1);
        await InsertTradeAsync(first, sequence: 1);
        await InsertTradeAsync(second, sequence: 2);
        (await ChunksCoveringAsync(first, second)).Should().Be(1);
        await InsertReceiptAsync(DateOnly.FromDateTime(first.UtcDateTime), TapeExportReceipt.StateUploaded);
        await CompressChunkCoveringAsync(first);

        TapeRetentionRun run = await PruneAsync(batchSize: 10);

        run.Rows.Should().Be(0);
        (await SequencesAsync()).Should().Equal(1L, 2L);
        (await CompressedChunksCoveringAsync(first)).Should().Be(1);
    }

    [Fact]
    public async Task Prune_WhenAChunkIsFullyExportedAndPastTheCutoff_DropsIt()
    {
        DateTimeOffset old = new(2026, 1, 6, 15, 0, 0, TimeSpan.Zero);
        await InsertTradeAsync(old, sequence: 1);
        await InsertReceiptAsync(DateOnly.FromDateTime(old.UtcDateTime), TapeExportReceipt.StateUploaded);
        (await ChunksCoveringAsync(old, old)).Should().Be(1);

        TapeRetentionRun run = await PruneAsync(batchSize: 10);

        run.Rows.Should().BeGreaterThan(0);
        (await SequencesAsync()).Should().BeEmpty();
        (await ChunksCoveringAsync(old, old)).Should().Be(0);
    }

    [Fact]
    public async Task DeleteBatch_UsesTheContractTimeIndex()
    {
        DateOnly oldDay = new(2026, 7, 2);
        await InsertTradeAsync(DayStart(oldDay).AddHours(1), sequence: 1);

        await using NpgsqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await using (NpgsqlCommand settings = new("SET LOCAL enable_seqscan = off", connection, transaction))
        {
            await settings.ExecuteNonQueryAsync();
        }

        await using NpgsqlCommand command = new(
            "EXPLAIN " + TapeRetentionPass.DeleteBatchSql,
            connection,
            transaction);
        command.Parameters.AddWithValue("instrument", Instrument);
        command.Parameters.AddWithValue("contract", Contract);
        command.Parameters.AddWithValue("dayStart", DayStart(oldDay));
        command.Parameters.AddWithValue("dayEnd", DayStart(oldDay).AddDays(1));
        command.Parameters.AddWithValue("batch", 2);

        StringBuilder plan = new();
        await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                plan.AppendLine(reader.GetString(0));
            }
        }

        await transaction.RollbackAsync();
        plan.ToString().Should().Contain(TapeRetentionPass.DeleteIndex);
    }

    private TapeRetentionPass CreatePass(
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

    private Task<TapeRetentionRun> PruneAsync(int batchSize) =>
        PruneAsync(new HostTelemetry(), new CapturingLogger<TapeRetentionPass>(), batchSize);

    private Task<TapeRetentionRun> PruneAsync(
        HostTelemetry telemetry,
        CapturingLogger<TapeRetentionPass> logs,
        int batchSize) =>
        CreatePass(telemetry, logs, batchSize).PruneAsync(static () => true, CancellationToken.None);

    private async Task InsertTradeAsync(DateTimeOffset time, long sequence, string? contract = null)
    {
        string contractId = contract ?? Contract;
        await _database.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "Trades"
                ("Venue", "Instrument", "ContractId", "TradeTimeUtc", "Sequence",
                 "Price", "Size", "Direction", "RecordedAt")
            VALUES
                ({Venue}, {Instrument}, {contractId}, {time}, {sequence},
                 {5000m}, {1L}, {1}, {time})
            """);
    }

    private async Task InsertReceiptAsync(
        DateOnly day,
        string state,
        string? contract = null,
        int rowCount = 1)
    {
        string contractId = contract ?? Contract;
        await _database.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "TapeExportReceipts"
                ("Day", "Kind", "Format", "Venue", "Instrument", "ContractId", "ResolutionMinutes",
                 "RelativePath", "Sha256", "RowCount", "ExportedAtUtc", "UploadState")
            VALUES
                ({day}, {"trades"}, {"csv.gz"}, {Venue}, {Instrument}, {contractId}, {0},
                 {"trades/test/ES/" + contractId + "/" + day.ToString("yyyy-MM-dd") + ".v1.csv.gz"},
                 {"abc123"}, {rowCount}, {_now}, {state})
            """);
    }

    private async Task<List<long>> SequencesAsync() =>
        await _database.Database
            .SqlQuery<long>($"""SELECT "Sequence" AS "Value" FROM "Trades" ORDER BY "Sequence" """)
            .ToListAsync();

    private long CountTrades()
    {
        using TopstepXDbContext database = _fixture.CreateContext();
        return database.Trades.LongCount();
    }

    private async Task<long> ChunksCoveringAsync(DateTimeOffset first, DateTimeOffset second)
    {
        List<long> counts = await _database.Database.SqlQuery<long>(
                $"""
                SELECT count(*)::bigint AS "Value"
                FROM timescaledb_information.chunks
                WHERE hypertable_name = 'Trades'
                  AND range_start <= {first}
                  AND range_end > {first}
                  AND range_start <= {second}
                  AND range_end > {second}
                """)
            .ToListAsync();
        return counts[0];
    }

    private async Task<long> CompressedChunksCoveringAsync(DateTimeOffset time)
    {
        List<long> counts = await _database.Database.SqlQuery<long>(
                $"""
                SELECT count(*)::bigint AS "Value"
                FROM timescaledb_information.chunks
                WHERE hypertable_name = 'Trades'
                  AND is_compressed
                  AND range_start <= {time}
                  AND range_end > {time}
                """)
            .ToListAsync();
        return counts[0];
    }

    private async Task CompressChunkCoveringAsync(DateTimeOffset time)
    {
        _ = await _database.Database.SqlQuery<string>(
                $"""
                SELECT compress_chunk(format('%I.%I', chunk_schema, chunk_name)::regclass)::text AS "Value"
                FROM timescaledb_information.chunks
                WHERE hypertable_name = 'Trades'
                  AND range_start <= {time}
                  AND range_end > {time}
                """)
            .ToListAsync();
    }

    private static DateTimeOffset DayStart(DateOnly day) => new(day, TimeOnly.MinValue, TimeSpan.Zero);
}

/// <summary>
/// The same receipt rule on plain Postgres, where <c>Trades</c> is not a hypertable (ADR-0004).
/// </summary>
public sealed class TapeRetentionPlainPostgresTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg17")
        .WithDatabase("topstepx_mcp_plain")
        .WithUsername("topstepx")
        .WithPassword("test-only")
        .Build();

    private static readonly DateTimeOffset _now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    /// <inheritdoc />
    public async Task InitializeAsync() => await _postgres.StartAsync();

    /// <inheritdoc />
    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    [Fact]
    public async Task Prune_OnPlainPostgres_DeletesOnlyADayWithAnUploadedReceipt()
    {
        await using TopstepXDbContext database = new(new DbContextOptionsBuilder<TopstepXDbContext>()
            .UseNpgsql(_postgres.GetConnectionString(), npgsql => npgsql.UseVector())
            .Options);
        await database.Database.MigrateAsync();

        List<int> timescale = await database.Database.SqlQuery<int>(
                $"""SELECT 1 AS "Value" FROM pg_extension WHERE extname = 'timescaledb'""")
            .ToListAsync();
        timescale.Should().BeEmpty();

        DateOnly exported = new(2026, 6, 1);
        DateOnly kept = new(2026, 6, 2);
        await InsertAsync(database, exported, sequence: 1);
        await InsertAsync(database, kept, sequence: 2);
        await InsertReceiptAsync(database, exported);

        TapeRetentionRun run = await new TapeRetentionPass(
                database,
                Options.Create(new TapeRetentionOptions()),
                new FakeTimeProvider(_now),
                new CapturingLogger<TapeRetentionPass>(),
                new HostTelemetry())
            .PruneAsync(static () => true, CancellationToken.None);

        run.Rows.Should().Be(1);
        run.DaysSkipped.Should().Be(1);
        List<long> sequences = await database.Database
            .SqlQuery<long>($"""SELECT "Sequence" AS "Value" FROM "Trades" ORDER BY "Sequence" """)
            .ToListAsync();
        sequences.Should().Equal(2L);
    }

    private static async Task InsertAsync(TopstepXDbContext database, DateOnly day, long sequence)
    {
        DateTimeOffset time = new(day, new TimeOnly(15, 0), TimeSpan.Zero);
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "Trades"
                ("Venue", "Instrument", "ContractId", "TradeTimeUtc", "Sequence",
                 "Price", "Size", "Direction", "RecordedAt")
            VALUES
                ({"test"}, {"ES"}, {"CON.F.US.EP.Z26"}, {time}, {sequence},
                 {5000m}, {1L}, {1}, {time})
            """);
    }

    private static async Task InsertReceiptAsync(TopstepXDbContext database, DateOnly day)
    {
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "TapeExportReceipts"
                ("Day", "Kind", "Format", "Venue", "Instrument", "ContractId", "ResolutionMinutes",
                 "RelativePath", "Sha256", "RowCount", "ExportedAtUtc", "UploadState")
            VALUES
                ({day}, {"trades"}, {"csv.gz"}, {"test"}, {"ES"}, {"CON.F.US.EP.Z26"}, {0},
                 {"trades/test/ES/x.csv.gz"}, {"abc123"}, {1}, {_now}, {"uploaded"})
            """);
    }
}
