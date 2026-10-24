using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Telemetry;
using Microsoft.EntityFrameworkCore;
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
/// The three reads share one <see cref="IsolationLevel.RepeatableRead"/> transaction, so the file is
/// one snapshot. A row committed by another session after the snapshot is not in this file and is
/// still in Postgres for the next pass. Nothing in the transaction deletes.
/// </para>
/// <para>
/// An empty database does not rediscover a day, so a second pass does not replace a verified archive
/// with an empty file. Still-listening coverage is not archived. A UTC day that has not ended is not
/// archived either: the file would freeze a partial day.
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

        foreach (DateOnly day in days)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TapeExportPolicy.IsExportable(day, now))
            {
                _telemetry.TapeExport(TapeExportOutcome.SkippedOpen, 1);
                continue;
            }

            using Activity? exportSpan = _telemetry.Activities.StartActivity("tape.export", ActivityKind.Internal);
            exportSpan?.SetTag("tape.day", day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

            await using var transaction = await _database.Database
                .BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken)
                .ConfigureAwait(false);

            TapeExportVerification written;
            try
            {
                TapeExportDay payload = await LoadAsync(day, cancellationToken).ConfigureAwait(false);

                written = await TapeExportArchive.WriteDayAsync(
                    _options.Path,
                    payload,
                    now,
                    cancellationToken).ConfigureAwait(false);

                if (!written.Verified)
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    exportSpan?.SetStatus(ActivityStatusCode.Error);
                    _telemetry.TapeExport(TapeExportOutcome.VerificationFailed, 1);
                    daysRefused++;
                    _logger.LogError(
                        "Tape export of {Day} did not verify ({Reason}). Local rows were left in place.",
                        day,
                        written.Reason);
                    continue;
                }

                // The snapshot is done. Do not hold it across the upload.
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                exportSpan?.SetStatus(ActivityStatusCode.Error);
                _telemetry.TapeExport(TapeExportOutcome.ExportFailed, 1);
                daysRefused++;
                _logger.LogError(
                    exception,
                    "Tape export of {Day} failed. Local rows were left in place.",
                    day);
                continue;
            }

            bool uploadFailed = await UploadAndRecordAsync(day, written, now, cancellationToken)
                .ConfigureAwait(false);
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

            _telemetry.TapeExport(TapeExportOutcome.DaysExported, 1);
            _telemetry.TapeExport(TapeExportOutcome.RowsExported, rows);
            daysExported++;
            rowsExported += rows;
        }

        _logger.LogInformation(
            "Tape export pass: {DaysExported} days exported, {RowsExported} rows written, "
            + "{DaysRefused} days refused. Local tape was not deleted.",
            daysExported,
            rowsExported,
            daysRefused);

        return new TapeExportReport(daysExported, daysRefused, rowsExported, 0);
    }

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

    private async Task<bool> UploadAndRecordAsync(
        DateOnly day,
        TapeExportVerification written,
        DateTimeOffset exportedAtUtc,
        CancellationToken cancellationToken)
    {
        List<(TapeExportStoredFile File, TapeDriveUploadResult Result)> outcomes = [];
        foreach (TapeExportStoredFile file in written.Files ?? [])
        {
            await using FileStream stream = File.OpenRead(
                Path.Combine(_options.Path, file.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            TapeDriveUploadResult result = await _drive.UploadAsync(file.RelativePath, stream, cancellationToken)
                .ConfigureAwait(false);
            outcomes.Add((file, result));
        }

        string manifestRelative = TapeExportArchive.ManifestRelative(day);
        string manifestAbsolute = TapeExportArchive.ManifestPath(_options.Path, day);
        await using (FileStream manifest = File.OpenRead(manifestAbsolute))
        {
            TapeDriveUploadResult manifestResult = await _drive
                .UploadAsync(manifestRelative, manifest, cancellationToken)
                .ConfigureAwait(false);
            outcomes.Add((
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
                manifestResult));
        }

        await using var status = await _database.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);

        foreach ((TapeExportStoredFile file, TapeDriveUploadResult result) in outcomes)
        {
            string state = result.Status switch
            {
                TapeDriveUploadStatus.Uploaded => TapeExportReceipt.StateUploaded,
                TapeDriveUploadStatus.Failed => TapeExportReceipt.StateFailed,
                _ => TapeExportReceipt.StateLocal,
            };

            await _database.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "TapeExportReceipts"
                    ("Day", "Kind", "Format", "Venue", "Instrument", "ContractId", "ResolutionMinutes",
                     "RelativePath", "Sha256", "RowCount", "ExportedAtUtc", "DriveFileId", "UploadState")
                VALUES
                    ({day}, {file.Kind}, {file.Format}, {file.Venue}, {file.Instrument}, {file.ContractId},
                     {file.ResolutionMinutes}, {file.RelativePath}, {file.Sha256}, {file.Rows},
                     {exportedAtUtc}, {result.FileId}, {state})
                ON CONFLICT ("Day", "Kind", "Format", "Venue", "Instrument", "ContractId", "ResolutionMinutes")
                DO UPDATE SET
                    "RelativePath" = EXCLUDED."RelativePath",
                    "Sha256" = EXCLUDED."Sha256",
                    "RowCount" = EXCLUDED."RowCount",
                    "ExportedAtUtc" = EXCLUDED."ExportedAtUtc",
                    "DriveFileId" = EXCLUDED."DriveFileId",
                    "UploadState" = EXCLUDED."UploadState"
                """,
                cancellationToken).ConfigureAwait(false);
        }

        await status.CommitAsync(cancellationToken).ConfigureAwait(false);
        return outcomes.Any(outcome => outcome.Result.Status == TapeDriveUploadStatus.Failed);
    }

    private async Task<List<DateOnly>> DiscoverAsync(CancellationToken cancellationToken)
    {
        HashSet<DateOnly> days = [];

        await foreach (DateTimeOffset stamp in _database.Trades.AsNoTracking()
            .Select(row => row.TradeTimeUtc)
            .AsAsyncEnumerable()
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            days.Add(TapeExportPolicy.DayOf(stamp));
        }

        await foreach (DateTimeOffset stamp in _database.TapeCoverage.AsNoTracking()
            .Where(row => row.RangeEnd != TapeCoverageRecord.StillListeningEnd)
            .Select(row => row.RangeStart)
            .AsAsyncEnumerable()
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            days.Add(TapeExportPolicy.DayOf(stamp));
        }

        await foreach (DateTimeOffset stamp in _database.FootprintCells.AsNoTracking()
            .Select(row => row.BucketStart)
            .AsAsyncEnumerable()
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            days.Add(TapeExportPolicy.DayOf(stamp));
        }

        return [.. days.Order()];
    }

    private async Task<TapeExportDay> LoadAsync(DateOnly day, CancellationToken cancellationToken)
    {
        (DateTimeOffset start, DateTimeOffset end) = Bounds(day);

        List<TradeRecord> trades = await _database.Trades.AsNoTracking()
            .Where(row => row.TradeTimeUtc >= start && row.TradeTimeUtc < end)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<TapeCoverageRecord> coverage = await _database.TapeCoverage.AsNoTracking()
            .Where(row => row.RangeStart >= start && row.RangeStart < end
                && row.RangeEnd != TapeCoverageRecord.StillListeningEnd)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<FootprintCellRecord> cells = await _database.FootprintCells.AsNoTracking()
            .Where(row => row.BucketStart >= start && row.BucketStart < end)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new TapeExportDay(
            day,
            trades.Select(row => new TapeExportTradeRow(
                row.Venue,
                row.Instrument,
                row.ContractId,
                row.TradeTimeUtc,
                row.Sequence,
                row.Price,
                row.Size,
                row.Direction,
                row.RecordedAt)).ToList(),
            coverage.Select(row => new TapeExportCoverageRow(
                row.Venue,
                row.Instrument,
                row.ContractId,
                row.RangeStart,
                row.RangeEnd,
                row.RecordedAt)).ToList(),
            cells.Select(row => new TapeExportFootprintRow(
                row.Venue,
                row.Instrument,
                row.ResolutionMinutes,
                row.BucketStart,
                row.Price,
                row.BuyVolume,
                row.SellVolume,
                row.RecordedAt)).ToList());
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
