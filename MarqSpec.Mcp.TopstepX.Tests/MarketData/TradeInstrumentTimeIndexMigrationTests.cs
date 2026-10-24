using System.Reflection;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Data.Migrations;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// The instrument-time index is built outside the migration transaction (gh#756).
/// </summary>
public sealed class TradeInstrumentTimeIndexMigrationTests
{
    [Fact]
    public void Up_WhenBuildingTheInstrumentTimeIndex_UsesAPerChunkIndexOutsideTheTransaction()
    {
        SqlOperation sql = Operation("Up");

        sql.SuppressTransaction.Should().BeTrue();
        sql.Sql.Should().Contain("CREATE INDEX IF NOT EXISTS \"IX_Trades_Instrument_TradeTimeUtc\"");
        sql.Sql.Should().Contain("(\"Instrument\", \"TradeTimeUtc\")");
        sql.Sql.Should().Contain("WITH (timescaledb.transaction_per_chunk)");
    }

    [Fact]
    public void Down_WhenRevertingTheInstrumentTimeIndex_DropsItOutsideTheTransaction()
    {
        SqlOperation sql = Operation("Down");

        sql.SuppressTransaction.Should().BeTrue();
        sql.Sql.Should().Contain("DROP INDEX IF EXISTS \"IX_Trades_Instrument_TradeTimeUtc\"");
    }

    private static SqlOperation Operation(string direction)
    {
        AddTradeInstrumentTimeIndex migration = new();
        MigrationBuilder builder = new(activeProvider: null);
        MethodInfo method = typeof(AddTradeInstrumentTimeIndex).GetMethod(
            direction,
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(migration, [builder]);

        return builder.Operations.Should().ContainSingle().Which.Should().BeOfType<SqlOperation>().Subject;
    }
}
