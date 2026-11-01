using System.Diagnostics;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Telemetry;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>One stored quote, with age computed on the database clock.</summary>
/// <param name="Venue">The venue id.</param>
/// <param name="Instrument">The normalised symbol.</param>
/// <param name="ContractId">The venue contract id.</param>
/// <param name="Bid">Best bid.</param>
/// <param name="Ask">Best ask.</param>
/// <param name="Last">Last trade.</param>
/// <param name="Volume">Session volume, in contracts.</param>
/// <param name="QuoteTimeUtc">The venue stamp, when the payload had one.</param>
/// <param name="ReceivedAt">Database <c>clock_timestamp()</c> at upsert.</param>
/// <param name="AgeSeconds"><c>clock_timestamp() - ReceivedAt</c>, in seconds, clamped at zero.</param>
public sealed record LatestQuoteRow(
    string Venue,
    string Instrument,
    string ContractId,
    decimal Bid,
    decimal Ask,
    decimal Last,
    long Volume,
    DateTimeOffset? QuoteTimeUtc,
    DateTimeOffset ReceivedAt,
    double AgeSeconds);

/// <summary>The latest-quote table. One upsert statement, then a read.</summary>
public interface ILatestQuoteStore
{
    /// <summary>Inserts or replaces each quote. One statement for the whole batch.</summary>
    /// <param name="quotes">The rows. Empty does not touch the store.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>How many rows the statement reports it wrote or revised.</returns>
    Task<int> UpsertAsync(IReadOnlyList<LatestQuoteDraft> quotes, CancellationToken cancellationToken);

    /// <summary>The stored row, or null when this contract has none.</summary>
    /// <param name="venue">The venue id.</param>
    /// <param name="contractId">The contract id.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The row and its database-clock age, or null.</returns>
    Task<LatestQuoteRow?> FindAsync(string venue, string contractId, CancellationToken cancellationToken);
}

/// <summary>Postgres implementation of <see cref="ILatestQuoteStore"/>.</summary>
public sealed class LatestQuoteStore(TopstepXDbContext database, HostTelemetry telemetry) : ILatestQuoteStore
{
    private readonly TopstepXDbContext _database = database;
    private readonly HostTelemetry _telemetry = telemetry;

    private const string UpsertSql = """
        INSERT INTO "LatestQuotes" (
            "Venue", "ContractId", "Instrument", "Bid", "Ask", "Last", "Volume",
            "QuoteTimeUtc", "ReceivedAt")
        SELECT a.venue, a.contract_id, a.instrument, a.bid, a.ask, a.last_price, a.volume,
               a.quote_time, clock_timestamp()
        FROM unnest(
            @venues, @contracts, @instruments, @bids, @asks, @lasts, @volumes, @quoteTimes)
            AS a(venue, contract_id, instrument, bid, ask, last_price, volume, quote_time)
        ON CONFLICT ("Venue", "ContractId") DO UPDATE SET
            "Instrument" = excluded."Instrument",
            "Bid" = excluded."Bid",
            "Ask" = excluded."Ask",
            "Last" = excluded."Last",
            "Volume" = excluded."Volume",
            "QuoteTimeUtc" = excluded."QuoteTimeUtc",
            "ReceivedAt" = excluded."ReceivedAt"
        WHERE "LatestQuotes"."ReceivedAt" <= excluded."ReceivedAt"
        """;

    private const string FindSql = """
        SELECT "Venue", "Instrument", "ContractId", "Bid", "Ask", "Last", "Volume",
               "QuoteTimeUtc", "ReceivedAt",
               GREATEST(EXTRACT(EPOCH FROM (clock_timestamp() - "ReceivedAt")), 0)::float8
                   AS "AgeSeconds"
        FROM "LatestQuotes"
        WHERE "Venue" = {0} AND "ContractId" = {1}
        """;

    /// <inheritdoc />
    public async Task<int> UpsertAsync(
        IReadOnlyList<LatestQuoteDraft> quotes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(quotes);
        if (quotes.Count == 0)
        {
            return 0;
        }

        NpgsqlParameter[] parameters =
        [
            TextArray("venues", quotes.Select(q => q.Venue)),
            TextArray("contracts", quotes.Select(q => q.ContractId)),
            TextArray("instruments", quotes.Select(q => q.Instrument)),
            NumericArray("bids", quotes.Select(q => q.Bid)),
            NumericArray("asks", quotes.Select(q => q.Ask)),
            NumericArray("lasts", quotes.Select(q => q.Last)),
            new("volumes", NpgsqlDbType.Array | NpgsqlDbType.Bigint)
            {
                Value = quotes.Select(q => q.Volume).ToArray(),
            },
            new("quoteTimes", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz)
            {
                Value = quotes.Select(q => q.QuoteTimeUtc).ToArray(),
            },
        ];

        return await _database.Database
            .ExecuteSqlRawAsync(UpsertSql, parameters, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<LatestQuoteRow?> FindAsync(
        string venue,
        string contractId,
        CancellationToken cancellationToken)
    {
        using Activity? span = _telemetry.StartQuoteRead();
        LatestQuoteSqlRow? row = await _database.Database
            .SqlQueryRaw<LatestQuoteSqlRow>(FindSql, venue, contractId)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        return new LatestQuoteRow(
            row.Venue,
            row.Instrument,
            row.ContractId,
            row.Bid,
            row.Ask,
            row.Last,
            row.Volume,
            row.QuoteTimeUtc,
            row.ReceivedAt,
            row.AgeSeconds);
    }

    private static NpgsqlParameter TextArray(string name, IEnumerable<string> values) =>
        new(name, NpgsqlDbType.Array | NpgsqlDbType.Varchar) { Value = values.ToArray() };

    private static NpgsqlParameter NumericArray(string name, IEnumerable<decimal> values) =>
        new(name, NpgsqlDbType.Array | NpgsqlDbType.Numeric) { Value = values.ToArray() };

    private sealed class LatestQuoteSqlRow
    {
        public string Venue { get; set; } = string.Empty;

        public string Instrument { get; set; } = string.Empty;

        public string ContractId { get; set; } = string.Empty;

        public decimal Bid { get; set; }

        public decimal Ask { get; set; }

        public decimal Last { get; set; }

        public long Volume { get; set; }

        public DateTimeOffset? QuoteTimeUtc { get; set; }

        public DateTimeOffset ReceivedAt { get; set; }

        public double AgeSeconds { get; set; }
    }
}
