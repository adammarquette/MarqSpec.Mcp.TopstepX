#!/usr/bin/env bash
# check-no-order-path-selftest.sh — require that check-no-order-path.sh and check-gate-tests-ran.sh can still
# go red (gh#718).
#
# Both are text matchers over files they do not own, the shape of every guard here that once could not fire
# (gh#43, gh#98, gh#126). So the REAL scripts run against fixture trees whose faults are known, and each case
# asserts BOTH the exit status AND the words naming its own fault -- a gate that exits 1 for everything (or 0
# for everything) cannot satisfy this file. Passing cases assert the counts on the green line, so a gate that
# read nothing cannot pass them. Failure cases also assert the green line is ABSENT, which is what catches an
# `exit` swallowed inside a command substitution (the gh#718 review's blocker 1).
#
# Hermetic: mktemp -d, no network, no SDK. Tool failures are simulated with PATH shims.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GATE="$SCRIPT_DIR/check-no-order-path.sh"
RAN="$SCRIPT_DIR/check-gate-tests-ran.sh"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

H="MarqSpec.Mcp.TopstepX"
GF="$H/Configuration/TradingRiskGate.cs"
GREEN="Gated order boundary tripwire holds"
pass=0
failed=0

# A minimal tree that satisfies every rule.
base() {
  local t="$1"
  rm -rf "$t"; mkdir -p "$t/$H/Venue" "$t/$H/Configuration" "$t/$H/Tools" "$t/$H.Domain" "$t/$H.Data" "$t/$H.Tests"
  touch "$t/$H/$H.csproj" "$t/$H.Domain/$H.Domain.csproj" "$t/$H.Data/$H.Data.csproj"
  printf '<Project>\n  <ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" /></ItemGroup>\n</Project>\n' \
    >"$t/$H.Tests/$H.Tests.csproj"
  cat >"$t/$H/Venue/IExecutionGateway.cs" <<'CS'
public interface IExecutionGateway { Task PlaceOrder(); Task CancelOrder(); }
CS
  cat >"$t/$H/Venue/ProjectXExecutionGateway.cs" <<'CS'
public sealed class ProjectXExecutionGateway : IExecutionGateway {
  // The venue route is allowed here and only here: /api/Order/place
  public Task PlaceOrder() { PlaceOrderRequest r = new(); return _client.PlaceOrderAsync(r); }
  public Task CancelOrder() => _client.CancelOrderAsync(1, 2);
}
CS
  cat >"$t/$H/Venue/UnconfiguredExecutionGateway.cs" <<'CS'
public sealed class UnconfiguredExecutionGateway : IExecutionGateway { public Task PlaceOrder() => null; public Task CancelOrder() => null; }
CS
  cat >"$t/$H/Program.cs" <<'CS'
if (configured)
{
    services.AddScoped<IExecutionGateway, ProjectXExecutionGateway>();
}
else
{
    services.AddScoped<IExecutionGateway, UnconfiguredExecutionGateway>();
}
CS
  cat >"$t/$H/Configuration/TradingExecutionOptions.cs" <<'CS'
public sealed class TradingExecutionOptions
{
    public bool Enabled { get; init; }
}
CS
  # Prose naming the key is not a key; neither record is an order name (whole-identifier matching).
  cat >"$t/$H/Configuration/TradingExecutionRefusal.cs" <<'CS'
public static readonly Refusal Disabled = new("trading_disabled", "Set Trading__Enabled=true to opt in.");
CS
  cat >"$t/$H/Tools/Summary.cs" <<'CS'
public sealed record PlaceOrderSummary(int ClosePositionCount);
CS
  echo 'public sealed record Bar();' >"$t/$H.Domain/Bar.cs"
  echo 'public sealed class Store {}' >"$t/$H.Data/Store.cs"
  cat >"$t/$GF" <<'CS'
namespace MarqSpec.Mcp.TopstepX.Configuration;

public sealed class TradingRiskGate(TradingExecutionGate gate, IExecutionGateway gateway)
{
    private const string Note = "ResolveStage( in a string is not a call";

    // ResolveStage is called below, after the master switch.
    public async Task<object> Place(Account account)
    {
        if (gate.RefuseIfDisabledOrHalted() is { } halted) return halted;
        AccountStage stage = ProjectXMapping.ResolveStage(account.Name);
        if (stage != AccountStage.Practice) return Refuse(stage);
        if (gate.RefuseIfNotAllowlisted(account.Id) is { } r) return r;
        return await gateway.PlaceOrder();
    }

    public async Task<object> Cancel(Account account)
    {
        if (gate.RefuseIfDisabledOrHalted() is { } halted)
        {
            return halted;
        }
        return await gateway.CancelOrder();
    }
}
CS
  cat >"$t/$H.Tests/TradingRiskGateTests.cs" <<'CS'
[Trait("Gate", "ADR-0024")]
public sealed class TradingRiskGateTests { TradingRiskGate _gate; IExecutionGateway _double; }
CS
  cat >"$t/docker-compose.yml" <<'YML'
services:
  mcp:
    environment:
      Trading__Enabled: "${Trading__Enabled:-false}"
YML
  cat >"$t/docker-compose.dev.yml" <<'YML'
services:
  mcp:
    environment:
      - TRADING__ENABLED=false
YML
  printf 'FROM scratch\nENV DOTNET_X=1\n' >"$t/Dockerfile"
  printf '# Trading__Enabled=true would opt in; do not.\nTrading__Enabled=false\n' >"$t/.env.example"
  echo '{"Trading":{"MaxContractsPerOrder":5,"Enabled":false}}' >"$t/$H/appsettings.json"
}

# run_case <name> <status> [<needle>...] — runs the gate on $T, with extra PATH from $SHIM if set.
run_case() {
  local name="$1" want="$2"; shift 2
  local out status=0
  out="$(PATH="${SHIM:+$SHIM:}$PATH" NO_ORDER_PATH_ROOT="$T" "$GATE" 2>&1)" || status=$?
  judge "$name" "$want" "$status" "$out" "$@"
}

judge() {
  local name="$1" want="$2" status="$3" out="$4"; shift 4
  local bad="" n
  [ "$status" -eq "$want" ] || bad="exit $status, wanted $want"
  for n in "$@"; do
    printf '%s' "$out" | grep -qF -- "$n" || bad="${bad:+$bad; }missing '$n'"
  done
  if [ "$want" -ne 0 ] && printf '%s' "$out" | grep -qF -- "$GREEN"; then
    bad="${bad:+$bad; }printed the green line on a failing case"
  fi
  if [ -n "$bad" ]; then
    printf '\033[31mFAIL\033[0m  %s: %s\n%s\n' "$name" "$bad" "$out" >&2
    failed=$((failed + 1))
  else
    printf '\033[32mok\033[0m    %s\n' "$name"
    pass=$((pass + 1))
  fi
}

# shellcheck disable=SC2016  # shim bodies are literal scripts, expanded when the shim runs
shim() { # shim <tool> <body>: a PATH shim that runs <body> and otherwise defers to the real tool.
  SHIM="$WORK/shim"; rm -rf "$SHIM"; mkdir -p "$SHIM"
  local real; real="$(command -v "$1")"
  printf '#!/usr/bin/env bash\n%s\nexec %s "$@"\n' "$2" "$real" >"$SHIM/$1"
  chmod +x "$SHIM/$1"
}

T="$WORK/tree"
SHIM=""

# --- accepted shapes ---------------------------------------------------------------------------------
base "$T"
run_case "pass: valid tree" 0 "$GREEN: 10 .cs file(s) across 3 discovered product project(s)" \
  "6 order-name reference(s)" "2 Program.cs registration(s)" "1 ADR-0024 gate test file(s)"

base "$T"; mkdir -p "$T/$H.Extra"; touch "$T/$H.Extra/$H.Extra.csproj"; echo 'class Clean {}' >"$T/$H.Extra/Clean.cs"
run_case "pass: a new clean project is discovered and searched" 0 "across 4 discovered product project(s)"

# --- (0) coverage and tool failures ------------------------------------------------------------------
# Every search the gate makes, failed one at a time: a single shim that broke ALL of them would let one
# swallowed status hide behind the next search's honest failure (found by mutating this file).
# grep_fails_on <substring>: grep exits 2 when its arguments contain <substring>.
grep_fails_on() {
  base "$T"
  SHIM="$WORK/shim"; rm -rf "$SHIM"; mkdir -p "$SHIM"
  local real; real="$(command -v grep)"
  {
    printf '#!/usr/bin/env bash
'
    printf 'case "$*" in *%q*) echo "grep: shim: Permission denied" >&2; exit 2 ;; esac
' "$1"
    printf 'exec %q "$@"
' "$real"
  } >"$SHIM/grep"
  chmod +x "$SHIM/grep"
}
for probe in 'PartialClosePosition' '.cs IExecutionGateway' 'uU' 'GetMethod' 'api/(Order' 'trading(:' \
             'trading__enabled docker-compose.yml' 'trading__enabled Dockerfile' 'class[[:space:]]+TradingRiskGate' 'Gate",'; do
  grep_fails_on "$probe"
  run_case "fail: grep exit 2 on the '$probe' search is CANNOT SEARCH" 1 "CANNOT SEARCH"
done
SHIM=""

base "$T"; shim find 'case "$*" in *".cs"*) [[ "$*" == *csproj* ]] || { echo "find: shim: I/O error" >&2; exit 1; } ;; esac'
run_case "fail: find failure while counting is CANNOT COUNT" 1 "CANNOT COUNT"
SHIM=""

base "$T"; shim find 'case "$*" in *csproj*) echo "find: shim" >&2; exit 1 ;; esac'
run_case "fail: find failure while discovering projects is CANNOT LIST" 1 "CANNOT LIST"
SHIM=""

base "$T"; mkdir -p "$T/$H.Trading"; touch "$T/$H.Trading/$H.Trading.csproj"
echo 'class Sneak { Task Go() => client.PlaceOrderAsync(r); }' >"$T/$H.Trading/Sneak.cs"
run_case "fail: order call in an unlisted project" 1 "ORDER PATH  $H.Trading/Sneak.cs:1"

base "$T"; rm "$T/$H/$H.csproj"
run_case "fail: host project missing" 1 "NOT SEARCHED  the host project"

base "$T"; rm "$T/$H.Domain/Bar.cs"
run_case "fail: project with no C#" 1 "NOT SEARCHED  $H.Domain has a *.csproj but no .cs"

base "$T"; mkdir -p "$T/$H.ExecutionTests"; touch "$T/$H.ExecutionTests/$H.ExecutionTests.csproj"
echo 'class Sneak { Task Go() => client.PlaceOrderAsync(r); }' >"$T/$H.ExecutionTests/Sneak.cs"
run_case "fail: product project named *Tests (no test SDK) is searched" 1 "ORDER PATH  $H.ExecutionTests/Sneak.cs:1"

base "$T"; printf '<Project><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>\n' >"$T/$H.Tests/$H.Tests.csproj"
run_case "pass: IsTestProject=true marks a real test project (not searched)" 0 "across 3 discovered product project(s)"

base "$T"; mkdir -p "$T/$H.Stubs"; printf '<Project/>\n' >"$T/$H.Stubs/$H.Stubs.csproj"
echo 'class S(IExecutionGateway g) {}' >"$T/$H.Stubs/S.cs"
run_case "fail: a non-test project is searched even beside the tests" 1 "GATEWAY BYPASS  $H.Stubs/S.cs:1"

# --- (1) order names ---------------------------------------------------------------------------------
base "$T"; echo 'class X { void F() => c.PlaceOrderAsync(r); }' >"$T/$H.Domain/Leak.cs"
run_case "fail: Async name in Domain" 1 "ORDER PATH  $H.Domain/Leak.cs:1"

base "$T"; echo 'class T { Task Go() => gw.ClosePosition(); }' >"$T/$H/Tools/Close.cs"
run_case "fail: suffix-less name in a tool" 1 "ORDER PATH  $H/Tools/Close.cs:1"

base "$T"; echo 'class D { ModifyOrderRequest r; }' >"$T/$H.Data/Req.cs"
run_case "fail: request type in Data" 1 "ORDER PATH  $H.Data/Req.cs:1"

base "$T"; echo 'app.MapPost("/x", (IServiceProvider sp) => gw.PlaceOrder());' >>"$T/$H/Program.cs"
run_case "fail: order call in Program.cs" 1 "ORDER PATH  $H/Program.cs:9"

# --- (2) gateway bypass ------------------------------------------------------------------------------
base "$T"; echo 'class Tool(IExecutionGateway g) {}' >"$T/$H/Tools/Tool.cs"
run_case "fail: tool takes the gateway" 1 "GATEWAY BYPASS  $H/Tools/Tool.cs:1"

base "$T"; echo 'var g = sp.GetRequiredService<IExecutionGateway>();' >>"$T/$H/Program.cs"
run_case "fail: Program.cs resolves the gateway outside a registration" 1 "GATEWAY BYPASS  $H/Program.cs:9"

# --- (3) cheap bypass catches ------------------------------------------------------------------------
base "$T"; printf '%s\n' 'class U { Task Go() => client.Place\u004FrderAsync(r); }' >"$T/$H/Tools/Esc.cs"
run_case "fail: unicode-escaped identifier" 1 "UNICODE ESCAPE  $H/Tools/Esc.cs:1"

base "$T"; echo 'var m = typeof(Client).GetMethod("Place" + "OrderAsync");' >"$T/$H/Tools/Refl.cs"
run_case "fail: reflection" 1 "REFLECTION  $H/Tools/Refl.cs:1"

base "$T"; echo 'await http.PostAsync("/api/Order/place", body);' >"$T/$H/Tools/Raw.cs"
run_case "fail: order route literal outside the adapter" 1 "ORDER ROUTE  $H/Tools/Raw.cs:1"

# --- (4) master switch -------------------------------------------------------------------------------
base "$T"; sed -i 's/{ get; init; }/{ get; init; } = true;/' "$T/$H/Configuration/TradingExecutionOptions.cs"
run_case "fail: Enabled initialised true" 1 "MASTER SWITCH  $H/Configuration/TradingExecutionOptions.cs" "breaks this"

base "$T"; echo 'services.Configure<TradingExecutionOptions>(o => o.Enabled = true);' >>"$T/$H/Program.cs"
run_case "fail: Configure sets Enabled = true" 1 "code-side override at $H/Program.cs:9"

base "$T"; echo 'config.AddInMemoryCollection(new() { ["trading:enabled"] = "false" });' >"$T/$H/Tools/Cfg.cs"
run_case "fail: Trading:Enabled key literal in code" 1 "code-side override at $H/Tools/Cfg.cs:1"

base "$T"; echo 'Environment.SetEnvironmentVariable("TRADING__ENABLED", "true");' >"$T/$H/Tools/Env.cs"
run_case "fail: SetEnvironmentVariable" 1 "code-side override at $H/Tools/Env.cs:1"

base "$T"; echo 'string key = TradingExecutionOptions.SectionName + "__" + nameof(TradingExecutionOptions.Enabled);' >"$T/$H/Tools/Key.cs"
run_case "fail: SectionName + \"__\" + nameof(Enabled)" 1 "key idiom for Enabled at $H/Tools/Key.cs:1"

base "$T"; echo 'config.GetSection(TradingExecutionOptions.SectionName)["Enabled"] = "true";' >"$T/$H/Tools/Idx.cs"
run_case "fail: GetSection(SectionName)[\"Enabled\"]" 1 "key idiom for Enabled at $H/Tools/Idx.cs:1"

base "$T"; echo 'section[ "enabled" ] = "1";' >"$T/$H/Tools/Idx2.cs"
run_case "fail: lowercase [\"enabled\"] indexer" 1 "key idiom for Enabled at $H/Tools/Idx2.cs:1"

base "$T"; echo 'var k = ConfigurationPath.Combine(TradingExecutionOptions.SectionName, "Enabled");' >"$T/$H/Tools/Comb.cs"
run_case "fail: ConfigurationPath.Combine(SectionName, \"Enabled\")" 1 "key idiom for Enabled at $H/Tools/Comb.cs:1"

base "$T"; echo 'var k = $"{TradingExecutionOptions.SectionName}:Enabled";' >"$T/$H/Tools/Interp.cs"
run_case "fail: interpolated \$\"{SectionName}:Enabled\"" 1 "key idiom for Enabled at $H/Tools/Interp.cs:1"

base "$T"; echo 'var k = TradingExecutionOptions.SectionName + ":Enabled";' >"$T/$H/Tools/Concat.cs"
run_case "fail: SectionName + \":Enabled\"" 1 "key idiom for Enabled at $H/Tools/Concat.cs:1"

base "$T"; echo 'pairs.Add(new(TradingExecutionOptions.SectionName + ":" + "Enabled", "true"));' >"$T/$H/Tools/Concat2.cs"
run_case "fail: SectionName + \":\" + \"Enabled\"" 1 "key idiom for Enabled at $H/Tools/Concat2.cs:1"

base "$T"; echo 'if (child.Key.Equals("Enabled", StringComparison.OrdinalIgnoreCase)) { continue; }' >"$T/$H/Configuration/TradingExecutionOptionsConfiguration.cs"
run_case "pass: the CODEOWNERS-owned binder may read the Enabled key" 0 "$GREEN"

base "$T"; printf '    string k = SectionName + "__" + nameof(Enabled);\n' >>"$T/$H/Configuration/TradingExecutionOptions.cs"
run_case "pass: the idiom inside the options file itself is not an override" 0 "$GREEN"

base "$T"; echo '{"trading":{"enabled":true}}' >"$T/$H/appsettings.Development.json"
run_case "fail: appsettings lowercase keys" 1 "appsettings.Development.json sets Trading:Enabled=True"

base "$T"; echo '{"Trading:Enabled": true}' >"$T/$H/appsettings.Local.json"
run_case "fail: appsettings flattened ':' key" 1 "appsettings.Local.json sets Trading:Enabled=True"

base "$T"; echo '{"TRADING__ENABLED": "True"}' >"$T/$H/appsettings.Prod.json"
run_case "fail: appsettings '__' key, string value" 1 "appsettings.Prod.json sets Trading:Enabled='True'"

base "$T"; echo '{"Trading": ' >"$T/$H/appsettings.Broken.json"
run_case "fail: unparseable appsettings" 1 "cannot parse $H/appsettings.Broken.json"

base "$T"; sed -i 's/:-false/:-true/' "$T/docker-compose.yml"
run_case "fail: compose default true" 1 "MASTER SWITCH  docker-compose.yml:4"

base "$T"; sed -i 's/=false/=true/' "$T/docker-compose.dev.yml"
run_case "fail: docker-compose.dev.yml true (upper-case key)" 1 "MASTER SWITCH  docker-compose.dev.yml:4"

base "$T"; mkdir -p "$T/deploy/local"; printf 'services:\n  mcp:\n    environment:\n      Trading__Enabled: "true"\n' >"$T/deploy/local/compose.yaml"
run_case "fail: nested compose.yaml enables trading" 1 "MASTER SWITCH  deploy/local/compose.yaml:4"

base "$T"; printf 'ENV Trading__Enabled=true\n' >>"$T/Dockerfile"
run_case "fail: Dockerfile ENV true" 1 "MASTER SWITCH  Dockerfile:3"

base "$T"; printf 'ARG TRADING__ENABLED=1\n' >>"$T/Dockerfile"
run_case "fail: Dockerfile ARG non-false" 1 "MASTER SWITCH  Dockerfile:3"

base "$T"; echo 'Trading__Enabled=true' >>"$T/.env.example"
run_case "fail: .env.example enables trading" 1 "MASTER SWITCH  .env.example:3"

# --- (5) the gate ------------------------------------------------------------------------------------
base "$T"; rm "$T/$GF"
run_case "fail: gate missing is red, not skipped" 1 "GATE MISSING  $GF"

base "$T"; mkdir -p "$T/$H/Trading"; mv "$T/$GF" "$T/$H/Trading/"
run_case "fail: gate file at another path" 1 "GATE LOCATION" "$H/Trading/TradingRiskGate.cs" "GATE MISSING"

base "$T"; echo 'public sealed class TradingRiskGate {}' >"$T/$H/Tools/RiskGate.cs"
run_case "fail: class TradingRiskGate declared in another file" 1 "GATE LOCATION  TradingRiskGate found at: $H/Tools/RiskGate.cs"

base "$T"; sed -i 's/AccountStage.Practice/AccountStage.Funded/' "$T/$GF"
run_case "fail: gate never names Practice" 1 "never compares against AccountStage.Practice"

base "$T"; sed -i '/RefuseIfDisabledOrHalted/,+3d' "$T/$GF"
run_case "fail: gate never checks disabled/halted" 1 "never calls RefuseIfDisabledOrHalted()"

base "$T"; sed -i 's/^    public async Task<object> Place(Account account)$/&\n    {\n        var s = ProjectXMapping.ResolveStage(account.Name);\n    }\n    public void Unused()/' "$T/$GF"
run_case "fail: stage resolved before disabled/halted" 1 "GATE ORDER  $GF:10 'ResolveStage("

base "$T"; sed -i 's/^}$/    public Task<object> Close() => gateway.ClosePosition();\n}/' "$T/$GF"
run_case "fail: a second, unguarded member" 1 "GATE ORDER  $GF:25" "ClosePosition("

base "$T"; sed -i 's/^        if (gate.RefuseIfDisabledOrHalted() is { } halted)$/        await gateway.CancelOrder();\n&/' "$T/$GF"
run_case "fail: order call before the check in the second member" 1 "GATE ORDER  $GF:19"

base "$T"; sed -i 's/^        if (gate.RefuseIfDisabledOrHalted() is { } halted) return halted;$/        var x = gateway.PlaceOrder(); if (gate.RefuseIfDisabledOrHalted() is { } halted) return halted;/' "$T/$GF"
run_case "fail: order call before the check on the same line" 1 "GATE ORDER  $GF:10"

base "$T"
sed -i 's|^    public async Task<object> Cancel(Account account)$|    private const string Doc = """\n        ResolveStage( and gateway.PlaceOrder( { inside a raw string\n        """;\n\n&|' "$T/$GF"
run_case "pass: raw \"\"\" string contents are not calls or braces" 0 "$GREEN"

base "$T"
sed -i 's|^    public async Task<object> Cancel(Account account)$|    private const string Doc = """ResolveStage( { """;\n    public Task<object> Leak() => gateway.CancelOrder();\n&|' "$T/$GF"
run_case "fail: code after a one-line raw string is still checked" 1 "GATE ORDER  $GF:18"

base "$T"
sed -i 's|^    public async Task<object> Cancel(Account account)$|    public Task<object> PlaceOrderAsync(Account a) =>\n        Place(a);\n\n&|' "$T/$GF"
run_case "pass: a member DECLARED with an order name that delegates to a guarded member" 0 "$GREEN"

base "$T"
sed -i 's|^    public async Task<object> Cancel(Account account)$|    public Task<object> CancelOrderAsync(Account a) =>\n        gateway.CancelOrder();\n\n&|' "$T/$GF"
run_case "fail: declared order-named member whose body calls the gateway unguarded" 1 "GATE ORDER  $GF:18"

base "$T"; sed -i 's/if (stage != AccountStage.Practice) return Refuse(stage);/if (gate.RefuseIfStageBlocksWrite(stage) is { } s) return s;/' "$T/$GF"
printf 'public sealed class TradingExecutionGate {\n  public Refusal? RefuseIfStageBlocksWrite(AccountStage stage) { if (stage == AccountStage.Practice) return null; return Block; }\n}\n' >"$T/$H/Configuration/TradingExecutionGate.cs"
run_case "pass: Practice compared in TradingExecutionGate.RefuseIfStageBlocksWrite" 0 "Practice checked via $H/Configuration/TradingExecutionGate.cs"

base "$T"; sed -i 's/if (stage != AccountStage.Practice) return Refuse(stage);/if (gate.RefuseIfStageBlocksWrite(stage) is { } s) return s;/' "$T/$GF"
printf 'public sealed class TradingExecutionGate {\n  // AccountStage.Practice\n  public Refusal? RefuseIfStageBlocksWrite(AccountStage stage) => null;\n}\n' >"$T/$H/Configuration/TradingExecutionGate.cs"
run_case "fail: delegated stage check that never names Practice in code" 1 "never compares against AccountStage.Practice"

# Declarations vs calls (review 5563414877): only an explicit [attr] [mods] Type Name[<T>]( before "=" is a
# declaration; property and field initialisers and expression bodies are always probed.
gate_insert() { # gate_insert <sed replacement lines, \n-separated> -- inserted before the Cancel member
  sed -i "s|^    public async Task<object> Cancel(Account account)\$|$1\n\n&|" "$T/$GF"
}
base "$T"; gate_insert '    public Task<object> Leak => gateway.PlaceOrder();'
run_case "fail: expression-bodied property calling the gateway" 1 "GATE ORDER  $GF:17" "PlaceOrder("
base "$T"; gate_insert '    private readonly Task _leak = gateway.CancelOrder();'
run_case "fail: field initialiser calling the gateway" 1 "GATE ORDER  $GF:17" "CancelOrder("
base "$T"; gate_insert '    public Task<T> Leak<T>() => (Task<T>)(object)gateway.CancelOrder();'
run_case "fail: generic method calling the gateway" 1 "GATE ORDER  $GF:17" "CancelOrder("
base "$T"; gate_insert '    public Task<object> Leak =>\n        gateway.CancelOrder();'
run_case "fail: property whose expression body is on the next line" 1 "GATE ORDER  $GF:18"
base "$T"; gate_insert '    async Task<object> PlaceOrderAsync(Account a) => await Place(a);'
run_case "pass: declaration with no access modifier" 0 "$GREEN"
base "$T"; gate_insert '    [Obsolete] public Task<object> CancelOrderAsync(Account a) => Cancel(a);'
run_case "pass: declaration with an attribute on the same line" 0 "$GREEN"
base "$T"; gate_insert '    static internal Task<object> ClosePositionAsync() => Task.FromResult<object>(null);'
run_case "pass: static-first modifiers" 0 "$GREEN"
base "$T"; gate_insert '    public Task<T> PlaceOrder<T>(T x) => Wrap(x);'
run_case "pass: generic declaration named like an order method" 0 "$GREEN"

# Delegated Practice check, tightened.
delegate_stage() {
  sed -i 's/if (stage != AccountStage.Practice) return Refuse(stage);/if (gate.RefuseIfStageBlocksWrite(stage) is { } s) return s;/' "$T/$GF"
}
base "$T"; delegate_stage; sed -i 's/gate.RefuseIfStageBlocksWrite(stage)/RefuseIfStageBlocksWrite(stage)/' "$T/$GF"
printf 'public sealed class TradingExecutionGate {\n  public Refusal? RefuseIfStageBlocksWrite(AccountStage stage) { if (stage == AccountStage.Practice) return null; return Block; }\n}\n' >"$T/$H/Configuration/TradingExecutionGate.cs"
run_case "fail: delegated stage check not CALLED through the execution gate" 1 "never compares against AccountStage.Practice"
base "$T"; delegate_stage
printf 'public sealed class TradingExecutionGate {\n  private static readonly AccountStage Allowed = AccountStage.Practice;\n  public Refusal? RefuseIfStageBlocksWrite(AccountStage stage)\n  {\n    return null;\n  }\n}\n' >"$T/$H/Configuration/TradingExecutionGate.cs"
run_case "fail: Practice named in TradingExecutionGate.cs but outside RefuseIfStageBlocksWrite" 1 "never compares against AccountStage.Practice"
base "$T"; delegate_stage
printf 'public sealed class TradingExecutionGate {\n  public Refusal? RefuseIfStageBlocksWrite(\n      AccountStage stage)\n  {\n    if (stage == AccountStage.Practice)\n    {\n      return null;\n    }\n    return Block;\n  }\n}\n' >"$T/$H/Configuration/TradingExecutionGate.cs"
run_case "pass: Practice inside a multi-line RefuseIfStageBlocksWrite body" 0 "Practice checked via $H/Configuration/TradingExecutionGate.cs"

# --- (6) gate tests ----------------------------------------------------------------------------------
base "$T"; sed -i '/Trait/d' "$T/$H.Tests/TradingRiskGateTests.cs"
run_case "fail: no ADR-0024 tagged tests" 1 "GATE TESTS  no test class"

base "$T"; sed -i 's/TradingRiskGate _gate; //; s/TradingRiskGateTests/GatewayTests/' "$T/$H.Tests/TradingRiskGateTests.cs"
run_case "fail: tagged tests never name TradingRiskGate" 1 "names TradingRiskGate"

# --- check-gate-tests-ran.sh --------------------------------------------------------------------------
trx() { # trx <file> <attrs...>
  printf '<?xml version="1.0" encoding="utf-8"?>\n<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><ResultSummary outcome="Completed"><Counters %s /></ResultSummary></TestRun>\n' "$2" >"$1"
}
ran_case() {
  local name="$1" want="$2" file="$3"; shift 3
  local out status=0
  out="$("$RAN" "$file" 2>&1)" || status=$?
  judge "$name" "$want" "$status" "$out" "$@"
}
trx "$WORK/ok.trx" 'passed="15" failed="0" executed="15" total="15" notExecuted="0"'
ran_case "trx pass: 15 executed (attribute order irrelevant)" 0 "$WORK/ok.trx" "Executed 15 ADR-0024 gate test(s), 15 passed"
trx "$WORK/skip.trx" 'total="4" executed="0" passed="0" failed="0" notExecuted="4"'
ran_case "trx fail: skip-only suite" 1 "$WORK/skip.trx" "executed 0 and passed 0 of 4" "skipped tests do not count"
trx "$WORK/red.trx" 'total="5" executed="5" passed="4" failed="1"'
ran_case "trx fail: a gate test failed" 1 "$WORK/red.trx" "1 Gate=ADR-0024 test(s) failed"
printf '<TestRun><ResultSummary><Counters executed="3"\n' >"$WORK/bad.trx"
ran_case "trx fail: malformed XML" 1 "$WORK/bad.trx" "not a readable test-results file"
trx "$WORK/nan.trx" 'executed="x" passed="1" failed="0" total="1"'
ran_case "trx fail: non-numeric counter" 1 "$WORK/nan.trx" "not a readable test-results file"
printf '<TestRun/>\n' >"$WORK/none.trx"
ran_case "trx fail: no Counters" 1 "$WORK/none.trx" "has no Counters element"
ran_case "trx fail: missing file" 1 "$WORK/absent.trx" "No trx at"

echo
if [ "$failed" -gt 0 ]; then
  printf '\033[31m%d of %d self-test case(s) failed: the order-path tripwire no longer catches what it claims to.\033[0m\n' "$failed" "$((pass + failed))" >&2
  exit 1
fi
printf '\033[32mAll %d check-no-order-path self-test cases behaved.\033[0m\n' "$pass"
