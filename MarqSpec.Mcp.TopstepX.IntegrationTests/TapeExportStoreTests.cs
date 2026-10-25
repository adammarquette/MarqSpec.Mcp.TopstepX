using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// Export and import against Postgres. Local rows stay. A receipt records each file (gh#755).
/// </summary>
[Collection(SeriesStoreCollection.Name)]
public sealed class TapeExportStoreTests : IAsyncLifetime
{
    private const string Venue = "test";

    private const string Contract = "CON.F.US.EP.Z26";

    private static readonly DateTimeOffset _now = new(2026, 10, 17, 6, 0, 0, TimeSpan.Zero);

    private static readonly DateOnly _oldDay = new(2026, 9, 1);

    private static readonly DateOnly _keptDay = new(2026, 10, 10);

    private static readonly DateOnly _openDay = new(2026, 10, 17);

    private readonly SeriesStoreFixture _fixture;

    private readonly TopstepXDbContext _database;

    private readonly string _destination;

    /// <param name="fixture">The shared container.</param>
    public TapeExportStoreTests(SeriesStoreFixture fixture)
    {
        _fixture = fixture;
        _database = fixture.CreateContext();
        _destination = Path.Combine(Path.GetTempPath(), "tape-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_destination);
    }

    /// <inheritdoc />
    public Task InitializeAsync() => _fixture.ResetAsync();

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        await _database.DisposeAsync();
        if (Directory.Exists(_destination))
        {
            Directory.Delete(_destination, recursive: true);
        }
    }

    [Fact]
    public async Task Export_WhenTheDayHasNotEnded_WritesNothingAndLeavesRows()
    {
        await SeedTradeAsync(On(_openDay, 4), 1, 10m);
        HostTelemetry telemetry = new();

        TapeExportReport report = await Pass(telemetry).ExportAsync(null, CancellationToken.None);

        report.DaysExported.Should().Be(0);
        report.RowsExported.Should().Be(0);
        File.Exists(TapeExportArchive.ManifestPath(_destination, _openDay)).Should().BeFalse();
        (await CountTradesAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Export_WhenTheDayHasEnded_WritesGzipAndLeavesRows()
    {
        await SeedTradeAsync(On(_keptDay, 14), 1, 5123.25m);

        TapeExportReport report = await Pass(new HostTelemetry())
            .ExportAsync(null, CancellationToken.None);

        report.DaysExported.Should().Be(1);
        report.DaysRefused.Should().Be(0);
        (await TapeExportArchive.VerifyAsync(_destination, _keptDay, CancellationToken.None)).Verified.Should().BeTrue();
        (await CountTradesAsync()).Should().Be(1);
        TapeExportReceipt receipt = await _database.TapeExportReceipts.SingleAsync(row =>
            row.Format == TapeExportArchive.CsvGzFormat && row.Kind == "trades");
        receipt.UploadState.Should().Be(TapeExportReceipt.StateDriveDisabled);
        receipt.UploadState.Should().NotBe(TapeExportReceipt.StatePending);
        receipt.ContractId.Should().Be(Contract);
        receipt.RowCount.Should().Be(1);
        receipt.DriveFileId.Should().BeNull();
    }

    [Fact]
    public async Task Export_WhenSeveralFinishedDaysAreCopied_LeavesEveryRow()
    {
        await SeedTradeAsync(On(_oldDay, 14), 1, 5123.25m);
        await SeedTradeAsync(On(_keptDay, 15), 2, 10m);
        await SeedCoverageAsync(On(_oldDay, 14), On(_oldDay, 15));
        await SeedCellAsync(On(_oldDay, 14, 30), 5123.25m);
        using HostTelemetry telemetry = new();
        using MetricCollector<long> exports =
            new(telemetry, HostTelemetry.Name, HostTelemetry.TapeExportsInstrument);
        using MetricCollector<double> duration =
            new(telemetry, HostTelemetry.Name, HostTelemetry.TapeExportDurationInstrument);

        TapeExportReport report = await Pass(telemetry).ExportAsync(null, CancellationToken.None);

        report.DaysExported.Should().Be(2);
        report.RowsExported.Should().Be(4);
        (await _database.Trades.CountAsync(row => row.Sequence == 1)).Should().Be(1);
        (await _database.Trades.CountAsync(row => row.Sequence == 2)).Should().Be(1);
        (await _database.TapeCoverage.CountAsync()).Should().Be(1);
        (await _database.FootprintCells.CountAsync()).Should().Be(1);
        (await TapeExportArchive.ReadDayAsync(_destination, _oldDay, CancellationToken.None)).Trades
            .Should().ContainSingle()
            .Which.Price.Should().Be(5123.25m);
        exports.GetMeasurementSnapshot()
            .Where(measurement => Equals(measurement.Tags[HostTelemetry.OutcomeTag], TapeExportOutcome.RowsExported))
            .Sum(measurement => measurement.Value)
            .Should().Be(4);
        duration.GetMeasurementSnapshot().Should().NotBeEmpty();
    }

    [Fact]
    public async Task Export_WhenTheFileWasTampered_RewritesItAndLeavesRows()
    {
        await SeedTradeAsync(On(_oldDay, 14), 1, 5123.25m);
        await Pass(new HostTelemetry()).ExportAsync(null, CancellationToken.None);
        string path = TapeExportArchive.TradesPath(_destination, Venue, "ES", Contract, _oldDay);
        byte[] bytes = await File.ReadAllBytesAsync(path);
        bytes[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(path, bytes);

        TapeExportReport report = await Pass(new HostTelemetry())
            .ExportAsync(null, CancellationToken.None);

        report.RowsExported.Should().Be(1);
        report.DaysRefused.Should().Be(0);
        (await TapeExportArchive.VerifyAsync(_destination, _oldDay, CancellationToken.None)).Verified
            .Should().BeTrue();
        (await CountTradesAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Export_WhenCoverageExtendsPastTheDay_CopiesItAndLeavesRows()
    {
        await SeedTradeAsync(On(_oldDay, 14), 1, 5123.25m);
        await SeedCoverageAsync(On(_oldDay, 14), new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero));

        TapeExportReport report = await Pass(new HostTelemetry())
            .ExportAsync(null, CancellationToken.None);

        report.RowsExported.Should().Be(2);
        (await CountTradesAsync()).Should().Be(1);
        (await _database.TapeCoverage.CountAsync()).Should().Be(1);
        (await TapeExportArchive.ReadDayAsync(_destination, _oldDay, CancellationToken.None)).Trades
            .Should().ContainSingle()
            .Which.Price.Should().Be(5123.25m);
    }

    [Fact]
    public async Task Export_WhenAnOpenRunOverlapsTheDay_WritesNothingUntilTheRunCloses()
    {
        await SeedTradeAsync(On(_oldDay, 14), 1, 5123.25m);
        await SeedOpenCoverageAsync(On(_oldDay, 15));

        TapeExportReport blocked = await Pass(new HostTelemetry()).ExportAsync(null, CancellationToken.None);

        blocked.DaysExported.Should().Be(0);
        blocked.RowsExported.Should().Be(0);
        File.Exists(TapeExportArchive.ManifestPath(_destination, _oldDay)).Should().BeFalse();
        (await CountTradesAsync()).Should().Be(1);

        await _database.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE "TapeCoverage" SET "RangeEnd" = {On(_oldDay, 16)} WHERE "RangeEnd" = {TapeCoverageRecord.StillListeningEnd}""");
        _database.ChangeTracker.Clear();

        TapeExportReport closed = await Pass(new HostTelemetry()).ExportAsync(null, CancellationToken.None);

        closed.DaysExported.Should().Be(1);
        closed.RowsExported.Should().Be(2);
        (await TapeExportArchive.VerifyAsync(_destination, _oldDay, CancellationToken.None)).Verified.Should().BeTrue();
        (await TapeExportArchive.ReadDayAsync(_destination, _oldDay, CancellationToken.None)).Trades
            .Should().ContainSingle().Which.Price.Should().Be(5123.25m);
        (await CountTradesAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Export_WhenThePathLockIsHeld_WritesNothing()
    {
        await SeedTradeAsync(On(_oldDay, 14), 1, 5123.25m);
        string lockPath = Path.Combine(_destination, TapeExportArchive.LockFileName);
        await using FileStream held = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        TapeExportReport report = await Pass(new HostTelemetry())
            .ExportAsync(null, CancellationToken.None);

        report.RowsExported.Should().Be(0);
        (await CountTradesAsync()).Should().Be(1);
        File.Exists(TapeExportArchive.ManifestPath(_destination, _oldDay)).Should().BeFalse();
    }

    [Fact]
    public async Task Import_WhenManifestVerified_RestoresRowsAndASecondImportDoesNotDuplicate()
    {
        DateTimeOffset traded = On(_oldDay, 14);
        await SeedTradeAsync(traded, 7, 5123.25m, TradeDirection.Sell);
        await SeedCoverageAsync(traded, traded.AddMinutes(5));
        await SeedCellAsync(traded, 5123.25m);
        await Pass(new HostTelemetry()).ExportAsync(null, CancellationToken.None);
        await _database.Trades.ExecuteDeleteAsync();
        await _database.TapeCoverage.ExecuteDeleteAsync();
        await _database.FootprintCells.ExecuteDeleteAsync();
        _database.ChangeTracker.Clear();

        TapeExportReport first = await Pass(new HostTelemetry())
            .ImportAsync(null, CancellationToken.None);
        TapeExportReport second = await Pass(new HostTelemetry())
            .ImportAsync(null, CancellationToken.None);

        first.RowsImported.Should().Be(3);
        first.DaysRefused.Should().Be(0);
        second.RowsImported.Should().Be(0);
        TradeRecord restored = await _database.Trades.SingleAsync();
        restored.Price.Should().Be(5123.25m);
        restored.Direction.Should().Be(TradeDirection.Sell);
        restored.Sequence.Should().Be(7);
        (await _database.TapeCoverage.CountAsync()).Should().Be(1);
        FootprintCellRecord cell = await _database.FootprintCells.SingleAsync();
        cell.BuyVolume.Should().Be(3);
        cell.SellVolume.Should().Be(1);
    }

    [Fact]
    public async Task Import_WhenChecksumMismatches_WritesNoRows()
    {
        await SeedTradeAsync(On(_oldDay, 14), 1, 5123.25m);
        await Pass(new HostTelemetry()).ExportAsync(null, CancellationToken.None);
        await _database.Trades.ExecuteDeleteAsync();
        _database.ChangeTracker.Clear();
        string path = TapeExportArchive.TradesPath(_destination, Venue, "ES", Contract, _oldDay);
        byte[] bytes = await File.ReadAllBytesAsync(path);
        bytes[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(path, bytes);

        TapeExportReport report = await Pass(new HostTelemetry())
            .ImportAsync(null, CancellationToken.None);

        report.RowsImported.Should().Be(0);
        report.DaysRefused.Should().BeGreaterThan(0);
        (await CountTradesAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Export_WhenTheUploadFails_LeavesRowsAndRecordsTheFailure()
    {
        await SeedTradeAsync(On(_oldDay, 14), 1, 5123.25m);
        using HostTelemetry telemetry = new();
        FakeTapeDriveClient client = new() { FailuresBeforeSuccess = 10 };
        string csv = TapeExportArchive.TradesPath(_destination, Venue, "ES", Contract, _oldDay);

        TapeExportReport report = await Pass(telemetry, new RetryingTapeDriveUploader(client, telemetry))
            .ExportAsync(null, CancellationToken.None);

        report.DaysExported.Should().Be(0);
        report.DaysRefused.Should().Be(1);
        (await CountTradesAsync()).Should().Be(1);
        File.Exists(csv).Should().BeTrue();
        File.Exists(TapeExportArchive.TradesPath(
            _destination, Venue, "ES", Contract, _oldDay, TapeExportArchive.ParquetFormat)).Should().BeTrue();
        client.Files.Should().BeEmpty();
        client.Attempts.Should().BeGreaterThanOrEqualTo(RetryingTapeDriveUploader.MaxAttempts);
        TapeExportReceipt receipt = await _database.TapeExportReceipts.SingleAsync(row =>
            row.Format == TapeExportArchive.CsvGzFormat);
        receipt.UploadState.Should().Be(TapeExportReceipt.StateFailed);
        receipt.DriveFileId.Should().BeNull();
    }

    [Fact]
    public async Task Export_WhenATransientUploadThenSucceeds_SecondPassExportsNothing()
    {
        await SeedTradeAsync(On(_oldDay, 14), 1, 5123.25m);
        using HostTelemetry telemetry = new();
        FakeTapeDriveClient client = new() { FailuresBeforeSuccess = 2 };

        TapeExportReport first = await Pass(telemetry, new RetryingTapeDriveUploader(client, telemetry))
            .ExportAsync(null, CancellationToken.None);
        int filesAfterFirst = client.Files.Count;
        int scans = 0;
        PriceScanInterceptor interceptor = new(() => scans++);
        await using TopstepXDbContext secondDatabase = _fixture.CreateContext(interceptor);
        TapeExportReport second = await Pass(
                telemetry,
                new RetryingTapeDriveUploader(client, telemetry),
                secondDatabase)
            .ExportAsync(null, CancellationToken.None);

        first.DaysExported.Should().Be(1);
        second.DaysExported.Should().Be(0);
        scans.Should().Be(0);
        (await CountTradesAsync()).Should().Be(1);
        client.Files.Should().HaveCount(filesAfterFirst);
        client.Creates.Should().Be(filesAfterFirst);
        (await _database.TapeExportReceipts.CountAsync(row => row.Day == _oldDay && row.Kind == "trades"))
            .Should().Be(2);
        (await _database.TapeExportReceipts.CountAsync(row => row.UploadState == TapeExportReceipt.StateUploaded))
            .Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Export_WhenALaterUploadFails_KeepsTheLastDriveFileId()
    {
        await SeedTradeAsync(On(_oldDay, 14), 1, 5123.25m);
        using HostTelemetry telemetry = new();
        FakeTapeDriveClient client = new();
        await Pass(telemetry, new RetryingTapeDriveUploader(client, telemetry))
            .ExportAsync(null, CancellationToken.None);
        _database.ChangeTracker.Clear();
        TapeExportReceipt stored = await _database.TapeExportReceipts.SingleAsync(row =>
            row.Format == TapeExportArchive.CsvGzFormat && row.Kind == "trades");
        string fileId = stored.DriveFileId!;
        string path = Path.Combine(_destination, stored.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        byte[] bytes = await File.ReadAllBytesAsync(path);
        bytes[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(path, bytes);
        client.Permanent = true;

        TapeExportReport report = await Pass(telemetry, new RetryingTapeDriveUploader(client, telemetry))
            .ExportAsync(null, CancellationToken.None);

        report.DaysExported.Should().Be(0);
        _database.ChangeTracker.Clear();
        TapeExportReceipt after = await _database.TapeExportReceipts.SingleAsync(row =>
            row.Format == TapeExportArchive.CsvGzFormat && row.Kind == "trades");
        after.DriveFileId.Should().Be(fileId);
        after.UploadState.Should().Be(TapeExportReceipt.StateFailed);
        after.FailureType.Should().Be("GoogleApiException");
        after.FailureStatus.Should().Be(403);
        (await CountTradesAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Export_WhenDriveIsOn_RecordsPendingBeforeTheUploadCompletes()
    {
        await SeedTradeAsync(On(_oldDay, 14), 1, 5123.25m);
        ObservingUploader uploader = new(_database);

        await Pass(new HostTelemetry(), uploader).ExportAsync(null, CancellationToken.None);

        uploader.Seen.Should().Be(TapeExportReceipt.StatePending);
        _database.ChangeTracker.Clear();
        TapeExportReceipt receipt = await _database.TapeExportReceipts.SingleAsync(row =>
            row.Format == TapeExportArchive.CsvGzFormat && row.Kind == "trades");
        receipt.UploadState.Should().Be(TapeExportReceipt.StateUploaded);
        receipt.DriveFileId.Should().Be("observed-file");
    }

    [Fact]
    public async Task Export_WhenTwoConnectionsRaceOneIdentity_CreatesOneDriveFile()
    {
        await SeedTradeAsync(On(_oldDay, 14), 1, 5123.25m);
        string left = Path.Combine(_destination, "left");
        string right = Path.Combine(_destination, "right");
        Directory.CreateDirectory(left);
        Directory.CreateDirectory(right);
        RacingDriveClient client = new(_fixture.ConnectionString, new FakeTimeProvider(DateTimeOffset.UtcNow));
        using HostTelemetry telemetry = new();
        RetryingTapeDriveUploader uploader = new(client, telemetry);
        await using TopstepXDbContext other = _fixture.CreateContext();

        await Task.WhenAll(
            Pass(telemetry, uploader, path: left).ExportAsync(null, CancellationToken.None),
            Pass(telemetry, uploader, other, right).ExportAsync(null, CancellationToken.None));

        client.CreatesPerIdentity.Should().NotBeEmpty();
        client.CreatesPerIdentity.Values.Should().OnlyContain(count => count == 1);
    }

    [Fact]
    public async Task Export_WhenTheDeadlinePassesDuringBackoff_RecordsTimeoutAndContinues()
    {
        await SeedTradeAsync(On(_oldDay, 14), 1, 5123.25m);
        FakeTimeProvider clock = new(_now);
        BackoffThenStopClient client = new();
        TapeDriveRetryTiming timing = new()
        {
            Wait = async (delay, waitToken) =>
            {
                clock.Advance(TapeDriveRetryTiming.Deadline);
                await Task.Delay(delay, clock, waitToken);
            },
        };
        using HostTelemetry telemetry = new();
        using MetricCollector<long> uploads =
            new(telemetry, HostTelemetry.Name, HostTelemetry.TapeUploadsInstrument);
        List<Activity> spans = [];
        using ActivityListener listener = Listen(spans, telemetry.Activities);

        TapeExportReport report = await Pass(
                telemetry,
                new RetryingTapeDriveUploader(client, telemetry, NullLogger.Instance, timing, clock))
            .ExportAsync(null, CancellationToken.None);

        report.DaysRefused.Should().Be(1);
        AssertUpload(
            uploads.GetMeasurementSnapshot(),
            spans,
            TapeUploadReason.Timeout,
            TapeUploadOperation.Upload,
            TapeUploadOutcome.Failed,
            ActivityStatusCode.Error);
        _database.ChangeTracker.Clear();
        TapeExportReceipt timedOut = await _database.TapeExportReceipts.SingleAsync(row =>
            row.Kind == "trades" && row.Format == TapeExportArchive.CsvGzFormat);
        timedOut.UploadState.Should().Be(TapeExportReceipt.StateFailed);
        timedOut.UploadState.Should().NotBe(TapeExportReceipt.StatePending);
        timedOut.FailureType.Should().Be("Timeout");
        TapeExportReceipt parquet = await _database.TapeExportReceipts.SingleAsync(row =>
            row.Kind == "trades" && row.Format == TapeExportArchive.ParquetFormat);
        parquet.UploadState.Should().Be(TapeExportReceipt.StateUploaded);
        client.Formats.Should().Contain("trades:" + TapeExportArchive.ParquetFormat);
    }

    [Fact]
    public async Task Export_WhenDriveIsOn_ReleasesTheLockConnection()
    {
        await SeedTradeAsync(On(_oldDay, 14), 1, 5123.25m);
        LockPidUploader uploader = new(_fixture.ConnectionString);

        await Pass(new HostTelemetry(), uploader).ExportAsync(null, CancellationToken.None);

        uploader.LockPid.Should().BeGreaterThan(0);
        await using NpgsqlConnection check = new(_fixture.ConnectionString);
        await check.OpenAsync();
        await using NpgsqlCommand command = new("SELECT COUNT(*) FROM pg_stat_activity WHERE pid = @pid", check);
        command.Parameters.AddWithValue("pid", uploader.LockPid);
        Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture).Should().Be(0);
    }

    [Fact]
    public async Task Export_WhenTheLockConnectionDrops_RecordsLockLostAndContinues()
    {
        await SeedTradeAsync(On(_oldDay, 14), 1, 5123.25m);
        DroppingUploader uploader = new(_fixture.ConnectionString);
        using HostTelemetry telemetry = new();
        using MetricCollector<long> uploads =
            new(telemetry, HostTelemetry.Name, HostTelemetry.TapeUploadsInstrument);
        List<Activity> spans = [];
        using ActivityListener listener = Listen(spans, telemetry.Activities);

        TapeExportReport report = await Pass(telemetry, uploader)
            .ExportAsync(null, CancellationToken.None);

        report.DaysRefused.Should().Be(1);
        AssertUpload(
            uploads.GetMeasurementSnapshot(),
            spans,
            TapeUploadReason.LockLost,
            TapeUploadOperation.Lock,
            TapeUploadOutcome.Failed,
            ActivityStatusCode.Error);
        uploads.GetMeasurementSnapshot().Count(measurement =>
            (string)measurement.Tags[HostTelemetry.OutcomeTag]! == TapeUploadOutcome.Uploaded)
            .Should().Be(await UploadedReceiptsAsync());
        _database.ChangeTracker.Clear();
        TapeExportReceipt dropped = await _database.TapeExportReceipts.SingleAsync(row =>
            row.Kind == "trades" && row.Format == TapeExportArchive.CsvGzFormat);
        dropped.UploadState.Should().Be(TapeExportReceipt.StateFailed);
        dropped.FailureType.Should().Be("LockLost");
        dropped.DriveFileId.Should().Be("dropped-file");
        TapeExportReceipt next = await _database.TapeExportReceipts.SingleAsync(row =>
            row.Kind == "trades" && row.Format == TapeExportArchive.ParquetFormat);
        next.UploadState.Should().Be(TapeExportReceipt.StateUploaded);
        uploader.Seen.Should().Contain("trades:" + TapeExportArchive.ParquetFormat);
    }

    [Fact]
    public async Task Export_WhenTheIdentityLockIsHeld_RecordsIdentityBusyAndContinues()
    {
        await SeedTradeAsync(On(_oldDay, 14), 1, 5123.25m);
        string key = new TapeDriveIdentity(
            _oldDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "trades",
            TapeExportArchive.CsvGzFormat,
            Venue,
            "ES",
            Contract,
            0).Canonical();
        await using NpgsqlConnection holder = new(_fixture.ConnectionString);
        await holder.OpenAsync();
        await using NpgsqlCommand take = new("SELECT pg_advisory_lock(hashtextextended(@key, 0))", holder);
        take.Parameters.AddWithValue("key", key);
        await take.ExecuteScalarAsync();
        try
        {
            CountingUploader uploader = new();
            using HostTelemetry telemetry = new();
            using MetricCollector<long> uploads =
                new(telemetry, HostTelemetry.Name, HostTelemetry.TapeUploadsInstrument);
            List<Activity> spans = [];
            using ActivityListener listener = Listen(spans, telemetry.Activities);

            TapeExportReport report = await Pass(telemetry, uploader)
                .ExportAsync(null, CancellationToken.None);

            report.DaysRefused.Should().Be(1);
            AssertUpload(
                uploads.GetMeasurementSnapshot(),
                spans,
                TapeUploadReason.IdentityBusy,
                TapeUploadOperation.Lock,
                TapeUploadOutcome.Failed,
                ActivityStatusCode.Error);
            _database.ChangeTracker.Clear();
            TapeExportReceipt busy = await _database.TapeExportReceipts.SingleAsync(row =>
                row.Kind == "trades" && row.Format == TapeExportArchive.CsvGzFormat);
            busy.UploadState.Should().Be(TapeExportReceipt.StateFailed);
            busy.UploadState.Should().NotBe(TapeExportReceipt.StatePending);
            busy.FailureType.Should().Be("IdentityBusy");
            TapeExportReceipt next = await _database.TapeExportReceipts.SingleAsync(row =>
                row.Kind == "trades" && row.Format == TapeExportArchive.ParquetFormat);
            next.UploadState.Should().Be(TapeExportReceipt.StateUploaded);
        }
        finally
        {
            await using NpgsqlCommand release = new("SELECT pg_advisory_unlock(hashtextextended(@key, 0))", holder);
            release.Parameters.AddWithValue("key", key);
            await release.ExecuteScalarAsync();
        }
    }

    [Fact]
    public async Task Export_WhenAPendingReceiptAlreadyHasADriveFile_UpdatesItAndReportsTheOrphan()
    {
        await SeedTradeAsync(On(_oldDay, 14), 1, 5123.25m);
        string key = new TapeDriveIdentity(
            _oldDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "trades",
            TapeExportArchive.CsvGzFormat,
            Venue,
            "ES",
            Contract,
            0).Canonical();
        PropertyClient client = new();
        client.Existing[key] = "already-there";
        _database.TapeExportReceipts.Add(new TapeExportReceipt
        {
            Day = _oldDay,
            Kind = "trades",
            Format = TapeExportArchive.CsvGzFormat,
            Venue = Venue,
            Instrument = "ES",
            ContractId = Contract,
            ResolutionMinutes = 0,
            RelativePath = "trades/test/ES/" + Contract + "/2026-09-01.v1.csv.gz",
            Sha256 = "abc",
            RowCount = 1,
            ExportedAtUtc = _now,
            DriveFileId = null,
            UploadState = TapeExportReceipt.StatePending,
        });
        await _database.SaveChangesAsync();
        _database.ChangeTracker.Clear();
        using HostTelemetry telemetry = new();
        using MetricCollector<long> uploads =
            new(telemetry, HostTelemetry.Name, HostTelemetry.TapeUploadsInstrument);
        CollectingLogger logger = new();

        await Pass(telemetry, new DirectUploader(client), logger: logger)
            .ExportAsync(null, CancellationToken.None);

        _database.ChangeTracker.Clear();
        TapeExportReceipt receipt = await _database.TapeExportReceipts.SingleAsync(row =>
            row.Kind == "trades" && row.Format == TapeExportArchive.CsvGzFormat);
        receipt.UploadState.Should().Be(TapeExportReceipt.StateUploaded);
        receipt.DriveFileId.Should().Be("already-there");
        client.Updates.Should().BeGreaterThan(0);
        client.CreatedKeys.Should().NotContain(key);
        uploads.GetMeasurementSnapshot().Should().Contain(measurement =>
            (string)measurement.Tags[HostTelemetry.OutcomeTag]! == TapeUploadOutcome.OrphanPending
            && measurement.Value >= 1);
        logger.Lines.Should().Contain(line => line.Contains("1", StringComparison.Ordinal)
            && line.Contains("pending", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Export_WhenDriveIsEnabledLater_UploadsADisabledDay()
    {
        await SeedTradeAsync(On(_oldDay, 14), 1, 5123.25m);
        using HostTelemetry telemetry = new();

        TapeExportReport disabled = await Pass(telemetry).ExportAsync(null, CancellationToken.None);
        TapeExportReport stillOff = await Pass(telemetry).ExportAsync(null, CancellationToken.None);
        _database.ChangeTracker.Clear();
        FakeTapeDriveClient client = new();
        TapeExportReport enabled = await Pass(telemetry, new RetryingTapeDriveUploader(client, telemetry))
            .ExportAsync(null, CancellationToken.None);

        disabled.DaysExported.Should().Be(1);
        stillOff.DaysExported.Should().Be(0);
        enabled.DaysExported.Should().Be(1);
        _database.ChangeTracker.Clear();
        TapeExportReceipt receipt = await _database.TapeExportReceipts.SingleAsync(row =>
            row.Kind == "trades" && row.Format == TapeExportArchive.CsvGzFormat);
        receipt.UploadState.Should().Be(TapeExportReceipt.StateUploaded);
        client.Creates.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Export_WhenTheLockDropsAfterTheBytesLand_CountsLockLostNotUploaded()
    {
        await SeedTradeAsync(On(_oldDay, 14), 1, 5123.25m);
        using HostTelemetry telemetry = new();
        using MetricCollector<long> uploads =
            new(telemetry, HostTelemetry.Name, HostTelemetry.TapeUploadsInstrument);
        List<Activity> spans = [];
        using ActivityListener listener = Listen(spans, telemetry.Activities);
        KillingClient client = new(_fixture.ConnectionString);

        await Pass(
                telemetry,
                new RetryingTapeDriveUploader(client, telemetry, NullLogger.Instance, new TapeDriveRetryTiming()))
            .ExportAsync(null, CancellationToken.None);

        _database.ChangeTracker.Clear();
        TapeExportReceipt dropped = await _database.TapeExportReceipts.SingleAsync(row =>
            row.Kind == "trades" && row.Format == TapeExportArchive.CsvGzFormat);
        dropped.UploadState.Should().Be(TapeExportReceipt.StateFailed);
        dropped.FailureType.Should().Be("LockLost");
        dropped.DriveFileId.Should().NotBeNullOrEmpty();
        AssertUpload(
            uploads.GetMeasurementSnapshot(),
            spans,
            TapeUploadReason.LockLost,
            TapeUploadOperation.Lock,
            TapeUploadOutcome.Failed,
            ActivityStatusCode.Error);
        uploads.GetMeasurementSnapshot().Count(measurement =>
            (string)measurement.Tags[HostTelemetry.OutcomeTag]! == TapeUploadOutcome.Uploaded)
            .Should().Be(await UploadedReceiptsAsync());
    }

    [Fact]
    public async Task Export_WhenDriveFailsAndTheLockProbeFails_CountsLockLostOnce()
    {
        await SeedTradeAsync(On(_oldDay, 14), 1, 5123.25m);
        using HostTelemetry telemetry = new();
        using MetricCollector<long> uploads =
            new(telemetry, HostTelemetry.Name, HostTelemetry.TapeUploadsInstrument);
        List<Activity> spans = [];
        using ActivityListener listener = Listen(spans, telemetry.Activities);

        await Pass(
                telemetry,
                new RetryingTapeDriveUploader(
                    new FailingAndKillingClient(_fixture.ConnectionString),
                    telemetry,
                    NullLogger.Instance,
                    new TapeDriveRetryTiming()))
            .ExportAsync(null, CancellationToken.None);

        _database.ChangeTracker.Clear();
        TapeExportReceipt dropped = await _database.TapeExportReceipts.SingleAsync(row =>
            row.Kind == "trades" && row.Format == TapeExportArchive.CsvGzFormat);
        dropped.UploadState.Should().Be(TapeExportReceipt.StateFailed);
        dropped.FailureType.Should().Be("LockLost");

        List<CollectedMeasurement<long>> terminal = uploads.GetMeasurementSnapshot()
            .Where(measurement => (string)measurement.Tags[HostTelemetry.ReasonTag]! is TapeUploadReason.Failed
                or TapeUploadReason.Permanent
                or TapeUploadReason.Timeout
                or TapeUploadReason.LockLost)
            .ToList();
        CollectedMeasurement<long> measurement = terminal.Should().ContainSingle().Subject;
        measurement.Value.Should().Be(1);
        measurement.Tags[HostTelemetry.OutcomeTag].Should().Be(TapeUploadOutcome.Failed);
        measurement.Tags[HostTelemetry.OperationTag].Should().Be(TapeUploadOperation.Lock);
        measurement.Tags[HostTelemetry.ReasonTag].Should().Be(TapeUploadReason.LockLost);

        Activity span = spans
            .Where(candidate => candidate.OperationName == "tape.upload"
                && candidate.GetTagItem(HostTelemetry.ReasonTag) is string reason
                && reason is TapeUploadReason.Failed
                    or TapeUploadReason.Permanent
                    or TapeUploadReason.Timeout
                    or TapeUploadReason.LockLost)
            .Should().ContainSingle().Subject;
        span.Status.Should().Be(ActivityStatusCode.Error);
        span.GetTagItem(HostTelemetry.OutcomeTag).Should().Be(TapeUploadOutcome.Failed);
        span.GetTagItem(HostTelemetry.OperationTag).Should().Be(TapeUploadOperation.Lock);
        span.GetTagItem(HostTelemetry.ReasonTag).Should().Be(TapeUploadReason.LockLost);

        uploads.GetMeasurementSnapshot().Count(row =>
            (string)row.Tags[HostTelemetry.OutcomeTag]! == TapeUploadOutcome.Uploaded)
            .Should().Be(await UploadedReceiptsAsync());
    }

    [Fact]
    public async Task Export_WhenTwoDaysUpload_UsesOneLockConnection()
    {
        await SeedTradeAsync(On(_oldDay, 14), 1, 5123.25m);
        await SeedTradeAsync(On(_keptDay, 11), 2, 5100m);
        LockPidUploader uploader = new(_fixture.ConnectionString);

        await Pass(new HostTelemetry(), uploader).ExportAsync(null, CancellationToken.None);

        uploader.Pids.Should().NotBeEmpty();
        uploader.Pids.Distinct().Should().ContainSingle();
    }

    [Fact]
    public async Task Export_WhenTheNextFileStarts_HasReleasedThePreviousLock()
    {
        await SeedTradeAsync(On(_oldDay, 14), 1, 5123.25m);
        UnlockProbeUploader uploader = new(_fixture.ConnectionString);

        await Pass(new HostTelemetry(), uploader).ExportAsync(null, CancellationToken.None);

        uploader.FirstKey.Should().NotBeNullOrEmpty();
        uploader.FirstKeyStillHeld.Should().Be(0);
    }

    private async Task<int> UploadedReceiptsAsync()
    {
        _database.ChangeTracker.Clear();
        return await _database.TapeExportReceipts.AsNoTracking()
            .CountAsync(row => row.UploadState == TapeExportReceipt.StateUploaded);
    }

    private static void AssertUpload(
        IReadOnlyList<CollectedMeasurement<long>> measurements,
        IReadOnlyList<Activity> spans,
        string reason,
        string operation,
        string outcome,
        ActivityStatusCode status)
    {
        measurements.Should().Contain(measurement =>
            (string)measurement.Tags[HostTelemetry.ReasonTag]! == reason
            && (string)measurement.Tags[HostTelemetry.OperationTag]! == operation
            && (string)measurement.Tags[HostTelemetry.OutcomeTag]! == outcome);
        spans.Should().Contain(span =>
            span.OperationName == "tape.upload"
            && span.Status == status
            && (string?)span.GetTagItem(HostTelemetry.ReasonTag) == reason
            && (string?)span.GetTagItem(HostTelemetry.OperationTag) == operation);
    }

    private static ActivityListener Listen(List<Activity> into, ActivitySource source)
    {
        ActivityListener listener = new()
        {
            ShouldListenTo = candidate => ReferenceEquals(candidate, source),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = into.Add,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private TapeExportPass Pass(
        HostTelemetry telemetry,
        ITapeDriveUploader? drive = null,
        TopstepXDbContext? database = null,
        string? path = null,
        ILogger<TapeExportPass>? logger = null) =>
        new(
            database ?? _database,
            Options.Create(new TapeExportOptions
            {
                Enabled = true,
                Path = path ?? _destination,
                Schedule = TapeExportOptions.DefaultSchedule,
                Format = TapeExportOptions.DefaultFormat,
            }),
            new FakeTimeProvider(_now),
            telemetry,
            drive ?? new DisabledTapeDriveUploader(),
            logger ?? NullLogger<TapeExportPass>.Instance);

    private async Task SeedTradeAsync(
        DateTimeOffset when,
        long sequence,
        decimal price,
        TradeDirection direction = TradeDirection.Buy)
    {
        _database.Trades.Add(new TradeRecord
        {
            Venue = Venue,
            Instrument = "ES",
            ContractId = Contract,
            TradeTimeUtc = when,
            Sequence = sequence,
            Price = price,
            Size = 2,
            Direction = direction,
            RecordedAt = when,
        });
        await _database.SaveChangesAsync();
        _database.ChangeTracker.Clear();
    }

    private async Task SeedCoverageAsync(DateTimeOffset start, DateTimeOffset end)
    {
        _database.TapeCoverage.Add(new TapeCoverageRecord
        {
            Venue = Venue,
            Instrument = "ES",
            ContractId = Contract,
            RangeStart = start,
            RangeEnd = end,
            RecordedAt = start,
        });
        await _database.SaveChangesAsync();
        _database.ChangeTracker.Clear();
    }

    private Task SeedOpenCoverageAsync(DateTimeOffset start) =>
        SeedCoverageAsync(start, TapeCoverageRecord.StillListeningEnd);

    private async Task SeedCellAsync(DateTimeOffset bucket, decimal price)
    {
        _database.FootprintCells.Add(new FootprintCellRecord
        {
            Venue = Venue,
            Instrument = "ES",
            ResolutionMinutes = 5,
            BucketStart = bucket,
            Price = price,
            BuyVolume = 3,
            SellVolume = 1,
            RecordedAt = bucket,
        });
        await _database.SaveChangesAsync();
        _database.ChangeTracker.Clear();
    }

    private Task<int> CountTradesAsync() => _database.Trades.CountAsync();

    private static DateTimeOffset On(DateOnly day, int hour, int minute = 0) =>
        new(day.Year, day.Month, day.Day, hour, minute, 0, TimeSpan.Zero);

    private static string Gunzip(byte[] gzip)
    {
        using MemoryStream stream = new(gzip);
        using GZipStream decompress = new(stream, CompressionMode.Decompress);
        using StreamReader reader = new(decompress, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private sealed class PriceScanInterceptor(Action onScan) : DbCommandInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Note(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken)
        {
            Note(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Note(DbCommand command)
        {
            if (command.CommandText.Contains("\"Price\"", StringComparison.Ordinal)
                && command.CommandText.Contains("\"Trades\"", StringComparison.Ordinal))
            {
                onScan();
            }
        }
    }

    private sealed class ObservingUploader(TopstepXDbContext database) : ITapeDriveUploader
    {
        public bool IsEnabled => true;

        public string? Seen { get; private set; }

        public async Task<TapeDriveUploadResult> UploadAsync(TapeDriveUpload upload, CancellationToken cancellationToken)
        {
            Seen = await database.TapeExportReceipts.AsNoTracking()
                .Where(row => row.Kind == upload.Identity.Kind && row.Format == upload.Identity.Format)
                .Select(row => row.UploadState)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            return new TapeDriveUploadResult(TapeDriveUploadStatus.Uploaded, "observed-file", 1, null, null);
        }
    }

    private sealed class FakeTapeDriveClient : ITapeDriveClient
    {
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _ids = new(StringComparer.Ordinal);
        private int _attempts;
        private int _creates;

        public int FailuresBeforeSuccess { get; init; }

        public bool Permanent { get; set; }

        public int Attempts => _attempts;

        public int Creates => _creates;

        public ConcurrentDictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

        public async Task<string> UploadAsync(TapeDriveUpload upload, CancellationToken cancellationToken)
        {
            string key = upload.Identity.Canonical();
            SemaphoreSlim gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                int attempt = Interlocked.Increment(ref _attempts);
                if (Permanent)
                {
                    throw new TapeDriveException(
                        "Drive request failed.",
                        403,
                        permanent: true,
                        failureType: "GoogleApiException");
                }

                if (attempt <= FailuresBeforeSuccess)
                {
                    throw new TapeDriveException(
                        "Drive request failed.",
                        503,
                        permanent: false,
                        failureType: "IOException");
                }

                await using Stream content = upload.Open();
                using MemoryStream buffer = new();
                await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (_ids.TryGetValue(key, out string? existing))
                {
                    Files[existing] = buffer.ToArray();
                    return existing;
                }

                string created = "id-" + Interlocked.Increment(ref _creates).ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!_ids.TryAdd(key, created))
                {
                    return _ids[key];
                }

                Files[created] = buffer.ToArray();
                return created;
            }
            finally
            {
                gate.Release();
            }
        }
    }

    /// <summary>
    /// No in-process lock. The first create waits until a second caller is inside or a session is blocked
    /// on this identity's advisory lock, so removing <c>pg_advisory_lock</c> lets both creates land.
    /// The wait bound is the injected clock. The 20ms pause is real so the other session can block.
    /// </summary>
    private sealed class RacingDriveClient(string connectionString, TimeProvider clock) : ITapeDriveClient
    {
        private readonly ConcurrentDictionary<string, int> _inside = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, int> _creates = new(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, int> CreatesPerIdentity => _creates;

        public async Task<string> UploadAsync(TapeDriveUpload upload, CancellationToken cancellationToken)
        {
            string key = upload.Identity.Canonical();
            if (!string.IsNullOrEmpty(upload.KnownFileId))
            {
                await using Stream ignored = upload.Open();
                return upload.KnownFileId;
            }

            int ticket = _inside.AddOrUpdate(key, 1, (_, count) => count + 1);
            if (ticket == 1)
            {
                DateTimeOffset started = clock.GetUtcNow();
                while (clock.GetUtcNow() - started < TimeSpan.FromSeconds(8)
                    && _inside.GetValueOrDefault(key) < 2
                    && !await AdvisoryWaiterAsync(key, cancellationToken).ConfigureAwait(false))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken).ConfigureAwait(false);
                    if (clock is FakeTimeProvider fake)
                    {
                        fake.Advance(TimeSpan.FromMilliseconds(20));
                    }
                }
            }

            int created = _creates.AddOrUpdate(key, 1, (_, count) => count + 1);
            await using Stream content = upload.Open();
            return "race-" + created.ToString(CultureInfo.InvariantCulture);
        }

        private async Task<bool> AdvisoryWaiterAsync(string key, CancellationToken cancellationToken)
        {
            await using NpgsqlConnection connection = new(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using NpgsqlCommand command = new(
                """
                SELECT COUNT(*)
                FROM pg_locks
                WHERE locktype = 'advisory'
                  AND NOT granted
                  AND objsubid = 1
                  AND database = (SELECT oid FROM pg_database WHERE datname = current_database())
                  AND classid::bigint = ((hashtextextended(@key, 0) >> 32) & 4294967295)
                  AND objid::bigint = (hashtextextended(@key, 0) & 4294967295)
                """,
                connection);
            command.Parameters.AddWithValue("key", key);
            object? scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return Convert.ToInt64(scalar, CultureInfo.InvariantCulture) > 0;
        }
    }

    private sealed class BackoffThenStopClient : ITapeDriveClient
    {
        public List<string> Formats { get; } = [];

        public Task<string> UploadAsync(TapeDriveUpload upload, CancellationToken cancellationToken)
        {
            string format = upload.Identity.Kind + ":" + upload.Identity.Format;
            Formats.Add(format);
            if (upload.Identity.Kind == "trades" && upload.Identity.Format == TapeExportArchive.CsvGzFormat)
            {
                throw new TapeDriveException("Drive request failed.", 503, permanent: false, failureType: "IOException");
            }

            return Task.FromResult("id-" + format);
        }
    }

    private sealed class LockPidUploader(string connectionString) : ITapeDriveUploader
    {
        public bool IsEnabled => true;

        public int LockPid { get; private set; }

        public List<int> Pids { get; } = [];

        public async Task<TapeDriveUploadResult> UploadAsync(TapeDriveUpload upload, CancellationToken cancellationToken)
        {
            await using NpgsqlConnection connection = new(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using NpgsqlCommand command = new(
                """
                SELECT pid FROM pg_stat_activity
                WHERE application_name = 'tape-export-lock' AND pid <> pg_backend_pid()
                LIMIT 1
                """,
                connection);
            object? scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (scalar is not null and not DBNull)
            {
                LockPid = Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
                Pids.Add(LockPid);
            }

            return new TapeDriveUploadResult(TapeDriveUploadStatus.Uploaded, "locked-file", 1, null, null);
        }
    }

    private sealed class UnlockProbeUploader(string connectionString) : ITapeDriveUploader
    {
        public bool IsEnabled => true;

        public string? FirstKey { get; private set; }

        public long FirstKeyStillHeld { get; private set; } = -1;

        public async Task<TapeDriveUploadResult> UploadAsync(TapeDriveUpload upload, CancellationToken cancellationToken)
        {
            if (FirstKey is null)
            {
                FirstKey = upload.Identity.Canonical();
            }
            else if (FirstKeyStillHeld < 0)
            {
                await using NpgsqlConnection connection = new(connectionString);
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using NpgsqlCommand command = new(
                    """
                    SELECT COUNT(*)
                    FROM pg_locks
                    WHERE locktype = 'advisory'
                      AND granted
                      AND objsubid = 1
                      AND database = (SELECT oid FROM pg_database WHERE datname = current_database())
                      AND classid::bigint = ((hashtextextended(@key, 0) >> 32) & 4294967295)
                      AND objid::bigint = (hashtextextended(@key, 0) & 4294967295)
                    """,
                    connection);
                command.Parameters.AddWithValue("key", FirstKey);
                object? scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                FirstKeyStillHeld = Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
            }

            return new TapeDriveUploadResult(TapeDriveUploadStatus.Uploaded, "unlocked-file", 1, null, null);
        }
    }

    private sealed class KillingClient(string connectionString) : ITapeDriveClient
    {
        private int _calls;

        public async Task<string> UploadAsync(TapeDriveUpload upload, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                await using NpgsqlConnection connection = new(connectionString);
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using NpgsqlCommand command = new(
                    """
                    SELECT pg_terminate_backend(pid)
                    FROM pg_stat_activity
                    WHERE application_name = 'tape-export-lock' AND pid <> pg_backend_pid()
                    """,
                    connection);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            return "killed-" + upload.Identity.Kind + "-" + upload.Identity.Format;
        }
    }

    /// <summary>
    /// The first identity fails in Drive and the lock session is killed before that failure returns,
    /// so the probe fails too. Later identities upload. One file must not be counted twice.
    /// </summary>
    private sealed class FailingAndKillingClient(string connectionString) : ITapeDriveClient
    {
        private int _calls;

        public async Task<string> UploadAsync(TapeDriveUpload upload, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                await using NpgsqlConnection connection = new(connectionString);
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using NpgsqlCommand command = new(
                    """
                    SELECT pg_terminate_backend(pid)
                    FROM pg_stat_activity
                    WHERE application_name = 'tape-export-lock' AND pid <> pg_backend_pid()
                    """,
                    connection);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                throw new TapeDriveException(
                    "Drive request failed.",
                    403,
                    permanent: true,
                    failureType: "GoogleApiException");
            }

            return "kept-" + upload.Identity.Kind + "-" + upload.Identity.Format;
        }
    }

    private sealed class DroppingUploader(string connectionString) : ITapeDriveUploader
    {
        public bool IsEnabled => true;

        public List<string> Seen { get; } = [];

        public async Task<TapeDriveUploadResult> UploadAsync(TapeDriveUpload upload, CancellationToken cancellationToken)
        {
            string name = upload.Identity.Kind + ":" + upload.Identity.Format;
            Seen.Add(name);
            if (Seen.Count == 1)
            {
                await using NpgsqlConnection connection = new(connectionString);
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using NpgsqlCommand command = new(
                    """
                    SELECT pg_terminate_backend(pid)
                    FROM pg_stat_activity
                    WHERE application_name = 'tape-export-lock' AND pid <> pg_backend_pid()
                    """,
                    connection);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                return new TapeDriveUploadResult(TapeDriveUploadStatus.Uploaded, "dropped-file", 1, null, null);
            }

            return new TapeDriveUploadResult(TapeDriveUploadStatus.Uploaded, "kept-file", 1, null, null);
        }
    }

    private sealed class CountingUploader : ITapeDriveUploader
    {
        public bool IsEnabled => true;

        public Task<TapeDriveUploadResult> UploadAsync(TapeDriveUpload upload, CancellationToken cancellationToken) =>
            Task.FromResult(new TapeDriveUploadResult(
                TapeDriveUploadStatus.Uploaded,
                "busy-" + upload.Identity.Format,
                1,
                null,
                null));
    }

    private sealed class PropertyClient : ITapeDriveClient
    {
        public Dictionary<string, string> Existing { get; } = new(StringComparer.Ordinal);

        public int Updates { get; private set; }

        public List<string> CreatedKeys { get; } = [];

        public Task<string> UploadAsync(TapeDriveUpload upload, CancellationToken cancellationToken)
        {
            if (!string.IsNullOrEmpty(upload.KnownFileId))
            {
                Updates++;
                return Task.FromResult(upload.KnownFileId);
            }

            string key = upload.Identity.Canonical();
            if (Existing.TryGetValue(key, out string? id))
            {
                Updates++;
                return Task.FromResult(id);
            }

            CreatedKeys.Add(key);
            string created = "created-" + CreatedKeys.Count.ToString(CultureInfo.InvariantCulture);
            Existing[key] = created;
            return Task.FromResult(created);
        }
    }

    private sealed class DirectUploader(ITapeDriveClient client) : ITapeDriveUploader
    {
        public bool IsEnabled => true;

        public async Task<TapeDriveUploadResult> UploadAsync(TapeDriveUpload upload, CancellationToken cancellationToken)
        {
            string id = await client.UploadAsync(upload, cancellationToken).ConfigureAwait(false);
            return new TapeDriveUploadResult(TapeDriveUploadStatus.Uploaded, id, 1, null, null);
        }
    }

    private sealed class CollectingLogger : ILogger<TapeExportPass>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }
}
