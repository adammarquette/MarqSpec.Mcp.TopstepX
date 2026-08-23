using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace MarqSpec.Mcp.TopstepX.Embeddings;

/// <summary>
/// Which path answered an observation search.
/// </summary>
public enum ObservationSearchMode
{
    /// <summary>Unset.</summary>
    Unknown = 0,

    /// <summary>Substring matching. A supported state, not a degraded one.</summary>
    Text = 1,

    /// <summary>Cosine similarity over the vector index.</summary>
    Semantic = 2,
}

/// <summary>
/// One match, and how good it is.
/// </summary>
/// <param name="Observation">The observation.</param>
/// <param name="Similarity">
/// Cosine similarity in <c>[-1, 1]</c>, higher being closer, or <see langword="null"/> when the text path
/// answered. Null rather than a stand-in: substring matching produces no score, and inventing one — a 1.0 for
/// "it matched" — would let a caller compare across modes as though the numbers meant the same thing.
/// </param>
public sealed record ObservationMatch(ObservationRecord Observation, double? Similarity);

/// <summary>
/// The result of a search, including why it answered the way it did.
/// </summary>
/// <param name="Mode">Which path answered.</param>
/// <param name="Reason">Why, when it was not semantic. Null when it was.</param>
/// <param name="Matches">The matches, best first for semantic and most recent first for text.</param>
/// <param name="UnsearchableCount">
/// How many observations in scope have no vector and so could not take part in a semantic search. Always zero
/// on the text path, which reads every row.
/// </param>
public sealed record ObservationSearchOutcome(
    ObservationSearchMode Mode,
    string? Reason,
    IReadOnlyList<ObservationMatch> Matches,
    int UnsearchableCount);

/// <summary>
/// Searches observations by meaning, and by substring when it cannot.
/// </summary>
/// <remarks>
/// <para>
/// The two paths are one call with one shape. A caller never has to ask which one it got — the outcome says
/// so — because an empty result is otherwise ambiguous between "nothing is similar" and "similarity never
/// ran", and those warrant different next steps.
/// </para>
/// <para>
/// **The text path is a fallback, not an error path.** No key, a rate limit, an outage, an unusable response:
/// each answers the question less precisely rather than refusing to answer it.
/// </para>
/// </remarks>
/// <param name="database">The store.</param>
/// <param name="provider">The embedding provider.</param>
/// <param name="availability">Whether embeddings can be produced at all.</param>
/// <param name="logger">The logger.</param>
public sealed class ObservationSearchService(
    TopstepXDbContext database,
    IEmbeddingProvider provider,
    EmbeddingAvailabilityHolder availability,
    ILogger<ObservationSearchService> logger)
{
    private readonly TopstepXDbContext _database = database;
    private readonly IEmbeddingProvider _provider = provider;
    private readonly EmbeddingAvailabilityHolder _availability = availability;
    private readonly ILogger<ObservationSearchService> _logger = logger;

    /// <summary>
    /// Finds observations matching a query.
    /// </summary>
    /// <param name="query">What to look for. Blank lists by recency.</param>
    /// <param name="symbol">An already-normalised instrument symbol, or null for all.</param>
    /// <param name="limit">How many to return. Already validated against the read cap.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The matches and how they were found.</returns>
    public async Task<ObservationSearchOutcome> SearchAsync(
        string? query,
        string? symbol,
        int limit,
        CancellationToken cancellationToken)
    {
        // Null rather than an unfiltered queryable: the semantic path uses null to mean "no filter at all",
        // which is what lets its query stay in the shape the vector index can serve.
        IQueryable<ObservationRecord>? filter = string.IsNullOrWhiteSpace(symbol)
            ? null
            : _database.Observations.Where(o => o.Instrument == symbol);

        IQueryable<ObservationRecord> scope = filter ?? _database.Observations;

        // A blank query has nothing to embed and nothing to match. Listing by recency is the honest answer,
        // and calling it Text is honest too -- it is what the caller would get from an unconfigured server.
        if (string.IsNullOrWhiteSpace(query))
        {
            return await TextAsync(scope, null, limit, null, cancellationToken).ConfigureAwait(false);
        }

        string needle = query.Trim();

        if (!_availability.Value.IsAvailable)
        {
            return await TextAsync(
                    scope, needle, limit, _availability.Value.Explanation, cancellationToken)
                .ConfigureAwait(false);
        }

        // search_query, NOT search_document. The model embeds a question and a statement into deliberately
        // different regions, and using the storage type here would return well-formed vectors that retrieve
        // measurably worse, with nothing in the result to indicate why (ADR-0009).
        EmbeddingResult embedded = await _provider
            .EmbedAsync(needle, EmbeddingPurpose.Query, cancellationToken)
            .ConfigureAwait(false);

        if (!embedded.HasVector)
        {
            // The question still gets an answer. A rate limit degrading to substring matching is the whole
            // point of the seam; throwing here would turn a busy vendor into a broken tool.
            _logger.LogInformation(
                "Query embedding unavailable ({Outcome}); answering by substring instead.", embedded.Outcome);

            return await TextAsync(
                    scope, needle, limit, ExplainFallback(embedded.Outcome), cancellationToken)
                .ConfigureAwait(false);
        }

        return await SemanticAsync(
                filter,
                new Vector(embedded.Vector!.ToArray().AsMemory()),
                limit,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ObservationSearchOutcome> SemanticAsync(
        IQueryable<ObservationRecord>? filter,
        Vector query,
        int limit,
        CancellationToken cancellationToken)
    {
        // -- Why this runs in a transaction --------------------------------------------------------------
        //
        // An HNSW scan visits a fixed number of candidates (hnsw.ef_search) and applies remaining filters
        // afterwards, so a filtered scan can return fewer rows than asked for while matching rows sit in the
        // table. It does not error -- it returns a SHORT list, which reads exactly like "that is all there
        // is". pgvector 0.8's iterative scan keeps widening until it has enough; strict_order rather than
        // relaxed_order because the caller is handed similarity scores and will read the order as meaningful.
        //
        // SET LOCAL, so it dies with the transaction. A bare SET would outlive this query on a pooled
        // connection and silently change the cost of unrelated ones.
        await using IDbContextTransaction transaction = await _database.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await _database.Database
            .ExecuteSqlRawAsync("SET LOCAL hnsw.iterative_scan = strict_order;", cancellationToken)
            .ConfigureAwait(false);

        List<NearestOwner> nearest = await NearestQuery(_database, filter, _provider.Model, query)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Counted, not inferred from the result length. An observation whose embedding call failed at write
        // time is INVISIBLE to this path, and gh#46 told its author it would "match on text until
        // re-embedded" -- a promise this query cannot keep on its own. Reporting the number is what stops a
        // thin result being read as a thin corpus.
        IQueryable<ObservationRecord> scope = filter ?? _database.Observations;
        int unsearchable = await scope
            .CountAsync(
                o => !_database.Embeddings.Any(e =>
                    e.OwnerKind == EmbeddingOwnerKind.Observation
                    && e.Model == _provider.Model
                    && e.OwnerId == o.Id.ToString()),
                cancellationToken)
            .ConfigureAwait(false);

        // Hydrated in a second round trip rather than a join, and that is the whole point: a join in the
        // ordering query makes the planner hash-join both tables and sort EVERY vector, never touching the
        // HNSW index. Measured, not assumed -- ObservationSearchIndexTests takes the plan of the query above.
        // At most `limit` ids come back, so this lookup is bounded by the read cap.
        List<Guid> ids = [.. nearest.Select(n => Guid.Parse(n.OwnerId))];

        Dictionary<Guid, ObservationRecord> byId = await _database.Observations
            .Where(o => ids.Contains(o.Id))
            .ToDictionaryAsync(o => o.Id, cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        List<ObservationMatch> matches = [];
        foreach (NearestOwner hit in nearest)
        {
            // Order is carried from the vector query, not re-derived from the dictionary -- a dictionary has
            // no order, and re-sorting client-side would quietly discard the ranking this was all for.
            if (byId.TryGetValue(Guid.Parse(hit.OwnerId), out ObservationRecord? observation))
            {
                matches.Add(new ObservationMatch(observation, 1d - hit.Distance));
            }
        }

        return new ObservationSearchOutcome(ObservationSearchMode.Semantic, null, matches, unsearchable);
    }

    /// <summary>An owner and its distance from the query vector.</summary>
    /// <param name="OwnerId">The observation's id, as stored.</param>
    /// <param name="Distance">Cosine distance - <c>0</c> identical, <c>2</c> opposite.</param>
    public sealed record NearestOwner(string OwnerId, double Distance);

    /// <summary>
    /// The nearest-neighbour query, uncapped.
    /// </summary>
    /// <param name="database">The store.</param>
    /// <param name="filter">The observations in scope, or <see langword="null"/> for all of them.</param>
    /// <param name="model">The embedding model whose vectors to compare.</param>
    /// <param name="query">The query vector.</param>
    /// <returns>The query, for the caller to cap.</returns>
    /// <remarks>
    /// <para>
    /// <b>No join, deliberately.</b> Joining <c>Observations</c> here makes the planner hash-join both tables
    /// and sort every vector in the store - the HNSW index is never touched. Selecting owner ids alone keeps
    /// the <c>ORDER BY ... LIMIT</c> directly over the indexed column, which is the shape the index serves.
    /// </para>
    /// <para>
    /// Public so a test can take its plan rather than retyping the SQL. An index that exists but is never
    /// chosen is not an index, and a test that EXPLAINs a hand-written lookalike proves it about a query
    /// nobody runs - the two drift, and the day they do is the day the assertion stops meaning anything.
    /// </para>
    /// </remarks>
    public static IQueryable<NearestOwner> NearestQuery(
        TopstepXDbContext database,
        IQueryable<ObservationRecord>? filter,
        string model,
        Vector query)
    {
        ArgumentNullException.ThrowIfNull(database);

        IQueryable<EmbeddingRecord> embeddings = database.Embeddings
            .Where(e => e.OwnerKind == EmbeddingOwnerKind.Observation && e.Model == model);

        if (filter is not null)
        {
            // A semi-join, which the planner drives from the (small) filtered observation set. That plan does
            // NOT use the vector index -- and at the volumes a symbol filter produces it is both cheaper and
            // complete, which matters more. The unfiltered path is the one that has to scale, and it is the
            // one the index serves.
            embeddings = embeddings.Where(e => filter.Any(o => o.Id.ToString() == e.OwnerId));
        }

        return embeddings
            .OrderBy(e => e.Embedding.CosineDistance(query))
            .Select(e => new NearestOwner(e.OwnerId, e.Embedding.CosineDistance(query)));
    }

    private static async Task<ObservationSearchOutcome> TextAsync(
        IQueryable<ObservationRecord> scope,
        string? needle,
        int limit,
        string? reason,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(needle))
        {
            scope = scope.Where(o => EF.Functions.ILike(o.Text, "%" + needle + "%"));
        }

        List<ObservationRecord> rows = await scope
            .OrderByDescending(o => o.RecordedAt)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Zero unsearchable, and that is not a shortcut: this path reads Observations directly, so every row
        // takes part whether or not it has a vector.
        return new ObservationSearchOutcome(
            ObservationSearchMode.Text,
            reason,
            [.. rows.Select(o => new ObservationMatch(o, null))],
            0);
    }

    private static string ExplainFallback(EmbeddingOutcome outcome) => outcome switch
    {
        EmbeddingOutcome.RateLimited =>
            "The embedding provider rate-limited the query, so this result is substring matching. Retrying "
            + "shortly should get semantic matching.",
        EmbeddingOutcome.Malformed =>
            "The embedding provider returned an unusable response, so this result is substring matching.",
        _ =>
            "The embedding provider could not be reached, so this result is substring matching.",
    };
}
