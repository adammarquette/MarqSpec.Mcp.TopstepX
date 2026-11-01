using System.Net.Sockets;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MarqSpec.Mcp.TopstepX.Tests.Configuration;

/// <summary>Which failures are a trading-store outage (gh#958).</summary>
[Trait("Gate", "ADR-0024")]
public sealed class TradingStoreFaultTests
{
    [Fact]
    public void TradingRiskGate_StoreFault_IncludesAConnectionFailure()
    {
        NpgsqlException broken = new(
            "Failed to connect",
            new SocketException((int)SocketError.ConnectionRefused));

        TradingStoreFault.IsInfrastructure(broken).Should().BeTrue();
        TradingStoreFault.IsInfrastructure(new InvalidOperationException("transient failure", broken))
            .Should()
            .BeTrue();
        TradingStoreFault.IsInfrastructure(new DbUpdateException("save failed", broken)).Should().BeTrue();
        TradingStoreFault.IsInfrastructure(new TimeoutException("command timeout")).Should().BeTrue();
        TradingStoreFault.IsInfrastructure(Postgres("08006")).Should().BeTrue();
        TradingStoreFault.IsInfrastructure(Postgres("57P01")).Should().BeTrue();
        TradingStoreFault.IsInfrastructure(new AggregateException(Postgres("08006"), Postgres("57P01")))
            .Should()
            .BeTrue();
    }

    [Fact]
    public void TradingRiskGate_StoreFault_ExcludesSchemaConstraintAndProgrammingErrors()
    {
        using CancellationTokenSource caller = new();
        caller.Cancel();

        TradingStoreFault.IsInfrastructure(Postgres("42P01")).Should().BeFalse();
        TradingStoreFault.IsInfrastructure(Postgres("23505")).Should().BeFalse();
        TradingStoreFault.IsInfrastructure(new DbUpdateException("unique", Postgres("23505")))
            .Should()
            .BeFalse();
        TradingStoreFault.IsInfrastructure(new NullReferenceException()).Should().BeFalse();
        TradingStoreFault.IsInfrastructure(new ArgumentException("bad")).Should().BeFalse();
        TradingStoreFault.IsInfrastructure(
                new ArgumentException("wrapper", new SocketException((int)SocketError.ConnectionRefused)))
            .Should()
            .BeFalse();
        TradingStoreFault.IsInfrastructure(new InvalidOperationException("our bug")).Should().BeFalse();
        TradingStoreFault.IsInfrastructure(new OperationCanceledException(), caller.Token).Should().BeFalse();
        TradingStoreFault.IsInfrastructure(new OperationCanceledException()).Should().BeTrue();
        TradingStoreFault.IsInfrastructure(new AggregateException(Postgres("42P01"))).Should().BeFalse();
    }

    private static PostgresException Postgres(string sqlState) =>
        new("store", "ERROR", "ERROR", sqlState);
}
