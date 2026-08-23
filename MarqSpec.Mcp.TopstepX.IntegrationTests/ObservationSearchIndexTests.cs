using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Embeddings;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pgvector.Npgsql;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// The two claims about the vector index that only a populated database can settle.
/// </summary>
/// <remarks>
/// Separate from <see cref="ObservationSearchTests"/> because these need thousands of rows: below a few
/// hundred a sequential scan genuinely is cheaper, the planner correctly picks it, and a test asserting
/// otherwise would be asserting that Postgres should make a worse choice.
/// </remarks>
/// <param name="fixture">The shared container.</param>
[Collection(SchemaCollection.Name)]
public sealed class ObservationSearchIndexTests(SchemaFixture fixture)
{
    private const string Model = "index-probe-v1";

    /// <summary>Enough rows that an index scan is genuinely the cheaper plan.</summary>
    private const int Rows = 4000;

    private const string Symbol = "IDXPROBE";

    /// <summary>Seeds once for the whole class; the rows are read-only and identical for both tests.</summary>
    private static readonly SemaphoreSlim _gate = new(1, 1);
    private static bool _seeded;

    private async Task SeedAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_seeded)
            {
                return;
            }

            await using TopstepXDbContext database = fixture.CreateContext();

            if (await database.Embeddings.AnyAsync(e => e.Model == Model))
            {
                _seeded = true;
                return;
            }

            for (int i = 0; i < Rows; i++)
            {
                // Spread around the circle. The 1-in-200 that carry the probe symbol sit in the arc FURTHEST
                // from where the queries below point, which is what makes the filtered case hard.
                bool probe = i % 200 == 0;
                double degrees = probe ? 170 + (i % 7) : i % 160;

                ObservationSeed.Add(
                    database,
                    $"index probe row {i}",
                    degrees,
                    Model,
                    probe ? Symbol : null);
            }

            await database.SaveChangesAsync();

            // The planner needs statistics before it will believe the table is big.
            await database.Database.ExecuteSqlRawAsync("ANALYZE \"Embeddings\"; ANALYZE \"Observations\";");
            _seeded = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    [Fact]
    public async Task TheCosineIndexIsActuallyChosen()
    {
        // An index that exists but is never chosen is not an index. The migration's HNSW index is asserted to
        // EXIST elsewhere; this asserts the query the service really runs USES it, which is a different claim
        // and the one that decides whether search stays fast as observations accumulate.
        await SeedAsync();
        await using TopstepXDbContext database = fixture.CreateContext();

        string plan = await ExplainAsync(database, fixture.ConnectionString, null);

        plan.Should().Contain(
            "IX_Embeddings_Vector_Cosine",
            "the nearest-neighbour query must reach the HNSW index rather than sorting the whole table");
        plan.Should().NotContain(
            "Seq Scan on \"Embeddings\"",
            "a sequential scan over every vector is what the index exists to avoid");
    }

    [Fact]
    public async Task AFilteredSearchReturnsWhatWasAskedFor_NotWhateverTheIndexHappenedToVisit()
    {
        // THE TRAP. An HNSW scan visits a fixed number of candidates and applies the WHERE clause afterwards.
        // The probe rows are deliberately the furthest from the query, so a plain scan's first candidates are
        // all non-probe rows, all of which the filter discards.
        //
        // What comes back then is not an error and not an empty list -- it is a SHORT list, which reads
        // exactly like "that is all there is". pgvector 0.8's iterative scan is what makes the count honest,
        // and this test is the only thing standing between that setting and someone removing it as noise.
        await SeedAsync();
        await using TopstepXDbContext database = fixture.CreateContext();

        int available = await database.Observations.CountAsync(o => o.Instrument == Symbol);
        available.Should().BeGreaterThan(10, "the fixture must hold more rows than the search asks for");

        ScriptedEmbeddingProvider provider = new();
        provider.At("probe query", 0);

        ObservationSearchOutcome result = await ObservationSeed
            .Service(WithModel(database, provider), provider)
            .SearchAsync("probe query", Symbol, 10, CancellationToken.None);

        result.Mode.Should().Be(ObservationSearchMode.Semantic);
        result.Matches.Should().HaveCount(
            10,
            "ten matching observations exist, so returning fewer would be a truncation the caller cannot see");
    }

    /// <summary>The seeded vectors are filed under this class's model, so the provider must agree.</summary>
    private static TopstepXDbContext WithModel(TopstepXDbContext database, ScriptedEmbeddingProvider provider)
    {
        provider.ModelOverride = Model;
        return database;
    }

    /// <summary>Takes the plan of the query the service itself builds.</summary>
    private static async Task<string> ExplainAsync(
        TopstepXDbContext database,
        string connectionString,
        string? symbol)
    {
        IQueryable<ObservationRecord>? scope = symbol is null
            ? null
            : database.Observations.Where(o => o.Instrument == symbol);

        // ToQueryString on the SERVICE'S OWN queryable, so this cannot drift from what runs in production.
        // It renders parameter VALUES into leading comments and leaves named placeholders in the body, so the
        // three are re-bound below rather than pasted in -- a literal 1024-element vector inlined into the SQL
        // would be a different query from the parameterised one, and could get a different plan.
        string sql = ObservationSearchService
            .NearestQuery(database, scope, Model, ScriptedEmbeddingProvider.VectorAt(0))
            .Take(10)
            .ToQueryString();

        NpgsqlDataSourceBuilder builder = new(connectionString);
        builder.UseVector();
        await using NpgsqlDataSource source = builder.Build();

        await using NpgsqlCommand command = source.CreateCommand("EXPLAIN " + sql);
        command.Parameters.AddWithValue("query", ScriptedEmbeddingProvider.VectorAt(0));
        command.Parameters.AddWithValue("model", Model);
        if (symbol is not null)
        {
            command.Parameters.AddWithValue("instrument", symbol);
        }

        command.Parameters.AddWithValue("p", 10);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        System.Text.StringBuilder plan = new();
        while (await reader.ReadAsync())
        {
            plan.AppendLine(reader.GetString(0));
        }

        return plan.ToString();
    }
}
