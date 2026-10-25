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
using Npgsql;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>What <see cref="ITapeIdentitySession.TryLockAsync"/> decided.</summary>
internal enum TapeIdentityLockTake
{
    /// <summary>This pass holds the identity.</summary>
    Held,

    /// <summary>Another session still holds it after <c>lock_timeout</c>.</summary>
    Busy,

    /// <summary>The lock connection dropped.</summary>
    Lost,
}

/// <summary>
/// The session that holds every identity lock for one pass. Production uses one direct Postgres
/// connection. A test can supply a session that throws.
/// </summary>
internal interface ITapeIdentitySession : IAsyncDisposable
{
    /// <summary>Takes the advisory lock for <paramref name="key"/>.</summary>
    /// <param name="key">The identity's canonical string.</param>
    /// <param name="cancellationToken">Cancels the take. A drop is <see cref="TapeIdentityLockTake.Lost"/>, not this.</param>
    /// <returns>Held, busy, or lost. A provider error that is not a drop escapes.</returns>
    Task<TapeIdentityLockTake> TryLockAsync(string key, CancellationToken cancellationToken);

    /// <summary>True when the session still answers. A provider error that is not a drop escapes.</summary>
    /// <returns>False when the session has dropped.</returns>
    Task<bool> ProbeAsync();

    /// <summary>Releases <paramref name="key"/>. A provider error that is not a drop escapes.</summary>
    /// <param name="key">The identity's canonical string.</param>
    /// <returns>The release.</returns>
    Task UnlockAsync(string key);

    /// <summary>Closes the session and opens another. The next file uses the new one.</summary>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The reopen.</returns>
    Task ReopenAsync(CancellationToken cancellationToken);
}

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
/// archived either. The receipt for a failed upload keeps the last Drive file id. One identity is
/// serialised with a session advisory lock on one direct connection for the pass. That connection
/// is not pooled, sets <c>lock_timeout</c> and TCP keepalive (the socket option, not Npgsql's
/// keepalive query), and is reopened if it drops. A provider error that is not a dropped
/// session fails that file and the pass continues. The
/// pending receipt is committed before the upload, and the file id is committed after it. A
/// <c>drive_disabled</c> day is exported again once Drive is on.
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

    /// <summary>
    /// When set, the pass locks through this session and does not open a Postgres identity connection.
    /// Production leaves it unset.
    /// </summary>
    internal ITapeIdentitySession? IdentitySession { get; set; }

    /// <summary>
    /// Span tag on <c>tape.upload</c> when a lock outcome replaces a Drive failure. Not a counter tag:
    /// a failure-type dimension would multiply <c>mcp.tape.uploads</c>.
    /// </summary>
    internal const string DriveFailureSpanTag = "drive.failure_type";

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
        await ReportPendingAsync(cancellationToken).ConfigureAwait(false);

        // One session for every identity in the pass. Opened on the first upload and held until the pass
        // finishes, including across days. A drop reopens that same gate for the next file.
        ITapeIdentitySession? gate = IdentitySession;
        try
        {
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

                if (gate is null && _drive.IsEnabled)
                {
                    gate = await IdentityGate.OpenAsync(_database, _logger, cancellationToken).ConfigureAwait(false);
                }

                (bool uploadFailed, bool driveDisabled) = await UploadAndRecordAsync(
                    day,
                    written,
                    now,
                    gate,
                    cancellationToken).ConfigureAwait(false);
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
        }
        finally
        {
            if (gate is not null)
            {
                await gate.DisposeAsync().ConfigureAwait(false);
            }
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
            RecordUpload(
                TapeUploadOutcome.Disabled,
                TapeUploadOperation.Upload,
                TapeUploadReason.Disabled,
                failed: false);
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

        List<SeriesKey> tradeKeys;
        List<SeriesKey> coverageKeys;
        List<SeriesKey> footprintKeys;
        await using (var transaction = await _database.Database
            .BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken)
            .ConfigureAwait(false))
        {
            try
            {
                tradeKeys = await _database.Trades
                    .AsNoTracking()
                    .Where(row => row.TradeTimeUtc >= start && row.TradeTimeUtc < end)
                    .Select(row => new SeriesKey(row.Venue, row.Instrument, row.ContractId, 0))
                    .Distinct()
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
                coverageKeys = await _database.TapeCoverage
                    .AsNoTracking()
                    .Where(row => row.RangeStart >= start && row.RangeStart < end
                        && row.RangeEnd != TapeCoverageRecord.StillListeningEnd)
                    .Select(row => new SeriesKey(row.Venue, row.Instrument, row.ContractId, 0))
                    .Distinct()
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
                footprintKeys = await _database.FootprintCells
                    .AsNoTracking()
                    .Where(row => row.BucketStart >= start && row.BucketStart < end)
                    .Select(row => new SeriesKey(row.Venue, row.Instrument, string.Empty, row.ResolutionMinutes))
                    .Distinct()
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }

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
        ITapeIdentitySession? gate,
        CancellationToken cancellationToken)
    {
        bool failed = false;
        bool driveDisabled = !_drive.IsEnabled;
        foreach (TapeExportStoredFile file in written.Files ?? [])
        {
            failed |= await UploadOneAsync(day, file, exportedAtUtc, gate, cancellationToken).ConfigureAwait(false);
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
                gate,
                cancellationToken).ConfigureAwait(false);
        }

        return (failed, driveDisabled);
    }

    private async Task<bool> UploadOneAsync(
        DateOnly day,
        TapeExportStoredFile file,
        DateTimeOffset exportedAtUtc,
        ITapeIdentitySession? gate,
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

        if (!_drive.IsEnabled)
        {
            await RecordReceiptAsync(
                day,
                file,
                exportedAtUtc,
                driveFileId: null,
                TapeExportReceipt.StateDriveDisabled,
                failureType: null,
                failureStatus: null,
                cancellationToken).ConfigureAwait(false);
            return false;
        }

        string key = identity.Canonical();
        bool held = false;
        Activity? uploadSpan = null;
        try
        {
            TapeIdentityLockTake take;
            try
            {
                take = await gate!.TryLockAsync(key, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsLockSessionFault(exception))
            {
                await FailLockSessionAsync(
                    gate!,
                    day,
                    file,
                    exportedAtUtc,
                    exception,
                    uploadSpan,
                    upload: null,
                    cancellationToken).ConfigureAwait(false);
                return true;
            }

            if (take == TapeIdentityLockTake.Busy)
            {
                await CommitResultAsync(
                    day,
                    file,
                    exportedAtUtc,
                    driveFileId: null,
                    TapeExportReceipt.StateFailed,
                    "IdentityBusy",
                    failureStatus: null,
                    cancellationToken).ConfigureAwait(false);
                RecordUpload(
                    TapeUploadOutcome.Failed,
                    TapeUploadOperation.Lock,
                    TapeUploadReason.IdentityBusy,
                    failed: true);
                await ReopenQuietlyAsync(gate, cancellationToken).ConfigureAwait(false);
                return true;
            }

            if (take == TapeIdentityLockTake.Lost)
            {
                await CommitResultAsync(
                    day,
                    file,
                    exportedAtUtc,
                    driveFileId: null,
                    TapeExportReceipt.StateFailed,
                    "LockLost",
                    failureStatus: null,
                    cancellationToken).ConfigureAwait(false);
                RecordUpload(
                    TapeUploadOutcome.Failed,
                    TapeUploadOperation.Lock,
                    TapeUploadReason.LockLost,
                    failed: true);
                await ReopenQuietlyAsync(gate, cancellationToken).ConfigureAwait(false);
                return true;
            }

            held = true;
            string? known = await CommitPendingAsync(day, file, exportedAtUtc, cancellationToken).ConfigureAwait(false);
            string absolute = Path.Combine(
                _options.Path,
                file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            // Open before the upload so the span covers every retry and parents the Drive HTTP spans.
            // The outcome tags are set when it ends, after the probe. Disposed in the finally.
            uploadSpan = _telemetry.Activities.StartActivity("tape.upload", ActivityKind.Client);
            TapeDriveUploadResult result = await _drive.UploadAsync(
                new TapeDriveUpload(identity, known, file.RelativePath, () => File.OpenRead(absolute)),
                cancellationToken).ConfigureAwait(false);

            bool locked;
            try
            {
                locked = await gate.ProbeAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (IsLockSessionFault(exception))
            {
                await FailLockSessionAsync(
                    gate,
                    day,
                    file,
                    exportedAtUtc,
                    exception,
                    uploadSpan,
                    result,
                    cancellationToken).ConfigureAwait(false);
                return true;
            }

            if (!locked)
            {
                held = false;
                await ReopenQuietlyAsync(gate, cancellationToken).ConfigureAwait(false);
                await CommitResultAsync(
                    day,
                    file,
                    exportedAtUtc,
                    result.FileId,
                    TapeExportReceipt.StateFailed,
                    "LockLost",
                    failureStatus: null,
                    cancellationToken).ConfigureAwait(false);
                // One file, one terminal measurement. A lost lock wins over a Drive failure
                // on the same file: the receipt just written is LockLost, so this counter is
                // lock_lost and not also failed, permanent, or upload_timeout. The uploader
                // records retries only and does not record the Drive failure. drive_retried,
                // when any retries happened, counts attempts. It is not a second file outcome.
                // The span tag keeps the Drive error the receipt no longer names.
                FinishUpload(
                    uploadSpan,
                    TapeUploadOutcome.Failed,
                    TapeUploadOperation.Lock,
                    TapeUploadReason.LockLost,
                    failed: true,
                    result.Status == TapeDriveUploadStatus.Failed ? result.FailureType : null);
                return true;
            }

            if (result.Status == TapeDriveUploadStatus.Failed)
            {
                _logger.LogError(
                    "Drive upload failed for {Path}. Type {FailureType}, status {FailureStatus}.",
                    file.RelativePath,
                    result.FailureType,
                    result.FailureStatus);
            }

            string state = result.Status == TapeDriveUploadStatus.Uploaded
                ? TapeExportReceipt.StateUploaded
                : TapeExportReceipt.StateFailed;
            await CommitResultAsync(
                day,
                file,
                exportedAtUtc,
                result.FileId,
                state,
                result.FailureType,
                result.FailureStatus,
                cancellationToken).ConfigureAwait(false);
            if (result.Status == TapeDriveUploadStatus.Uploaded)
            {
                FinishUpload(
                    uploadSpan,
                    TapeUploadOutcome.Uploaded,
                    TapeUploadOperation.Upload,
                    TapeUploadReason.Uploaded,
                    failed: false);
            }
            else
            {
                // The lock is still held, so the Drive result is the file's one outcome.
                // A missing pair is a contract breach. Recording a generic failed would hide it.
                (string outcome, string reason) = RequireTerminalOutcome(result);
                FinishUpload(uploadSpan, outcome, TapeUploadOperation.Upload, reason, failed: true);
            }

            return result.Status == TapeDriveUploadStatus.Failed;
        }
        finally
        {
            if (held)
            {
                try
                {
                    await gate!.UnlockAsync(key).ConfigureAwait(false);
                }
                catch (Exception exception) when (IsLockSessionFault(exception))
                {
                    // The file outcome is already decided. An unlock fault must not replace it
                    // and must not abort the remaining files.
                    _logger.LogWarning(
                        "Tape export could not release its identity lock ({ExceptionType}). The file outcome stands.",
                        exception.GetType().Name);
                }
            }

            uploadSpan?.Dispose();
        }
    }

    /// <summary>
    /// The outcome and reason a failed upload carries. Null is a contract breach, not a generic failure.
    /// </summary>
    /// <param name="result">The uploader's result. Status is failed.</param>
    /// <returns>The tags the pass records.</returns>
    /// <exception cref="InvalidOperationException">Either tag is missing.</exception>
    internal static (string Outcome, string Reason) RequireTerminalOutcome(TapeDriveUploadResult result)
    {
        string? outcome = result.TerminalOutcome;
        string? reason = result.TerminalReason;
        if (string.IsNullOrEmpty(outcome) || string.IsNullOrEmpty(reason))
        {
            throw new InvalidOperationException("A failed Drive upload returned no terminal outcome.");
        }

        return (outcome, reason);
    }

    private async Task ReportPendingAsync(CancellationToken cancellationToken)
    {
        int pending = await _database.TapeExportReceipts.AsNoTracking()
            .CountAsync(row => row.UploadState == TapeExportReceipt.StatePending, cancellationToken)
            .ConfigureAwait(false);
        if (pending == 0)
        {
            return;
        }

        _logger.LogWarning(
            "Tape export found {PendingReceipts} receipt(s) still pending from an earlier pass.",
            pending);
        RecordUpload(
            TapeUploadOutcome.OrphanPending,
            TapeUploadOperation.Upload,
            TapeUploadReason.OrphanPending,
            failed: false);
    }

    private async Task FailLockSessionAsync(
        ITapeIdentitySession gate,
        DateOnly day,
        TapeExportStoredFile file,
        DateTimeOffset exportedAtUtc,
        Exception exception,
        Activity? uploadSpan,
        TapeDriveUploadResult? upload,
        CancellationToken cancellationToken)
    {
        // One file. The receipt must not stay pending, and this is its only terminal measurement.
        // export_failed is the pass counter architecture.md names for a caught failure. Returning
        // true marks the day's tape.export span Error and lets the remaining files continue.
        _logger.LogError(
            "Tape export lock session failed for {Path} ({ExceptionType}). Drive failure {FailureType}. The file failed and the pass continued.",
            file.RelativePath,
            exception.GetType().Name,
            upload?.FailureType);
        await CommitResultAsync(
            day,
            file,
            exportedAtUtc,
            upload?.FileId,
            TapeExportReceipt.StateFailed,
            exception.GetType().Name,
            failureStatus: null,
            cancellationToken).ConfigureAwait(false);
        string? driveFailure = upload?.Status == TapeDriveUploadStatus.Failed ? upload.FailureType : null;
        if (uploadSpan is null)
        {
            RecordUpload(
                TapeUploadOutcome.Failed,
                TapeUploadOperation.Lock,
                TapeUploadReason.LockFault,
                failed: true,
                driveFailure);
        }
        else
        {
            FinishUpload(
                uploadSpan,
                TapeUploadOutcome.Failed,
                TapeUploadOperation.Lock,
                TapeUploadReason.LockFault,
                failed: true,
                driveFailure);
        }

        _telemetry.TapeExport(TapeExportOutcome.ExportFailed, 1);
        await ReopenQuietlyAsync(gate, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReopenQuietlyAsync(ITapeIdentitySession gate, CancellationToken cancellationToken)
    {
        try
        {
            await gate.ReopenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsLockSessionFault(exception))
        {
            _logger.LogWarning(
                "Tape export could not reopen its identity lock ({ExceptionType}).",
                exception.GetType().Name);
        }
    }

    private static bool IsLockSessionFault(Exception exception) =>
        exception is OperationCanceledException || exception is not OperationCanceledException;

    private void RecordUpload(
        string outcome,
        string operation,
        string reason,
        bool failed,
        string? driveFailureType = null)
    {
        using Activity? span = _telemetry.Activities.StartActivity("tape.upload", ActivityKind.Client);
        FinishUpload(span, outcome, operation, reason, failed, driveFailureType);
    }

    private void FinishUpload(
        Activity? span,
        string outcome,
        string operation,
        string reason,
        bool failed,
        string? driveFailureType = null)
    {
        span?.SetTag(HostTelemetry.OperationTag, operation);
        span?.SetTag(HostTelemetry.ReasonTag, reason);
        span?.SetTag(HostTelemetry.OutcomeTag, outcome);
        if (!string.IsNullOrEmpty(driveFailureType))
        {
            span?.SetTag(DriveFailureSpanTag, driveFailureType);
        }

        span?.SetStatus(failed ? ActivityStatusCode.Error : ActivityStatusCode.Ok);
        _telemetry.TapeUpload(outcome, 1, operation, reason);
    }

    private static async Task AdvisoryAsync(
        NpgsqlConnection connection,
        string function,
        string key,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(
            "SELECT " + function + "(hashtextextended(@key, 0))",
            connection);
        // Longer than lock_timeout, so a busy identity returns 55P03 instead of a client-side timeout.
        command.CommandTimeout = 60;
        command.Parameters.AddWithValue("key", key);
        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> CommitPendingAsync(
        DateOnly day,
        TapeExportStoredFile file,
        DateTimeOffset exportedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var pending = await _database.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            string? known = await _database.TapeExportReceipts.AsNoTracking()
                .Where(row => row.Day == day && row.Kind == file.Kind && row.Format == file.Format
                    && row.Venue == file.Venue && row.Instrument == file.Instrument
                    && row.ContractId == file.ContractId && row.ResolutionMinutes == file.ResolutionMinutes)
                .Select(row => row.DriveFileId)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            await RecordReceiptAsync(
                day,
                file,
                exportedAtUtc,
                known,
                TapeExportReceipt.StatePending,
                failureType: null,
                failureStatus: null,
                cancellationToken).ConfigureAwait(false);
            await pending.CommitAsync(cancellationToken).ConfigureAwait(false);
            return known;
        }
        catch
        {
            await pending.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task CommitResultAsync(
        DateOnly day,
        TapeExportStoredFile file,
        DateTimeOffset exportedAtUtc,
        string? driveFileId,
        string state,
        string? failureType,
        int? failureStatus,
        CancellationToken cancellationToken)
    {
        await using var done = await _database.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await RecordReceiptAsync(
                day,
                file,
                exportedAtUtc,
                driveFileId,
                state,
                failureType,
                failureStatus,
                cancellationToken).ConfigureAwait(false);
            await done.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await done.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
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
        bool driveOn = _drive.IsEnabled;
        List<DateOnly> finished = await _database.TapeExportReceipts.AsNoTracking()
            .Where(row => row.UploadState == TapeExportReceipt.StateUploaded
                || (!driveOn && row.UploadState == TapeExportReceipt.StateDriveDisabled))
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

        bool driveOn = _drive.IsEnabled;
        List<TapeExportReceipt> done = await _database.TapeExportReceipts.AsNoTracking()
            .Where(row => row.UploadState == TapeExportReceipt.StateUploaded
                || (!driveOn && row.UploadState == TapeExportReceipt.StateDriveDisabled))
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

    private string TradeDaysSql =>
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
              AND 
        """ + FinishedUploadStateSql + """
              AND r."Sha256" <> '')
        """;

    private string CoverageDaysSql =>
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
              AND 
        """ + FinishedUploadStateSql + """
              AND r."Sha256" <> '')
        """;

    private string FootprintDaysSql =>
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
              AND 
        """ + FinishedUploadStateSql + """
              AND r."Sha256" <> '')
        """;

    private string FinishedUploadStateSql => _drive.IsEnabled
        ? "r.\"UploadState\" = 'uploaded'"
        : "r.\"UploadState\" IN ('uploaded', 'drive_disabled')";

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

    /// <summary>
    /// The session string for the identity lock. Pooling is off. Keepalive is the TCP socket option.
    /// </summary>
    /// <param name="source">The store connection string, including the password.</param>
    /// <returns>The string passed to <c>CloneWith</c>.</returns>
    internal static string IdentityLockConnectionString(string source)
    {
        NpgsqlConnectionStringBuilder builder = new(source)
        {
            Pooling = false,
            ApplicationName = IdentityGate.ApplicationName,
            KeepAlive = 0,
            TcpKeepAlive = true,
            TcpKeepAliveTime = 30,
            TcpKeepAliveInterval = 30,
        };
        return builder.ConnectionString;
    }

    /// <summary>Whether a lost-lock catch should treat this as the connection dying.</summary>
    /// <param name="exception">The exception from the lock, the probe, or the unlock.</param>
    /// <returns>
    /// True for a dropped session. False for a programming error, an ordinary SQL error, or a
    /// bare <see cref="NpgsqlException"/>. A false result is not a lost lock. The pass fails that
    /// file and continues.
    /// </returns>
    internal static bool IsIdentityConnectionFailure(Exception exception) =>
        IdentityGate.IsConnectionFailure(exception);

    /// <summary>
    /// One direct session for every identity in a pass. Pooling stays off so the advisory lock cannot
    /// go back to the pool still held. <c>lock_timeout</c> bounds a waiter. TCP keepalive (the socket
    /// option, not Npgsql's keepalive query) notices a half-open session. A drop is a lost lock, not a
    /// second owner.
    /// </summary>
    private sealed class IdentityGate : ITapeIdentitySession
    {
        internal const string ApplicationName = "tape-export-lock";

        private readonly TopstepXDbContext _database;
        private readonly ILogger _logger;
        private NpgsqlConnection? _connection;

        private IdentityGate(TopstepXDbContext database, NpgsqlConnection connection, ILogger logger)
        {
            _database = database;
            _connection = connection;
            _logger = logger;
        }

        internal static async Task<IdentityGate> OpenAsync(
            TopstepXDbContext database,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            NpgsqlConnection connection = await OpenConnectionAsync(database, cancellationToken).ConfigureAwait(false);
            return new IdentityGate(database, connection, logger);
        }

        public async Task<TapeIdentityLockTake> TryLockAsync(string key, CancellationToken cancellationToken)
        {
            try
            {
                if (_connection is null || _connection.State != ConnectionState.Open)
                {
                    await ReopenAsync(cancellationToken).ConfigureAwait(false);
                }

                await AdvisoryAsync(_connection!, "pg_advisory_lock", key, cancellationToken).ConfigureAwait(false);
                return TapeIdentityLockTake.Held;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (IsLockTimeout(exception))
            {
                return TapeIdentityLockTake.Busy;
            }
            catch (Exception exception) when (IsConnectionFailure(exception))
            {
                _logger.LogWarning(
                    "Tape export lost its identity lock ({ExceptionType}).",
                    exception.GetType().Name);
                return TapeIdentityLockTake.Lost;
            }
        }

        public async Task<bool> ProbeAsync()
        {
            if (_connection is null || _connection.State != ConnectionState.Open)
            {
                return false;
            }

            try
            {
                await using NpgsqlCommand command = new("SELECT 1", _connection);
                await command.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false);
                return true;
            }
            catch (Exception exception) when (IsConnectionFailure(exception))
            {
                _logger.LogWarning(
                    "Tape export lost its identity lock ({ExceptionType}).",
                    exception.GetType().Name);
                return false;
            }
        }

        public async Task UnlockAsync(string key)
        {
            if (_connection is null || _connection.State != ConnectionState.Open)
            {
                return;
            }

            try
            {
                await AdvisoryAsync(_connection, "pg_advisory_unlock", key, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsConnectionFailure(exception))
            {
                _logger.LogWarning(
                    "Tape export could not release its identity lock ({ExceptionType}).",
                    exception.GetType().Name);
            }
        }

        public async Task ReopenAsync(CancellationToken cancellationToken)
        {
            await DisposeConnectionAsync().ConfigureAwait(false);
            _connection = await OpenConnectionAsync(_database, cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync() => await DisposeConnectionAsync().ConfigureAwait(false);

        private async Task DisposeConnectionAsync()
        {
            if (_connection is null)
            {
                return;
            }

            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }

        private static async Task<NpgsqlConnection> OpenConnectionAsync(
            TopstepXDbContext database,
            CancellationToken cancellationToken)
        {
            if (database.Database.GetDbConnection() is not NpgsqlConnection source)
            {
                throw new InvalidOperationException("Tape export store is not Postgres.");
            }

            // The password is stripped from ConnectionString after open. CloneWith keeps it and turns pooling off,
            // so this session lock cannot return to the pool still held. A direct connection is required:
            // transaction-mode pooling would drop the session lock at the end of the statement.
            NpgsqlConnection connection = source.CloneWith(
                TapeExportPass.IdentityLockConnectionString(source.ConnectionString));
            try
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using NpgsqlCommand timeout = new("SELECT set_config('lock_timeout', '30s', false)", connection);
                await timeout.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                return connection;
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        private static bool IsLockTimeout(Exception exception)
        {
            for (Exception? current = exception; current is not null; current = current.InnerException)
            {
                if (current is PostgresException postgres && postgres.SqlState == PostgresErrorCodes.LockNotAvailable)
                {
                    return true;
                }
            }

            return false;
        }

        internal static bool IsConnectionFailure(Exception exception)
        {
            for (Exception? current = exception; current is not null; current = current.InnerException)
            {
                // SocketException does not extend IOException. A reset socket is a dropped session.
                if (current is TimeoutException or IOException or System.Net.Sockets.SocketException)
                {
                    return true;
                }

                if (current is PostgresException postgres)
                {
                    // A server error is a lost lock only for these connection and shutdown states.
                    // Serialization failure is transient and is not this. 55P03 is IdentityBusy.
                    return postgres.SqlState is PostgresErrorCodes.ConnectionException
                        or PostgresErrorCodes.ConnectionDoesNotExist
                        or PostgresErrorCodes.ConnectionFailure
                        or PostgresErrorCodes.SqlClientUnableToEstablishSqlConnection
                        or PostgresErrorCodes.SqlServerRejectedEstablishmentOfSqlConnection
                        or PostgresErrorCodes.AdminShutdown
                        or PostgresErrorCodes.CrashShutdown
                        or PostgresErrorCodes.CannotConnectNow;
                }

                // A bare NpgsqlException is not a lost lock. The walk above already matched an
                // IOException, a SocketException, or a TimeoutException further in. Npgsql's
                // IsTransient is that same chain, so a separate branch would change nothing.
                // The pass fails the file and continues.
            }

            return false;
        }
    }
}
