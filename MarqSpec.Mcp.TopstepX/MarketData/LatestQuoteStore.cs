using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace MarqSpec.Mcp.TopstepX.MarketData;

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
    /// <returns>The row, or null.</returns>
    Task<LatestQuoteDraft?> FindAsync(string venue, string contractId, CancellationToken cancellationToken);
}

/// <summary>Postgres implementation of <see cref="ILatestQuoteStore"/>.</summary>
public sealed class LatestQuoteStore(TopstepXDbContext database) : ILatestQuoteStore
{
    private readonly TopstepXDbContext _database = database;

    private const string UpsertSql = """
        INSERT INTO "LatestQuotes" (
            "Venue", "ContractId", "Instrument", "Bid", "Ask", "Last", "Volume",
            "QuoteTimeUtc", "ReceivedAt")
        SELECT a.venue, a.contract_id, a.instrument, a.bid, a.ask, a.last_price, a.volume,
               a.quote_time, a.received_at
        FROM unnest(
            @venues, @contracts, @instruments, @bids, @asks, @lasts, @volumes,
            @quoteTimes, @receivedAts)
            AS a(venue, contract_id, instrument, bid, ask, last_price, volume, quote_time, received_at)
        ON CONFLICT ("Venue", "ContractId") DO UPDATE SET
            "Instrument" = excluded."Instrument",
            "Bid" = excluded."Bid",
            "Ask" = excluded."Ask",
            "Last" = excluded."Last",
            "Volume" = excluded."Volume",
            "QuoteTimeUtc" = excluded."QuoteTimeUtc",
            "ReceivedAt" = excluded."ReceivedAt"
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
            NumericArray("volumes", quotes.Select(q => q.Volume)),
            new("quoteTimes", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz)
            {
                Value = quotes.Select(q => q.QuoteTimeUtc).ToArray(),
            },
            new("receivedAts", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz)
            {
                Value = quotes.Select(q => q.ReceivedAt).ToArray(),
            },
        ];

        return await _database.Database
            .ExecuteSqlRawAsync(UpsertSql, parameters, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<LatestQuoteDraft?> FindAsync(
        string venue,
        string contractId,
        CancellationToken cancellationToken)
    {
        LatestQuoteRecord? row = await _database.LatestQuotes
            .AsNoTracking()
            .SingleOrDefaultAsync(
                quote => quote.Venue == venue && quote.ContractId == contractId,
                cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        return new LatestQuoteDraft(
            row.Venue,
            row.Instrument,
            row.ContractId,
            row.Bid,
            row.Ask,
            row.Last,
            row.Volume,
            row.QuoteTimeUtc,
            row.ReceivedAt);
    }

    private static NpgsqlParameter TextArray(string name, IEnumerable<string> values) =>
        new(name, NpgsqlDbType.Array | NpgsqlDbType.Varchar) { Value = values.ToArray() };

    private static NpgsqlParameter NumericArray(string name, IEnumerable<decimal> values) =>
        new(name, NpgsqlDbType.Array | NpgsqlDbType.Numeric) { Value = values.ToArray() };
}
