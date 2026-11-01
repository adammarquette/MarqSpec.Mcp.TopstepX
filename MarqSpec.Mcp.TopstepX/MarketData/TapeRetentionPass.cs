using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>One retention pass: how many rows it removed, in how many delete batches.</summary>
/// <param name="Rows">Rows removed, including rows that left with a whole chunk.</param>
/// <param name="Batches">Delete statements that removed at least one row.</param>
/// <param name="CutoffUtc">Rows strictly older than this instant were eligible.</param>
public readonly record struct TapeRetentionRun(long Rows, int Batches, DateTimeOffset CutoffUtc);

/// <summary>
/// Deletes <c>Trades</c> rows older than <see cref="TapeRetentionOptions.Days"/> (gh#763).
/// </summary>
/// <remarks>
/// <para>
/// <b>The recorder owns this.</b> <see cref="TapeRetentionWorker"/> calls it only while this
/// process holds a recorder lease. A second run deletes nothing that the first run left inside
/// the window, so the pass is safe to repeat.
/// </para>
/// <para>
/// <b>Two steps, so a long lock and a WAL spike are both avoided.</b> When <c>Trades</c> is a
/// hypertable, <c>drop_chunks</c> removes chunks that end before the cutoff. That is a metadata
/// operation, including on a compressed chunk. Chunks that still contain a row inside the window
/// stay. Any compressed chunk that overlaps the cutoff is decompressed, and the rows older than
/// the cutoff are then deleted in batches of <see cref="DefaultBatchSize"/>. Each batch is its
/// own autocommit. The batch select is an index range scan on
/// <see cref="DeleteIndex"/> (<c>Instrument</c>, then <c>TradeTimeUtc</c>). A row at the cutoff
/// is not older than the window and is not deleted.
/// </para>
/// <para>
/// The caller must not wrap <see cref="PruneAsync"/> in a transaction. One transaction around
/// every batch would hold the locks this pass exists to release.
/// </para>
/// </remarks>
public sealed class TapeRetentionPass
{
    /// <summary>Rows removed per delete statement. One statement, then a commit.</summary>
    public const int DefaultBatchSize = 1_000;

    /// <summary>
    /// The index the batched delete range-scans. <c>(Instrument, TradeTimeUtc)</c>, created by
    /// <c>AddTradeInstrumentTimeIndex</c> (gh#756).
    /// </summary>
    public const string DeleteIndex = "IX_Trades_Instrument_TradeTimeUtc";

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
    /// <param name="logger">Receives one Information line per pass.</param>
    /// <param name="telemetry">Counts rows removed, when the count is positive.</param>
    /// <param name="batchSize">
    /// Rows per delete. Defaults to <see cref="DefaultBatchSize"/>. Tests pass a smaller number.
    /// </param>
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

    /// <summary>Removes tape rows older than the configured window.</summary>
    /// <param name="cancellationToken">
    /// Cancels between batches. A batch already committed stays committed.
    /// </param>
    /// <returns>What this pass removed.</returns>
    /// <exception cref="InvalidOperationException">
    /// A transaction is already open on the database.
    /// </exception>
    public async Task<TapeRetentionRun> PruneAsync(CancellationToken cancellationToken)
    {
        if (_database.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException(
                "Tape retention refuses to run inside a transaction. Each batch commits on its own.");
        }

        DateTimeOffset cutoff = _clock.GetUtcNow() - TimeSpan.FromDays(_options.Days);
        long chunkRows = await DropWholeChunksAsync(cutoff, cancellationToken).ConfigureAwait(false);
        (int batches, long batchRows) = await DeleteRemainderAsync(cutoff, cancellationToken)
            .ConfigureAwait(false);
        long rows = chunkRows + batchRows;

        _logger.LogInformation(
            "Tape retention removed {Rows} Trades rows older than {Cutoff:u} ({Days} days) "
            + "in {Batches} delete batches. {ChunkRows} of those rows left with whole chunks.",
            rows,
            cutoff,
            _options.Days,
            batches,
            chunkRows);

        int measured = rows > int.MaxValue ? int.MaxValue : (int)rows;
        _telemetry.TapePruned(measured);
        return new TapeRetentionRun(rows, batches, cutoff);
    }

    private async Task<long> DropWholeChunksAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        if (!await TradesIsHypertableAsync(cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }

        long before = await CountOlderAsync(cutoff, cancellationToken).ConfigureAwait(false);

        // Consuming the set is the call. An empty result means no chunk ended before the cutoff.
        _ = await _database.Database
            .SqlQuery<string>(
                $"""
                SELECT drop_chunks('public."Trades"'::regclass, older_than => {cutoff})::text AS "Value"
                """)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // A compressed chunk that still holds a row inside the window cannot be row-deleted until
        // it is decompressed. drop_chunks has already taken every chunk that ended before the cutoff.
        _ = await _database.Database
            .SqlQuery<string>(
                $"""
                SELECT decompress_chunk(
                    format('%I.%I', chunk_schema, chunk_name)::regclass,
                    if_compressed => true)::text AS "Value"
                FROM timescaledb_information.chunks
                WHERE hypertable_schema = 'public'
                  AND hypertable_name = 'Trades'
                  AND is_compressed
                  AND range_start <= {cutoff}
                  AND range_end > {cutoff}
                """)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        long after = await CountOlderAsync(cutoff, cancellationToken).ConfigureAwait(false);
        return before - after;
    }

    private async Task<(int Batches, long Rows)> DeleteRemainderAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        List<string> instruments = await _database.Database
            .SqlQuery<string>(
                $"""
                SELECT DISTINCT "Instrument" AS "Value"
                FROM "Trades"
                WHERE "TradeTimeUtc" < {cutoff}
                """)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        int batches = 0;
        long rows = 0;
        foreach (string instrument in instruments)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // DeleteIndex leads with Instrument, then TradeTimeUtc, so this predicate is a
                // range scan and LIMIT can stop early. The statement autocommits.
                int deleted = await _database.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    WITH doomed AS (
                        SELECT "Venue", "Instrument", "ContractId", "TradeTimeUtc", "Sequence"
                        FROM "Trades"
                        WHERE "Instrument" = {instrument} AND "TradeTimeUtc" < {cutoff}
                        ORDER BY "TradeTimeUtc"
                        LIMIT {_batchSize}
                    )
                    DELETE FROM "Trades" AS t
                    USING doomed AS d
                    WHERE t."Venue" = d."Venue"
                      AND t."Instrument" = d."Instrument"
                      AND t."ContractId" = d."ContractId"
                      AND t."TradeTimeUtc" = d."TradeTimeUtc"
                      AND t."Sequence" = d."Sequence"
                    """,
                    cancellationToken).ConfigureAwait(false);

                if (deleted == 0)
                {
                    break;
                }

                batches++;
                rows += deleted;
            }
        }

        return (batches, rows);
    }

    private async Task<bool> TradesIsHypertableAsync(CancellationToken cancellationToken)
    {
        List<int> extension = await _database.Database
            .SqlQuery<int>(
                $"""
                SELECT 1 AS "Value" FROM pg_extension WHERE extname = 'timescaledb'
                """)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (extension.Count == 0)
        {
            return false;
        }

        List<int> hypertable = await _database.Database
            .SqlQuery<int>(
                $"""
                SELECT 1 AS "Value"
                FROM timescaledb_information.hypertables
                WHERE hypertable_schema = 'public' AND hypertable_name = 'Trades'
                """)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return hypertable.Count > 0;
    }

    private async Task<long> CountOlderAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        List<long> counts = await _database.Database
            .SqlQuery<long>(
                $"""
                SELECT count(*)::bigint AS "Value"
                FROM "Trades"
                WHERE "TradeTimeUtc" < {cutoff}
                """)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return counts[0];
    }
}
