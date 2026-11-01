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
public readonly record struct TapeRetentionRun(
    long Rows,
    int Batches,
    int DaysSkipped,
    bool StoppedForLease,
    DateTimeOffset CutoffUtc);

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
/// The select is a range on <see cref="DeleteIndex"/>. Each batch autocommits. The caller must not
/// wrap <see cref="PruneAsync"/> in a transaction.
/// </para>
/// </remarks>
public sealed class TapeRetentionPass : ITapeRetentionPass
{
    /// <summary>Rows removed per delete. One statement, then a commit.</summary>
    public const int DefaultBatchSize = 1_000;

    /// <summary>
    /// The index the batch select range-scans: <c>(Instrument, TradeTimeUtc)</c>. The outer
    /// delete repeats the day bounds so a hypertable can exclude other chunks.
    /// </summary>
    public const string DeleteIndex = "IX_Trades_Instrument_TradeTimeUtc";

    /// <summary>
    /// The delete the pass runs. Tests <c>EXPLAIN</c> this text. Parameters are
    /// <c>@instrument</c>, <c>@contract</c>, <c>@dayStart</c>, <c>@dayEnd</c> and <c>@batch</c>.
    /// </summary>
    public const string DeleteBatchSql =
        """
        WITH doomed AS (
            SELECT "Venue", "Instrument", "ContractId", "TradeTimeUtc", "Sequence"
            FROM "Trades"
            WHERE "Instrument" = @instrument
              AND "ContractId" = @contract
              AND "TradeTimeUtc" >= @dayStart
              AND "TradeTimeUtc" < @dayEnd
            ORDER BY "TradeTimeUtc"
            LIMIT @batch
        )
        DELETE FROM "Trades" AS t
        USING doomed AS d
        WHERE t."Venue" = d."Venue"
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

    /// <summary>Creates a pass over the store the caller opened.</summary>
    /// <param name="database">The store. The caller owns its lifetime.</param>
    /// <param name="options">The retention window.</param>
    /// <param name="clock">The clock the cutoff is read from.</param>
    /// <param name="logger">Receives one line per day, before any delete.</param>
    /// <param name="telemetry">Counts rows removed and days skipped.</param>
    /// <param name="batchSize">Rows per delete. Tests pass a smaller number.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="batchSize"/> is below 1.</exception>
    public TapeRetentionPass(
        TopstepXDbContext database,
        IOptions<TapeRetentionOptions> options,
        TimeProvider clock,
        ILogger<TapeRetentionPass> logger,
        HostTelemetry telemetry,
        int batchSize = DefaultBatchSize)
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
            span?.SetTag(
                HostTelemetry.OutcomeTag,
                run.StoppedForLease ? "lease_lost" : run.Rows > 0 ? "pruned" : "skipped");
            return run;
        }
        catch
        {
            span?.SetTag(HostTelemetry.OutcomeTag, "failed");
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
            return new TapeRetentionRun(0, 0, 0, true, cutoff);
        }

        List<ExportedDay> confirmed = await ConfirmedDaysAsync(eligibleEnd, cancellationToken)
            .ConfigureAwait(false);
        List<SkippedDay> skipped = await SkippedDaysAsync(eligibleEnd, cancellationToken)
            .ConfigureAwait(false);
        foreach (SkippedDay day in skipped)
        {
            _logger.LogInformation(
                "Tape retention skipped {Day:yyyy-MM-dd} {Instrument}: no uploaded trades receipt.",
                day.Day,
                day.Instrument);
        }

        _telemetry.TapePruneSkipped(skipped.Count);
        bool hypertable = await TradesIsHypertableAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset? droppedUntil = await DropExportedPrefixAsync(
            holdsLease, eligibleEnd, hypertable, cancellationToken).ConfigureAwait(false);
        if (droppedUntil is null && hypertable && !holdsLease())
        {
            return new TapeRetentionRun(0, 0, skipped.Count, true, cutoff);
        }

        long rows = 0;
        int batches = 0;
        foreach (ExportedDay day in confirmed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!holdsLease())
            {
                return Finish(rows, batches, skipped.Count, stopped: true, cutoff);
            }

            DateTimeOffset dayStart = DayStart(day.Day);
            DateTimeOffset dayEnd = dayStart.AddDays(1);
            if (droppedUntil is DateTimeOffset dropped && dayEnd <= dropped)
            {
                rows += day.RowCount;
                continue;
            }

            if (hypertable && await OverlapsCompressedChunkAsync(dayStart, dayEnd, cancellationToken)
                    .ConfigureAwait(false))
            {
                _logger.LogInformation(
                    "Tape retention left {Day:yyyy-MM-dd} {Instrument} {Contract}: "
                    + "the compressed chunk is not fully exported.",
                    day.Day,
                    day.Instrument,
                    day.ContractId);
                continue;
            }

            _logger.LogInformation(
                "Tape retention will delete about {Rows} Trades rows for {Instrument} {Contract} "
                + "on {Day:yyyy-MM-dd}.",
                day.RowCount,
                day.Instrument,
                day.ContractId,
                day.Day);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!holdsLease())
                {
                    return Finish(rows, batches, skipped.Count, stopped: true, cutoff);
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

        return Finish(rows, batches, skipped.Count, stopped: false, cutoff);
    }

    private TapeRetentionRun Finish(long rows, int batches, int skipped, bool stopped, DateTimeOffset cutoff)
    {
        _logger.LogInformation(
            "Tape retention removed {Rows} Trades rows in {Batches} delete batches. "
            + "Skipped {Skipped} day/instrument pairs with no uploaded trades receipt. Cutoff {Cutoff:u}.",
            rows,
            batches,
            skipped,
            cutoff);
        _telemetry.TapePruned(rows);
        return new TapeRetentionRun(rows, batches, skipped, stopped, cutoff);
    }

    private async Task<DateTimeOffset?> DropExportedPrefixAsync(
        Func<bool> holdsLease,
        DateTimeOffset eligibleEnd,
        bool hypertable,
        CancellationToken cancellationToken)
    {
        if (!hypertable)
        {
            return null;
        }

        List<ChunkBounds> chunks = await ChunksEndingBeforeAsync(eligibleEnd, cancellationToken)
            .ConfigureAwait(false);
        DateTimeOffset? prefixEnd = null;
        foreach (ChunkBounds chunk in chunks)
        {
            if (!holdsLease())
            {
                return null;
            }

            if (!await ChunkIsFullyExportedAsync(chunk.Start, chunk.End, cancellationToken)
                    .ConfigureAwait(false))
            {
                break;
            }

            prefixEnd = chunk.End;
        }

        if (prefixEnd is not DateTimeOffset olderThan)
        {
            return null;
        }

        if (!holdsLease())
        {
            return null;
        }

        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction =
            await _database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await _database.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '5s'", cancellationToken)
            .ConfigureAwait(false);
        _ = await _database.Database.SqlQuery<string>(
                $"""
                SELECT drop_chunks('public."Trades"'::regclass, older_than => {olderThan})::text AS "Value"
                """)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Tape retention dropped Trades chunks that end at or before {OlderThan:u}.",
            olderThan);
        return olderThan;
    }

    private async Task<int> DeleteBatchAsync(
        ExportedDay day,
        DateTimeOffset dayStart,
        DateTimeOffset dayEnd,
        CancellationToken cancellationToken)
    {
        NpgsqlParameter[] parameters =
        [
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
        CancellationToken cancellationToken)
    {
        List<SkippedDay> days = [];
        await _database.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using DbCommand command = _database.Database.GetDbConnection().CreateCommand();
            command.CommandText =
                """
                SELECT (t."TradeTimeUtc" AT TIME ZONE 'UTC')::date AS day, t."Instrument"
                FROM "Trades" AS t
                WHERE t."TradeTimeUtc" < @end
                  AND NOT EXISTS (
                      SELECT 1 FROM "TapeExportReceipts" AS r
                      WHERE r."Day" = (t."TradeTimeUtc" AT TIME ZONE 'UTC')::date
                        AND r."Venue" = t."Venue"
                        AND r."Instrument" = t."Instrument"
                        AND r."ContractId" = t."ContractId"
                        AND r."Kind" = 'trades'
                        AND r."Format" = 'csv.gz'
                        AND r."UploadState" = 'uploaded'
                        AND r."Sha256" <> '')
                GROUP BY 1, 2
                ORDER BY 1, 2
                """;
            DbParameter end = command.CreateParameter();
            end.ParameterName = "end";
            end.Value = eligibleEnd;
            command.Parameters.Add(end);
            await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                days.Add(new SkippedDay(reader.GetFieldValue<DateOnly>(0), reader.GetString(1)));
            }
        }
        finally
        {
            await _database.Database.CloseConnectionAsync().ConfigureAwait(false);
        }

        return days;
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
        CancellationToken cancellationToken)
    {
        List<int> missing = await _database.Database.SqlQuery<int>(
                $"""
                SELECT 1 AS "Value"
                FROM "Trades" AS t
                WHERE t."TradeTimeUtc" >= {start}
                  AND t."TradeTimeUtc" < {end}
                  AND NOT EXISTS (
                      SELECT 1 FROM "TapeExportReceipts" AS r
                      WHERE r."Day" = (t."TradeTimeUtc" AT TIME ZONE 'UTC')::date
                        AND r."Venue" = t."Venue"
                        AND r."Instrument" = t."Instrument"
                        AND r."ContractId" = t."ContractId"
                        AND r."Kind" = 'trades'
                        AND r."Format" = 'csv.gz'
                        AND r."UploadState" = 'uploaded'
                        AND r."Sha256" <> '')
                LIMIT 1
                """)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return missing.Count == 0;
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

    private readonly record struct ExportedDay(
        DateOnly Day,
        string Venue,
        string Instrument,
        string ContractId,
        int RowCount);

    private readonly record struct SkippedDay(DateOnly Day, string Instrument);

    private readonly record struct ChunkBounds(DateTimeOffset Start, DateTimeOffset End);
}
