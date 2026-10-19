// gh#720 Tier B (DRAFT). End-to-end over the MCP tool surface with the IExecutionGateway double swapped into DI.
// ASSUMED CONTRACT = ADR-0024 §4/§5 (PR #723). Names below (TradingTools, place_order args, status Preview/Live,
// confirmationToken, TradingAuditLog) come from the ADR, not from any implementation. Adjust to the shipped
// signatures without weakening assertions. Requirement IDs: ADR-0024 §2, §4, §5.1-§5.9, §6; gh#706; gh#711-#719.
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Venue;

namespace MarqSpec.Mcp.TopstepX.Tests.Trading.TierBDraft;

[Trait("Issue", "gh#720")]
[Trait("Tier", "B-tools-draft")]
public sealed class TradingToolsIntegrationTests : IAsyncLifetime
{
    // Harness: real Program.ConfigureServices, Mcp transport = in-memory stdio pipe (or HTTP TestServer for OAuth),
    // IExecutionGateway -> CountingExecutionGateway, IMarketDataGateway -> FakeMarketData (SyntheticAccounts),
    // TopstepXDbContext -> EF InMemory (or Testcontainers Postgres when Docker is present; optional per gh#720).
    private McpTestHost _host = null!;
    private CountingExecutionGateway Gateway => _host.Gateway;

    public async Task InitializeAsync() => _host = await McpTestHost.StartAsync(trading: new() { ["Trading:Enabled"] = "true" });
    public async Task DisposeAsync() => await _host.DisposeAsync();

    // ---------- registration & auth ----------
    [Fact(DisplayName = "TP-720-B01 write tools registered with ReadOnly=false, not idempotent (§4)")]
    public async Task Write_tools_registered()
    {
        var tools = await _host.ListToolsAsync();
        foreach (string name in new[] { "place_order", "modify_order", "cancel_order", "close_position", "partial_close_position", "confirm_trading_action" })
        {
            tools.Should().ContainKey(name);
            tools[name].ReadOnlyHint.Should().BeFalse();
            tools[name].IdempotentHint.Should().NotBe(true);
        }
    }

    [Fact(DisplayName = "TP-720-B02 Trading__Enabled=false: tools absent or deterministically refuse; zero writes (§5.1)")]
    public async Task Disabled_refuses()
    {
        await using var off = await McpTestHost.StartAsync(trading: new());
        var r = await off.CallAsync("place_order", Place(SyntheticAccounts.PracticeId));
        (r.IsError || r.ToolMissing).Should().BeTrue();
        off.Gateway.Writes.Should().BeEmpty();
    }

    [Fact(DisplayName = "TP-720-B03 OAuth: read-scope token refused on every write tool; trade scope accepted (§5.9, gh#719)")]
    public async Task Oauth_trade_scope_required()
    {
        await using var http = await McpTestHost.StartHttpOAuthAsync(scopes: "topstepx-mcp/read");
        (await http.CallAsync("place_order", Place(SyntheticAccounts.PracticeId))).IsAuthError.Should().BeTrue();
        http.Gateway.Writes.Should().BeEmpty();
        await using var trade = await McpTestHost.StartHttpOAuthAsync(scopes: "topstepx-mcp/read topstepx-mcp/trade");
        (await trade.CallAsync("place_order", Place(SyntheticAccounts.PracticeId, dryRun: true))).IsError.Should().BeFalse();
    }

    // ---------- practice-only, every tool, dryRun included ----------
    public static TheoryData<string, bool> RefusedMatrix()
    {
        var d = new TheoryData<string, bool>();
        foreach (string acct in new[] { "Evaluation", "Funded", "Unknown", "EvaluationAllowlisted", "FundedAllowlisted", "UnknownAllowlisted" })
        foreach (bool dry in new[] { false, true }) d.Add(acct, dry);
        return d;
    }

    [Theory(DisplayName = "TP-720-B04 non-Practice fixtures refused on every tool, dryRun or not, ZERO gateway writes (gh#720)")]
    [MemberData(nameof(RefusedMatrix))]
    public async Task Non_practice_refused_everywhere(string fixture, bool dryRun)
    {
        int id = _host.Fixture(fixture); // allowlisted variants set Trading:AllowedAccountIds to that id
        foreach ((string tool, Dictionary<string, object?> args) in AllWriteTools(id, dryRun))
        {
            var r = await _host.CallAsync(tool, args);
            r.IsError.Should().BeTrue(tool);
            r.Text.Should().Contain(id.ToString()).And.MatchRegex("Evaluation|Funded|Unknown", "names accountId and parsed stage (§2.4)");
        }
        Gateway.Writes.Should().BeEmpty();
        (await _host.AuditRowsAsync()).Should().OnlyContain(a => a.Outcome != "Live");
    }

    [Fact(DisplayName = "TP-720-B05 Practice place_order: Live only with broker ack; orderId numeric (§5 ack, ADR-0008)")]
    public async Task Practice_place_live()
    {
        var r = await _host.CallAsync("place_order", Place(SyntheticAccounts.PracticeId), confirm: true);
        r.Json["status"]!.GetValue<string>().Should().Be("Live");
        r.Json["orderId"]!.GetValue<long>().Should().Be(7_000_001);
        Gateway.Writes.Should().ContainSingle().Which.Should().BeOfType<ExecutionPlaceOrderCommand>()
            .Which.Size.Should().Be(1);
    }

    [Theory(DisplayName = "TP-720-B06 reject / unacked / timeout: never Live, never cached under the idempotency key (§5)")]
    [InlineData("reject")] [InlineData("noack")] [InlineData("timeout")]
    public async Task Reject_paths_not_live(string mode)
    {
        Gateway.Respond = mode switch
        {
            "reject" => _ => CountingExecutionGateway.Reject(2),
            "noack" => _ => new(ExecutionWriteLifecycle.PendingOrRejected, null, 0, null, null),
            _ => _ => throw new TimeoutException("synthetic"),
        };
        var args = Place(SyntheticAccounts.PracticeId, key: "k-" + mode);
        var first = await _host.CallAsync("place_order", args, confirm: true);
        (first.IsError || first.Json["status"]!.GetValue<string>() != "Live").Should().BeTrue();

        Gateway.Respond = _ => new(ExecutionWriteLifecycle.Acknowledged, null, 0, null, 7_000_002);
        var retry = await _host.CallAsync("place_order", args, confirm: true);
        retry.Json["status"]!.GetValue<string>().Should().Be("Live", "a failed attempt is not a cached outcome");
        Gateway.Writes.Should().HaveCount(2);
    }

    // ---------- risk layers (gh#711) ----------
    [Theory(DisplayName = "TP-720-B07 caps / denylist / allowlist-miss refuse before transmit (§5.4-§5.5)")]
    [InlineData("Trading:MaxContractsPerOrder", "1", 2, "MES", "order_size_exceeds_limit")]
    [InlineData("Trading:DeniedSymbols", "MES", 1, "MES", "symbol_denied")]
    [InlineData("Trading:AllowedAccountIds", "123", 1, "MES", "account_not_allowlisted")]
    public async Task Risk_layers(string key, string value, int size, string symbol, string code)
    {
        await using var h = await McpTestHost.StartAsync(trading: new() { ["Trading:Enabled"] = "true", [key] = value });
        var r = await h.CallAsync("place_order", Place(SyntheticAccounts.PracticeId, size: size, symbol: symbol));
        r.IsError.Should().BeTrue(); r.Text.Should().Contain(code);
        h.Gateway.Writes.Should().BeEmpty();
    }

    [Fact(DisplayName = "TP-720-B08 daily cap counts across calls per account (§5.5)")]
    public async Task Daily_cap()
    {
        await using var h = await McpTestHost.StartAsync(trading: new() { ["Trading:Enabled"] = "true", ["Trading:MaxContractsPerDay"] = "3", ["Trading:MaxContractsPerOrder"] = "2" });
        (await h.CallAsync("place_order", Place(SyntheticAccounts.PracticeId, size: 2, key: "d1"), confirm: true)).IsError.Should().BeFalse();
        var r = await h.CallAsync("place_order", Place(SyntheticAccounts.PracticeId, size: 2, key: "d2"), confirm: true);
        r.Text.Should().Contain("daily_contract_limit_exceeded");
        h.Gateway.Writes.Should().HaveCount(1);
    }

    // ---------- human in the loop (gh#717) ----------
    [Fact(DisplayName = "TP-720-B09 preview returns Preview + confirmationToken and makes ZERO writes (§5.6)")]
    public async Task Preview_no_write()
    {
        var r = await _host.CallAsync("place_order", Place(SyntheticAccounts.PracticeId, dryRun: true));
        r.Json["status"]!.GetValue<string>().Should().Be("Preview");
        r.Json["confirmationToken"].Should().NotBeNull();
        Gateway.Writes.Should().BeEmpty();
    }

    [Fact(DisplayName = "TP-720-B10 confirm transmits exactly once; replayed token does not transmit again")]
    public async Task Confirm_once()
    {
        string token = (await _host.CallAsync("place_order", Place(SyntheticAccounts.PracticeId, dryRun: true))).Json["confirmationToken"]!.GetValue<string>();
        (await _host.CallAsync("confirm_trading_action", new() { ["confirmationToken"] = token })).Json["status"]!.GetValue<string>().Should().Be("Live");
        var replay = await _host.CallAsync("confirm_trading_action", new() { ["confirmationToken"] = token });
        (replay.IsError || replay.Json["status"]!.GetValue<string>() == "Live").Should().BeTrue();
        Gateway.Writes.Should().HaveCount(1);
    }

    [Fact(DisplayName = "TP-720-B11 confirm re-runs gates 1-5: kill switch engaged after preview blocks transmit")]
    public async Task Confirm_reruns_gates()
    {
        string token = (await _host.CallAsync("place_order", Place(SyntheticAccounts.PracticeId, dryRun: true))).Json["confirmationToken"]!.GetValue<string>();
        _host.EngageKillSwitch();
        (await _host.CallAsync("confirm_trading_action", new() { ["confirmationToken"] = token })).Text.Should().Contain("kill_switch");
        Gateway.Writes.Should().BeEmpty();
    }

    [Fact(DisplayName = "TP-720-B12 confirm re-fetches stage: account renamed to Funded after preview is refused")]
    public async Task Confirm_refetches_stage()
    {
        string token = (await _host.CallAsync("place_order", Place(SyntheticAccounts.PracticeId, dryRun: true))).Json["confirmationToken"]!.GetValue<string>();
        _host.RenameAccount(SyntheticAccounts.PracticeId, "EXPRESS-V2-00000-00000001");
        (await _host.CallAsync("confirm_trading_action", new() { ["confirmationToken"] = token })).IsError.Should().BeTrue();
        Gateway.Writes.Should().BeEmpty();
    }

    [Fact(DisplayName = "TP-720-B13 expired / forged / other-account token refused, zero writes")]
    public async Task Bad_tokens()
    {
        string token = (await _host.CallAsync("place_order", Place(SyntheticAccounts.PracticeId, dryRun: true))).Json["confirmationToken"]!.GetValue<string>();
        (await _host.CallAsync("confirm_trading_action", new() { ["confirmationToken"] = "forged" })).IsError.Should().BeTrue();
        _host.AdvanceClock(TimeSpan.FromHours(1));
        (await _host.CallAsync("confirm_trading_action", new() { ["confirmationToken"] = token })).IsError.Should().BeTrue();
        Gateway.Writes.Should().BeEmpty();
    }

    // ---------- idempotency (gh#712) ----------
    [Fact(DisplayName = "TP-720-B14 duplicate idempotencyKey returns prior outcome; gateway called once")]
    public async Task Idempotent_duplicate()
    {
        var args = Place(SyntheticAccounts.PracticeId, key: "same");
        var a = await _host.CallAsync("place_order", args, confirm: true);
        var b = await _host.CallAsync("place_order", args, confirm: true);
        b.Json.ToJsonString().Should().Be(a.Json.ToJsonString());
        Gateway.Writes.Should().HaveCount(1);
    }

    [Fact(DisplayName = "TP-720-B15 same key, different request body is refused (request hash), zero extra writes")]
    public async Task Idempotent_conflict()
    {
        await _host.CallAsync("place_order", Place(SyntheticAccounts.PracticeId, key: "k"), confirm: true);
        (await _host.CallAsync("place_order", Place(SyntheticAccounts.PracticeId, size: 2, key: "k"), confirm: true)).IsError.Should().BeTrue();
        Gateway.Writes.Should().HaveCount(1);
    }

    // ---------- per-verb behaviour ----------
    [Fact(DisplayName = "TP-720-B16 modify/cancel/close/partial map 1:1 to gateway commands with the args given")]
    public async Task Verb_mapping()
    {
        int id = SyntheticAccounts.PracticeId;
        foreach ((string tool, Dictionary<string, object?> args) in AllWriteTools(id, dryRun: false).Skip(1))
            (await _host.CallAsync(tool, args, confirm: true)).IsError.Should().BeFalse(tool);
        Gateway.Writes.Select(w => w.GetType()).Should().Equal(typeof(ExecutionModifyOrderCommand), typeof(ExecutionCancelOrderCommand),
            typeof(ExecutionClosePositionCommand), typeof(ExecutionPartialClosePositionCommand));
        Gateway.Writes.OfType<ExecutionPartialClosePositionCommand>().Single().Size.Should().Be(1);
    }

    [Theory(DisplayName = "TP-720-B17 invalid input refused before gateway: size<=0, limit without price, unknown enum, customTag")]
    [InlineData("size", 0)] [InlineData("limitPrice", null)] [InlineData("orderType", "Iceberg")] [InlineData("customTag", "free text")]
    public async Task Input_validation(string field, object? value)
    {
        var args = Place(SyntheticAccounts.PracticeId); args[field] = value;
        (await _host.CallAsync("place_order", args)).IsError.Should().BeTrue();
        Gateway.Writes.Should().BeEmpty();
    }

    // ---------- audit (gh#709) ----------
    [Fact(DisplayName = "TP-720-B18 one append-only audit row per attempt (preview, confirm, refused, live); no secrets")]
    public async Task Audit_rows()
    {
        await _host.CallAsync("place_order", Place(SyntheticAccounts.FundedId, key: "a1"));
        string token = (await _host.CallAsync("place_order", Place(SyntheticAccounts.PracticeId, dryRun: true, key: "a2"))).Json["confirmationToken"]!.GetValue<string>();
        await _host.CallAsync("confirm_trading_action", new() { ["confirmationToken"] = token });
        var rows = await _host.AuditRowsAsync();
        rows.Should().HaveCountGreaterThanOrEqualTo(3);
        rows.Should().Contain(r => r.AccountId == SyntheticAccounts.FundedId && r.ParsedStage == "Funded" && r.Outcome != "Live");
        rows.Should().Contain(r => r.DryRun && r.IdempotencyKey == "a2");
        rows.Should().OnlyContain(r => !r.ToString()!.Contains("ApiSecret") && !r.ToString()!.Contains(token));
    }

    // ---------- helpers ----------
    private static Dictionary<string, object?> Place(int account, int size = 1, string symbol = "MES", bool dryRun = false, string? key = null) => new()
    {
        ["accountId"] = account, ["idempotencyKey"] = key ?? Guid.NewGuid().ToString("N"), ["dryRun"] = dryRun,
        ["contractId"] = $"CON.F.US.{symbol}.Z26", ["orderType"] = "Limit", ["side"] = "Buy", ["size"] = size, ["limitPrice"] = 6800.25m,
    };

    private static IEnumerable<(string, Dictionary<string, object?>)> AllWriteTools(int id, bool dryRun)
    {
        Dictionary<string, object?> Common() => new() { ["accountId"] = id, ["idempotencyKey"] = Guid.NewGuid().ToString("N"), ["dryRun"] = dryRun };
        yield return ("place_order", Place(id, dryRun: dryRun));
        var m = Common(); m["orderId"] = 42L; m["limitPrice"] = 6801m; yield return ("modify_order", m);
        var c = Common(); c["orderId"] = 42L; yield return ("cancel_order", c);
        var x = Common(); x["contractId"] = "CON.F.US.MES.Z26"; yield return ("close_position", x);
        var p = Common(); p["contractId"] = "CON.F.US.MES.Z26"; p["size"] = 1; yield return ("partial_close_position", p);
    }
}
