using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// A series of fixed-width bars: <c>Bars</c> and <c>IndicatorValues</c>, keyed by the bar size.
/// </summary>
/// <remarks>
/// <b>Every query and the statement below are the ones this projection has always run</b> — moved here rather
/// than rewritten, character for character. The stored series they produce already exists in every deployed
/// database, so a predicate, an ordering or a conflict target that changed even slightly would make the next
/// rebuild rewrite rows it should have confirmed (ADR-0006).
/// </remarks>
/// <param name="key">The series.</param>
/// <param name="database">The store.</param>
internal sealed class ResolutionSeriesTables(SeriesKey.Resolution key, TopstepXDbContext database)
    : ISeriesTables
{
    /// <summary>
    /// The value write, as one statement the store resolves against the rows it has committed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The conflict target is the composite primary key</b> — the same key the pre-read looked the values
    /// up by, reached directly instead of being inferred from a read of it. Under
    /// <see cref="SeriesUnitOfWork.Isolation"/> a conflict against a row committed <i>after</i> this
    /// transaction's snapshot is refused with <c>40001</c> rather than <c>23505</c>, which is what
    /// <c>R-2.10</c> already retries once — and the retry runs over the store the winner committed, where the
    /// pre-filter recognises those values as already produced.
    /// </para>
    /// <para>
    /// <b>There is no skip-unchanged <c>WHERE</c>, and its absence is deliberate.</b> Nothing reaches this
    /// statement that the C# comparison did not already find different, and that comparison is made at the
    /// column's own scale.
    /// </para>
    /// </remarks>
    private const string UpsertValuesSql = """
        INSERT INTO "IndicatorValues" (
            "Venue", "Instrument", "ResolutionMinutes", "Indicator", "Period", "BucketStart",
            "Value", "RecordedAt")
        SELECT @venue, @instrument, @resolution, a.indicator, a.period, a.bucket, a.value, @recorded
        FROM unnest(@indicators, @periods, @buckets, @values)
             AS a(indicator, period, bucket, value)
        ON CONFLICT ("Venue", "Instrument", "ResolutionMinutes", "Indicator", "Period", "BucketStart")
        DO UPDATE SET
            "Value" = excluded."Value",
            "RecordedAt" = excluded."RecordedAt"
        """;

    private readonly SeriesKey.Resolution _key = key;
    private readonly TopstepXDbContext _database = database;

    /// <summary>The rows <see cref="LoadValuesAsync"/> read, so <see cref="Remove"/> can take one out.</summary>
    private readonly Dictionary<(string Indicator, int Period, DateTimeOffset Bucket), IndicatorValueRecord>
        _rows = [];

    /// <inheritdoc />
    public string UpsertSql => UpsertValuesSql;

    /// <inheritdoc />
    public async Task<List<Bar>> LoadBarsAsync(CancellationToken cancellationToken)
    {
        // AsNoTracking because NOTHING HERE MUTATES A BAR -- the projection reads them and writes
        // IndicatorValues. Tracked, a whole series' history sits in the change tracker being re-examined by
        // every subsequent SaveChanges, and EF's change detection is superlinear in the tracked count. That
        // is invisible on one series and is the whole cost on a store-wide rebuild (gh#73 review).
        //
        // Safe against the cache-aside path too: BarCacheService saves its bars BEFORE projecting -- a query
        // cannot see tracked-only rows (gh#31) -- so this reads exactly what tracking would have returned.
        List<BarRecord> stored = await _database.Bars
            .AsNoTracking()
            .Where(b => b.Venue == _key.Venue
                && b.Instrument == _key.Instrument
                && b.ResolutionMinutes == _key.Minutes)
            .OrderBy(b => b.BucketStart)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. stored.Select(IndicatorProjector.ToBar)];
    }

    /// <inheritdoc />
    public Task<int> CountBarsAsync(CancellationToken cancellationToken) =>
        _database.Bars
            .CountAsync(
                b => b.Venue == _key.Venue
                    && b.Instrument == _key.Instrument
                    && b.ResolutionMinutes == _key.Minutes,
                cancellationToken);

    /// <inheritdoc />
    public Task<int> CountBarsAsync(int cap, CancellationToken cancellationToken) =>
        _database.Bars
            .Where(b => b.Venue == _key.Venue
                && b.Instrument == _key.Instrument
                && b.ResolutionMinutes == _key.Minutes)
            .Take(cap)
            .CountAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<Dictionary<(string Indicator, int Period, DateTimeOffset Bucket), decimal>>
        LoadValuesAsync(CancellationToken cancellationToken)
    {
        // AsNoTracking, and it is not tidiness (gh#103's identity-map finding). These rows are written by SQL
        // the change tracker never sees, so a tracked copy is a stale entity the identity map would hand back
        // to the next read of IndicatorValues in the same scope in preference to the row it just read.
        List<IndicatorValueRecord> stored = await _database.IndicatorValues
            .AsNoTracking()
            .Where(v => v.Venue == _key.Venue
                && v.Instrument == _key.Instrument
                && v.ResolutionMinutes == _key.Minutes)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        Dictionary<(string Indicator, int Period, DateTimeOffset Bucket), decimal> values = new(stored.Count);

        foreach (IndicatorValueRecord row in stored)
        {
            (string, int, DateTimeOffset) key = (row.Indicator, row.Period, row.BucketStart);
            _rows[key] = row;
            values[key] = row.Value;
        }

        return values;
    }

    /// <inheritdoc />
    public Task<List<DateTimeOffset>> LoadNewestBucketsAsync(int cap, CancellationToken cancellationToken) =>
        _database.Bars
            .Where(b => b.Venue == _key.Venue
                && b.Instrument == _key.Instrument
                && b.ResolutionMinutes == _key.Minutes)
            .OrderByDescending(b => b.BucketStart)
            .Select(b => b.BucketStart)
            .Take(cap)
            .ToListAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<Dictionary<(string Indicator, int Period), DateTimeOffset>> NewestHeldAsync(
        CancellationToken cancellationToken)
    {
        // Grouped rather than DISTINCT because the question is how far each pair reaches (gh#531). The tuple
        // is BUILT AFTER MATERIALISATION, not projected into: Npgsql reads a ValueTuple as a Postgres
        // composite `record`, so `.Select(v => ValueTuple.Create(…))` translates and then throws on the read
        // -- and the in-memory provider materialises it happily, so the fault is only reachable from the
        // integration tier (gh#282).
        var held = await _database.IndicatorValues
            .AsNoTracking()
            .Where(v => v.Venue == _key.Venue
                && v.Instrument == _key.Instrument
                && v.ResolutionMinutes == _key.Minutes)
            .GroupBy(v => new { v.Indicator, v.Period })
            .Select(g => new { g.Key.Indicator, g.Key.Period, Newest = g.Max(v => v.BucketStart) })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return held.ToDictionary(v => (v.Indicator, v.Period), v => v.Newest);
    }

    /// <inheritdoc />
    public void Remove((string Indicator, int Period, DateTimeOffset Bucket) key) =>
        _database.IndicatorValues.Remove(_rows[key]);

    /// <inheritdoc />
    public NpgsqlParameter[] UpsertParameters(List<PendingValue> pending, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(pending);

        return
        [
            new("venue", NpgsqlDbType.Varchar) { Value = _key.Venue },
            new("instrument", NpgsqlDbType.Varchar) { Value = _key.Instrument },
            new("resolution", NpgsqlDbType.Integer) { Value = _key.Minutes },
            new("recorded", NpgsqlDbType.TimestampTz) { Value = now },
            new("indicators", NpgsqlDbType.Array | NpgsqlDbType.Varchar)
            {
                Value = pending.Select(v => v.Indicator).ToArray(),
            },
            new("periods", NpgsqlDbType.Array | NpgsqlDbType.Integer)
            {
                Value = pending.Select(v => v.Period).ToArray(),
            },
            new("buckets", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz)
            {
                Value = pending.Select(v => v.BucketStart).ToArray(),
            },
            new("values", NpgsqlDbType.Array | NpgsqlDbType.Numeric)
            {
                Value = pending.Select(v => v.Value).ToArray(),
            },
        ];
    }
}
