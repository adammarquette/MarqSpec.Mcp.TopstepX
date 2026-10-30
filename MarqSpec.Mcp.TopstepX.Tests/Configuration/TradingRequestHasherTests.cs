using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Venue;

namespace MarqSpec.Mcp.TopstepX.Tests.Configuration;

/// <summary>Stable request digests for the trading audit log (gh#711).</summary>
public sealed class TradingRequestHasherTests
{
    [Fact]
    public void PlaceOrder_IsStable_ForTheSameCommand()
    {
        ExecutionPlaceOrderCommand command = new(
            1,
            "CON.F.US.MES.Z26",
            ExecutionOrderType.Limit,
            VenueSide.Buy,
            2,
            6800.25m,
            null,
            null);

        string first = TradingRequestHasher.HashPlace(command, dryRun: false);
        string second = TradingRequestHasher.HashPlace(command, dryRun: false);

        first.Should().Be(second);
        first.Should().HaveLength(64);
    }

    [Fact]
    public void PlaceOrder_Changes_WhenSizeChanges()
    {
        ExecutionPlaceOrderCommand baseline = new(
            1,
            "CON.F.US.MES.Z26",
            ExecutionOrderType.Limit,
            VenueSide.Buy,
            1,
            6800.25m,
            null,
            null);

        ExecutionPlaceOrderCommand changed = baseline with { Size = 2 };

        TradingRequestHasher.HashPlace(baseline, dryRun: false)
            .Should().NotBe(TradingRequestHasher.HashPlace(changed, dryRun: false));
    }

    [Fact]
    public void PlaceOrder_Changes_WhenDryRunChanges()
    {
        ExecutionPlaceOrderCommand command = new(
            1,
            "CON.F.US.MES.Z26",
            ExecutionOrderType.Market,
            VenueSide.Buy,
            1,
            null,
            null,
            null);

        TradingRequestHasher.HashPlace(command, dryRun: false)
            .Should().NotBe(TradingRequestHasher.HashPlace(command, dryRun: true));
    }
}
