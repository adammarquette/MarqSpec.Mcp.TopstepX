using System.Data;
using System.Data.Common;
using System.Diagnostics;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>What one retention pass did. A second call can repeat it.</summary>
/// <param name="Rows">Rows removed.</param>
/// <param name="Batches">Delete statements that removed at least one row.</param>
/// <param name="DaysSkipped">Day and instrument pairs left because no uploaded receipt exists.</param>
/// <param name="StoppedForLease">True when the recorder lease was lost before the pass finished.</param>
/// <param name="CutoffUtc">Days that ended at or before this instant were eligible.</param>
/// <param name="ChunksDropped">Chunks <c>drop_chunks</c> removed. Not a row count.</param>
/// <param name="ChunksDroppedUntil">
/// Those chunks ended at or before this instant. Null when none dropped.
/// </param>
public readonly record struct TapeRetentionRun(
    long Rows,
    int Batches,
    int DaysSkipped,
    bool StoppedForLease,
    DateTimeOffset CutoffUtc,
    int ChunksDropped = 0,
    DateTimeOffset? ChunksDroppedUntil = null);

/// <summary>The store operation the recorder's retention worker runs (ADR-0027).</summary>
public interface ITapeRetentionPass
{
    /// <summary>
    /// Deletes exported trade days older than the window, or nothing when no receipt exists.
    /// </summary>
    /// <param name="holdsLease">Read before every batch and before <c>drop_chunks</c>.</param>
    /// <param name="cancellationToken">Cancels between batches. A committed batch stays deleted.</param>
    /// <returns>What this pass removed and what it refused to remove.</returns>
    Task<TapeRetentionRun> PruneAsync(Func<bool> holdsLease, CancellationToken cancellationToken);
}

/// <summary>
/// Deletes <c>Trades</c> for a UTC day only when an uploaded trades receipt confirms it (gh#763).
/// </summary>
/// <remarks>
/// <para>
/// <b>No receipt means no delete.</b> The receipt is <c>Kind=trades</c>, <c>Format=csv.gz</c>,
/// <c>UploadState=uploaded</c>, and a non-empty <c>Sha256</c>, for that day, venue, instrument and
/// contract. <c>drive_disabled</c> does not qualify. <c>LatestQuotes</c> is not tape. This pass has
/// no statement against it.
/// </para>
/// <para>
/// <b>Chunks.</b> <c>drop_chunks</c> removes a prefix of chunks that end at or before the cutoff and
/// whose every row has that receipt. The statement runs in its own transaction with
/// <c>lock_timeout</c> of 5 seconds. A compressed chunk that fails the test is left. It is not
/// decompressed. The row delete then skips any day that still sits in a compressed chunk.
/// </para>
/// <para>
/// The batched delete adds the day bounds on the outer statement so the planner can exclude chunks.
/// The predicate is the leading columns of <c>PK_Trades</c>. The planner may use that key or
/// <see cref="DeleteIndex"/>, with venue and contract as filters. Each batch autocommits. The caller
/// must not wrap <see cref="PruneAsync"/> in a transaction.
/// </para>
/// </remarks>
public sealed class TapeRetentionPass : ITapeRetentionPass
{
    /// <summary>Rows removed per delete. One statement, then a commit.</summary>
    public const int DefaultBatchSize = 1_000;

    /// <summary>
    /// The predicate is venue, instrument, contract and the day: the leading columns of
    /// <c>PK_Trades</c>. The planner may use that key or
    /// <c>IX_Trades_Instrument_TradeTimeUtc</c>, with venue and contract as filters.
    /// The test requires an index scan, not one index name.
    /// </summary>
    public const string DeleteIndex = "IX_Trades_Instrument_TradeTimeUtc";

    /// <summary>How many distinct tape keys one pass will walk. Past this, a chunk is not dropped.</summary>
    public const int KeyCap = 4_096;

    /// <summary>
    /// How long <c>drop_chunks</c> waits for a lock before the pass continues with deletes.
    /// </summary>
    public static readonly TimeSpan DefaultDropLockTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The delete the pass runs. Tests <c>EXPLAIN</c> this text. Parameters are
    /// <c>@venue</c>, <c>@instrument</c>, <c>@contract</c>, <c>@dayStart</c>, <c>@dayEnd</c>
    /// and <c>@batch</c>.
    /// </summary>
    public const string DeleteBatchSql =
        """
        WITH doomed AS (
            SELECT "Venue", "Instrument", "ContractId", "TradeTimeUtc", "Sequence"
            FROM "Trades"
            WHERE "Venue" = @venue
              AND "Instrument" = @instrument
              AND "ContractId" = @contract
              AND "TradeTimeUtc" >= @dayStart
              AND "TradeTimeUtc" < @dayEnd
            ORDER BY "TradeTimeUtc"
            LIMIT @batch
        )
        DELETE FROM "Trades" AS t
        USING doomed AS d
        WHERE t."Venue" = d."Venue"
          AND t."Venue" = @venue
          AND t."Instrument" = d."Instrument"
          AND t."ContractId" = d."ContractId"
          AND t."TradeTimeUtc" = d."TradeTimeUtc"
          AND t."Sequence" = d."Sequence"
          AND t."TradeTimeUtc" >= @dayStart
          AND t."TradeTimeUtc" < @dayEnd
        """;

    private readonly TopstepXDbContext _database;
    private readonly TapeRetentionOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<TapeRetentionPass> _logger;
    private readonly HostTelemetry _telemetry;
    private readonly int _batchSize;
    private readonly TimeSpan _dropLockTimeout;

    /// <summary>Creates a pass over the store the caller opened.</summary>
    /// <param name="database">The store. The caller owns its lifetime.</param>
    /// <param name="options">The retention window.</param>
    /// <param name="clock">The clock the cutoff is read from.</param>
    /// <param name="logger">Receives one line per day, before any delete.</param>
    /// <param name="telemetry">Counts rows removed and days skipped.</param>
    /// <param name="batchSize">Rows per delete. Tests pass a smaller number.</param>
    /// <param name="dropLockTimeout">
    /// How long <c>drop_chunks</c> waits for a lock. A timeout skips the drop and the pass
    /// continues with row deletes. Tests pass a short wait.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="batchSize"/> is below 1.</exception>
    public TapeRetentionPass(
        TopstepXDbContext database,
        IOptions<TapeRetentionOptions> options,
        TimeProvider clock,
        ILogger<TapeRetentionPass> logger,
        HostTelemetry telemetry,
        int batchSize = DefaultBatchSize,
        TimeSpan? dropLockTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(telemetry);
        if (batchSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }

        _database = database;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
        _telemetry = telemetry;
        _batchSize = batchSize;
        _dropLockTimeout = dropLockTimeout ?? DefaultDropLockTimeout;
    }

    /// <inheritdoc />
    public async Task<TapeRetentionRun> PruneAsync(Func<bool> holdsLease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(holdsLease);
        if (_database.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException(
                "Tape retention refuses to run inside a transaction. Each batch commits on its own.");
        }

        DateTimeOffset cutoff = _clock.GetUtcNow() - TimeSpan.FromDays(_options.Days);
        DateTimeOffset eligibleEnd = FirstKeptDayStart(cutoff);
        using Activity? span = _telemetry.Activities.StartActivity("tape.retention");
        try
        {
            TapeRetentionRun run = await PruneInsideAsync(holdsLease, cutoff, eligibleEnd, cancellationToken)
                .ConfigureAwait(false);
            span?.SetTag(HostTelemetry.OutcomeTag, Outcome(run));
            if (run.DaysSkipped > 0)
            {
                span?.SetTag(TapeRetentionOutcome.SkippedDays, run.DaysSkipped);
            }

            if (run.ChunksDropped > 0)
            {
                span?.SetTag(TapeRetentionOutcome.ChunksDropped, run.ChunksDropped);
                if (run.ChunksDroppedUntil is DateTimeOffset until)
                {
                    span?.SetTag(TapeRetentionOutcome.DroppedUntil, until.ToString("u"));
                }
            }

            return run;
        }
        catch
        {
            span?.SetTag(HostTelemetry.OutcomeTag, TapeRetentionOutcome.Failed);
            throw;
        }
    }

    private async Task<TapeRetentionRun> PruneInsideAsync(
        Func<bool> holdsLease,
        DateTimeOffset cutoff,
        DateTimeOffset eligibleEnd,
        CancellationToken cancellationToken)
    {
        if (!holdsLease())
        {
            return LeaseLost(0, 0, 0, cutoff);
        }

        List<ExportedDay> confirmed = await ConfirmedDaysAsync(eligibleEnd, cancellationToken)
            .ConfigureAwait(false);
        HashSet<TapeKey> confirmedKeys = confirmed.Select(day => day.Key).ToHashSet();
        List<SkippedDay> skipped = await SkippedDaysAsync(eligibleEnd, confirmedKeys, cancellationToken)
            .ConfigureAwait(false);
        foreach (SkippedDay day in skipped)
        {
            _logger.LogInformation(
                "Tape retention skipped {Day:yyyy-MM-dd} {Venue} {Instrument} {Contract}: "
                + "no uploaded trades receipt.",
                day.Day,
                day.Venue,
                day.Instrument,
                day.ContractId);
        }

        _telemetry.TapePruneSkipped(skipped.Count, TapePruneReason.NoReceipt);
        bool hypertable = await TradesIsHypertableAsync(cancellationToken).ConfigureAwait(false);
        (DateTimeOffset? droppedUntil, int chunksDropped) = await DropExportedPrefixAsync(
            holdsLease, eligibleEnd, hypertable, confirmedKeys, cancellationToken).ConfigureAwait(false);
        if (droppedUntil is null && hypertable && !holdsLease())
        {
            return LeaseLost(0, 0, skipped.Count, cutoff, chunksDropped, droppedUntil);
        }

        long rows = 0;
        int batches = 0;
        foreach (ExportedDay day in confirmed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!holdsLease())
            {
                return LeaseLost(rows, batches, skipped.Count, cutoff, chunksDropped, droppedUntil);
            }

            DateTimeOffset dayStart = DayStart(day.Day);
            DateTimeOffset dayEnd = dayStart.AddDays(1);
            if (droppedUntil is DateTimeOffset dropped && dayEnd <= dropped)
            {
                _logger.LogInformation(
                    "Tape retention dropped {Day:yyyy-MM-dd} {Venue} {Instrument} {Contract} "
                    + "with its chunk. The receipt RowCount is about {ReceiptRows}, not a measured delete.",
                    day.Day,
                    day.Venue,
                    day.Instrument,
                    day.ContractId,
                    day.RowCount);
                continue;
            }

            if (hypertable && await OverlapsCompressedChunkAsync(dayStart, dayEnd, cancellationToken)
                    .ConfigureAwait(false))
            {
                _logger.LogInformation(
                    "Tape retention left {Day:yyyy-MM-dd} {Venue} {Instrument} {Contract}: "
                    + "the compressed chunk is not fully exported.",
                    day.Day,
                    day.Venue,
                    day.Instrument,
                    day.ContractId);
                continue;
            }

            _logger.LogInformation(
                "Tape retention will delete about {ReceiptRows} receipt rows for {Venue} "
                + "{Instrument} {Contract} on {Day:yyyy-MM-dd}.",
                day.RowCount,
                day.Venue,
                day.Instrument,
                day.ContractId,
                day.Day);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!holdsLease())
                {
                    return LeaseLost(
                        rows, batches, skipped.Count, cutoff, chunksDropped, droppedUntil);
                }

                int deleted = await DeleteBatchAsync(day, dayStart, dayEnd, cancellationToken)
                    .ConfigureAwait(false);
                if (deleted == 0)
                {
                    break;
                }

                batches++;
                rows += deleted;
            }
        }

        return Finish(rows, batches, skipped.Count, false, cutoff, chunksDropped, droppedUntil);
    }

    private TapeRetentionRun Finish(
        long rows,
        int batches,
        int skipped,
        bool stopped,
        DateTimeOffset cutoff,
        int chunksDropped = 0,
        DateTimeOffset? chunksDroppedUntil = null)
    {
        _logger.LogInformation(
            "Tape retention removed {Rows} Trades rows in {Batches} delete batches "
            + "and dropped {Chunks} chunks. "
            + "Skipped {Skipped} keys with no uploaded trades receipt. Cutoff {Cutoff:u}.",
            rows,
            batches,
            chunksDropped,
            skipped,
            cutoff);
        _telemetry.TapePruned(rows);
        return new TapeRetentionRun(
            rows, batches, skipped, stopped, cutoff, chunksDropped, chunksDroppedUntil);
    }

    private TapeRetentionRun LeaseLost(
        long rows,
        int batches,
        int skipped,
        DateTimeOffset cutoff,
        int chunksDropped = 0,
        DateTimeOffset? chunksDroppedUntil = null)
    {
        _telemetry.TapePruneLeaseLost();
        return Finish(rows, batches, skipped, true, cutoff, chunksDropped, chunksDroppedUntil);
    }

    private static string Outcome(TapeRetentionRun run)
    {
        if (run.StoppedForLease)
        {
            return TapeRetentionOutcome.LeaseLost;
        }

        if (run.Rows > 0 || run.ChunksDropped > 0)
        {
            return TapeRetentionOutcome.Pruned;
        }

        return run.DaysSkipped > 0 ? TapeRetentionOutcome.Skipped : TapeRetentionOutcome.Idle;
    }

    private async Task<(DateTimeOffset? Until, int Count)> DropExportedPrefixAsync(
        Func<bool> holdsLease,
        DateTimeOffset eligibleEnd,
        bool hypertable,
        HashSet<TapeKey> confirmed,
        CancellationToken cancellationToken)
    {
        if (!hypertable)
        {
            return (null, 0);
        }

        List<ChunkBounds> chunks = await ChunksEndingBeforeAsync(eligibleEnd, cancellationToken)
            .ConfigureAwait(false);
        DateTimeOffset? prefixEnd = null;
        foreach (ChunkBounds chunk in chunks)
        {
            if (!holdsLease())
            {
                return (null, 0);
            }

            if (!await ChunkIsFullyExportedAsync(chunk.Start, chunk.End, confirmed, cancellationToken)
                    .ConfigureAwait(false))
            {
                break;
            }

            prefixEnd = chunk.End;
        }

        if (prefixEnd is not DateTimeOffset olderThan)
        {
            return (null, 0);
        }

        if (!holdsLease())
        {
            return (null, 0);
        }

        string timeout = ((int)_dropLockTimeout.TotalMilliseconds).ToString(
            System.Globalization.CultureInfo.InvariantCulture) + "ms";
        bool dropped = false;
        int droppedCount = 0;
        await using (Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction =
            await _database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                _ = await _database.Database.SqlQuery<string>(
                        $"""SELECT set_config('lock_timeout', {timeout}, true) AS "Value" """)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
                List<string> names = await _database.Database.SqlQuery<string>(
                        $"""
                        SELECT drop_chunks(
                            'public."Trades"'::regclass, older_than => {olderThan})::text AS "Value"
                        """)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                droppedCount = names.Count;
                dropped = true;
            }
            catch (Exception exception) when (IsLockTimeout(exception))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogWarning(
                    exception,
                    "Tape retention skipped drop_chunks after lock_timeout. Row deletes continue.");
                _telemetry.TapePruneFailed(TapePruneReason.LockTimeout);
            }
        }

        if (!dropped)
        {
            return (null, 0);
        }

        _logger.LogInformation(
            "Tape retention dropped {Chunks} Trades chunks that end at or before {OlderThan:u}.",
            droppedCount,
            olderThan);
        _telemetry.TapeChunksDropped(droppedCount);
        return (olderThan, droppedCount);
    }

    private async Task<int> DeleteBatchAsync(
        ExportedDay day,
        DateTimeOffset dayStart,
        DateTimeOffset dayEnd,
        CancellationToken cancellationToken)
    {
        NpgsqlParameter[] parameters =
        [
            new("venue", day.Venue),
            new("instrument", day.Instrument),
            new("contract", day.ContractId),
            new("dayStart", dayStart),
            new("dayEnd", dayEnd),
            new("batch", _batchSize),
        ];
        return await _database.Database.ExecuteSqlRawAsync(DeleteBatchSql, parameters, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<List<ExportedDay>> ConfirmedDaysAsync(
        DateTimeOffset eligibleEnd,
        CancellationToken cancellationToken)
    {
        DateOnly firstKept = DateOnly.FromDateTime(eligibleEnd.UtcDateTime);
        return await _database.TapeExportReceipts.AsNoTracking()
            .Where(receipt => receipt.Kind == TapeExportArchive.TradesDirectory
                && receipt.Format == TapeExportArchive.CsvGzFormat
                && receipt.UploadState == TapeExportReceipt.StateUploaded
                && receipt.Sha256 != string.Empty
                && receipt.Day < firstKept)
            .OrderBy(receipt => receipt.Day)
            .ThenBy(receipt => receipt.Instrument)
            .ThenBy(receipt => receipt.ContractId)
            .Select(receipt => new ExportedDay(
                receipt.Day,
                receipt.Venue,
                receipt.Instrument,
                receipt.ContractId,
                receipt.RowCount))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<List<SkippedDay>> SkippedDaysAsync(
        DateTimeOffset eligibleEnd,
        HashSet<TapeKey> confirmed,
        CancellationToken cancellationToken)
    {
        List<TapeKey> keys = await KeysInRangeAsync(
            DateTimeOffset.MinValue, eligibleEnd, cancellationToken).ConfigureAwait(false);
        List<SkippedDay> skipped = [];
        foreach (TapeKey key in keys)
        {
            if (!confirmed.Contains(key))
            {
                skipped.Add(new SkippedDay(key.Day, key.Venue, key.Instrument, key.ContractId));
            }
        }

        if (keys.Count >= KeyCap)
        {
            _logger.LogInformation(
                "Tape retention stopped listing tape keys after {Cap}. Later keys wait for the next pass.",
                KeyCap);
            _telemetry.TapePruneSkipped(1, TapePruneReason.KeyCap);
        }

        return skipped;
    }

    private async Task<bool> TradesIsHypertableAsync(CancellationToken cancellationToken)
    {
        List<int> extension = await _database.Database.SqlQuery<int>(
                $"""SELECT 1 AS "Value" FROM pg_extension WHERE extname = 'timescaledb'""")
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (extension.Count == 0)
        {
            return false;
        }

        List<int> hypertable = await _database.Database.SqlQuery<int>(
                $"""
                SELECT 1 AS "Value"
                FROM timescaledb_information.hypertables
                WHERE hypertable_schema = 'public' AND hypertable_name = 'Trades'
                """)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return hypertable.Count > 0;
    }

    private async Task<List<ChunkBounds>> ChunksEndingBeforeAsync(
        DateTimeOffset eligibleEnd,
        CancellationToken cancellationToken)
    {
        List<ChunkBounds> chunks = [];
        await _database.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using DbCommand command = _database.Database.GetDbConnection().CreateCommand();
            command.CommandText =
                """
                SELECT range_start, range_end
                FROM timescaledb_information.chunks
                WHERE hypertable_schema = 'public'
                  AND hypertable_name = 'Trades'
                  AND range_end <= @end
                ORDER BY range_start
                """;
            DbParameter end = command.CreateParameter();
            end.ParameterName = "end";
            end.Value = eligibleEnd;
            command.Parameters.Add(end);
            await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                chunks.Add(new ChunkBounds(ReadTime(reader, 0), ReadTime(reader, 1)));
            }
        }
        finally
        {
            await _database.Database.CloseConnectionAsync().ConfigureAwait(false);
        }

        return chunks;
    }

    private async Task<bool> ChunkIsFullyExportedAsync(
        DateTimeOffset start,
        DateTimeOffset end,
        HashSet<TapeKey> confirmed,
        CancellationToken cancellationToken)
    {
        List<TapeKey> keys = await KeysInRangeAsync(start, end, cancellationToken).ConfigureAwait(false);
        if (keys.Count >= KeyCap)
        {
            _logger.LogInformation(
                "Tape retention did not drop the chunk ending {End:u}: the key walk hit the cap of {Cap}.",
                end,
                KeyCap);
            _telemetry.TapePruneSkipped(1, TapePruneReason.KeyCap);
            return false;
        }

        foreach (TapeKey key in keys)
        {
            if (!confirmed.Contains(key))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// One index probe per distinct (day, venue, instrument, contract), capped at <see cref="KeyCap"/>.
    /// </summary>
    private async Task<List<TapeKey>> KeysInRangeAsync(
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        List<TapeKey> keys = [];
        await _database.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using DbCommand command = _database.Database.GetDbConnection().CreateCommand();
            command.CommandText =
                """
                WITH RECURSIVE walk AS (
                    (
                        SELECT t."Venue", t."Instrument", t."ContractId",
                               (t."TradeTimeUtc" AT TIME ZONE 'UTC')::date AS day,
                               1 AS n
                        FROM "Trades" AS t
                        WHERE t."TradeTimeUtc" >= @start AND t."TradeTimeUtc" < @end
                        ORDER BY t."Venue", t."Instrument", t."ContractId", t."TradeTimeUtc"
                        LIMIT 1
                    )
                    UNION ALL
                    SELECT nxt."Venue", nxt."Instrument", nxt."ContractId", nxt.day, walk.n + 1
                    FROM walk
                    JOIN LATERAL (
                        SELECT t."Venue", t."Instrument", t."ContractId",
                               (t."TradeTimeUtc" AT TIME ZONE 'UTC')::date AS day
                        FROM "Trades" AS t
                        WHERE t."TradeTimeUtc" >= @start AND t."TradeTimeUtc" < @end
                          AND (
                              t."Venue" > walk."Venue"
                              OR (t."Venue" = walk."Venue" AND t."Instrument" > walk."Instrument")
                              OR (t."Venue" = walk."Venue" AND t."Instrument" = walk."Instrument"
                                  AND t."ContractId" > walk."ContractId")
                              OR (t."Venue" = walk."Venue" AND t."Instrument" = walk."Instrument"
                                  AND t."ContractId" = walk."ContractId"
                                  AND t."TradeTimeUtc" >= ((walk.day + 1)::timestamp AT TIME ZONE 'UTC'))
                          )
                        ORDER BY t."Venue", t."Instrument", t."ContractId", t."TradeTimeUtc"
                        LIMIT 1
                    ) AS nxt ON walk.n < @cap
                )
                SELECT "Venue", "Instrument", "ContractId", day FROM walk
                """;
            Add(command, "start", start);
            Add(command, "end", end);
            Add(command, "cap", KeyCap);
            await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                keys.Add(new TapeKey(
                    reader.GetFieldValue<DateOnly>(3),
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2)));
            }
        }
        finally
        {
            await _database.Database.CloseConnectionAsync().ConfigureAwait(false);
        }

        return keys;
    }

    private async Task<bool> OverlapsCompressedChunkAsync(
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        List<int> compressed = await _database.Database.SqlQuery<int>(
                $"""
                SELECT 1 AS "Value"
                FROM timescaledb_information.chunks
                WHERE hypertable_schema = 'public'
                  AND hypertable_name = 'Trades'
                  AND is_compressed
                  AND range_start < {end}
                  AND range_end > {start}
                LIMIT 1
                """)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return compressed.Count > 0;
    }

    private static DateTimeOffset FirstKeptDayStart(DateTimeOffset cutoff)
    {
        DateTime utc = cutoff.UtcDateTime;
        return new DateTimeOffset(DateOnly.FromDateTime(utc), TimeOnly.MinValue, TimeSpan.Zero);
    }

    private static DateTimeOffset DayStart(DateOnly day) =>
        new(day, TimeOnly.MinValue, TimeSpan.Zero);

    private static DateTimeOffset ReadTime(DbDataReader reader, int ordinal)
    {
        DateTime value = reader.GetFieldValue<DateTime>(ordinal);
        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }

    private static bool IsLockTimeout(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres
                && postgres.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                return true;
            }
        }

        return false;
    }

    private static void Add(DbCommand command, string name, object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private readonly record struct TapeKey(DateOnly Day, string Venue, string Instrument, string ContractId);

    private readonly record struct ExportedDay(
        DateOnly Day,
        string Venue,
        string Instrument,
        string ContractId,
        int RowCount)
    {
        public TapeKey Key => new(Day, Venue, Instrument, ContractId);
    }

    private readonly record struct SkippedDay(
        DateOnly Day,
        string Venue,
        string Instrument,
        string ContractId);

    private readonly record struct ChunkBounds(DateTimeOffset Start, DateTimeOffset End);
}
