using System.Data.Common;
using System.Diagnostics;
using System.Text;
using System.Threading;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
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

        TapeRetentionRun run = default;
        string? outcome = await OutcomeOfAsync(telemetry, async () =>
        {
            run = await PruneAsync(telemetry, logs, batchSize: 10);
        });

        outcome.Should().Be(TapeRetentionOutcome.Pruned);
        run.Rows.Should().Be(1);
        (await SequencesAsync()).Should().Equal(2L);
        logs.Messages.Should().Contain(message =>
            message.Contains("will delete about 1", StringComparison.Ordinal)
            && message.Contains("2026-07-02", StringComparison.Ordinal)
            && message.Contains(Instrument, StringComparison.Ordinal));
        pruned.LastMeasurement!.Value.Should().Be(1);
    }

    [Fact]
    public async Task Prune_WhenTheSourceCountChanged_KeepsThatDayAndDeletesTheMatch()
    {
        DateOnly oldDay = new(2026, 7, 2);
        await InsertTradeAsync(DayStart(oldDay).AddHours(1), sequence: 1, contract: "late");
        await InsertTradeAsync(DayStart(oldDay).AddHours(2), sequence: 2, contract: "late");
        await InsertTradeAsync(DayStart(oldDay).AddHours(1), sequence: 3, contract: "match");
        await InsertReceiptAsync(oldDay, TapeExportReceipt.StateUploaded, contract: "late", rowCount: 1);
        await InsertReceiptAsync(oldDay, TapeExportReceipt.StateUploaded, contract: "match", rowCount: 1);
        using HostTelemetry telemetry = new();
        using MetricCollector<long> skipped = new(
            telemetry, HostTelemetry.Name, HostTelemetry.TapePruneSkippedInstrument);
        CapturingLogger<TapeRetentionPass> logs = new();
        List<Activity> spans = [];
        using ActivityListener listener = RetentionSpans(telemetry, spans);

        TapeRetentionRun run = await PruneAsync(telemetry, logs, batchSize: 10);

        run.Rows.Should().Be(1);
        run.DaysSkipped.Should().Be(0);
        run.SourceCountChanged.Should().Be(1);
        (await SequencesAsync()).Should().Equal(1L, 2L);
        skipped.GetMeasurementSnapshot().Should().Contain(measurement =>
            Equals(measurement.Tags[HostTelemetry.ReasonTag], TapePruneReason.SourceCountChanged)
            && measurement.Value == 1);
        skipped.GetMeasurementSnapshot().Should().NotContain(measurement =>
            Equals(measurement.Tags[HostTelemetry.ReasonTag], TapePruneReason.NoReceipt));
        logs.Warnings.Should().Contain(message =>
            message.Contains("late", StringComparison.Ordinal)
            && message.Contains("source has 2", StringComparison.Ordinal));
        logs.Messages.Should().NotContain(message =>
            message.Contains("no uploaded trades receipt", StringComparison.Ordinal)
            && message.Contains("late", StringComparison.Ordinal));
        Activity span = spans.Should().ContainSingle(candidate => candidate.OperationName == "tape.retention")
            .Subject;
        span.GetTagItem(HostTelemetry.OutcomeTag).Should().Be(TapeRetentionOutcome.Pruned);
        span.GetTagItem(TapeRetentionOutcome.SourceCountChanged).Should().Be(1);
        TapeExportReceipt withdrawn = await TradesReceiptAsync("late");
        withdrawn.UploadState.Should().Be(TapeExportReceipt.StatePending);
        withdrawn.Sha256.Should().BeEmpty();
        withdrawn.FailureType.Should().Be(TapePruneReason.SourceCountChanged);
        (await new EfRecorderHealthStore(_database).TapeDaysKeptAsync(CancellationToken.None)).Should().Be(1);

        CapturingLogger<TapeRetentionPass> again = new();
        TapeRetentionRun second = await PruneAsync(telemetry, again, batchSize: 10);

        second.Rows.Should().Be(0);
        second.SourceCountChanged.Should().Be(0);
        again.Warnings.Should().BeEmpty();
        (await SequencesAsync()).Should().Equal(1L, 2L);
    }

    [Fact]
    public async Task Prune_WhenOnlyTheSourceCountChanged_SetsTheMismatchOutcome()
    {
        DateOnly oldDay = new(2026, 7, 2);
        await InsertTradeAsync(DayStart(oldDay).AddHours(1), sequence: 1);
        await InsertTradeAsync(DayStart(oldDay).AddHours(2), sequence: 2);
        await InsertReceiptAsync(oldDay, TapeExportReceipt.StateUploaded, rowCount: 1);
        using HostTelemetry telemetry = new();
        List<Activity> spans = [];
        using ActivityListener listener = RetentionSpans(telemetry, spans);

        TapeRetentionRun run = await PruneAsync(telemetry, new(), batchSize: 10);

        run.Rows.Should().Be(0);
        run.SourceCountChanged.Should().Be(1);
        (await SequencesAsync()).Should().Equal(1L, 2L);
        Activity span = spans.Should().ContainSingle(candidate => candidate.OperationName == "tape.retention")
            .Subject;
        span.GetTagItem(HostTelemetry.OutcomeTag).Should().Be(TapeRetentionOutcome.SourceCountChanged);
        span.GetTagItem(TapeRetentionOutcome.SourceCountChanged).Should().Be(1);
    }

    [Fact]
    public async Task Prune_WhenARowArrivesBeforeTheDelete_KeepsThatDay()
    {
        DateOnly oldDay = new(2026, 7, 2);
        await InsertTradeAsync(DayStart(oldDay).AddHours(1), sequence: 1);
        await InsertTradeAsync(DayStart(oldDay).AddHours(1), sequence: 2, contract: "other");
        await InsertReceiptAsync(oldDay, TapeExportReceipt.StateUploaded, rowCount: 1);
        InsertBeforeCommand extra = new(
            _fixture.ConnectionString,
            "\"RecordedAt\" <= @exportedAt",
            DayStart(oldDay).AddHours(3),
            sequence: 3,
            contract: Contract,
            recordedAt: _now.AddHours(1));
        await using TopstepXDbContext database = _fixture.CreateContext(extra);

        TapeRetentionRun run = await new TapeRetentionPass(
                database,
                Options.Create(new TapeRetentionOptions()),
                new FakeTimeProvider(_now),
                new CapturingLogger<TapeRetentionPass>(),
                new HostTelemetry(),
                batchSize: 10)
            .PruneAsync(static () => true, CancellationToken.None);

        extra.Inserts.Should().Be(1);
        run.Rows.Should().Be(1);
        run.SourceCountChanged.Should().Be(0);
        run.LateRows.Should().Be(0);
        (await SequencesAsync()).Should().Equal(2L, 3L);
        (await TradesReceiptAsync(Contract)).UploadState.Should().Be(TapeExportReceipt.StateUploaded);
    }

    [Fact]
    public async Task Prune_WhenTheDayHasMoreRowsThanOneBatch_DeletesThemAll()
    {
        DateOnly oldDay = new(2026, 7, 2);
        for (int sequence = 1; sequence <= 15; sequence++)
        {
            await InsertTradeAsync(DayStart(oldDay).AddMinutes(sequence), sequence);
        }

        await InsertTradeAsync(DayStart(oldDay).AddHours(2), sequence: 99, contract: "other");
        await InsertReceiptAsync(oldDay, TapeExportReceipt.StateUploaded, rowCount: 15);
        using HostTelemetry telemetry = new();
        using MetricCollector<long> skipped = new(
            telemetry, HostTelemetry.Name, HostTelemetry.TapePruneSkippedInstrument);

        TapeRetentionRun run = await PruneAsync(telemetry, new(), batchSize: 10);

        run.Rows.Should().Be(15);
        run.Batches.Should().Be(2);
        run.SourceCountChanged.Should().Be(0);
        (await SequencesAsync()).Should().Equal(99L);
        (await TradesReceiptAsync(Contract)).UploadState.Should().Be(TapeExportReceipt.StateUploaded);
        skipped.GetMeasurementSnapshot().Should().NotContain(measurement =>
            Equals(measurement.Tags[HostTelemetry.ReasonTag], TapePruneReason.SourceCountChanged));
    }

    [Fact]
    public async Task Prune_WhenARowArrivesBetweenBatches_KeepsThatRowAndTheReceipt()
    {
        DateOnly oldDay = new(2026, 7, 2);
        for (int sequence = 1; sequence <= 15; sequence++)
        {
            await InsertTradeAsync(DayStart(oldDay).AddMinutes(sequence), sequence);
        }

        await InsertTradeAsync(DayStart(oldDay).AddHours(2), sequence: 99, contract: "other");
        await InsertReceiptAsync(oldDay, TapeExportReceipt.StateUploaded, rowCount: 15);
        InsertBeforeCommand extra = new(
            _fixture.ConnectionString,
            "\"RecordedAt\" <= @exportedAt",
            DayStart(oldDay).AddHours(4),
            sequence: 50,
            contract: Contract,
            recordedAt: _now.AddHours(1),
            skip: 1);
        await using TopstepXDbContext database = _fixture.CreateContext(extra);
        using HostTelemetry telemetry = new();
        List<Activity> spans = [];
        using ActivityListener listener = RetentionSpans(telemetry, spans);

        TapeRetentionRun run = await new TapeRetentionPass(
                database,
                Options.Create(new TapeRetentionOptions()),
                new FakeTimeProvider(_now),
                new CapturingLogger<TapeRetentionPass>(),
                telemetry,
                batchSize: 10)
            .PruneAsync(static () => true, CancellationToken.None);

        extra.Inserts.Should().Be(1);
        run.Rows.Should().Be(15);
        run.SourceCountChanged.Should().Be(0);
        run.LateRows.Should().Be(0);
        (await SequencesAsync()).Should().Equal(50L, 99L);
        (await TradesReceiptAsync(Contract)).UploadState.Should().Be(TapeExportReceipt.StateUploaded);
        spans.Should().Contain(span =>
            span.OperationName == "tape.retention"
            && span.GetTagItem(TapeRetentionOutcome.LateRows) == null);
    }

    [Fact]
    public async Task Prune_WhenARowArrivesBeforeTheChunkDrop_KeepsTheChunk()
    {
        DateTimeOffset old = new(2026, 1, 6, 15, 0, 0, TimeSpan.Zero);
        await InsertTradeAsync(old, sequence: 1);
        await InsertReceiptAsync(DateOnly.FromDateTime(old.UtcDateTime), TapeExportReceipt.StateUploaded);
        (await ChunksCoveringAsync(old, old)).Should().Be(1);
        InsertBeforeCommand extra = new(
            _fixture.ConnectionString,
            "retention-drop-guard",
            old.AddMinutes(1),
            sequence: 2,
            contract: Contract,
            recordedAt: _now.AddHours(1));
        await using TopstepXDbContext database = _fixture.CreateContext(extra);
        using HostTelemetry telemetry = new();
        using MetricCollector<long> skipped = new(
            telemetry, HostTelemetry.Name, HostTelemetry.TapePruneSkippedInstrument);
        List<Activity> spans = [];
        using ActivityListener listener = RetentionSpans(telemetry, spans);
        CapturingLogger<TapeRetentionPass> logs = new();

        TapeRetentionRun run = await new TapeRetentionPass(
                database,
                Options.Create(new TapeRetentionOptions()),
                new FakeTimeProvider(_now),
                logs,
                telemetry,
                batchSize: 10)
            .PruneAsync(static () => true, CancellationToken.None);

        extra.Inserts.Should().Be(1);
        run.ChunksDropped.Should().Be(0);
        run.Rows.Should().Be(0);
        run.DropRefused.Should().Be(TapePruneReason.LateRows);
        run.LateRows.Should().Be(1);
        run.SourceCountChanged.Should().Be(0);
        (await ChunksCoveringAsync(old, old)).Should().Be(1);
        (await SequencesAsync()).Should().Equal(1L, 2L);
        TapeExportReceipt withdrawn = await TradesReceiptAsync(Contract);
        withdrawn.UploadState.Should().Be(TapeExportReceipt.StatePending);
        withdrawn.Sha256.Should().BeEmpty();
        withdrawn.FailureType.Should().Be(TapePruneReason.LateRows);
        withdrawn.RowCount.Should().Be(1);
        skipped.GetMeasurementSnapshot().Should().ContainSingle(measurement =>
            Equals(measurement.Tags[HostTelemetry.ReasonTag], TapePruneReason.LateRows))
            .Which.Value.Should().Be(1);
        logs.Warnings.Should().Contain(message => message.Contains("late_rows", StringComparison.Ordinal));
        spans.Should().Contain(span =>
            span.OperationName == "tape.retention"
            && (string?)span.GetTagItem(HostTelemetry.ReasonTag) == TapePruneReason.LateRows);

        using MetricCollector<long> again = new(
            telemetry, HostTelemetry.Name, HostTelemetry.TapePruneSkippedInstrument);
        TapeRetentionRun second = await PruneAsync(telemetry, new(), batchSize: 10);

        second.Rows.Should().Be(0);
        second.ChunksDropped.Should().Be(0);
        second.LateRows.Should().Be(0);
        second.DropRefused.Should().BeNull();
        (await SequencesAsync()).Should().Equal(1L, 2L);
        again.GetMeasurementSnapshot().Should().NotContain(measurement =>
            Equals(measurement.Tags[HostTelemetry.ReasonTag], TapePruneReason.LateRows));
    }

    [Fact]
    public async Task Prune_WhenARowWasRecordedAfterTheExport_WithdrawsThenDeletesAfterReexport()
    {
        DateOnly oldDay = new(2026, 7, 2);
        string destination = Path.Combine(
            Path.GetTempPath(), "tape-retention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(destination);
        try
        {
            await InsertTradeAsync(DayStart(oldDay).AddHours(1), sequence: 1);
            await InsertReceiptAsync(
                oldDay, TapeExportReceipt.StateUploaded, driveFileId: "kept-file");
            await InsertTradeAsync(
                DayStart(oldDay).AddHours(2),
                sequence: 2,
                recordedAt: _now.AddHours(1));

            TapeRetentionRun kept = await PruneAsync(batchSize: 10);

            kept.Rows.Should().Be(0);
            kept.LateRows.Should().Be(1);
            kept.SourceCountChanged.Should().Be(0);
            (await SequencesAsync()).Should().Equal(1L, 2L);
            TapeExportReceipt withdrawn = await TradesReceiptAsync(Contract);
            withdrawn.UploadState.Should().Be(TapeExportReceipt.StatePending);
            withdrawn.Sha256.Should().BeEmpty();
            withdrawn.FailureType.Should().Be(TapePruneReason.LateRows);
            withdrawn.DriveFileId.Should().Be("kept-file");
            withdrawn.RowCount.Should().Be(1);
            withdrawn.RelativePath.Should().Be(ReceiptPath(oldDay, Contract));
            (await new EfRecorderHealthStore(_database).TapeDaysKeptAsync(CancellationToken.None))
                .Should().Be(1);

            TapeRetentionRun repeat = await PruneAsync(batchSize: 10);
            repeat.LateRows.Should().Be(0);
            repeat.Rows.Should().Be(0);
            (await SequencesAsync()).Should().Equal(1L, 2L);

            RememberingUploader uploader = new();
            await ExportAsync(destination, uploader);
            TapeExportDay exported = await TapeExportArchive.ReadDayAsync(
                destination, oldDay, CancellationToken.None);
            exported.Trades.Select(row => row.Sequence).Should().Equal(1L, 2L);
            uploader.SeenFileId.Should().Be("kept-file");
            TapeExportReceipt again = await TradesReceiptAsync(Contract);
            again.UploadState.Should().Be(TapeExportReceipt.StateUploaded);
            again.RowCount.Should().Be(2);
            again.ExportedAtUtc.Should().BeAfter(_now.AddHours(1));

            TapeRetentionRun deleted = await PruneAsync(batchSize: 10);

            deleted.LateRows.Should().Be(0);
            deleted.SourceCountChanged.Should().Be(0);
            (await SequencesAsync()).Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Prune_WhenALateRowIsReexported_DropsTheCompressedChunk()
    {
        DateTimeOffset old = new(2026, 1, 6, 15, 0, 0, TimeSpan.Zero);
        DateOnly day = DateOnly.FromDateTime(old.UtcDateTime);
        string destination = Path.Combine(
            Path.GetTempPath(), "tape-retention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(destination);
        try
        {
            await InsertTradeAsync(old, sequence: 1);
            await InsertReceiptAsync(day, TapeExportReceipt.StateUploaded, driveFileId: "kept-file");
            await InsertTradeAsync(old.AddMinutes(1), sequence: 2, recordedAt: _now.AddHours(1));

            TapeRetentionRun kept = await PruneAsync(batchSize: 10);

            kept.Rows.Should().Be(0);
            kept.ChunksDropped.Should().Be(0);
            kept.LateRows.Should().Be(1);
            (await SequencesAsync()).Should().Equal(1L, 2L);
            (await ChunksCoveringAsync(old, old)).Should().Be(1);

            await ExportAsync(destination);
            await CompressChunkCoveringAsync(old);
            (await CompressedChunksCoveringAsync(old)).Should().Be(1);

            TapeRetentionRun dropped = await PruneAsync(batchSize: 10);

            dropped.LateRows.Should().Be(0);
            dropped.ChunksDropped.Should().BeGreaterThan(0);
            (await SequencesAsync()).Should().BeEmpty();
            (await ChunksCoveringAsync(old, old)).Should().Be(0);
        }
        finally
        {
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Prune_WhenALaterDayIsLate_StillDropsAnOlderExportedChunk()
    {
        DateTimeOffset older = new(2026, 1, 6, 15, 0, 0, TimeSpan.Zero);
        DateOnly laterDay = new(2026, 7, 2);
        await InsertTradeAsync(older, sequence: 1);
        await InsertReceiptAsync(
            DateOnly.FromDateTime(older.UtcDateTime), TapeExportReceipt.StateUploaded);
        await InsertTradeAsync(DayStart(laterDay).AddHours(1), sequence: 2);
        await InsertReceiptAsync(laterDay, TapeExportReceipt.StateUploaded);
        await InsertTradeAsync(
            DayStart(laterDay).AddHours(2), sequence: 3, recordedAt: _now.AddHours(1));

        TapeRetentionRun run = await PruneAsync(batchSize: 10);

        run.LateRows.Should().Be(1);
        (await SequencesAsync()).Should().Equal(2L, 3L);
        (await ChunksCoveringAsync(older, older)).Should().Be(0);
        TapeExportReceipt july = await TradesReceiptAsync(Contract, laterDay);
        july.UploadState.Should().Be(TapeExportReceipt.StatePending);
        july.FailureType.Should().Be(TapePruneReason.LateRows);
    }

    [Fact]
    public async Task Prune_WhenTheOldestChunkIsLate_LeavesANewerExportedChunk()
    {
        DateTimeOffset older = new(2026, 1, 6, 15, 0, 0, TimeSpan.Zero);
        DateTimeOffset newer = new(2026, 6, 1, 15, 0, 0, TimeSpan.Zero);
        DateOnly olderDay = DateOnly.FromDateTime(older.UtcDateTime);
        DateOnly newerDay = DateOnly.FromDateTime(newer.UtcDateTime);
        await InsertTradeAsync(older, sequence: 1);
        await InsertReceiptAsync(olderDay, TapeExportReceipt.StateUploaded);
        await InsertTradeAsync(older.AddMinutes(1), sequence: 2, recordedAt: _now.AddHours(1));
        await InsertTradeAsync(newer, sequence: 3);
        await InsertReceiptAsync(newerDay, TapeExportReceipt.StateUploaded);
        (await ChunksCoveringAsync(older, newer)).Should().Be(
            0,
            "these days sit in different chunks, so only the prefix rule can keep the newer one");

        TapeRetentionRun run = await PruneAsync(batchSize: 10);

        run.ChunksDropped.Should().Be(0);
        run.LateRows.Should().Be(1);
        run.Rows.Should().Be(1, "a newer exported day is still deleted; only the chunk drop waits");
        (await SequencesAsync()).Should().Equal(1L, 2L);
        (await ChunksCoveringAsync(newer, newer)).Should().Be(1);
        (await TradesReceiptAsync(Contract, newerDay)).UploadState
            .Should().Be(TapeExportReceipt.StateUploaded);
        (await TradesReceiptAsync(Contract, olderDay)).FailureType
            .Should().Be(TapePruneReason.LateRows);
    }

    [Fact]
    public async Task Prune_WhenTheSourceCountChanged_DeletesOnlyAfterTheDayIsExportedAgain()
    {
        DateOnly oldDay = new(2026, 7, 2);
        string destination = Path.Combine(
            Path.GetTempPath(), "tape-retention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(destination);
        try
        {
            await InsertTradeAsync(DayStart(oldDay).AddHours(1), sequence: 1);
            await ExportAsync(destination);
            (await TradesReceiptAsync(Contract)).RowCount.Should().Be(1);

            await InsertTradeAsync(DayStart(oldDay).AddHours(2), sequence: 2);
            TapeRetentionRun kept = await PruneAsync(batchSize: 10);

            kept.Rows.Should().Be(0);
            (await SequencesAsync()).Should().Equal(1L, 2L);
            TapeExportReceipt withdrawn = await TradesReceiptAsync(Contract);
            withdrawn.UploadState.Should().Be(TapeExportReceipt.StatePending);
            withdrawn.Sha256.Should().BeEmpty();

            await ExportAsync(destination);
            TapeExportDay exported = await TapeExportArchive.ReadDayAsync(
                destination, oldDay, CancellationToken.None);
            exported.Trades.Select(row => row.Sequence).Should().Equal(1L, 2L);
            TapeExportReceipt again = await TradesReceiptAsync(Contract);
            again.UploadState.Should().Be(TapeExportReceipt.StateUploaded);
            again.RowCount.Should().Be(2);

            TapeRetentionRun deleted = await PruneAsync(batchSize: 10);

            deleted.SourceCountChanged.Should().Be(0);
            (await SequencesAsync()).Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }
        }
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
        await InsertReceiptAsync(oldDay, TapeExportReceipt.StateUploaded, contract: "A", rowCount: 3);

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

        using HostTelemetry telemetry = new();
        using MetricCollector<long> chunks = new(
            telemetry, HostTelemetry.Name, HostTelemetry.TapeChunksDroppedInstrument);
        TapeRetentionRun run = default;
        string? outcome = await OutcomeOfAsync(telemetry, async () =>
        {
            run = await PruneAsync(telemetry, new(), 10);
        });

        (await SequencesAsync()).Should().BeEmpty();
        (await ChunksCoveringAsync(old, old)).Should().Be(0);
        run.Rows.Should().Be(0, "the chunk drop is not counted as a measured delete");
        run.ChunksDropped.Should().BeGreaterThan(0);
        run.ChunksDroppedUntil.Should().NotBeNull();
        outcome.Should().Be(TapeRetentionOutcome.Pruned);
        chunks.LastMeasurement!.Value.Should().Be(run.ChunksDropped);
    }

    [Fact]
    public async Task Prune_WhenAnUnreceiptedVenueSharesTheChunk_DoesNotDropIt()
    {
        DateTimeOffset old = new(2026, 1, 6, 15, 0, 0, TimeSpan.Zero);
        await InsertTradeAsync(old, sequence: 1, venue: "venue-a");
        await InsertTradeAsync(old, sequence: 2, venue: "venue-b");
        await InsertReceiptAsync(
            DateOnly.FromDateTime(old.UtcDateTime),
            TapeExportReceipt.StateUploaded,
            venue: "venue-a");
        (await ChunksCoveringAsync(old, old)).Should().Be(1);

        TapeRetentionRun run = await PruneAsync(batchSize: 10);

        run.ChunksDropped.Should().Be(0);
        (await ChunksCoveringAsync(old, old)).Should().Be(1);
        List<string> venues = await _database.Database
            .SqlQuery<string>($"""SELECT "Venue" AS "Value" FROM "Trades" ORDER BY "Venue" """)
            .ToListAsync();
        venues.Should().Equal("venue-b");
    }

    [Fact]
    public async Task Prune_WhenOnlyOneVenueHasAReceipt_KeepsTheOtherVenue()
    {
        DateOnly oldDay = new(2026, 6, 1);
        DateTimeOffset time = DayStart(oldDay).AddHours(2);
        await InsertTradeAsync(time, sequence: 1, venue: "venue-a");
        await InsertTradeAsync(time, sequence: 2, venue: "venue-b");
        await InsertReceiptAsync(oldDay, TapeExportReceipt.StateUploaded, venue: "venue-a");

        TapeRetentionRun run = await PruneAsync(batchSize: 10);

        run.Rows.Should().Be(1);
        List<string> venues = await _database.Database
            .SqlQuery<string>($"""SELECT "Venue" AS "Value" FROM "Trades" ORDER BY "Venue" """)
            .ToListAsync();
        venues.Should().Equal("venue-b");
    }

    [Fact]
    public async Task Prune_WhenNothingIsEligible_SetsTheIdleOutcome()
    {
        using HostTelemetry telemetry = new();
        string? outcome = await OutcomeOfAsync(telemetry, () => PruneAsync(telemetry, new(), 10));

        outcome.Should().Be(TapeRetentionOutcome.Idle);
    }

    [Fact]
    public async Task Prune_WhenADayHasNoReceipt_SetsSkippedAndCountsTheReason()
    {
        DateOnly oldDay = new(2026, 7, 2);
        await InsertTradeAsync(DayStart(oldDay).AddHours(1), sequence: 1);
        using HostTelemetry telemetry = new();
        using MetricCollector<long> skipped = new(
            telemetry, HostTelemetry.Name, HostTelemetry.TapePruneSkippedInstrument);

        string? outcome = await OutcomeOfAsync(telemetry, () => PruneAsync(telemetry, new(), 10));

        outcome.Should().Be(TapeRetentionOutcome.Skipped);
        skipped.LastMeasurement!.Value.Should().Be(1);
        skipped.LastMeasurement.Tags[HostTelemetry.ReasonTag].Should().Be(TapePruneReason.NoReceipt);
    }

    [Fact]
    public async Task Prune_WhenTheLeaseIsLost_CountsLeaseLostAndSetsTheSpan()
    {
        DateOnly oldDay = new(2026, 7, 2);
        await InsertTradeAsync(DayStart(oldDay).AddHours(1), sequence: 1, contract: "A");
        await InsertTradeAsync(DayStart(oldDay).AddHours(2), sequence: 2, contract: "A");
        await InsertTradeAsync(DayStart(oldDay).AddHours(3), sequence: 3, contract: "A");
        await InsertTradeAsync(DayStart(oldDay).AddHours(4), sequence: 4, contract: "B");
        await InsertReceiptAsync(oldDay, TapeExportReceipt.StateUploaded, contract: "A", rowCount: 3);
        using HostTelemetry telemetry = new();
        using MetricCollector<long> lost = new(
            telemetry, HostTelemetry.Name, HostTelemetry.TapePruneLeaseLostInstrument);
        TapeRetentionPass pass = CreatePass(telemetry, new(), 1);

        string? outcome = await OutcomeOfAsync(
            telemetry,
            () => pass.PruneAsync(() => CountTrades() > 3, CancellationToken.None));

        outcome.Should().Be(TapeRetentionOutcome.LeaseLost);
        lost.LastMeasurement!.Value.Should().Be(1);
        lost.LastMeasurement.Tags[HostTelemetry.ReasonTag].Should().Be(TapePruneReason.LeaseLost);
    }

    [Fact]
    public async Task Prune_WhenDropChunksTimesOut_DeletesRowsAnyway()
    {
        DateTimeOffset old = new(2026, 1, 6, 15, 0, 0, TimeSpan.Zero);
        await InsertTradeAsync(old, sequence: 1);
        await InsertReceiptAsync(DateOnly.FromDateTime(old.UtcDateTime), TapeExportReceipt.StateUploaded);
        using HostTelemetry telemetry = new();
        using MetricCollector<long> failures = new(
            telemetry, HostTelemetry.Name, HostTelemetry.TapePruneFailuresInstrument);
        await using NpgsqlConnection locker = new(_fixture.ConnectionString);
        await locker.OpenAsync();
        await using NpgsqlTransaction held = await locker.BeginTransactionAsync();
        await using (NpgsqlCommand share = new(
            "LOCK TABLE \"Trades\" IN ACCESS SHARE MODE",
            locker,
            held))
        {
            await share.ExecuteNonQueryAsync();
        }

        TapeRetentionRun run = await new TapeRetentionPass(
                _database,
                Options.Create(new TapeRetentionOptions()),
                new FakeTimeProvider(_now),
                new CapturingLogger<TapeRetentionPass>(),
                telemetry,
                batchSize: 10,
                dropLockTimeout: TimeSpan.FromMilliseconds(200))
            .PruneAsync(static () => true, CancellationToken.None);

        await held.RollbackAsync();
        run.Rows.Should().Be(1);
        run.DropRefused.Should().Be(TapePruneReason.LockTimeout);
        (await SequencesAsync()).Should().BeEmpty();
        failures.LastMeasurement!.Tags[HostTelemetry.ReasonTag]
            .Should().Be(TapePruneReason.LockTimeout);
    }

    [Fact]
    public async Task Prune_WhenTheDropStatementTimesOut_ReportsStatementTimeout()
    {
        TapeRetentionPass.DefaultDropLockTimeout.Should().Be(TimeSpan.FromSeconds(5));
        TapeRetentionPass.DefaultDropStatementTimeout.Should().Be(TimeSpan.FromSeconds(30));
        DateTimeOffset old = new(2026, 1, 6, 15, 0, 0, TimeSpan.Zero);
        await InsertTradeAsync(old, sequence: 1);
        await InsertReceiptAsync(DateOnly.FromDateTime(old.UtcDateTime), TapeExportReceipt.StateUploaded);
        using HostTelemetry telemetry = new();
        using MetricCollector<long> failures = new(
            telemetry, HostTelemetry.Name, HostTelemetry.TapePruneFailuresInstrument);
        await using TopstepXDbContext database = _fixture.CreateContext(new CancelDropOnce());

        TapeRetentionRun run = await new TapeRetentionPass(
                database,
                Options.Create(new TapeRetentionOptions()),
                new FakeTimeProvider(_now),
                new CapturingLogger<TapeRetentionPass>(),
                telemetry,
                batchSize: 10,
                dropLockTimeout: TimeSpan.FromSeconds(30),
                dropStatementTimeout: TimeSpan.FromMilliseconds(200))
            .PruneAsync(static () => true, CancellationToken.None);

        run.Rows.Should().Be(1);
        run.DropRefused.Should().Be(TapePruneReason.StatementTimeout);
        (await SequencesAsync()).Should().BeEmpty();
        failures.GetMeasurementSnapshot().Should().ContainSingle(measurement =>
            Equals(measurement.Tags[HostTelemetry.ReasonTag], TapePruneReason.StatementTimeout));
        failures.GetMeasurementSnapshot().Should().NotContain(measurement =>
            Equals(measurement.Tags[HostTelemetry.ReasonTag], TapePruneReason.LockTimeout));
    }

    [Fact]
    public async Task Prune_WhenThePassThrows_SetsTheFailedOutcome()
    {
        using HostTelemetry telemetry = new();
        await using TopstepXDbContext broken = _fixture.CreateContext(new ThrowOnRead());

        string? outcome = await OutcomeOfAsync(telemetry, () => new TapeRetentionPass(
                broken,
                Options.Create(new TapeRetentionOptions()),
                new FakeTimeProvider(_now),
                new CapturingLogger<TapeRetentionPass>(),
                telemetry)
            .PruneAsync(static () => true, CancellationToken.None));

        outcome.Should().Be(TapeRetentionOutcome.Failed);
    }

    [Fact]
    public async Task DeleteBatch_UsesAnIndexScan()
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
        command.Parameters.AddWithValue("venue", Venue);
        command.Parameters.AddWithValue("instrument", Instrument);
        command.Parameters.AddWithValue("contract", Contract);
        command.Parameters.AddWithValue("dayStart", DayStart(oldDay));
        command.Parameters.AddWithValue("dayEnd", DayStart(oldDay).AddDays(1));
        command.Parameters.AddWithValue("batch", 2);
        command.Parameters.AddWithValue("exportedAt", _now);

        StringBuilder plan = new();
        await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                plan.AppendLine(reader.GetString(0));
            }
        }

        await transaction.RollbackAsync();
        string planText = plan.ToString();
        planText.Should().Contain("Index Scan");
        planText.Should().NotContain("Seq Scan");
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

    private async Task InsertTradeAsync(
        DateTimeOffset time,
        long sequence,
        string? contract = null,
        string? venue = null,
        DateTimeOffset? recordedAt = null)
    {
        string contractId = contract ?? Contract;
        string venueId = venue ?? Venue;
        DateTimeOffset recorded = recordedAt ?? time;
        await _database.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "Trades"
                ("Venue", "Instrument", "ContractId", "TradeTimeUtc", "Sequence",
                 "Price", "Size", "Direction", "RecordedAt")
            VALUES
                ({venueId}, {Instrument}, {contractId}, {time}, {sequence},
                 {5000m}, {1L}, {1}, {recorded})
            """);
    }

    private async Task InsertReceiptAsync(
        DateOnly day,
        string state,
        string? contract = null,
        int rowCount = 1,
        string? venue = null,
        string? driveFileId = null)
    {
        string contractId = contract ?? Contract;
        string venueId = venue ?? Venue;
        string path = ReceiptPath(day, contractId);
        await _database.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "TapeExportReceipts"
                ("Day", "Kind", "Format", "Venue", "Instrument", "ContractId", "ResolutionMinutes",
                 "RelativePath", "Sha256", "RowCount", "ExportedAtUtc", "DriveFileId", "UploadState")
            VALUES
                ({day}, {"trades"}, {"csv.gz"}, {venueId}, {Instrument}, {contractId}, {0},
                 {path}, {"abc123"}, {rowCount}, {_now}, {driveFileId}, {state})
            """);
    }

    private static string ReceiptPath(DateOnly day, string contractId) =>
        "trades/test/ES/" + contractId + "/" + day.ToString("yyyy-MM-dd") + ".v1.csv.gz";

    private static async Task<string?> OutcomeOfAsync(HostTelemetry telemetry, Func<Task> act)
    {
        string? outcome = null;
        using ActivityListener listener = new()
        {
            ShouldListenTo = source => source.Name == HostTelemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == "tape.retention")
                {
                    outcome = activity.GetTagItem(HostTelemetry.OutcomeTag) as string;
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        try
        {
            await act();
        }
        catch (InvalidOperationException)
        {
            // The failed-outcome test throws on purpose. The span is already stopped.
        }

        return outcome;
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

    private async Task<TapeExportReceipt> TradesReceiptAsync(string contract, DateOnly? day = null)
    {
        _database.ChangeTracker.Clear();
        return await _database.TapeExportReceipts.AsNoTracking().SingleAsync(row =>
            row.Kind == TapeExportArchive.TradesDirectory
            && row.Format == TapeExportArchive.CsvGzFormat
            && row.ContractId == contract
            && (day == null || row.Day == day));
    }

    private async Task ExportAsync(string destination, ITapeDriveUploader? uploader = null)
    {
        await new TapeExportPass(
                _database,
                Options.Create(new TapeExportOptions
                {
                    Enabled = true,
                    Path = destination,
                    Schedule = TapeExportOptions.DefaultSchedule,
                    Format = TapeExportOptions.DefaultFormat,
                }),
                new FakeTimeProvider(_now),
                new HostTelemetry(),
                uploader ?? new AcceptingUploader(),
                NullLogger<TapeExportPass>.Instance)
            .ExportAsync(null, CancellationToken.None);
    }

    private static ActivityListener RetentionSpans(HostTelemetry telemetry, List<Activity> into)
    {
        ActivityListener listener = new()
        {
            ShouldListenTo = source => ReferenceEquals(source, telemetry.Activities),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = into.Add,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static DateTimeOffset DayStart(DateOnly day) => new(day, TimeOnly.MinValue, TimeSpan.Zero);

    private sealed class AcceptingUploader : ITapeDriveUploader
    {
        public bool IsEnabled => true;

        public Task<TapeDriveUploadResult> UploadAsync(
            TapeDriveUpload upload,
            CancellationToken cancellationToken) =>
            Task.FromResult(new TapeDriveUploadResult(
                TapeDriveUploadStatus.Uploaded,
                "drive-file",
                1,
                null,
                null));
    }

    private sealed class RememberingUploader : ITapeDriveUploader
    {
        public bool IsEnabled => true;

        public string? SeenFileId { get; private set; }

        public Task<TapeDriveUploadResult> UploadAsync(
            TapeDriveUpload upload,
            CancellationToken cancellationToken)
        {
            if (upload.RelativePath.StartsWith("trades/", StringComparison.Ordinal)
                && upload.RelativePath.EndsWith(".csv.gz", StringComparison.Ordinal))
            {
                SeenFileId = upload.KnownFileId;
            }

            return Task.FromResult(new TapeDriveUploadResult(
                TapeDriveUploadStatus.Uploaded,
                upload.KnownFileId ?? "drive-file",
                1,
                null,
                null));
        }
    }
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

/// <summary>Inserts one trade on another connection just before the marked command runs.</summary>
sealed class InsertBeforeCommand : DbCommandInterceptor
{
    private readonly string _connectionString;
    private readonly string _marker;
    private readonly DateTimeOffset _time;
    private readonly long _sequence;
    private readonly string _contract;
    private readonly DateTimeOffset _recordedAt;
    private readonly int _skip;
    private int _seen;

    public InsertBeforeCommand(
        string connectionString,
        string marker,
        DateTimeOffset time,
        long sequence,
        string contract,
        DateTimeOffset? recordedAt = null,
        int skip = 0)
    {
        _connectionString = connectionString;
        _marker = marker;
        _time = time;
        _sequence = sequence;
        _contract = contract;
        _recordedAt = recordedAt ?? time;
        _skip = skip;
    }

    public int Inserts { get; private set; }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        InsertIfMatch(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        InsertIfMatch(command);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    private void InsertIfMatch(DbCommand command)
    {
        if (!command.CommandText.Contains(_marker, StringComparison.Ordinal))
        {
            return;
        }

        if (_seen++ < _skip || _seen > _skip + 1)
        {
            return;
        }

        using NpgsqlConnection connection = new(_connectionString);
        connection.Open();
        using NpgsqlCommand insert = new(
            """
            INSERT INTO "Trades"
                ("Venue", "Instrument", "ContractId", "TradeTimeUtc", "Sequence",
                 "Price", "Size", "Direction", "RecordedAt")
            VALUES
                ('test', 'ES', @contract, @time, @sequence, 5000, 1, 1, @recorded)
            """,
            connection);
        insert.Parameters.AddWithValue("contract", _contract);
        insert.Parameters.AddWithValue("time", _time);
        insert.Parameters.AddWithValue("recorded", _recordedAt);
        insert.Parameters.AddWithValue("sequence", _sequence);
        insert.ExecuteNonQuery();
        Inserts++;
    }
}

/// <summary>The drop guard is cancelled once, as a statement timeout would.</summary>
sealed class CancelDropOnce : DbCommandInterceptor
{
    /// <inheritdoc />
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        if (command.CommandText.Contains("retention-drop-guard", StringComparison.Ordinal))
        {
            throw new PostgresException(
                "canceling statement due to statement timeout",
                "ERROR",
                "ERROR",
                PostgresErrorCodes.QueryCanceled);
        }

        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }
}

/// <summary>The first read throws, so the retention span can record <c>failed</c>.</summary>
sealed class ThrowOnRead : DbCommandInterceptor
{
    /// <inheritdoc />
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("boom");
}
