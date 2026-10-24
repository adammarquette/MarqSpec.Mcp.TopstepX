using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>One export or import of recorded tape. The worker and the CLI verbs both call this.</summary>
public interface ITapeExportPass
{
    /// <summary>Writes every finished UTC day. Local rows are not deleted.</summary>
    /// <param name="onlyDay">When set, only that day. A day with no local rows is left on disk.</param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>The counts.</returns>
    Task<TapeExportReport> ExportAsync(DateOnly? onlyDay, CancellationToken cancellationToken);

    /// <summary>
    /// Inserts rows from verified manifests. A day that does not verify is not inserted.
    /// </summary>
    /// <param name="onlyDay">When set, only that day.</param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>The counts.</returns>
    Task<TapeExportReport> ImportAsync(DateOnly? onlyDay, CancellationToken cancellationToken);
}

/// <summary>
/// Copies <c>Trades</c>, closed <c>TapeCoverage</c> and <c>FootprintCells</c> to the volume. Local
/// rows stay (gh#755, <c>R-10</c>).
/// </summary>
/// <remarks>
/// <para>
/// A day is a candidate only when some of its rows have no matching receipt, or a receipt's file
/// hash no longer matches. Finished days are not read back. Each contract is streamed into the gzip
/// and the Parquet row groups. The manifest is renamed into place after those files. Nothing deletes
/// a local row, a generation, or a Drive file.
/// </para>
/// <para>
/// A UTC day that has not ended is not archived. A day that an open listen still overlaps is not
/// archived either. The receipt for a failed upload keeps the last Drive file id.
/// </para>
/// </remarks>
public sealed class TapeExportPass(
    TopstepXDbContext database,
    IOptions<TapeExportOptions> options,
    TimeProvider clock,
    HostTelemetry telemetry,
    ITapeDriveUploader drive,
    ILogger<TapeExportPass> logger) : ITapeExportPass
{
    private readonly TopstepXDbContext _database = database;
    private readonly TapeExportOptions _options = options.Value;
    private readonly TimeProvider _clock = clock;
    private readonly HostTelemetry _telemetry = telemetry;
    private readonly ITapeDriveUploader _drive = drive;
    private readonly ILogger<TapeExportPass> _logger = logger;

    /// <inheritdoc />
    public Task<TapeExportReport> ExportAsync(DateOnly? onlyDay, CancellationToken cancellationToken) =>
        RunAsync(token => ExportInsideAsync(onlyDay, token), cancellationToken);

    /// <inheritdoc />
    public Task<TapeExportReport> ImportAsync(DateOnly? onlyDay, CancellationToken cancellationToken) =>
        RunAsync(token => ImportInsideAsync(onlyDay, token), cancellationToken);

    private async Task<TapeExportReport> RunAsync(
        Func<CancellationToken, Task<TapeExportReport>> body,
        CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            EnsureValid();
            return await WithLockAsync(body, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _telemetry.TapeExportDuration(Stopwatch.GetElapsedTime(started).TotalSeconds);
        }
    }

    private async Task<TapeExportReport> WithLockAsync(
        Func<CancellationToken, Task<TapeExportReport>> body,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.Path);
        string lockPath = System.IO.Path.Combine(_options.Path, TapeExportArchive.LockFileName);
        FileStream? stream = null;
        try
        {
            // FileShare.None is the exclusion. FileStream.Lock is a byte-range lock the analyzer
            // rejects on macOS (CA1416), and a second handle in this process would not block on it
            // under fcntl. Sharing none fails the open on Linux and macOS, including in-process.
            stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }

            _telemetry.TapeExport(TapeExportOutcome.LockHeld, 1);
            _logger.LogWarning(
                "Tape export skipped because {Path} is locked by another process. Local tape was not touched.",
                _options.Path);
            return Empty();
        }
        catch (UnauthorizedAccessException)
        {
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }

            _telemetry.TapeExport(TapeExportOutcome.LockHeld, 1);
            _logger.LogWarning(
                "Tape export skipped because {Path} is not writable. Local tape was not touched.",
                _options.Path);
            return Empty();
        }

        await using (stream)
        {
            return await body(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<TapeExportReport> ExportInsideAsync(DateOnly? onlyDay, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.GetUtcNow();
        List<DateOnly> days = await DiscoverAsync(cancellationToken).ConfigureAwait(false);
        if (onlyDay is DateOnly requested)
        {
            days = [.. days.Where(day => day == requested)];
        }

        int daysExported = 0;
        int daysRefused = 0;
        int rowsExported = 0;
        int skippedOpen = 0;
        int skippedReceipt = await CountFinishedDaysAsync(days, now, cancellationToken).ConfigureAwait(false);
        bool notedDriveDisabled = false;

        foreach (DateOnly day in days)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TapeExportPolicy.IsExportable(day, now) || await OpenRunCoversAsync(day, cancellationToken).ConfigureAwait(false))
            {
                skippedOpen++;
                continue;
            }

            using Activity? exportSpan = _telemetry.Activities.StartActivity("tape.export", ActivityKind.Internal);
            exportSpan?.SetTag("tape.day", day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

            TapeExportVerification written;
            try
            {
                TapeExportVerification existing = await TapeExportArchive.VerifyAsync(_options.Path, day, cancellationToken)
                    .ConfigureAwait(false);
                if (existing.Verified)
                {
                    written = existing;
                }
                else
                {
                    written = await StreamDayAsync(day, now, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                exportSpan?.SetStatus(ActivityStatusCode.Error);
                _telemetry.TapeExport(TapeExportOutcome.ExportFailed, 1);
                daysRefused++;
                _logger.LogError(
                    "Tape export of {Day} failed ({ExceptionType}). Local rows were left in place.",
                    day,
                    exception.GetType().Name);
                continue;
            }

            if (!written.Verified)
            {
                exportSpan?.SetStatus(ActivityStatusCode.Error);
                _telemetry.TapeExport(TapeExportOutcome.VerificationFailed, 1);
                daysRefused++;
                _logger.LogError(
                    "Tape export of {Day} did not verify ({Reason}). Local rows were left in place.",
                    day,
                    written.Reason);
                continue;
            }

            (bool uploadFailed, bool driveDisabled) = await UploadAndRecordAsync(day, written, now, cancellationToken)
                .ConfigureAwait(false);
            notedDriveDisabled |= driveDisabled;
            int rows = written.TradeRows + written.CoverageRows + written.FootprintRows;
            if (uploadFailed)
            {
                exportSpan?.SetStatus(ActivityStatusCode.Error);
                daysRefused++;
                _logger.LogError(
                    "Tape export of {Day} wrote the volume but a Drive upload failed. Local rows were left in place.",
                    day);
                continue;
            }

            daysExported++;
            rowsExported += rows;
        }

        if (skippedOpen > 0)
        {
            _telemetry.TapeExport(TapeExportOutcome.SkippedOpen, skippedOpen);
        }

        if (skippedReceipt > 0)
        {
            _telemetry.TapeExport(TapeExportOutcome.SkippedReceipt, skippedReceipt);
        }

        if (daysExported > 0)
        {
            _telemetry.TapeExport(TapeExportOutcome.DaysExported, daysExported);
            _telemetry.TapeExport(TapeExportOutcome.RowsExported, rowsExported);
        }

        if (notedDriveDisabled)
        {
            _telemetry.TapeUpload(TapeUploadOutcome.Disabled, 1);
        }

        _logger.LogInformation(
            "Tape export pass: {DaysExported} days exported, {RowsExported} rows written, "
            + "{DaysRefused} days refused. Local tape was not deleted.",
            daysExported,
            rowsExported,
            daysRefused);

        return new TapeExportReport(daysExported, daysRefused, rowsExported, 0);
    }

    private async Task<TapeExportVerification> StreamDayAsync(
        DateOnly day,
        DateTimeOffset exportedAtUtc,
        CancellationToken cancellationToken)
    {
        (DateTimeOffset start, DateTimeOffset end) = Bounds(day);
        int generation = TapeExportArchive.AllocateGeneration(_options.Path, day);
        List<TapeExportStoredFile> files = [];

        await using var transaction = await _database.Database
            .BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            List<SeriesKey> tradeKeys = await _database.Trades
                .AsNoTracking()
                .Where(row => row.TradeTimeUtc >= start && row.TradeTimeUtc < end)
                .Select(row => new SeriesKey(row.Venue, row.Instrument, row.ContractId, 0))
                .Distinct()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (SeriesKey key in tradeKeys
                .OrderBy(item => item.Venue, StringComparer.Ordinal)
                .ThenBy(item => item.Instrument, StringComparer.Ordinal)
                .ThenBy(item => item.ContractId, StringComparer.Ordinal))
            {
                files.Add(await TapeExportArchive.WriteTradeCsvAsync(
                    _options.Path,
                    generation,
                    day,
                    key.Venue,
                    key.Instrument,
                    key.ContractId,
                    Trades(start, end, key.Venue, key.Instrument, key.ContractId, cancellationToken),
                    cancellationToken).ConfigureAwait(false));
                files.Add(await TapeExportArchive.WriteTradeParquetAsync(
                    _options.Path,
                    generation,
                    day,
                    key.Venue,
                    key.Instrument,
                    key.ContractId,
                    Trades(start, end, key.Venue, key.Instrument, key.ContractId, cancellationToken),
                    cancellationToken).ConfigureAwait(false));
            }

            List<SeriesKey> coverageKeys = await _database.TapeCoverage
                .AsNoTracking()
                .Where(row => row.RangeStart >= start && row.RangeStart < end
                    && row.RangeEnd != TapeCoverageRecord.StillListeningEnd)
                .Select(row => new SeriesKey(row.Venue, row.Instrument, row.ContractId, 0))
                .Distinct()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (SeriesKey key in coverageKeys
                .OrderBy(item => item.Venue, StringComparer.Ordinal)
                .ThenBy(item => item.Instrument, StringComparer.Ordinal)
                .ThenBy(item => item.ContractId, StringComparer.Ordinal))
            {
                files.Add(await TapeExportArchive.WriteCoverageCsvAsync(
                    _options.Path,
                    generation,
                    day,
                    key.Venue,
                    key.Instrument,
                    key.ContractId,
                    Coverage(start, end, key.Venue, key.Instrument, key.ContractId, cancellationToken),
                    cancellationToken).ConfigureAwait(false));
                files.Add(await TapeExportArchive.WriteCoverageParquetAsync(
                    _options.Path,
                    generation,
                    day,
                    key.Venue,
                    key.Instrument,
                    key.ContractId,
                    Coverage(start, end, key.Venue, key.Instrument, key.ContractId, cancellationToken),
                    cancellationToken).ConfigureAwait(false));
            }

            List<SeriesKey> footprintKeys = await _database.FootprintCells
                .AsNoTracking()
                .Where(row => row.BucketStart >= start && row.BucketStart < end)
                .Select(row => new SeriesKey(row.Venue, row.Instrument, string.Empty, row.ResolutionMinutes))
                .Distinct()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (SeriesKey key in footprintKeys
                .OrderBy(item => item.Venue, StringComparer.Ordinal)
                .ThenBy(item => item.Instrument, StringComparer.Ordinal)
                .ThenBy(item => item.Resolution))
            {
                files.Add(await TapeExportArchive.WriteFootprintCsvAsync(
                    _options.Path,
                    generation,
                    day,
                    key.Venue,
                    key.Instrument,
                    key.Resolution,
                    Footprint(start, end, key.Venue, key.Instrument, key.Resolution, cancellationToken),
                    cancellationToken).ConfigureAwait(false));
                files.Add(await TapeExportArchive.WriteFootprintParquetAsync(
                    _options.Path,
                    generation,
                    day,
                    key.Venue,
                    key.Instrument,
                    key.Resolution,
                    Footprint(start, end, key.Venue, key.Instrument, key.Resolution, cancellationToken),
                    cancellationToken).ConfigureAwait(false));
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        if (files.Count == 0)
        {
            return await TapeExportArchive.VerifyAsync(_options.Path, day, cancellationToken).ConfigureAwait(false);
        }

        await TapeExportArchive.PublishManifestAsync(
            _options.Path,
            day,
            new TapeExportDraft(generation, files),
            exportedAtUtc,
            cancellationToken).ConfigureAwait(false);
        return await TapeExportArchive.VerifyAsync(_options.Path, day, cancellationToken).ConfigureAwait(false);
    }

    private IAsyncEnumerable<TapeExportTradeRow> Trades(
        DateTimeOffset start,
        DateTimeOffset end,
        string venue,
        string instrument,
        string contractId,
        CancellationToken cancellationToken) =>
        Map(
            _database.Trades.AsNoTracking()
                .Where(row => row.Venue == venue && row.Instrument == instrument && row.ContractId == contractId
                    && row.TradeTimeUtc >= start && row.TradeTimeUtc < end)
                .OrderBy(row => row.TradeTimeUtc)
                .ThenBy(row => row.Sequence)
                .AsAsyncEnumerable(),
            row => new TapeExportTradeRow(
                row.Venue,
                row.Instrument,
                row.ContractId,
                row.TradeTimeUtc,
                row.Sequence,
                row.Price,
                row.Size,
                row.Direction,
                row.RecordedAt),
            cancellationToken);

    private IAsyncEnumerable<TapeExportCoverageRow> Coverage(
        DateTimeOffset start,
        DateTimeOffset end,
        string venue,
        string instrument,
        string contractId,
        CancellationToken cancellationToken) =>
        Map(
            _database.TapeCoverage.AsNoTracking()
                .Where(row => row.Venue == venue && row.Instrument == instrument && row.ContractId == contractId
                    && row.RangeStart >= start && row.RangeStart < end
                    && row.RangeEnd != TapeCoverageRecord.StillListeningEnd)
                .OrderBy(row => row.RangeStart)
                .ThenBy(row => row.RangeEnd)
                .AsAsyncEnumerable(),
            row => new TapeExportCoverageRow(
                row.Venue,
                row.Instrument,
                row.ContractId,
                row.RangeStart,
                row.RangeEnd,
                row.RecordedAt),
            cancellationToken);

    private IAsyncEnumerable<TapeExportFootprintRow> Footprint(
        DateTimeOffset start,
        DateTimeOffset end,
        string venue,
        string instrument,
        int resolution,
        CancellationToken cancellationToken) =>
        Map(
            _database.FootprintCells.AsNoTracking()
                .Where(row => row.Venue == venue && row.Instrument == instrument && row.ResolutionMinutes == resolution
                    && row.BucketStart >= start && row.BucketStart < end)
                .OrderBy(row => row.BucketStart)
                .ThenBy(row => row.Price)
                .AsAsyncEnumerable(),
            row => new TapeExportFootprintRow(
                row.Venue,
                row.Instrument,
                row.ResolutionMinutes,
                row.BucketStart,
                row.Price,
                row.BuyVolume,
                row.SellVolume,
                row.RecordedAt),
            cancellationToken);

    private static async IAsyncEnumerable<TOut> Map<TIn, TOut>(
        IAsyncEnumerable<TIn> source,
        Func<TIn, TOut> project,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (TIn row in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return project(row);
        }
    }

    private async Task<(bool Failed, bool DriveDisabled)> UploadAndRecordAsync(
        DateOnly day,
        TapeExportVerification written,
        DateTimeOffset exportedAtUtc,
        CancellationToken cancellationToken)
    {
        bool failed = false;
        bool driveDisabled = !_drive.IsEnabled;
        foreach (TapeExportStoredFile file in written.Files ?? [])
        {
            failed |= await UploadOneAsync(day, file, exportedAtUtc, cancellationToken).ConfigureAwait(false);
        }

        string manifestRelative = TapeExportArchive.ManifestRelative(day);
        string manifestAbsolute = TapeExportArchive.ManifestPath(_options.Path, day);
        if (File.Exists(manifestAbsolute))
        {
            failed |= await UploadOneAsync(
                day,
                new TapeExportStoredFile(
                    TapeExportArchive.ManifestKind,
                    TapeExportArchive.ManifestFileFormat,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    0,
                    manifestRelative,
                    0,
                    TapeExportArchive.Sha256Of(manifestAbsolute)),
                exportedAtUtc,
                cancellationToken).ConfigureAwait(false);
        }

        return (failed, driveDisabled);
    }

    private async Task<bool> UploadOneAsync(
        DateOnly day,
        TapeExportStoredFile file,
        DateTimeOffset exportedAtUtc,
        CancellationToken cancellationToken)
    {
        TapeDriveIdentity identity = new(
            day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            file.Kind,
            file.Format,
            file.Venue,
            file.Instrument,
            file.ContractId,
            file.ResolutionMinutes);

        await using var status = await _database.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await LockIdentityAsync(status, identity.Canonical(), cancellationToken).ConfigureAwait(false);
            string? known = await _database.TapeExportReceipts.AsNoTracking()
                .Where(row => row.Day == day && row.Kind == file.Kind && row.Format == file.Format
                    && row.Venue == file.Venue && row.Instrument == file.Instrument
                    && row.ContractId == file.ContractId && row.ResolutionMinutes == file.ResolutionMinutes)
                .Select(row => row.DriveFileId)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            if (_drive.IsEnabled)
            {
                await RecordReceiptAsync(
                    day,
                    file,
                    exportedAtUtc,
                    known,
                    TapeExportReceipt.StatePending,
                    failureType: null,
                    failureStatus: null,
                    cancellationToken).ConfigureAwait(false);
            }

            string absolute = Path.Combine(
                _options.Path,
                file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            TapeDriveUploadResult result = _drive.IsEnabled
                ? await _drive.UploadAsync(
                    new TapeDriveUpload(identity, known, file.RelativePath, () => File.OpenRead(absolute)),
                    cancellationToken).ConfigureAwait(false)
                : TapeDriveUploadResult.NotConfigured;

            if (result.Status == TapeDriveUploadStatus.Failed)
            {
                _logger.LogError(
                    "Drive upload failed for {Path}. Type {FailureType}, status {FailureStatus}.",
                    file.RelativePath,
                    result.FailureType,
                    result.FailureStatus);
            }

            string state = result.Status switch
            {
                TapeDriveUploadStatus.Uploaded => TapeExportReceipt.StateUploaded,
                TapeDriveUploadStatus.Failed => TapeExportReceipt.StateFailed,
                _ => TapeExportReceipt.StateDriveDisabled,
            };

            await RecordReceiptAsync(
                day,
                file,
                exportedAtUtc,
                result.FileId,
                state,
                result.FailureType,
                result.FailureStatus,
                cancellationToken).ConfigureAwait(false);
            await status.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result.Status == TapeDriveUploadStatus.Failed;
        }
        catch
        {
            await status.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task LockIdentityAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        string key,
        CancellationToken cancellationToken)
    {
        System.Data.Common.DbConnection connection = _database.Database.GetDbConnection();
        await using System.Data.Common.DbCommand command = connection.CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = "SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))";
        System.Data.Common.DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "key";
        parameter.Value = key;
        command.Parameters.Add(parameter);
        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task RecordReceiptAsync(
        DateOnly day,
        TapeExportStoredFile file,
        DateTimeOffset exportedAtUtc,
        string? driveFileId,
        string state,
        string? failureType,
        int? failureStatus,
        CancellationToken cancellationToken) =>
        _database.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "TapeExportReceipts"
                ("Day", "Kind", "Format", "Venue", "Instrument", "ContractId", "ResolutionMinutes",
                 "RelativePath", "Sha256", "RowCount", "ExportedAtUtc", "DriveFileId", "UploadState",
                 "FailureType", "FailureStatus")
            VALUES
                ({day}, {file.Kind}, {file.Format}, {file.Venue}, {file.Instrument}, {file.ContractId},
                 {file.ResolutionMinutes}, {file.RelativePath}, {file.Sha256}, {file.Rows},
                 {exportedAtUtc}, {driveFileId}, {state}, {failureType}, {failureStatus})
            ON CONFLICT ("Day", "Kind", "Format", "Venue", "Instrument", "ContractId", "ResolutionMinutes")
            DO UPDATE SET
                "RelativePath" = EXCLUDED."RelativePath",
                "Sha256" = EXCLUDED."Sha256",
                "RowCount" = EXCLUDED."RowCount",
                "ExportedAtUtc" = EXCLUDED."ExportedAtUtc",
                "DriveFileId" = COALESCE(EXCLUDED."DriveFileId", "TapeExportReceipts"."DriveFileId"),
                "UploadState" = EXCLUDED."UploadState",
                "FailureType" = EXCLUDED."FailureType",
                "FailureStatus" = EXCLUDED."FailureStatus"
            """,
            cancellationToken);

    private async Task<bool> OpenRunCoversAsync(DateOnly day, CancellationToken cancellationToken)
    {
        (_, DateTimeOffset end) = Bounds(day);
        return await _database.TapeCoverage.AsNoTracking()
            .AnyAsync(
                row => row.RangeEnd == TapeCoverageRecord.StillListeningEnd && row.RangeStart < end,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<int> CountFinishedDaysAsync(
        IReadOnlyCollection<DateOnly> work,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        List<DateOnly> finished = await _database.TapeExportReceipts.AsNoTracking()
            .Where(row => row.UploadState == TapeExportReceipt.StateUploaded
                || row.UploadState == TapeExportReceipt.StateDriveDisabled)
            .Select(row => row.Day)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return finished.Count(day =>
            TapeExportPolicy.IsExportable(day, now) && !work.Contains(day));
    }

    private async Task<List<DateOnly>> DiscoverAsync(CancellationToken cancellationToken)
    {
        HashSet<DateOnly> days = [];
        foreach (DateOnly day in await QueryDaysAsync(TradeDaysSql, cancellationToken).ConfigureAwait(false))
        {
            days.Add(day);
        }

        foreach (DateOnly day in await QueryDaysAsync(CoverageDaysSql, cancellationToken).ConfigureAwait(false))
        {
            days.Add(day);
        }

        foreach (DateOnly day in await QueryDaysAsync(FootprintDaysSql, cancellationToken).ConfigureAwait(false))
        {
            days.Add(day);
        }

        List<TapeExportReceipt> done = await _database.TapeExportReceipts.AsNoTracking()
            .Where(row => row.UploadState == TapeExportReceipt.StateUploaded
                || row.UploadState == TapeExportReceipt.StateDriveDisabled)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (TapeExportReceipt receipt in done)
        {
            string absolute = Path.Combine(
                _options.Path,
                receipt.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(absolute)
                || !string.Equals(TapeExportArchive.Sha256Of(absolute), receipt.Sha256, StringComparison.Ordinal))
            {
                days.Add(receipt.Day);
            }
        }

        return [.. days.Order()];
    }

    private async Task<List<DateOnly>> QueryDaysAsync(string sql, CancellationToken cancellationToken)
    {
        List<DateOnly> days = [];
        await _database.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using System.Data.Common.DbCommand command = _database.Database.GetDbConnection().CreateCommand();
            command.CommandText = sql;
            await using System.Data.Common.DbDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                days.Add(reader.GetFieldValue<DateOnly>(0));
            }
        }
        finally
        {
            await _database.Database.CloseConnectionAsync().ConfigureAwait(false);
        }

        return days;
    }

    private const string TradeDaysSql =
        """
        SELECT DISTINCT (t."TradeTimeUtc" AT TIME ZONE 'UTC')::date
        FROM "Trades" AS t
        WHERE NOT EXISTS (
            SELECT 1 FROM "TapeExportReceipts" AS r
            WHERE r."Day" = (t."TradeTimeUtc" AT TIME ZONE 'UTC')::date
              AND r."Kind" = 'trades'
              AND r."Format" = 'csv.gz'
              AND r."Venue" = t."Venue"
              AND r."Instrument" = t."Instrument"
              AND r."ContractId" = t."ContractId"
              AND r."UploadState" IN ('uploaded', 'drive_disabled')
              AND r."Sha256" <> '')
        """;

    private const string CoverageDaysSql =
        """
        SELECT DISTINCT (c."RangeStart" AT TIME ZONE 'UTC')::date
        FROM "TapeCoverage" AS c
        WHERE c."RangeEnd" <> TIMESTAMPTZ '9999-12-31 00:00:00+00'
          AND NOT EXISTS (
            SELECT 1 FROM "TapeExportReceipts" AS r
            WHERE r."Day" = (c."RangeStart" AT TIME ZONE 'UTC')::date
              AND r."Kind" = 'tape-coverage'
              AND r."Format" = 'csv.gz'
              AND r."Venue" = c."Venue"
              AND r."Instrument" = c."Instrument"
              AND r."ContractId" = c."ContractId"
              AND r."UploadState" IN ('uploaded', 'drive_disabled')
              AND r."Sha256" <> '')
        """;

    private const string FootprintDaysSql =
        """
        SELECT DISTINCT (f."BucketStart" AT TIME ZONE 'UTC')::date
        FROM "FootprintCells" AS f
        WHERE NOT EXISTS (
            SELECT 1 FROM "TapeExportReceipts" AS r
            WHERE r."Day" = (f."BucketStart" AT TIME ZONE 'UTC')::date
              AND r."Kind" = 'footprint'
              AND r."Format" = 'csv.gz'
              AND r."Venue" = f."Venue"
              AND r."Instrument" = f."Instrument"
              AND r."ResolutionMinutes" = f."ResolutionMinutes"
              AND r."UploadState" IN ('uploaded', 'drive_disabled')
              AND r."Sha256" <> '')
        """;

    private readonly record struct SeriesKey(string Venue, string Instrument, string ContractId, int Resolution);


    private async Task<TapeExportReport> ImportInsideAsync(DateOnly? onlyDay, CancellationToken cancellationToken)
    {
        int daysRefused = 0;
        int rowsImported = 0;
        int daysExported = 0;

        foreach (DateOnly day in ManifestDays())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (onlyDay is DateOnly requested && day != requested)
            {
                continue;
            }

            TapeExportDay payload;
            try
            {
                payload = await TapeExportArchive.ReadDayAsync(_options.Path, day, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidDataException exception)
            {
                _telemetry.TapeExport(TapeExportOutcome.ImportRefused, 1);
                daysRefused++;
                _logger.LogError(
                    "Tape import refuses {Day}: {Reason}",
                    day,
                    exception.Message);
                continue;
            }

            int inserted = await InsertAsync(payload, cancellationToken).ConfigureAwait(false);
            _telemetry.TapeExport(TapeExportOutcome.RowsImported, inserted);
            rowsImported += inserted;
            daysExported++;
        }

        _logger.LogInformation(
            "Tape import pass: {DaysImported} days, {RowsImported} rows inserted, {DaysRefused} days refused.",
            daysExported,
            rowsImported,
            daysRefused);

        return new TapeExportReport(daysExported, daysRefused, 0, rowsImported);
    }

    private async Task<int> InsertAsync(TapeExportDay day, CancellationToken cancellationToken)
    {
        int inserted = 0;

        await using var transaction = await _database.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);

        foreach (TapeExportTradeRow row in day.Trades)
        {
            inserted += await _database.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "Trades"
                    ("Venue", "Instrument", "ContractId", "TradeTimeUtc", "Sequence", "Price", "Size", "Direction", "RecordedAt")
                VALUES
                    ({row.Venue}, {row.Instrument}, {row.ContractId}, {row.TradeTimeUtc}, {row.Sequence}, {row.Price}, {row.Size}, {(int)row.Direction}, {row.RecordedAt})
                ON CONFLICT ("Venue", "Instrument", "ContractId", "TradeTimeUtc", "Sequence") DO NOTHING
                """,
                cancellationToken).ConfigureAwait(false);
        }

        foreach (TapeExportCoverageRow row in day.Coverage)
        {
            if (row.RangeEnd == TapeCoverageRecord.StillListeningEnd)
            {
                continue;
            }

            inserted += await _database.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "TapeCoverage"
                    ("Venue", "Instrument", "ContractId", "RangeStart", "RangeEnd", "RecordedAt")
                VALUES
                    ({row.Venue}, {row.Instrument}, {row.ContractId}, {row.RangeStart}, {row.RangeEnd}, {row.RecordedAt})
                ON CONFLICT ("Venue", "Instrument", "ContractId", "RangeStart", "RangeEnd") DO NOTHING
                """,
                cancellationToken).ConfigureAwait(false);
        }

        foreach (TapeExportFootprintRow row in day.Footprint)
        {
            inserted += await _database.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "FootprintCells"
                    ("Venue", "Instrument", "ResolutionMinutes", "BucketStart", "Price", "BuyVolume", "SellVolume", "RecordedAt")
                VALUES
                    ({row.Venue}, {row.Instrument}, {row.ResolutionMinutes}, {row.BucketStart}, {row.Price}, {row.BuyVolume}, {row.SellVolume}, {row.RecordedAt})
                ON CONFLICT ("Venue", "Instrument", "ResolutionMinutes", "BucketStart", "Price") DO NOTHING
                """,
                cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return inserted;
    }

    private IEnumerable<DateOnly> ManifestDays()
    {
        string directory = System.IO.Path.Combine(_options.Path, TapeExportArchive.ManifestDirectory);
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        foreach (string file in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (DateOnly.TryParseExact(
                name,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateOnly day))
            {
                yield return day;
            }
        }
    }

    private void EnsureValid()
    {
        List<ValidationResult> results = [];
        Validator.TryValidateObject(
            _options,
            new ValidationContext(_options),
            results,
            validateAllProperties: true);

        if (results.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" ", results.Select(result => result.ErrorMessage)));
        }
    }

    private static (DateTimeOffset Start, DateTimeOffset End) Bounds(DateOnly day)
    {
        DateTimeOffset start = new(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        return (start, start.AddDays(1));
    }

    private static TapeExportReport Empty() => new(0, 0, 0, 0);
}
