using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// The claims a real Postgres is needed to make.
/// </summary>
/// <remarks>
/// Nothing here would fail against an in-memory provider, which is exactly why none of it belongs in the unit
/// tier: an in-memory run would go green and prove nothing about hypertables, vector indexes or constraints.
/// </remarks>
[Collection(SchemaCollection.Name)]
public sealed class SchemaTests(SchemaFixture fixture)
{
    private readonly SchemaFixture _fixture = fixture;

    [Fact]
    public async Task Migrations_ApplyFromEmpty()
    {
        // The fixture already migrated. This asserts there is nothing left pending, which is the failure mode
        // where a migration was added and the snapshot was not regenerated.
        await using TopstepXDbContext database = _fixture.CreateContext();

        IEnumerable<string> pending = await database.Database.GetPendingMigrationsAsync();
        pending.Should().BeEmpty();
    }

    [Fact]
    public async Task Migrations_AreIdempotent()
    {
        // Migrating an already-migrated database must be a no-op rather than an error. The server migrates at
        // every startup, so this runs far more often than the first-time path does.
        await using TopstepXDbContext database = _fixture.CreateContext();
        Func<Task> migrate = () => database.Database.MigrateAsync();

        await migrate.Should().NotThrowAsync();
    }

    [Theory]
    [InlineData("Bars")]
    [InlineData("IndicatorValues")]
    public async Task TimeSeriesTables_AreHypertables(string table)
    {
        long count = await ScalarAsync(
            "SELECT count(*) FROM timescaledb_information.hypertables WHERE hypertable_name = @t;",
            ("t", table));

        count.Should().Be(1, "the migration probes for timescaledb and this image has it");
    }

    [Fact]
    public async Task EmbeddingsCarriesAnHnswCosineIndex()
    {
        // HNSW rather than IVFFlat: IVFFlat's lists are only meaningful once it has seen representative data,
        // and this table starts empty.
        long count = await ScalarAsync(
            "SELECT count(*) FROM pg_indexes WHERE tablename = 'Embeddings' "
            + "AND indexdef ILIKE '%hnsw%' AND indexdef ILIKE '%vector_cosine_ops%';");

        count.Should().Be(1);
    }

    [Fact]
    public async Task WritingTheSameBucketTwice_UpdatesRatherThanDuplicating()
    {
        // The composite primary key IS the idempotence guard. If this ever fails, an overlapping re-fetch
        // starts appending, and every series quietly grows duplicate buckets that reads then average over.
        await using TopstepXDbContext database = _fixture.CreateContext();

        DateTimeOffset bucket = new(2026, 8, 18, 14, 0, 0, TimeSpan.Zero);
        BarRecord first = NewBar("IDEM", bucket, close: 100m);
        database.Bars.Add(first);
        await database.SaveChangesAsync();

        BarRecord stored = await database.Bars.SingleAsync(b =>
            b.Instrument == "IDEM" && b.BucketStart == bucket);
        stored.Close = 123m;
        await database.SaveChangesAsync();

        List<BarRecord> rows = await database.Bars
            .Where(b => b.Instrument == "IDEM" && b.BucketStart == bucket)
            .ToListAsync();

        rows.Should().ContainSingle();
        rows[0].Close.Should().Be(123m);
    }

    [Fact]
    public async Task InsertingASecondRowForTheSameBucket_IsRejected()
    {
        await using TopstepXDbContext database = _fixture.CreateContext();
        DateTimeOffset bucket = new(2026, 8, 18, 15, 0, 0, TimeSpan.Zero);

        database.Bars.Add(NewBar("DUPE", bucket, close: 100m));
        await database.SaveChangesAsync();

        await using TopstepXDbContext second = _fixture.CreateContext();
        second.Bars.Add(NewBar("DUPE", bucket, close: 200m));

        Func<Task> save = () => second.SaveChangesAsync();
        await save.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task AnInvertedPriceLevelZone_IsRejectedByTheDatabase()
    {
        // The detection pass's bugs are geometric, and an inverted zone reads as entirely plausible
        // everywhere except at this constraint.
        await using TopstepXDbContext database = _fixture.CreateContext();

        database.PriceLevels.Add(new PriceLevelRecord
        {
            Id = Guid.NewGuid(),
            Venue = "test",
            Instrument = "ES",
            TimeframeMinutes = 5,
            Bottom = 100m,
            Top = 90m, // inverted
            Kind = KeyLevelKind.Support,
            Significance = 1m,
            FormedAtBucket = DateTimeOffset.UtcNow,
            TouchCount = 1,
            Active = true,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        Func<Task> save = () => database.SaveChangesAsync();
        await save.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task APriceLevelWithAnUnsetKind_IsRejectedByTheDatabase()
    {
        await using TopstepXDbContext database = _fixture.CreateContext();

        database.PriceLevels.Add(new PriceLevelRecord
        {
            Id = Guid.NewGuid(),
            Venue = "test",
            Instrument = "ES",
            TimeframeMinutes = 5,
            Bottom = 90m,
            Top = 100m,
            Kind = KeyLevelKind.Unknown, // an unlabelled band in front of a reader
            Significance = 1m,
            FormedAtBucket = DateTimeOffset.UtcNow,
            TouchCount = 1,
            Active = true,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        Func<Task> save = () => database.SaveChangesAsync();
        await save.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task ABarsContractId_IsNullableSoUnknownProvenanceIsRepresentable()
    {
        // gh#42 / ADR-0011. Bars stored before this column existed carry no contract and it cannot be
        // recovered — the information was never captured. NOT NULL here would force the migration to invent
        // a value for every one of them, and a guessed provenance is indistinguishable from a recorded one
        // once written. The nullability IS the decision, so it is pinned in the database rather than only in
        // the entity, where a later `required` would silently change it.
        long nullable = await ScalarAsync(
            "SELECT count(*) FROM information_schema.columns "
            + "WHERE table_name = 'Bars' AND column_name = 'ContractId' AND is_nullable = 'YES';");

        nullable.Should().Be(1);
    }

    [Fact]
    public async Task ABarWithNoContract_IsAcceptedAndReadsBackAsUnknown()
    {
        // The state every pre-existing row is in. It must round-trip as null rather than as an empty string:
        // "" is a contract id nobody has, and it would compare equal to itself across a roll, which is the
        // splice being hidden again by a different mechanism.
        await using TopstepXDbContext database = _fixture.CreateContext();
        DateTimeOffset bucket = new(2026, 8, 18, 16, 0, 0, TimeSpan.Zero);

        database.Bars.Add(NewBar("NOCON", bucket, close: 100m));
        await database.SaveChangesAsync();

        await using TopstepXDbContext reader = _fixture.CreateContext();
        BarRecord stored = await reader.Bars.SingleAsync(b =>
            b.Instrument == "NOCON" && b.BucketStart == bucket);

        stored.ContractId.Should().BeNull();
    }

    [Fact]
    public async Task TwoContractsCanShareASymbol_WhichIsWhyTheSeamHasToBeRecorded()
    {
        // The roll, in the store: consecutive buckets under one symbol, produced by two different contracts.
        // The key does not change — that is deliberate (ADR-0011 keeps symbol keying) — so the ONLY thing
        // distinguishing them is this column.
        await using TopstepXDbContext database = _fixture.CreateContext();
        DateTimeOffset first = new(2026, 8, 18, 17, 0, 0, TimeSpan.Zero);

        BarRecord expiring = NewBar("ROLL", first, close: 100m);
        expiring.ContractId = "CON.F.US.EP.U26";
        BarRecord newFront = NewBar("ROLL", first.AddMinutes(5), close: 140m);
        newFront.ContractId = "CON.F.US.EP.Z26";

        database.Bars.AddRange(expiring, newFront);
        await database.SaveChangesAsync();

        await using TopstepXDbContext reader = _fixture.CreateContext();
        List<string?> contracts = await reader.Bars
            .Where(b => b.Instrument == "ROLL")
            .OrderBy(b => b.BucketStart)
            .Select(b => b.ContractId)
            .ToListAsync();

        contracts.Should().Equal("CON.F.US.EP.U26", "CON.F.US.EP.Z26");
    }

    [Fact]
    public async Task BarsAndIndicatorValues_CarryNoRetentionPolicy()
    {
        // Deliberate, and worth pinning: both are records rather than pipelines. A replay reaching for the
        // numbers behind a past decision should find what was actually used, not a window that aged out.
        long count = await ScalarAsync(
            "SELECT count(*) FROM timescaledb_information.jobs "
            + "WHERE proc_name = 'policy_retention';");

        count.Should().Be(0);
    }

    private static BarRecord NewBar(string instrument, DateTimeOffset bucket, decimal close) => new()
    {
        Venue = "test",
        Instrument = instrument,
        ResolutionMinutes = 5,
        BucketStart = bucket,
        Open = 100m,
        High = 101m,
        Low = 99m,
        Close = close,
        Volume = 1_000,
        RecordedAt = DateTimeOffset.UtcNow,
    };

    private async Task<long> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using NpgsqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();

        await using NpgsqlCommand command = new(sql, connection);
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        object? result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }
}
