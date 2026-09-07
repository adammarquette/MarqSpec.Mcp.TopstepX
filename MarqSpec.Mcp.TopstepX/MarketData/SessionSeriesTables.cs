using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// A named daily session: <c>SessionBars</c> and <c>SessionIndicatorValues</c>, keyed by the session name.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ordered and keyed by <c>OpenUtc</c>, never by the trade date.</b> A session bar's identity in its own
/// table is the CME trade date, because the session's UTC bounds move with daylight saving — but a projection
/// is over a sequence of instants, and every other projection in this store keys a value by the instant the
/// bar it describes begins. The unique <c>(Venue, Instrument, Session, OpenUtc)</c> index is what makes that
/// sound: exactly one session bar per opening, so exactly one bar for any value to belong to (ADR-0022).
/// </para>
/// <para>
/// <b>One bar a day, which is the only thing that behaves differently upstream.</b> A warm-up of thirty-four
/// is thirty-four trading days here rather than a few hours, so the honest answer on a short series is that
/// most members measure nothing — an absence the bars justify, and a fact rather than a gap (`R-2.3`).
/// </para>
/// </remarks>
/// <param name="key">The series.</param>
/// <param name="database">The store.</param>
internal sealed class SessionSeriesTables(SeriesKey.Session key, TopstepXDbContext database) : ISeriesTables
{
    /// <summary>
    /// The value write — <see cref="ResolutionSeriesTables"/>'s statement with the session name where the bar
    /// size sits, against the table and the conflict target that carry it.
    /// </summary>
    private const string UpsertValuesSql = """
        INSERT INTO "SessionIndicatorValues" (
            "Venue", "Instrument", "Session", "Indicator", "Period", "BucketStart",
            "Value", "RecordedAt")
        SELECT @venue, @instrument, @session, a.indicator, a.period, a.bucket, a.value, @recorded
        FROM unnest(@indicators, @periods, @buckets, @values)
             AS a(indicator, period, bucket, value)
        ON CONFLICT ("Venue", "Instrument", "Session", "Indicator", "Period", "BucketStart")
        DO UPDATE SET
            "Value" = excluded."Value",
            "RecordedAt" = excluded."RecordedAt"
        """;

    private readonly SeriesKey.Session _key = key;
    private readonly TopstepXDbContext _database = database;

    /// <summary>The rows <see cref="LoadValuesAsync"/> read, so <see cref="Remove"/> can take one out.</summary>
    private readonly
        Dictionary<(string Indicator, int Period, DateTimeOffset Bucket), SessionIndicatorValueRecord>
        _rows = [];

    /// <inheritdoc />
    public string UpsertSql => UpsertValuesSql;

    /// <inheritdoc />
    public async Task<List<Bar>> LoadBarsAsync(CancellationToken cancellationToken)
    {
        // AsNoTracking for the reason every read of a raw-SQL-written table here is (gh#103), and ordered by
        // the OPENING rather than by the trade date: the two agree today, and the opening is what the values
        // are keyed by, so ordering by it is the claim the projection actually depends on.
        List<SessionBarRecord> stored = await _database.SessionBars
            .AsNoTracking()
            .Where(s => s.Venue == _key.Venue
                && s.Instrument == _key.Instrument
                && s.Session == _key.Name)
            .OrderBy(s => s.OpenUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. stored.Select(ToBar)];
    }

    /// <inheritdoc />
    public Task<int> CountBarsAsync(CancellationToken cancellationToken) =>
        _database.SessionBars
            .CountAsync(
                s => s.Venue == _key.Venue
                    && s.Instrument == _key.Instrument
                    && s.Session == _key.Name,
                cancellationToken);

    /// <inheritdoc />
    public Task<int> CountBarsAsync(int cap, CancellationToken cancellationToken) =>
        _database.SessionBars
            .Where(s => s.Venue == _key.Venue
                && s.Instrument == _key.Instrument
                && s.Session == _key.Name)
            .Take(cap)
            .CountAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<Dictionary<(string Indicator, int Period, DateTimeOffset Bucket), decimal>>
        LoadValuesAsync(CancellationToken cancellationToken)
    {
        List<SessionIndicatorValueRecord> stored = await _database.SessionIndicatorValues
            .AsNoTracking()
            .Where(v => v.Venue == _key.Venue
                && v.Instrument == _key.Instrument
                && v.Session == _key.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        Dictionary<(string Indicator, int Period, DateTimeOffset Bucket), decimal> values = new(stored.Count);

        foreach (SessionIndicatorValueRecord row in stored)
        {
            (string, int, DateTimeOffset) key = (row.Indicator, row.Period, row.BucketStart);
            _rows[key] = row;
            values[key] = row.Value;
        }

        return values;
    }

    /// <inheritdoc />
    public Task<List<DateTimeOffset>> LoadNewestBucketsAsync(int cap, CancellationToken cancellationToken) =>
        _database.SessionBars
            .Where(s => s.Venue == _key.Venue
                && s.Instrument == _key.Instrument
                && s.Session == _key.Name)
            .OrderByDescending(s => s.OpenUtc)
            .Select(s => s.OpenUtc)
            .Take(cap)
            .ToListAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<Dictionary<(string Indicator, int Period), DateTimeOffset>> NewestHeldAsync(
        CancellationToken cancellationToken)
    {
        // An anonymous type, and the tuple built after materialisation: a ValueTuple inside the Select
        // translates to a Postgres row constructor Npgsql then refuses to read (gh#282). Grouped for how far
        // each pair reaches (gh#531), not merely which ones exist.
        var held = await _database.SessionIndicatorValues
            .AsNoTracking()
            .Where(v => v.Venue == _key.Venue
                && v.Instrument == _key.Instrument
                && v.Session == _key.Name)
            .GroupBy(v => new { v.Indicator, v.Period })
            .Select(g => new { g.Key.Indicator, g.Key.Period, Newest = g.Max(v => v.BucketStart) })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return held.ToDictionary(v => (v.Indicator, v.Period), v => v.Newest);
    }

    /// <inheritdoc />
    public void Remove((string Indicator, int Period, DateTimeOffset Bucket) key) =>
        _database.SessionIndicatorValues.Remove(_rows[key]);

    /// <inheritdoc />
    public NpgsqlParameter[] UpsertParameters(List<PendingValue> pending, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(pending);

        return
        [
            new("venue", NpgsqlDbType.Varchar) { Value = _key.Venue },
            new("instrument", NpgsqlDbType.Varchar) { Value = _key.Instrument },
            new("session", NpgsqlDbType.Varchar) { Value = _key.Name },
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

    /// <summary>Maps a stored session row to the domain bar the indicators compute over.</summary>
    /// <param name="record">The stored row.</param>
    /// <returns>The bar, opening at <see cref="SessionBarRecord.OpenUtc"/>.</returns>
    /// <remarks>
    /// <b><see cref="SessionBarRecord.ContractId"/> is carried, and it is not decoration.</b>
    /// <c>ContractRollDetector</c> splits the series on it, so a session series that lived through a roll is
    /// projected as two runs and the warm-up restarts — the same refusal to smooth across a bookkeeping event
    /// the resolution series makes (ADR-0011).
    /// </remarks>
    private static Bar ToBar(SessionBarRecord record) =>
        new(
            record.OpenUtc,
            record.Open,
            record.High,
            record.Low,
            record.Close,
            record.Volume,
            record.ContractId);
}
