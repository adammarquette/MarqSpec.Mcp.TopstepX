using System.Globalization;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// Recomputes stored footprint cells from the trade tape (ADR-0006, gh#220).
/// </summary>
/// <remarks>
/// <para>
/// The same shape as <see cref="IndicatorProjector"/>: read the source series, project, write what
/// changed, and <b>reconcile</b> — cells the current tape no longer justifies are removed. An
/// upsert-only pass would leave a cell behind after its print was deleted, which is the failure
/// gh#42 was for indicators.
/// </para>
/// <para>
/// <b>Each contiguous counted single-contract run is projected on its own</b> (ADR-0011).
/// Uncounted prints — <see cref="TradeDirection.Unknown"/>, and size of 0 or less — do not open a
/// seam. The aggregator already skips them before it records a contract; a host split that
/// included them would treat two same-contract buys around an Unknown as a splice and write no
/// cell, which reads as a bar that did not trade. A bucket whose counted prints come from more
/// than one contract still produces none.
/// </para>
/// <para>
/// <b><c>RecordedAt</c> is handed in.</b> The aggregator does not read a clock. A confirming
/// rebuild leaves every timestamp alone.
/// </para>
/// <para>
/// An empty tape yields empty cells, not a fabricated 0/0 profile. Nothing here calls the venue.
/// </para>
/// </remarks>
/// <param name="database">The store.</param>
/// <param name="logger">The logger.</param>
public sealed class FootprintProjector(
    TopstepXDbContext database,
    ILogger<FootprintProjector> logger)
{
    private readonly TopstepXDbContext _database = database;
    private readonly ILogger<FootprintProjector> _logger = logger;

    /// <summary>
    /// Recomputes footprint cells for one series and writes the rows that changed.
    /// </summary>
    /// <param name="venue">The venue.</param>
    /// <param name="instrument">The instrument.</param>
    /// <param name="resolutionMinutes">The bar size in minutes.</param>
    /// <param name="now">The instant this pass runs at, stamped on changed rows.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>How many rows this pass changed — written, updated, or <b>removed</b>.</returns>
    /// <exception cref="InvalidOperationException">
    /// The caller opened no transaction, so the two halves of this pass could not commit together.
    /// </exception>
    /// <remarks>
    /// Does <b>not</b> call <c>SaveChanges</c>. The caller owns the unit of work.
    /// A confirming rebuild still produces an <b>empty diff</b>.
    /// </remarks>
    public async Task<int> ProjectAsync(
        string venue,
        InstrumentId instrument,
        int resolutionMinutes,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(venue);

        if (resolutionMinutes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(resolutionMinutes),
                resolutionMinutes,
                "A bar size must be positive.");
        }

        if (_database.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "A footprint projection pass writes its cells with one statement the store runs as it is "
                + "sent, and removes the cells the tape no longer justifies through the change tracker, "
                + "which waits for the caller's SaveChanges. Outside a transaction the first commits on "
                + "its own and the second does not, leaving cells standing that this very pass decided to "
                + "remove. Wrap this call in a transaction.");
        }

        List<TradeRecord> stored = await _database.Trades
            .AsNoTracking()
            .Where(t => t.Venue == venue && t.Instrument == instrument.Symbol)
            .OrderBy(t => t.TradeTimeUtc)
            .ThenBy(t => t.Sequence)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        IQueryable<FootprintCellRecord> cells = _database.FootprintCells
            .Where(c => c.Venue == venue
                && c.Instrument == instrument.Symbol
                && c.ResolutionMinutes == resolutionMinutes);

        Dictionary<(DateTimeOffset Bucket, decimal Price), FootprintCellRecord> existing =
            await cells.AsNoTracking()
                .ToDictionaryAsync(c => (c.BucketStart, c.Price), cancellationToken)
                .ConfigureAwait(false);

        HashSet<(DateTimeOffset Bucket, decimal Price)> produced = [];
        List<PendingCell> pending = [];

        foreach (FootprintCell cell in ProjectRuns(stored, resolutionMinutes))
        {
            Collect(cell, existing, produced, pending);
        }

        int written = pending.Count == 0
            ? 0
            : await WriteAsync(venue, instrument, resolutionMinutes, pending, now, cancellationToken)
                .ConfigureAwait(false);

        int removed = await ReconcileAsync(
            venue, instrument, resolutionMinutes, stored.Count, existing, produced, cancellationToken)
            .ConfigureAwait(false);

        if (written + removed > 0)
        {
            _logger.LogDebug(
                "Projected {Count} footprint cells for {Instrument} {Resolution}m over {Prints} prints; "
                + "removed {Removed} the tape no longer justifies.",
                written,
                instrument.Symbol,
                resolutionMinutes,
                stored.Count,
                removed);
        }

        return written + removed;
    }

    private static void Collect(
        FootprintCell cell,
        Dictionary<(DateTimeOffset Bucket, decimal Price), FootprintCellRecord> existing,
        HashSet<(DateTimeOffset Bucket, decimal Price)> produced,
        List<PendingCell> pending)
    {
        decimal price = Math.Round(cell.Price, TopstepXDbContext.PriceScale, MidpointRounding.AwayFromZero);
        (DateTimeOffset BucketStart, decimal Price) key = (cell.BucketStart, price);

        produced.Add(key);

        if (existing.TryGetValue(key, out FootprintCellRecord? row)
            && row.BuyVolume == cell.BuyVolume
            && row.SellVolume == cell.SellVolume)
        {
            return;
        }

        pending.Add(new PendingCell(cell.BucketStart, price, cell.BuyVolume, cell.SellVolume));
    }

    private async Task<int> ReconcileAsync(
        string venue,
        InstrumentId instrument,
        int resolutionMinutes,
        int tradesRead,
        Dictionary<(DateTimeOffset Bucket, decimal Price), FootprintCellRecord> existing,
        HashSet<(DateTimeOffset Bucket, decimal Price)> produced,
        CancellationToken cancellationToken)
    {
        List<FootprintCellRecord> unjustified = [];

        foreach ((var key, FootprintCellRecord row) in existing)
        {
            if (produced.Contains(key))
            {
                continue;
            }

            unjustified.Add(row);
        }

        if (unjustified.Count == 0)
        {
            return 0;
        }

        int storedTrades = await _database.Trades
            .CountAsync(
                t => t.Venue == venue && t.Instrument == instrument.Symbol,
                cancellationToken)
            .ConfigureAwait(false);

        if (storedTrades != tradesRead)
        {
            throw new InvalidOperationException(
                "This footprint pass read " + tradesRead.ToString(CultureInfo.InvariantCulture)
                + " trades for " + instrument.Symbol + " on '" + venue + "', but the store holds "
                + storedTrades.ToString(CultureInfo.InvariantCulture)
                + " — so it did not read the whole tape. Reconciliation removes every cell the pass "
                + "did not produce and is not scoped by bucket range, so completing it would delete "
                + "cells whose prints this pass never read. Read the whole tape in one snapshot.");
        }

        foreach (FootprintCellRecord row in unjustified)
        {
            _database.FootprintCells.Remove(row);
        }

        return unjustified.Count;
    }

    /// <summary>
    /// Projects each contiguous counted single-contract run, then drops any bucket more than one
    /// counted run touched.
    /// </summary>
    /// <remarks>
    /// A clean roll lands in adjacent buckets and both cells survive. A counted roll inside one bar
    /// would otherwise write each run's cell under the same key — last write wins, and the survivor
    /// looks like the bar's footprint. Refusing the bucket is an absence; merging it is a wrong
    /// number. An uncounted print in the middle of a counted run is not a roll.
    /// </remarks>
    private static IReadOnlyList<FootprintCell> ProjectRuns(
        IReadOnlyList<TradeRecord> trades,
        int resolutionMinutes)
    {
        List<(int Run, FootprintCell Cell)> projected = [];
        int runIndex = 0;

        foreach (IReadOnlyList<TradeRecord> run in SegmentByContract(trades))
        {
            foreach (FootprintCell cell in FootprintAggregator.Aggregate(
                [.. run.Select(ToPrint)],
                resolutionMinutes))
            {
                projected.Add((runIndex, cell));
            }

            runIndex++;
        }

        HashSet<DateTimeOffset> mixedBuckets = MixedBuckets(projected);
        return [.. projected.Where(p => !mixedBuckets.Contains(p.Cell.BucketStart)).Select(p => p.Cell)];
    }

    private static HashSet<DateTimeOffset> MixedBuckets(
        IReadOnlyList<(int Run, FootprintCell Cell)> projected)
    {
        Dictionary<DateTimeOffset, int> owner = [];
        HashSet<DateTimeOffset> mixed = [];

        foreach ((int run, FootprintCell cell) in projected)
        {
            if (mixed.Contains(cell.BucketStart))
            {
                continue;
            }

            if (owner.TryGetValue(cell.BucketStart, out int seen) && seen != run)
            {
                mixed.Add(cell.BucketStart);
                continue;
            }

            owner[cell.BucketStart] = run;
        }

        return mixed;
    }

    /// <summary>
    /// Splits a time-ordered tape into contiguous counted single-contract runs (ADR-0011).
    /// </summary>
    /// <param name="trades">The tape, already ordered by time then sequence.</param>
    /// <returns>
    /// The runs, in tape order. Uncounted prints are omitted. Empty when nothing on the tape is
    /// counted.
    /// </returns>
    private static IReadOnlyList<IReadOnlyList<TradeRecord>> SegmentByContract(
        IReadOnlyList<TradeRecord> trades)
    {
        List<TradeRecord> counted = [];
        foreach (TradeRecord trade in trades)
        {
            if (IsCounted(trade))
            {
                counted.Add(trade);
            }
        }

        if (counted.Count == 0)
        {
            return [];
        }

        List<IReadOnlyList<TradeRecord>> runs = [];
        int start = 0;

        for (int i = 1; i <= counted.Count; i++)
        {
            bool boundary = i == counted.Count
                || !string.Equals(
                    counted[i].ContractId,
                    counted[start].ContractId,
                    StringComparison.Ordinal);

            if (!boundary)
            {
                continue;
            }

            List<TradeRecord> run = [];
            for (int j = start; j < i; j++)
            {
                run.Add(counted[j]);
            }

            runs.Add(run);
            start = i;
        }

        return runs;
    }

    /// <summary>
    /// The same filter <see cref="FootprintAggregator"/> applies before it records a contract:
    /// only a Buy or Sell with size greater than zero is a counted print.
    /// </summary>
    private static bool IsCounted(TradeRecord trade) =>
        trade.Direction is TradeDirection.Buy or TradeDirection.Sell && trade.Size > 0;

    private static TradePrint ToPrint(TradeRecord record) =>
        new(
            record.Instrument,
            record.ContractId,
            record.TradeTimeUtc,
            record.Price,
            record.Size,
            record.Direction);

    private const string UpsertCellsSql = """
        INSERT INTO "FootprintCells" (
            "Venue", "Instrument", "ResolutionMinutes", "BucketStart", "Price",
            "BuyVolume", "SellVolume", "RecordedAt")
        SELECT @venue, @instrument, @resolution, a.bucket, a.price, a.buy, a.sell, @recorded
        FROM unnest(@buckets, @prices, @buys, @sells)
             AS a(bucket, price, buy, sell)
        ON CONFLICT ("Venue", "Instrument", "ResolutionMinutes", "BucketStart", "Price")
        DO UPDATE SET
            "BuyVolume" = excluded."BuyVolume",
            "SellVolume" = excluded."SellVolume",
            "RecordedAt" = excluded."RecordedAt"
        """;

    private readonly record struct PendingCell(
        DateTimeOffset BucketStart,
        decimal Price,
        long BuyVolume,
        long SellVolume);

    /// <summary>Writes the cells this pass found the store does not already hold.</summary>
    /// <param name="venue">The venue.</param>
    /// <param name="instrument">The instrument.</param>
    /// <param name="resolutionMinutes">The bar size in minutes.</param>
    /// <param name="pending">The cells to write.</param>
    /// <param name="now">The instant this pass runs at.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>
    /// <b>How many rows the store reports it wrote or revised</b> — the statement's own row count, never
    /// <c>pending.Count</c> (gh#387).
    /// </returns>
    private async Task<int> WriteAsync(
        string venue,
        InstrumentId instrument,
        int resolutionMinutes,
        List<PendingCell> pending,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        NpgsqlParameter[] parameters =
        [
            new("venue", NpgsqlDbType.Varchar) { Value = venue },
            new("instrument", NpgsqlDbType.Varchar) { Value = instrument.Symbol },
            new("resolution", NpgsqlDbType.Integer) { Value = resolutionMinutes },
            new("recorded", NpgsqlDbType.TimestampTz) { Value = now },
            new("buckets", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz)
            {
                Value = pending.Select(c => c.BucketStart).ToArray(),
            },
            new("prices", NpgsqlDbType.Array | NpgsqlDbType.Numeric)
            {
                Value = pending.Select(c => c.Price).ToArray(),
            },
            new("buys", NpgsqlDbType.Array | NpgsqlDbType.Bigint)
            {
                Value = pending.Select(c => c.BuyVolume).ToArray(),
            },
            new("sells", NpgsqlDbType.Array | NpgsqlDbType.Bigint)
            {
                Value = pending.Select(c => c.SellVolume).ToArray(),
            },
        ];

        return await _database.Database
            .ExecuteSqlRawAsync(UpsertCellsSql, parameters, cancellationToken)
            .ConfigureAwait(false);
    }
}
