using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.Tests.Configuration;

/// <summary>Configuration-layer refusals for trading writes before any MCP tool exists (gh#708).</summary>
public sealed class TradingExecutionGateTests
{
    private readonly FakeKillSwitch _killSwitch = new();

    /// <summary>Disabled trading refuses before any other check matters.</summary>
    [Fact]
    public void RefuseIfDisabledOrHalted_ReturnsTradingDisabled_WhenEnableIsOff()
    {
        TradingExecutionGate gate = Gate(new TradingExecutionOptions { Enabled = false });

        gate.RefuseIfDisabledOrHalted()
            .Should().Be(TradingExecutionRefusal.TradingDisabled);
    }

    /// <summary>The kill switch refuses even when enable is on.</summary>
    [Fact]
    public void RefuseIfDisabledOrHalted_ReturnsKillSwitch_WhenEngaged()
    {
        _killSwitch.Engaged = true;
        TradingExecutionGate gate = Gate(new TradingExecutionOptions { Enabled = true });

        gate.RefuseIfDisabledOrHalted()
            .Should().Be(TradingExecutionRefusal.KillSwitchEngaged);
    }

    /// <summary>An allowlist mismatch names the account id.</summary>
    [Fact]
    public void RefuseIfNotAllowlisted_ReturnsRefusal_WhenTheAccountIsMissing()
    {
        TradingExecutionGate gate = Gate(new TradingExecutionOptions
        {
            Enabled = true,
            AllowedAccountIds = "100",
        });

        gate.RefuseIfNotAllowlisted(200)
            .Should().Be(TradingExecutionRefusal.AccountNotAllowlisted(200));
    }

    /// <summary>An unset allowlist passes every account through this layer.</summary>
    [Fact]
    public void RefuseIfNotAllowlisted_Passes_WhenAllowlistIsUnset()
    {
        TradingExecutionGate gate = Gate(new TradingExecutionOptions { Enabled = true });

        gate.RefuseIfNotAllowlisted(999).Should().BeNull();
    }

    /// <summary>A denied symbol refuses with the normalised symbol.</summary>
    [Fact]
    public void RefuseIfSymbolDenied_ReturnsRefusal_ForListedSymbols()
    {
        TradingExecutionGate gate = Gate(new TradingExecutionOptions
        {
            Enabled = true,
            DeniedSymbols = "CL",
        });

        gate.RefuseIfSymbolDenied("cl")
            .Should().Be(TradingExecutionRefusal.SymbolDenied("CL"));
    }

    /// <summary>Per-order size above the cap is refused.</summary>
    [Fact]
    public void RefuseIfOrderSizeExceedsLimit_ReturnsRefusal_WhenOverTheCap()
    {
        TradingExecutionGate gate = Gate(new TradingExecutionOptions
        {
            Enabled = true,
            MaxContractsPerOrder = 3,
        });

        gate.RefuseIfOrderSizeExceedsLimit(4)
            .Should().Be(TradingExecutionRefusal.OrderSizeExceedsLimit(4, 3));
    }

    /// <summary>Daily totals include the request being evaluated.</summary>
    [Fact]
    public void RefuseIfDailyLimitExceeded_ReturnsRefusal_WhenTheDayWouldOverflow()
    {
        TradingExecutionGate gate = Gate(new TradingExecutionOptions
        {
            Enabled = true,
            MaxContractsPerDay = 10,
        });

        gate.RefuseIfDailyLimitExceeded(accountId: 1, contractsAlreadyToday: 8, requestedSize: 3)
            .Should().Be(TradingExecutionRefusal.DailyContractLimitExceeded(1, 8, 3, 10));
    }

    private TradingExecutionGate Gate(TradingExecutionOptions options)
    {
        return new TradingExecutionGate(Options.Create(options), _killSwitch);
    }

    private sealed class FakeKillSwitch : ITradingKillSwitch
    {
        public bool Engaged { get; set; }

        public bool IsEngaged => Engaged;
    }
}
