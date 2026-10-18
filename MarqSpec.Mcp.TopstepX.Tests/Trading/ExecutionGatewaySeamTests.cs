// Sgt Sentinel — gh#720 Tier A: the gated IExecutionGateway seam (gh#710, PR #734) against a fake venue client.
// Requirements: ADR-0024 §2 (practice-only from a fresh account fetch), §5 layers 1-3 and broker-ack rule,
// gh#706 epic, gh#720 body (zero gateway write calls for Evaluation/Funded/Unknown, allowlisted or not).
// Synthetic accounts only; no network, no credentials.
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Venue;

namespace MarqSpec.Mcp.TopstepX.Tests.Trading;

[Trait("Issue", "gh#720")]
[Trait("Tier", "A-gateway-seam")]
public sealed class ExecutionGatewaySeamTests
{
    public static TheoryData<int, string> Practice => new()
    {
        { SyntheticAccounts.PracticeId, "PRAC-*" },
        { SyntheticAccounts.LegacyPracticeId, "PRACTICE*" },
    };

    public static TheoryData<int, string> Refused => new()
    {
        { SyntheticAccounts.EvaluationId, "non_practice_account" },
        { SyntheticAccounts.FundedId, "non_practice_account" },
        { SyntheticAccounts.UnknownId, "unknown_account_stage" },
        { SyntheticAccounts.MissingId, "account_not_found" },
    };

    [Theory(DisplayName = "TP-720-A01 practice account: every verb transmits and is broker-acknowledged")]
    [MemberData(nameof(Practice))]
    public async Task Practice_account_transmits_every_verb(int accountId, string family)
    {
        GatewayRig rig = new();
        foreach ((string verb, Func<Task<ExecutionWriteResult>> call) in rig.AllVerbs(accountId))
        {
            ExecutionWriteResult result = await call();
            result.GateRefusal.Should().BeNull($"{family} {verb}");
            result.Lifecycle.Should().Be(ExecutionWriteLifecycle.Acknowledged, $"{family} {verb}");
        }

        rig.Venue.WriteCalls.Select(c => c.Verb).Should().Equal(
            "PlaceOrderAsync", "ModifyOrderAsync", "CancelOrderAsync", "ClosePositionAsync", "PartialClosePositionAsync");
        rig.Accounts.AccountFetches.Should().Be(5, "ADR-0024 §2: stage is re-derived from a FRESH fetch on every write");
    }

    [Theory(DisplayName = "TP-720-A02 non-practice/unknown/missing: refused on every verb with ZERO venue writes")]
    [MemberData(nameof(Refused))]
    public async Task Non_practice_account_is_refused_with_zero_writes(int accountId, string code)
    {
        GatewayRig rig = new();
        foreach ((string verb, Func<Task<ExecutionWriteResult>> call) in rig.AllVerbs(accountId))
        {
            ExecutionWriteResult result = await call();
            result.Lifecycle.Should().Be(ExecutionWriteLifecycle.NotTransmitted, verb);
            result.GateRefusal!.Value.Code.Should().Be(code, verb);
            result.GateRefusal!.Value.Message.Should().Contain(accountId.ToString(), "the refusal names the accountId (§2.4)");
        }

        rig.Venue.WriteCalls.Should().BeEmpty();
    }

    [Theory(DisplayName = "TP-720-A03 allowlist cannot bypass practice-only (allowlisted Eval/Funded/Unknown still refused)")]
    [InlineData(SyntheticAccounts.EvaluationId)]
    [InlineData(SyntheticAccounts.FundedId)]
    [InlineData(SyntheticAccounts.UnknownId)]
    public async Task Allowlisted_non_practice_is_still_refused(int accountId)
    {
        GatewayRig rig = new(new TradingExecutionOptions { Enabled = true, AllowedAccountIds = accountId.ToString() });

        ExecutionWriteResult result = await rig.Gateway.PlaceOrder(GatewayRig.Limit(accountId), default);

        result.Lifecycle.Should().Be(ExecutionWriteLifecycle.NotTransmitted);
        rig.Venue.WriteCalls.Should().BeEmpty();
    }

    [Fact(DisplayName = "TP-720-A04 Unknown stage refusal raises the parser-gap signal (ADR-0024 §2, gh#733)")]
    public async Task Unknown_stage_raises_signal()
    {
        GatewayRig rig = new();
        await rig.Gateway.PlaceOrder(GatewayRig.Limit(SyntheticAccounts.UnknownId), default);
        rig.Unknown.Refused.Should().Equal(SyntheticAccounts.UnknownId);
    }

    [Fact(DisplayName = "TP-720-A05 master enable off (the default): refused before any account read or write")]
    public async Task Disabled_by_default_refuses_without_touching_the_venue()
    {
        GatewayRig rig = new(new TradingExecutionOptions()); // Enabled defaults to false (ADR-0024 §5.1)
        foreach ((string verb, Func<Task<ExecutionWriteResult>> call) in rig.AllVerbs(SyntheticAccounts.PracticeId))
        {
            (await call()).GateRefusal!.Value.Code.Should().Be("trading_disabled", verb);
        }

        rig.Venue.WriteCalls.Should().BeEmpty();
        rig.Accounts.AccountFetches.Should().Be(0);
    }

    [Fact(DisplayName = "TP-720-A06 kill switch: refused even for practice, flips without rebuild")]
    public async Task Kill_switch_refuses_and_is_live()
    {
        GatewayRig rig = new(killSwitch: true);
        (await rig.Gateway.PlaceOrder(GatewayRig.Limit(SyntheticAccounts.PracticeId), default))
            .GateRefusal!.Value.Code.Should().Be("kill_switch");
        rig.Venue.WriteCalls.Should().BeEmpty();

        rig.KillSwitch.IsEngaged = false;
        (await rig.Gateway.PlaceOrder(GatewayRig.Limit(SyntheticAccounts.PracticeId), default))
            .Lifecycle.Should().Be(ExecutionWriteLifecycle.Acknowledged);
    }

    [Fact(DisplayName = "TP-720-A07 HTTP-200 success:false reject is NOT live and carries the venue code")]
    public async Task Venue_reject_is_not_live()
    {
        GatewayRig rig = new();
        rig.Venue.Success = false;
        rig.Venue.ErrorCode = 2;
        rig.Venue.ErrorMessage = "synthetic reject";

        foreach ((string verb, Func<Task<ExecutionWriteResult>> call) in rig.AllVerbs(SyntheticAccounts.PracticeId))
        {
            ExecutionWriteResult result = await call();
            result.Lifecycle.Should().Be(ExecutionWriteLifecycle.PendingOrRejected, verb);
            result.VenueErrorCode.Should().Be(2, verb);
            result.OrderId.Should().BeNull(verb);
        }
    }

    [Fact(DisplayName = "TP-720-A08 place with success:true but no orderId is NOT live (ADR-0024 §5 ack rule)")]
    public async Task Place_without_order_id_is_not_live()
    {
        GatewayRig rig = new();
        rig.Venue.OrderId = null;
        ExecutionWriteResult result = await rig.Gateway.PlaceOrder(GatewayRig.Limit(SyntheticAccounts.PracticeId), default);
        result.Lifecycle.Should().NotBe(ExecutionWriteLifecycle.Acknowledged);
    }

    [Fact(DisplayName = "TP-720-A09 transport fault/timeout never surfaces as an acknowledged write")]
    public async Task Transport_fault_is_not_live()
    {
        GatewayRig rig = new();
        rig.Venue.Throw = new HttpRequestException("synthetic timeout");

        Func<Task<ExecutionWriteResult>> act = () => rig.Gateway.PlaceOrder(GatewayRig.Limit(SyntheticAccounts.PracticeId), default);

        // Either an exception (translated) or a non-ack result is acceptable; an Acknowledged result is not.
        try
        {
            (await act()).Lifecycle.Should().NotBe(ExecutionWriteLifecycle.Acknowledged);
        }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
        {
            ex.Should().NotBeNull();
        }
    }

    [Fact(DisplayName = "TP-720-A10 stage re-derived per call: rename Practice->Funded between calls is honoured")]
    public async Task Stage_is_not_cached_between_calls()
    {
        GatewayRig rig = new();
        (await rig.Gateway.PlaceOrder(GatewayRig.Limit(SyntheticAccounts.PracticeId), default))
            .Lifecycle.Should().Be(ExecutionWriteLifecycle.Acknowledged);

        rig.Accounts.Accounts = [new VenueAccount(SyntheticAccounts.PracticeId,
            ProjectXMapping.ResolveStage("EXPRESS-V2-00000-00000001"), true, true, 50_000m)];

        (await rig.Gateway.PlaceOrder(GatewayRig.Limit(SyntheticAccounts.PracticeId), default))
            .GateRefusal!.Value.Code.Should().Be("non_practice_account");
        rig.Venue.WriteCalls.Should().HaveCount(1);
    }

    [Fact(DisplayName = "TP-720-A11 the venue `simulated` flag is never consulted (§2) — stage alone decides")]
    public void Venue_account_has_no_simulated_member_to_lean_on()
    {
        typeof(VenueAccount).GetProperties().Select(p => p.Name).Should().NotContain(n => n.Contains("Simulated"));
    }
}
