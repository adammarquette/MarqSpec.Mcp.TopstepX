using System.Reflection;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Data.Migrations;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// The instrument-time index follows ADR-0004 and does not keep an invalid build (gh#756).
/// </summary>
public sealed class TradeInstrumentTimeIndexMigrationTests
{
    [Fact]
    public void Up_WhenTimescaleIsAbsent_CreatesAPlainIndexOutsideTheTransaction()
    {
        IReadOnlyList<SqlOperation> sql = Up();

        sql.Should().HaveCount(3);
        sql.Should().OnlyContain(operation => operation.SuppressTransaction);
        SqlOperation procedure = sql[0];
        procedure.Sql.Should().Contain("pg_extension");
        procedure.Sql.Should().Contain("timescaledb_information.hypertables");
        procedure.Sql.Should().Contain("ON \"Trades\" (\"Instrument\", \"TradeTimeUtc\");");
        procedure.Sql.Should().Contain("WITH (timescaledb.transaction_per_chunk)");
        sql[1].Sql.Should().Be("CALL marqspec_build_ix_trades_instrument_time();");
        sql[1].Sql.Should().NotContain("CREATE INDEX");
        sql[2].Sql.Should().Contain("DROP PROCEDURE IF EXISTS marqspec_build_ix_trades_instrument_time()");
    }

    [Fact]
    public void Up_WhenTheIndexIsInvalid_DropsItBeforeCreating()
    {
        string sql = Up()[0].Sql;

        int invalid = sql.IndexOf("NOT i.indisvalid", StringComparison.Ordinal);
        int drop = sql.IndexOf("DROP INDEX public.\"IX_Trades_Instrument_TradeTimeUtc\"", StringComparison.Ordinal);
        int create = sql.IndexOf("CREATE INDEX IF NOT EXISTS", StringComparison.Ordinal);

        invalid.Should().BeGreaterThan(0);
        drop.Should().BeGreaterThan(invalid);
        create.Should().BeGreaterThan(drop);
    }

    [Fact]
    public void Up_WhenTwoReplicasBuild_TakesASessionAdvisoryLock()
    {
        string sql = Up()[0].Sql;

        sql.Should().Contain("pg_advisory_lock(123207681, 190601)");
        sql.Should().Contain("pg_advisory_unlock(123207681, 190601)");
        int commit = sql.IndexOf("COMMIT;", StringComparison.Ordinal);
        int perChunk = sql.IndexOf("timescaledb.transaction_per_chunk", StringComparison.Ordinal);
        int unlock = sql.IndexOf("pg_advisory_unlock", StringComparison.Ordinal);
        commit.Should().BeGreaterThan(0);
        perChunk.Should().BeGreaterThan(commit);
        unlock.Should().BeGreaterThan(perChunk);
    }

    [Fact]
    public void Down_WhenRevertingTheInstrumentTimeIndex_DropsItInsideTheMigrationTransaction()
    {
        SqlOperation sql = Down();

        sql.SuppressTransaction.Should().BeFalse();
        sql.Sql.Should().Contain("DROP INDEX IF EXISTS \"IX_Trades_Instrument_TradeTimeUtc\"");
    }

    private static IReadOnlyList<SqlOperation> Up() => Operations("Up");

    private static SqlOperation Down() => Operations("Down").Should().ContainSingle().Subject;

    private static IReadOnlyList<SqlOperation> Operations(string direction)
    {
        AddTradeInstrumentTimeIndex migration = new();
        MigrationBuilder builder = new(activeProvider: null);
        MethodInfo method = typeof(AddTradeInstrumentTimeIndex).GetMethod(
            direction,
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(migration, [builder]);

        builder.Operations.Should().AllBeOfType<SqlOperation>();
        return builder.Operations.Cast<SqlOperation>().ToList();
    }
}
