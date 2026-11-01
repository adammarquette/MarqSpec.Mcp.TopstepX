using MarqSpec.Mcp.TopstepX.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>The latest-quote table. One upsert statement for a batch.</summary>
public interface ILatestQuoteStore
{
    /// <summary>Inserts or replaces each quote. One statement for the whole batch.</summary>
    /// <param name="quotes">The rows. Empty does not touch the store.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>How many rows the statement reports it wrote or revised.</returns>
    Task<int> UpsertAsync(IReadOnlyList<LatestQuoteDraft> quotes, CancellationToken cancellationToken);
}

/// <summary>Postgres implementation of <see cref="ILatestQuoteStore"/>.</summary>
/// <remarks>
/// The read that reports age is the next slice. This type only writes.
/// <c>ReceivedAt</c> is <c>clock_timestamp()</c>, and an older receipt cannot replace a newer one.
/// </remarks>
public sealed class LatestQuoteStore(TopstepXDbContext database) : ILatestQuoteStore
{
    private readonly TopstepXDbContext _database = database;

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

    private static NpgsqlParameter TextArray(string name, IEnumerable<string> values) =>
        new(name, NpgsqlDbType.Array | NpgsqlDbType.Varchar) { Value = values.ToArray() };

    private static NpgsqlParameter NumericArray(string name, IEnumerable<decimal> values) =>
        new(name, NpgsqlDbType.Array | NpgsqlDbType.Numeric) { Value = values.ToArray() };
}
