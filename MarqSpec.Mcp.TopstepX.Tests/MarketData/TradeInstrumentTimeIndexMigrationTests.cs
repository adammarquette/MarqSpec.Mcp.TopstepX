using System.Reflection;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
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

        sql.Should().HaveCount(5);
        sql.Should().OnlyContain(operation => operation.SuppressTransaction);
        SqlOperation procedure = sql[0];
        procedure.Sql.Should().Contain("pg_extension");
        procedure.Sql.Should().Contain("timescaledb_information.hypertables");
        procedure.Sql.Should().Contain("hypertable_name = 'Trades'");
        procedure.Sql.Should().NotContain("hypertable_name = 'trades'");
        procedure.Sql.Should().Contain("ON public.\"Trades\" (\"Instrument\", \"TradeTimeUtc\");");
        procedure.Sql.Should().Contain("CREATE OR REPLACE PROCEDURE public.marqspec_build_ix_trades_instrument_time()");
        procedure.Sql.Should().NotContain(TradeInstrumentIndexBuildInterceptor.PerChunkMarker);
        procedure.Sql.Should().NotContain("pg_advisory_unlock");
        procedure.Sql.Should().Contain("set_config('" + TradeInstrumentIndexBuildInterceptor.SessionSetting + "', '0'");
        sql[1].Sql.Should().Be("CALL public.marqspec_build_ix_trades_instrument_time();");
        sql[1].Sql.Should().NotContain("CREATE INDEX");
        sql[2].Sql.Should().Contain(TradeInstrumentIndexBuildInterceptor.PerChunkMarker);
        sql[2].Sql.Should().Contain("ON public.\"Trades\"");
        sql[2].Sql.Should().Contain("CREATE INDEX IF NOT EXISTS");
        sql[3].Sql.Should().Be(TradeInstrumentIndexBuildInterceptor.ReleaseLockSql);
        sql.Should().ContainSingle(operation => operation.Sql.Contains("pg_advisory_unlock", StringComparison.Ordinal));
        sql[4].Sql.Should().Contain("DROP PROCEDURE IF EXISTS public.marqspec_build_ix_trades_instrument_time()");
    }

    [Fact]
    public void Up_WhenTheBuildIsPerChunk_IsItsOwnTopLevelStatement()
    {
        IReadOnlyList<SqlOperation> sql = Up();

        sql[0].Sql.Should().NotContain(TradeInstrumentIndexBuildInterceptor.PerChunkMarker);
        sql[0].Sql.Should().Contain("set_config('" + TradeInstrumentIndexBuildInterceptor.SessionSetting + "', '1'");
        sql[1].Sql.Should().Be("CALL public.marqspec_build_ix_trades_instrument_time();");
        sql[2].Sql.Should().Contain("WITH (" + TradeInstrumentIndexBuildInterceptor.PerChunkMarker + ")");
        sql[2].SuppressTransaction.Should().BeTrue();
        sql[3].Sql.IndexOf("pg_advisory_unlock", StringComparison.Ordinal).Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void SessionRequestsPerChunk_WhenTheMarkIsAbsent_IsFalse()
    {
        TradeInstrumentIndexBuildInterceptor.SessionRequestsPerChunk(null).Should().BeFalse();
        TradeInstrumentIndexBuildInterceptor.SessionRequestsPerChunk(DBNull.Value).Should().BeFalse();
        TradeInstrumentIndexBuildInterceptor.SessionRequestsPerChunk("0").Should().BeFalse();
        TradeInstrumentIndexBuildInterceptor.SessionRequestsPerChunk("1").Should().BeTrue();
    }

    [Fact]
    public void Context_WhenConstructed_InstallsThePerChunkIndexGate()
    {
        DbContextOptions<TopstepXDbContext> options = new DbContextOptionsBuilder<TopstepXDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using TopstepXDbContext context = new(options);

        CoreOptionsExtension core = context.GetService<IDbContextOptions>()
            .Extensions.OfType<CoreOptionsExtension>().Single();
        core.Interceptors.Should().Contain(interceptor => interceptor is TradeInstrumentIndexBuildInterceptor);
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
    public void Up_WhenTwoReplicasBuild_HoldsOneSessionAdvisoryLockUntilTheStatementAfterTheBuild()
    {
        IReadOnlyList<SqlOperation> operations = Up();
        string procedure = operations[0].Sql;

        // 123207681 is 0x07580001. The procedure takes the lock and does not release it. The one unlock
        // is the statement after the build, so a plain database does not unlock twice. A second replica
        // waits: the SQL does not set lock_timeout, which is the only way pg_advisory_lock raises 55P03.
        procedure.Should().Contain("pg_advisory_lock(123207681, 190601)");
        procedure.Should().NotContain("pg_advisory_unlock");
        procedure.Should().NotContain("lock_timeout");
        procedure.Should().Contain("0x07580001");
        procedure.Should().NotContain("0x07560001");
        procedure.IndexOf("pg_advisory_lock", StringComparison.Ordinal)
            .Should().BeLessThan(procedure.IndexOf("DROP INDEX", StringComparison.Ordinal));
        operations[2].Sql.Should().Contain(TradeInstrumentIndexBuildInterceptor.PerChunkMarker);
        operations[3].Sql.Should().Be("SELECT pg_advisory_unlock(123207681, 190601);");
        operations.Count(operation => operation.Sql.Contains("pg_advisory_unlock", StringComparison.Ordinal))
            .Should().Be(1);
    }

    [Fact]
    public void Down_WhenRevertingTheInstrumentTimeIndex_DropsItInsideTheMigrationTransaction()
    {
        SqlOperation sql = Down();

        sql.SuppressTransaction.Should().BeFalse();
        sql.Sql.Should().Contain("DROP INDEX IF EXISTS public.\"IX_Trades_Instrument_TradeTimeUtc\"");
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
