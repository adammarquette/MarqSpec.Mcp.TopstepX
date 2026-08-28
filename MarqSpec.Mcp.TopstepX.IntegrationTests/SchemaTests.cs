using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
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
    [InlineData("Trades")]
    public async Task TimeSeriesTables_AreHypertables(string table)
    {
        long count = await ScalarAsync(
            "SELECT count(*) FROM timescaledb_information.hypertables WHERE hypertable_name = @t;",
            ("t", table));

        count.Should().Be(1, "the migration probes for timescaledb and this image has it");
    }

    [Theory]
    [InlineData("TapeCoverage")]
    [InlineData("FootprintCells")]
    public async Task LedgerAndProjectionTables_AreNotHypertables(string table)
    {
        // TapeCoverage is a listening ledger (like BarCoverage); FootprintCells are a projection
        // keyed on bucket+price. Neither is the high-volume tape, so neither is partitioned.
        long tables = await ScalarAsync(
            "SELECT count(*) FROM information_schema.tables "
            + "WHERE table_schema = 'public' AND table_name = @t;",
            ("t", table));
        tables.Should().Be(1);

        long hypertables = await ScalarAsync(
            "SELECT count(*) FROM timescaledb_information.hypertables WHERE hypertable_name = @t;",
            ("t", table));
        hypertables.Should().Be(0);
    }

    [Fact]
    public async Task Trades_CarriesACompressionPolicy()
    {
        // gh#215 / ADR-0004. This is the store's first compression policy: keep the tape, shrink
        // the chunks. Compression is a different job type from retention, so the no-retention
        // assertion below staying green is not evidence this was considered.
        long jobs = await ScalarAsync(
            "SELECT count(*) FROM timescaledb_information.jobs "
            + "WHERE proc_name = 'policy_compression' AND hypertable_name = 'Trades';");

        jobs.Should().Be(1);
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
    public async Task ATradesContractId_IsRequiredBecauseAPrintWithoutAContractHasNoMeaning()
    {
        // gh#215. Unlike Bars, where ContractId is nullable provenance beside the key, a tape row
        // that cannot be attributed cannot be stored. The column is in the key and NOT NULL.
        long notNullable = await ScalarAsync(
            "SELECT count(*) FROM information_schema.columns "
            + "WHERE table_name = 'Trades' AND column_name = 'ContractId' AND is_nullable = 'NO';");

        notNullable.Should().Be(1);
    }

    [Fact]
    public async Task FootprintCells_HaveNoContractId_TheAsymmetryTheDictionaryHasToState()
    {
        // The cell key is (venue, instrument, resolution, bucket, price). Contract is a property
        // of the trades that produced the cell, not of the cell — same reason IndicatorValues
        // does not copy Bars.ContractId. Stating the absence is what keeps someone from "fixing"
        // it later.
        long columns = await ScalarAsync(
            "SELECT count(*) FROM information_schema.columns "
            + "WHERE table_name = 'FootprintCells' AND column_name = 'ContractId';");

        columns.Should().Be(0);
    }

    [Theory]
    [InlineData("Trades", "Price")]
    [InlineData("FootprintCells", "Price")]
    public async Task TapePrices_AreNumericEighteenEight(string table, string column)
    {
        long count = await ScalarAsync(
            "SELECT count(*) FROM information_schema.columns "
            + "WHERE table_name = @t AND column_name = @c "
            + "AND numeric_precision = 18 AND numeric_scale = 8;",
            ("t", table),
            ("c", column));

        count.Should().Be(1);
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

    [Fact]
    public async Task PriceLevels_IsGoneFromTheSchema()
    {
        // gh#276 / ADR-0013. Levels are computed on read and no pass ever wrote this table, so it was
        // dropped rather than kept as an empty promise. This replaces the two tests that inserted into it to
        // prove its CHECK constraints rejected inverted zones — coverage of a table's rules cannot outlive
        // the table.
        //
        // Asserted against the database rather than against the model: a DbSet deleted without a migration
        // leaves the relation standing in every already-migrated database, and nothing else here would
        // notice.
        long relations = await ScalarAsync(
            "SELECT count(*) FROM information_schema.tables "
            + "WHERE table_schema = 'public' AND table_name = 'PriceLevels';");

        relations.Should().Be(0);
    }

    [Fact]
    public async Task TwoPrintsAtTheSameMillisecond_SurviveBecauseSequenceBreaksTheTie()
    {
        // The venue supplies no trade id, and two prints share a millisecond routinely. Without
        // Sequence the primary key silently collapses them and the survivor looks ordinary.
        await using TopstepXDbContext database = _fixture.CreateContext();
        DateTimeOffset printed = new(2026, 8, 28, 14, 0, 0, TimeSpan.Zero);

        database.Trades.AddRange(
            NewTrade("TIE", printed, sequence: 1, price: 5000m),
            NewTrade("TIE", printed, sequence: 2, price: 5000.25m));
        await database.SaveChangesAsync();

        await using TopstepXDbContext reader = _fixture.CreateContext();
        List<decimal> prices = await reader.Trades
            .Where(t => t.Instrument == "TIE")
            .OrderBy(t => t.Sequence)
            .Select(t => t.Price)
            .ToListAsync();

        prices.Should().Equal(5000m, 5000.25m);
    }

    [Fact]
    public async Task InsertingASecondPrintForTheSameSequence_IsRejected()
    {
        await using TopstepXDbContext database = _fixture.CreateContext();
        DateTimeOffset printed = new(2026, 8, 28, 15, 0, 0, TimeSpan.Zero);

        database.Trades.Add(NewTrade("DUPETAPE", printed, sequence: 1, price: 5000m));
        await database.SaveChangesAsync();

        await using TopstepXDbContext second = _fixture.CreateContext();
        second.Trades.Add(NewTrade("DUPETAPE", printed, sequence: 1, price: 5001m));

        Func<Task> save = () => second.SaveChangesAsync();
        await save.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task ATradeWithoutAContract_IsRejected()
    {
        // The column is NOT NULL in the database. Empty-string would still be a value; the
        // required-ness is the decision that a print with no attribution has no meaning.
        long notNullable = await ScalarAsync(
            "SELECT count(*) FROM information_schema.columns "
            + "WHERE table_name = 'Trades' AND column_name = 'ContractId' AND is_nullable = 'NO';");
        notNullable.Should().Be(1);

        await using NpgsqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            """
            INSERT INTO "Trades" ("Venue", "Instrument", "ContractId", "TradeTimeUtc", "Sequence",
                                  "Price", "Size", "Direction", "RecordedAt")
            VALUES ('test', 'NOCON', NULL, TIMESTAMPTZ '2026-08-28 16:00:00+00', 1,
                    5000, 1, 1, TIMESTAMPTZ '2026-08-28 16:00:00+00');
            """,
            connection);

        Func<Task> insert = () => command.ExecuteNonQueryAsync();
        await insert.Should().ThrowAsync<PostgresException>()
            .Where(ex => ex.SqlState == PostgresErrorCodes.NotNullViolation);
    }

    [Fact]
    public async Task WritingTheSameFootprintCellTwice_UpdatesRatherThanDuplicating()
    {
        await using TopstepXDbContext database = _fixture.CreateContext();
        DateTimeOffset bucket = new(2026, 8, 28, 14, 0, 0, TimeSpan.Zero);

        database.FootprintCells.Add(NewCell("CELL", bucket, price: 5000m, buy: 10, sell: 4));
        await database.SaveChangesAsync();

        FootprintCellRecord stored = await database.FootprintCells.SingleAsync(c =>
            c.Instrument == "CELL" && c.BucketStart == bucket && c.Price == 5000m);
        stored.BuyVolume = 12;
        await database.SaveChangesAsync();

        List<FootprintCellRecord> rows = await database.FootprintCells
            .Where(c => c.Instrument == "CELL" && c.BucketStart == bucket && c.Price == 5000m)
            .ToListAsync();

        rows.Should().ContainSingle();
        rows[0].BuyVolume.Should().Be(12);
    }

    [Fact]
    public async Task TapeCoverage_RoundTripsAHalfOpenListeningRange()
    {
        await using TopstepXDbContext database = _fixture.CreateContext();
        DateTimeOffset start = new(2026, 8, 28, 13, 0, 0, TimeSpan.Zero);
        DateTimeOffset end = start.AddHours(1);

        database.TapeCoverage.Add(new TapeCoverageRecord
        {
            Venue = "test",
            Instrument = "COV",
            ContractId = "CON.F.US.EP.U26",
            RangeStart = start,
            RangeEnd = end,
            RecordedAt = DateTimeOffset.UtcNow,
        });
        await database.SaveChangesAsync();

        await using TopstepXDbContext reader = _fixture.CreateContext();
        TapeCoverageRecord stored = await reader.TapeCoverage.SingleAsync(c => c.Instrument == "COV");

        stored.RangeStart.Should().Be(start);
        stored.RangeEnd.Should().Be(end);
        stored.ContractId.Should().Be("CON.F.US.EP.U26");
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

    private static TradeRecord NewTrade(
        string instrument,
        DateTimeOffset printed,
        long sequence,
        decimal price) => new()
        {
            Venue = "test",
            Instrument = instrument,
            ContractId = "CON.F.US.EP.U26",
            TradeTimeUtc = printed,
            Sequence = sequence,
            Price = price,
            Size = 1,
            Direction = TradeDirection.Buy,
            RecordedAt = DateTimeOffset.UtcNow,
        };

    private static FootprintCellRecord NewCell(
        string instrument,
        DateTimeOffset bucket,
        decimal price,
        long buy,
        long sell) => new()
        {
            Venue = "test",
            Instrument = instrument,
            ResolutionMinutes = 5,
            BucketStart = bucket,
            Price = price,
            BuyVolume = buy,
            SellVolume = sell,
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
