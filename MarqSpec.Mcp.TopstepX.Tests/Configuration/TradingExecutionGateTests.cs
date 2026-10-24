using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.Tests.Configuration;

/// <summary>Configuration-layer refusals for trading writes before any MCP tool exists (gh#708).</summary>
[Trait("Gate", "ADR-0024")]
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

    /// <summary>Unknown stage refuses and invokes the observability hook.</summary>
    [Fact]
    public void RefuseIfStageBlocksWrite_InvokesSignalAndRefuses_ForUnknown()
    {
        RecordingUnknownSignal signal = new();
        TradingExecutionGate gate = Gate(new TradingExecutionOptions { Enabled = true });

        gate.RefuseIfStageBlocksWrite(AccountStage.Unknown, accountId: 55, signal)
            .Should().Be(TradingExecutionRefusal.UnknownAccountStage(55));

        signal.RefusedAccountIds.Should().ContainSingle().Which.Should().Be(55);
    }

    /// <summary>Practice stage passes the practice-only layer.</summary>
    [Fact]
    public void RefuseIfStageBlocksWrite_Passes_ForPractice()
    {
        TradingExecutionGate gate = Gate(new TradingExecutionOptions { Enabled = true });

        gate.RefuseIfStageBlocksWrite(AccountStage.Practice, accountId: 1, new NullTradingUnknownStageRefusalSignal())
            .Should().BeNull();
    }

    /// <summary>Funded stage is refused before any adapter call.</summary>
    [Fact]
    public void RefuseIfStageBlocksWrite_Refuses_ForFunded()
    {
        TradingExecutionGate gate = Gate(new TradingExecutionOptions { Enabled = true });

        gate.RefuseIfStageBlocksWrite(AccountStage.Funded, accountId: 9, new NullTradingUnknownStageRefusalSignal())
            .Should().Be(TradingExecutionRefusal.NonPracticeAccountStage(9, AccountStage.Funded));
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
        return new TradingExecutionGate(TradingOptionsMonitors.Fixed(options), _killSwitch, new ProjectXAccountStageResolver());
    }

    private sealed class FakeKillSwitch : ITradingKillSwitch
    {
        public bool Engaged { get; set; }

        public bool IsEngaged => Engaged;
    }

    private sealed class RecordingUnknownSignal : ITradingUnknownStageRefusalSignal
    {
        public List<int> RefusedAccountIds { get; } = [];

        public void UnknownStageWriteRefused(int accountId) => RefusedAccountIds.Add(accountId);
    }

    [Fact]
    public void TradingExecutionGate_OptionsCreate_StillRefusesWhenTradingIsDisabled()
    {
        TradingExecutionGate gate = new(
            Options.Create(new TradingExecutionOptions { Enabled = false }),
            new FakeKillSwitch(),
            new ProjectXAccountStageResolver());

        gate.RefuseIfDisabledOrHalted().Should().Be(TradingExecutionRefusal.TradingDisabled);
    }

    [Fact]
    public void TradingExecutionGate_MasterSwitch_FollowsARealOptionsMonitorChange()
    {
        Dictionary<string, string?> settings = new()
        {
            ["ConnectionStrings:Default"] = "Host=localhost;Database=x;Username=u;Password=p",
            ["MarketData:Instruments"] = "ES,NQ,MES",
            ["MarketData:SessionCloseCentral"] = "16:00",
            ["MarketData:MaxRows"] = "5000",
            ["Trading:Enabled"] = "true",
            ["Trading:AllowedAccountIds"] = "1",
        };
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(settings);
        Program.ConfigureServices(builder, new McpOptions { Transport = McpTransport.Stdio });
        using ServiceProvider provider = builder.Services.BuildServiceProvider();
        TradingExecutionGate gate = provider.GetRequiredService<TradingExecutionGate>();

        gate.RefuseIfDisabledOrHalted().Should().BeNull();

        IConfigurationRoot configuration = builder.Configuration;
        MemoryConfigurationProvider memory = configuration.Providers
            .OfType<MemoryConfigurationProvider>()
            .Last();
        memory.Set("Trading:Enabled", "false");
        configuration.Reload();

        gate.RefuseIfDisabledOrHalted().Should().Be(TradingExecutionRefusal.TradingDisabled);
    }

    [Fact]
    public void TradingKillSwitch_FollowsARealOptionsMonitorChange()
    {
        using ServiceProvider provider = TradingHost(out IConfigurationRoot configuration, out MemoryConfigurationProvider memory);
        TradingKillSwitch killSwitch = provider.GetRequiredService<TradingKillSwitch>();
        TradingExecutionGate gate = provider.GetRequiredService<TradingExecutionGate>();

        killSwitch.IsEngaged.Should().BeFalse();
        gate.RefuseIfDisabledOrHalted().Should().BeNull();

        memory.Set("Trading:KillSwitch", "true");
        configuration.Reload();

        killSwitch.IsEngaged.Should().BeTrue();
        gate.RefuseIfDisabledOrHalted().Should().Be(TradingExecutionRefusal.KillSwitchEngaged);
    }

    [Fact]
    public void TradingContractDenylist_FollowsARealOptionsMonitorChange()
    {
        using ServiceProvider provider = TradingHost(out IConfigurationRoot configuration, out MemoryConfigurationProvider memory);
        using IServiceScope scope = provider.CreateScope();
        TradingContractDenylist denylist = scope.ServiceProvider.GetRequiredService<TradingContractDenylist>();

        denylist.RefuseIfContractDenied("CON.F.US.MES.Z26").Should().BeNull();

        memory.Set("Trading:DeniedSymbols", "MES");
        configuration.Reload();

        denylist.RefuseIfContractDenied("CON.F.US.MES.Z26")
            .Should().Be(TradingExecutionRefusal.SymbolDenied("MES"));
    }

    [Fact]
    public void TradingExecutionGate_InvalidReload_FailsClosed()
    {
        using ServiceProvider provider = TradingHost(out IConfigurationRoot configuration, out MemoryConfigurationProvider memory);
        using IServiceScope scope = provider.CreateScope();
        TradingExecutionGate gate = provider.GetRequiredService<TradingExecutionGate>();
        TradingContractDenylist denylist = scope.ServiceProvider.GetRequiredService<TradingContractDenylist>();

        gate.RefuseIfDisabledOrHalted().Should().BeNull();

        memory.Set("Trading:MaxContractsPerOrder", "0");
        FluentActions.Invoking(configuration.Reload)
            .Should().Throw<AggregateException>()
            .Which.Flatten().InnerExceptions.Should().Contain(exception =>
                exception is OptionsValidationException);

        TradingExecutionRefusal refusal = gate.RefuseIfDisabledOrHalted()
            ?? throw new InvalidOperationException("an invalid reload should refuse");
        refusal.Code.Should().Be("trading_configuration_invalid");
        refusal.Message.Should().Be(
            "Trading configuration failed validation. The last good values are not kept.");
        gate.RefuseIfDisabledOrHalted().Should().Be(refusal);

        TradingExecutionRefusal denied = denylist.RefuseIfContractDenied("CON.F.US.MES.Z26")
            ?? throw new InvalidOperationException("an invalid reload should refuse the denylist");
        denied.Code.Should().Be("trading_configuration_invalid");
    }

    private static ServiceProvider TradingHost(
        out IConfigurationRoot configuration,
        out MemoryConfigurationProvider memory)
    {
        Dictionary<string, string?> settings = new()
        {
            ["ConnectionStrings:Default"] = "Host=localhost;Database=x;Username=u;Password=p",
            ["MarketData:Instruments"] = "ES,NQ,MES",
            ["MarketData:SessionCloseCentral"] = "16:00",
            ["MarketData:MaxRows"] = "5000",
            ["Trading:Enabled"] = "true",
            ["Trading:AllowedAccountIds"] = "1",
            ["Trading:KillSwitch"] = "false",
            ["Trading:DeniedSymbols"] = "",
        };
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(settings);
        Program.ConfigureServices(builder, new McpOptions { Transport = McpTransport.Stdio });
        ServiceProvider provider = builder.Services.BuildServiceProvider();
        configuration = builder.Configuration;
        memory = configuration.Providers.OfType<MemoryConfigurationProvider>().Last();
        return provider;
    }

    /// <summary>Kill switch refuses before any account fetch.</summary>
    [Fact]
    [Trait("Gate", "ADR-0024")]
    public async Task RefuseWriteUnlessPermittedAsync_RefusesOnKillSwitch_WithoutCallingMarketData()
    {
        CountingAccountsGateway marketData = new([Account(1, AccountStage.Practice)]);
        FakeKillSwitch killSwitch = new() { Engaged = true };
        TradingExecutionGate gate = new(
            TradingOptionsMonitors.Fixed(new TradingExecutionOptions { Enabled = true }),
            killSwitch,
            new ProjectXAccountStageResolver());

        TradingExecutionRefusal? refusal = await gate.RefuseWriteUnlessPermittedAsync(
            accountId: 1,
            marketData,
            new NullTradingUnknownStageRefusalSignal(),
            CancellationToken.None);

        refusal.Should().Be(TradingExecutionRefusal.KillSwitchEngaged);
        marketData.GetAccountsCallCount.Should().Be(0);
    }

    /// <summary>Missing account id refuses after a fresh fetch.</summary>
    [Fact]
    [Trait("Gate", "ADR-0024")]
    public async Task RefuseWriteUnlessPermittedAsync_ReturnsAccountNotFound_WhenIdIsMissing()
    {
        CountingAccountsGateway marketData = new([Account(1, AccountStage.Practice)]);
        TradingExecutionGate gate = Gate(new TradingExecutionOptions { Enabled = true });

        TradingExecutionRefusal? refusal = await gate.RefuseWriteUnlessPermittedAsync(
            accountId: 999,
            marketData,
            new NullTradingUnknownStageRefusalSignal(),
            CancellationToken.None);

        refusal.Should().Be(TradingExecutionRefusal.AccountNotFound(999));
        marketData.GetAccountsCallCount.Should().Be(1);
    }

    /// <summary>Inactive accounts refuse even when the stage is practice.</summary>
    [Fact]
    [Trait("Gate", "ADR-0024")]
    public async Task RefuseWriteUnlessPermittedAsync_ReturnsAccountNotActive_WhenCanTradeIsFalse()
    {
        CountingAccountsGateway marketData = new([Account(1, AccountStage.Practice, canTrade: false)]);
        TradingExecutionGate gate = Gate(new TradingExecutionOptions { Enabled = true });

        TradingExecutionRefusal? refusal = await gate.RefuseWriteUnlessPermittedAsync(
            accountId: 1,
            marketData,
            new NullTradingUnknownStageRefusalSignal(),
            CancellationToken.None);

        refusal.Should().Be(TradingExecutionRefusal.AccountNotActive(1));
    }

    private static VenueAccount Account(int accountId, AccountStage stage, bool canTrade = true) =>
        new(accountId, stage, CanTrade: canTrade, IsVisible: true, Balance: 50_000m);

    private sealed class CountingAccountsGateway(IReadOnlyList<VenueAccount> accounts) : IMarketDataGateway
    {
        public int GetAccountsCallCount { get; private set; }

        public string VenueId => "stub";

        public Task<IReadOnlyList<VenueAccount>> GetAccountsAsync(
            bool onlyActive,
            CancellationToken cancellationToken)
        {
            GetAccountsCallCount++;
            _ = onlyActive;
            return Task.FromResult(accounts);
        }

        public Task<IReadOnlyList<VenueContract>> ResolveContractsAsync(
            InstrumentId instrument,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<VenueContract?> FindContractAsync(
            InstrumentId instrument,
            ContractExpiry expiry,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Bar>> GetBarsAsync(
            string contractId,
            BarRange window,
            TimeSpan barSize,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<VenuePosition>> GetOpenPositionsAsync(
            int accountId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<VenueOrder>> GetOrdersAsync(
            int accountId,
            BarRange? window,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<VenueTrade>> GetTradesAsync(
            int accountId,
            BarRange window,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
