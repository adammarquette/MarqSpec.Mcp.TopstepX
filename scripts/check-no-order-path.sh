#!/usr/bin/env bash
# check-no-order-path.sh — a TRIPWIRE for the ADR-0024 gated order boundary (gh#718; was ADR-0002, gh#11).
#
# WHAT IT IS, AND WHAT IT IS NOT
#
# This server is driven by a language model, over MCP, and the venue client it depends on has a working order
# surface reaching a real brokerage account. ADR-0002 made the boundary the ABSENCE of the call; ADR-0024
# replaces that with "orders only through TradingRiskGate -> IExecutionGateway, behind Trading__Enabled
# (default false), Practice accounts only". This script is a cheap, loud TRIPWIRE for that boundary. It is
# NOT a proof. A text gate cannot see: a reflection call on a BUILT string, an innocuously named wrapper added
# inside an allowlisted file and called from a tool, a venue order method missing from ORDER_RE, raw HTTP built
# without a route literal, or Railway's service variables. What holds the boundary is this tripwire PLUS code
# review PLUS CODEOWNERS on every allowlisted file (.github/CODEOWNERS -- NOT an independent reviewer for PRs
# authored from the owning account itself, since GitHub ignores an author's own code-owner approval) PLUS the [Trait("Gate","ADR-0024")]
# tests. Widening an allowlist here to get green is the failure this file exists to make visible.
#
# THE CHECKS
#
#   (0) COVERAGE. Product projects are DISCOVERED: every *.csproj that is not a real test project (one carrying
#       <IsTestProject>true</IsTestProject> or a Microsoft.NET.Test.Sdk / xunit reference) is searched, whatever
#       its directory is called -- a product project named *Tests is searched too. Every search's exit status is read in the main shell: a
#       grep or find that could not look FAILS the run (gh#126) -- it is never a quiet pass.
#   (1) ORDER NAMES only in the seam: five venue order methods (PlaceOrder, ModifyOrder, CancelOrder,
#       ClosePosition, PartialClosePosition), with and without Async, and three request types, as whole
#       identifiers, only in ORDER_ALLOWED. Program.cs is NOT on it: it registers the gateway, it never calls it.
#   (2) THE GATEWAY only in GATEWAY_ALLOWED files, and in Program.cs only on the exact AddScoped registration
#       lines -- an endpoint or hosted service there cannot resolve the gateway and stay green.
#   (3) CHEAP BYPASS CATCHES anywhere in product code: \uXXXX escapes (a legal way to spell PlaceOrderAsync
#       that -w cannot see), GetMethod(/GetMethods(/InvokeMember( reflection, and /api/Order or /api/Position
#       route literals outside the adapter.
#   (4) MASTER SWITCH DEFAULTS OFF, statically, on every surface CI can read:
#         - TradingExecutionOptions.Enabled is exactly `public bool Enabled { get; init; }` (no initialiser; a
#           reformat breaks the match, failing closed);
#         - no product code sets it: no `Enabled = true`, no "Trading:Enabled"/"Trading__Enabled" key literal
#           (Configure/PostConfigure/AddInMemoryCollection/SetEnvironmentVariable), any casing, and -- outside
#           the options file -- neither of this repo's own key idioms for it: nameof(Enabled) (as in
#           SectionName + "__" + nameof(Enabled)) or any literal that is "Enabled" or ends ":Enabled"/"__Enabled"
#           (["Enabled"], ConfigurationPath.Combine(SectionName, "Enabled"), $"{SectionName}:Enabled");
#         - every appsettings*.json, keys flattened (":" and "__") and lower-cased as .NET binds them;
#         - every docker-compose*.y(a)ml / compose*.y(a)ml at any depth passes ${Trading__Enabled:-false} (or false), any key casing;
#         - Dockerfile ENV/ARG and .env.example never set it to anything but false.
#         - every file under .railway/ (the Railway IaC template, gh#758) and any railway.json/railway.toml
#           sets it only as the literal "false" (code comments skipped), and .railway/railway.ts sets it at all.
#       RAILWAY'S DASHBOARD VARIABLES ARE STILL NOT CI-VERIFIABLE: the template is what a reviewed deploy
#       applies, but an edit made in Railway afterwards is invisible here and rests on the code default and on
#       whoever made it.
#   (5) THE GATE exists at exactly GATE_FILE -- absent, or present anywhere else, FAILS (no skip). In EVERY
#       member of TradingRiskGate, RefuseIfDisabledOrHalted( comes before any ResolveStage(, allowlist,
#       denylist or order call in that member; the file calls ResolveStage( and names AccountStage.Practice.
#       STAGE: the account stage comes from an IAccountStageResolver the gate declares (or ResolveStage(,
#       internal from #734), and in every member a resolver call -- direct, or via a gate helper that makes one
#       -- precedes any gateway call (".PlaceOrder(" etc.). PRACTICE: an explicit `stage != AccountStage.Practice`
#       / `is not AccountStage.Practice` whose line (or, when the condition ends the line, the next code line)
#       returns or throws.
#       Entry points named Submit* (SubmitPlace, SubmitModify, ...) are NOT order names and are not allowlisted;
#       they are gate members like any other. A private continuation that calls the gateway must itself be
#       guarded and staged -- the check does not follow calls INTO helpers, only to stage-resolving ones.
#       The Practice comparison may live in TradingExecutionGate.RefuseIfStageBlocksWrite( when the gate calls it.
#       Member declarations named like order methods are not calls (matched explicitly: [attr] [modifiers]
#       Type Name[<T>]( before any "="); initialisers and expression bodies are always probed. The delegated
#       Practice check needs a .RefuseIfStageBlocksWrite( CALL and AccountStage.Practice inside that method's
#       body in TradingExecutionGate.cs. FILES THE BOUNDARY RESTS ON (all on CODEOWNERS): the seam files, the
#       gate, TradingExecutionGate.cs, TradingExecutionOptions.cs, TradingExecutionOptionsConfiguration.cs
#       (exempt from the key-idiom check because it binds the section by hand), Program.cs.
#       A product csproj that adds an xunit/test-SDK reference is treated as a test project and skipped --
#       deliberate evasion, left to review.
#       Limits (documented, not fixed): it proves Practice is NAMED, not that the comparison is the right way
#       round; a guard inside an unrelated nested block (`if (x) { RefuseIfDisabledOrHalted(); }`) marks the
#       member guarded; comments, "..." strings and raw """...""" strings are stripped first. A private helper
#       that calls the gateway must call RefuseIfDisabledOrHalted( itself, or it is flagged.
#   (6) GATE TESTS: [Trait("Gate","ADR-0024")] test classes exist and one names TradingRiskGate. CI then runs
#       that filter; scripts/check-gate-tests-ran.sh fails unless tests EXECUTED and passed (skips don't count).
#
# scripts/check-no-order-path-selftest.sh proves each check can still go red. Test projects are not searched
# for (1)-(4): a test double legitimately implements the seam.

set -euo pipefail

die()    { printf '\033[31m%s\033[0m\n' "$*" >&2; }
ok()     { printf '\033[32m%s\033[0m\n' "$*"; }

# NO_ORDER_PATH_ROOT exists for the self-test's fixtures only.
REPO_ROOT="${NO_ORDER_PATH_ROOT:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"
cd "$REPO_ROOT"

HOST="MarqSpec.Mcp.TopstepX"
TEST_DIR="MarqSpec.Mcp.TopstepX.Tests"
GATEWAY_INTERFACE="$HOST/Venue/IExecutionGateway.cs"
GATEWAY_ADAPTER="$HOST/Venue/ProjectXExecutionGateway.cs"
GATEWAY_STUB="$HOST/Venue/UnconfiguredExecutionGateway.cs"
GATE_FILE="$HOST/Configuration/TradingRiskGate.cs"   # beside TradingExecutionGate (gh#708)
PROGRAM="$HOST/Program.cs"
OPTIONS_FILE="$HOST/Configuration/TradingExecutionOptions.cs"
CONFIG_FILE="$HOST/Configuration/TradingExecutionOptionsConfiguration.cs"   # rebuilds the Trading section (gh#734)
EXEC_GATE="$HOST/Configuration/TradingExecutionGate.cs"                     # disabled/halted + Practice (gh#708)

# (1) Who may NAME an order method, each for its own reason:
#   the interface declares them; the adapter implements them against the venue client (the ONLY file that
#   may name the *Async/*Request client surface); the stub implements them as refusals; the gate calls them.
ORDER_ALLOWED=("$GATEWAY_INTERFACE" "$GATEWAY_ADAPTER" "$GATEWAY_STUB" "$GATE_FILE")
# (2) Who may name IExecutionGateway: its own file, its two implementations, and the gate that holds it.
#   Program.cs is handled separately, line by line (PROGRAM_REGISTRATION_RE).
GATEWAY_ALLOWED=("$GATEWAY_INTERFACE" "$GATEWAY_ADAPTER" "$GATEWAY_STUB" "$GATE_FILE")
PROGRAM_REGISTRATION_RE='^[[:space:]]*services\.AddScoped<IExecutionGateway,[[:space:]]*(ProjectXExecutionGateway|UnconfiguredExecutionGateway)>\(\);[[:space:]]*$'

ORDER_RE='(PlaceOrder|ModifyOrder|CancelOrder|ClosePosition|PartialClosePosition)(Async)?|(PlaceOrder|ModifyOrder|CancelOrder)Request'
TRAIT_RE='\[Trait\("Gate",[[:space:]]*"ADR-0024"\)\]'

failures=0
practice_via=""
fail() { die "  $1"; shift; local l; for l in "$@"; do die "              $l"; done; failures=$((failures + 1)); }
env_fail() { die "  $1"; die "That is an environment failure rather than a verdict on the code, and it is NOT a pass."; exit 1; }

in_list() {
  local needle="$1"; shift
  local e
  for e in "$@"; do [ "$e" = "$needle" ] && return 0; done
  return 1
}

# grep_r <flags> <regex> <dirs...> — prints hits (minus obj/bin). Returns 2 when grep could not look. Callers
# capture in the MAIN shell (`x="$(grep_r ...)" || env_fail ...`); an exit inside $(...) ends only the subshell.
grep_r() {
  local flags="$1" re="$2"; shift 2
  local out status=0
  out="$(grep -r "$flags" --include='*.cs' "$re" "$@")" || status=$?
  if [ "$status" -gt 1 ]; then return 2; fi
  if [ -n "$out" ]; then
    printf '%s\n' "$out" | grep -v -e '/obj/' -e '/bin/' || true
  fi
  return 0
}

# --- (0) coverage ---------------------------------------------------------------------------------------
csprojs="$(find . -name '*.csproj' -not -path '*/obj/*' -not -path '*/bin/*' -not -path './node_modules/*' -print)" \
  || env_fail "CANNOT LIST  find exited $? looking for *.csproj"
PRODUCT_DIRS=()
while IFS= read -r p; do
  [ -n "$p" ] || continue
  d="$(dirname "${p#./}")"
  status=0
  grep -qiE '<IsTestProject>[[:space:]]*true[[:space:]]*</IsTestProject>|Include="(Microsoft\.NET\.Test\.Sdk|xunit(\.v3)?)"' "$p" || status=$?
  [ "$status" -gt 1 ] && env_fail "CANNOT READ  grep exited $status over $p"
  if [ "$status" -eq 0 ]; then continue; fi
  in_list "$d" "${PRODUCT_DIRS[@]+"${PRODUCT_DIRS[@]}"}" || PRODUCT_DIRS+=("$d")
done <<<"$csprojs"
if [ "${#PRODUCT_DIRS[@]}" -eq 0 ]; then
  die "  NOT SEARCHED  no non-test *.csproj found under $REPO_ROOT"; exit 1
fi
if ! in_list "$HOST" "${PRODUCT_DIRS[@]}"; then
  die "  NOT SEARCHED  the host project $HOST has no *.csproj in this checkout"; exit 1
fi

searched=0
for dir in "${PRODUCT_DIRS[@]}"; do
  listing="$(find "$dir" -type f -name '*.cs' -not -path '*/obj/*' -not -path '*/bin/*' -print)" \
    || env_fail "CANNOT COUNT  find exited $? over $dir, so this run cannot say what it read"
  n="$(printf '%s' "$listing" | grep -c . || true)"
  if [ "$n" -eq 0 ]; then
    die "  NOT SEARCHED  $dir has a *.csproj but no .cs files outside obj/ and bin/"; exit 1
  fi
  searched=$((searched + n))
done

# --- (1) order names only in the seam --------------------------------------------------------------------
hits="$(grep_r -nwE "$ORDER_RE" "${PRODUCT_DIRS[@]}")" \
  || env_fail "CANNOT SEARCH  grep could not search ${PRODUCT_DIRS[*]} for order names (its own message is above)"
order_hits=0
while IFS= read -r hit; do
  [ -n "$hit" ] || continue
  file="${hit%%:*}"; rest="${hit#*:}"; line="${rest%%:*}"; text="${rest#*:}"
  if in_list "$file" "${ORDER_ALLOWED[@]}"; then order_hits=$((order_hits + 1)); continue; fi
  fail "ORDER PATH  $file:$line" "${text#"${text%%[![:space:]]*}"}" \
    "Order names may appear only in: ${ORDER_ALLOWED[*]}"
done <<<"$hits"

# --- (2) only the gate holds the gateway -----------------------------------------------------------------
hits="$(grep_r -nw 'IExecutionGateway' "${PRODUCT_DIRS[@]}")" \
  || env_fail "CANNOT SEARCH  grep could not search ${PRODUCT_DIRS[*]} for IExecutionGateway"
registrations=0
while IFS= read -r hit; do
  [ -n "$hit" ] || continue
  file="${hit%%:*}"; rest="${hit#*:}"; line="${rest%%:*}"; text="${rest#*:}"
  in_list "$file" "${GATEWAY_ALLOWED[@]}" && continue
  if [ "$file" = "$PROGRAM" ] && grep -qE "$PROGRAM_REGISTRATION_RE" <<<"$text"; then
    registrations=$((registrations + 1)); continue
  fi
  fail "GATEWAY BYPASS  $file:$line" "${text#"${text%%[![:space:]]*}"}" \
    "IExecutionGateway may be named only by: ${GATEWAY_ALLOWED[*]}," \
    "and in $PROGRAM only as services.AddScoped<IExecutionGateway, ProjectX|UnconfiguredExecutionGateway>();"
done <<<"$hits"

# --- (3) cheap bypass catches ------------------------------------------------------------------------------
hits="$(grep_r -nE '\\[uU][0-9A-Fa-f]{4}' "${PRODUCT_DIRS[@]}")" || env_fail "CANNOT SEARCH  grep failed looking for \\u escapes"
while IFS= read -r hit; do
  [ -n "$hit" ] || continue
  fail "UNICODE ESCAPE  ${hit%%:*}:$(cut -d: -f2 <<<"$hit")" \
    "A \\uXXXX escape can spell an order identifier the name check cannot see. Write the character itself."
done <<<"$hits"
hits="$(grep_r -nE '\b(GetMethod|GetMethods|InvokeMember)[[:space:]]*\(' "${PRODUCT_DIRS[@]}")" || env_fail "CANNOT SEARCH  grep failed looking for reflection"
while IFS= read -r hit; do
  [ -n "$hit" ] || continue
  fail "REFLECTION  ${hit%%:*}:$(cut -d: -f2 <<<"$hit")" "Reflection can reach the venue's order surface by a built name."
done <<<"$hits"
hits="$(grep_r -niE '/api/(Order|Position)\b' "${PRODUCT_DIRS[@]}")" || env_fail "CANNOT SEARCH  grep failed looking for order routes"
while IFS= read -r hit; do
  [ -n "$hit" ] || continue
  [ "${hit%%:*}" = "$GATEWAY_ADAPTER" ] && continue
  fail "ORDER ROUTE  ${hit%%:*}:$(cut -d: -f2 <<<"$hit")" "A venue order/position route literal outside $GATEWAY_ADAPTER."
done <<<"$hits"

# --- (4) master switch defaults off -----------------------------------------------------------------------
if [ ! -f "$OPTIONS_FILE" ]; then
  fail "MASTER SWITCH  $OPTIONS_FILE is missing" "Cannot prove Trading__Enabled defaults to false."
elif ! grep -qE '^[[:space:]]*public[[:space:]]+bool[[:space:]]+Enabled[[:space:]]*\{[[:space:]]*get;[[:space:]]*init;[[:space:]]*\}[[:space:]]*$' "$OPTIONS_FILE"; then
  fail "MASTER SWITCH  $OPTIONS_FILE: Enabled must be exactly 'public bool Enabled { get; init; }'" \
    "No initialiser, so false is the default. An attribute or comment on that line, or any reformat, breaks this" \
    "match on purpose (it fails closed); keep the declaration on its own line."
fi

# Code-side overrides. The key as a WHOLE string literal, any casing ("Trading:Enabled", "TRADING__ENABLED")
# -- prose such as "Set Trading__Enabled=true to opt in" is not a key -- and any `Enabled = true` assignment.
hits="$(grep_r -niE '"trading(:|__)enabled"|\bEnabled[[:space:]]*=[[:space:]]*true\b' "${PRODUCT_DIRS[@]}")" \
  || env_fail "CANNOT SEARCH  grep failed looking for code-side Trading:Enabled overrides"
while IFS= read -r hit; do
  [ -n "$hit" ] || continue
  text="${hit#*:}"; text="${text#*:}"
  fail "MASTER SWITCH  code-side override at ${hit%%:*}:$(cut -d: -f2 <<<"$hit")" "${text#"${text%%[![:space:]]*}"}" \
    "Only configuration may turn trading on; product code must not set Trading:Enabled or Enabled = true."
done <<<"$hits"
# Any literal that IS "Enabled" or ENDS in ":Enabled"/"__Enabled" (covers ["Enabled"], Combine(Section, "Enabled"),
# SectionName + ":Enabled", $"{SectionName}:Enabled", ":" + "Enabled"), and nameof(Enabled), any casing.
hits="$(grep_r -niE 'nameof[[:space:]]*\([[:space:]]*(TradingExecutionOptions[[:space:]]*\.[[:space:]]*)?Enabled[[:space:]]*\)|"([^"]*(:|__))?enabled"' "${PRODUCT_DIRS[@]}")" \
  || env_fail "CANNOT SEARCH  grep failed looking for nameof(Enabled) / [\"Enabled\"] key idioms"
while IFS= read -r hit; do
  [ -n "$hit" ] || continue
  [ "${hit%%:*}" = "$OPTIONS_FILE" ] && continue
  [ "${hit%%:*}" = "$CONFIG_FILE" ] && continue   # the binder reads the key by design; CODEOWNERS-owned
  text="${hit#*:}"; text="${text#*:}"
  fail "MASTER SWITCH  key idiom for Enabled at ${hit%%:*}:$(cut -d: -f2 <<<"$hit")" "${text#"${text%%[![:space:]]*}"}" \
    "SectionName + \"__\" + nameof(Enabled) or GetSection(SectionName)[\"Enabled\"] builds the master-switch key in code."
done <<<"$hits"

# appsettings*.json, flattened and lower-cased the way .NET binds configuration.
jsons="$(find "${PRODUCT_DIRS[@]}" -name 'appsettings*.json' -not -path '*/obj/*' -not -path '*/bin/*' -print)" \
  || env_fail "CANNOT LIST  find exited $? looking for appsettings*.json"
while IFS= read -r json; do
  [ -n "$json" ] || continue
  if ! verdict="$(python3 - "$json" <<'PY' 2>&1
import json, sys
def walk(node, path):
    if isinstance(node, dict):
        for k, v in node.items():
            yield from walk(v, path + str(k).replace("__", ":").split(":"))
    elif isinstance(node, list):
        for i, v in enumerate(node):
            yield from walk(v, path + [str(i)])
    else:
        yield ":".join(p.lower() for p in path), node
with open(sys.argv[1], encoding="utf-8-sig") as f:
    data = json.load(f)
bad = [repr(v) for k, v in walk(data, []) if k == "trading:enabled" and str(v).strip().lower() != "false"]
print(",".join(bad))
PY
)"; then
    fail "MASTER SWITCH  cannot parse $json" "$verdict"
  elif [ -n "$verdict" ]; then
    fail "MASTER SWITCH  $json sets Trading:Enabled=$verdict (keys are case-insensitive and ':'/'__' flatten)"
  fi
done <<<"$jsons"

# docker-compose*.yml: every Trading__Enabled key (any casing) must default to false.
composes="$(find . \( -name .git -o -name node_modules -o -name bin -o -name obj \) -prune -o -type f \( -name 'docker-compose*.yml' -o -name 'docker-compose*.yaml' -o -name 'compose*.yml' -o -name 'compose*.yaml' \) -print)" || env_fail "CANNOT LIST  find failed looking for compose files"
[ -n "$composes" ] || fail "MASTER SWITCH  no docker-compose*.yml found to check"
while IFS= read -r c; do
  [ -n "$c" ] || continue
  c="${c#./}"
  status=0; lines="$(grep -niE 'trading__enabled' "$c")" || status=$?
  [ "$status" -gt 1 ] && env_fail "CANNOT SEARCH  grep exited $status over $c"
  while IFS= read -r l; do
    [ -n "$l" ] || continue
    grep -qiE '^[[:space:]]*-?[[:space:]]*trading__enabled[[:space:]]*[:=][[:space:]]*"?(\$\{trading__enabled:-false\}|false)"?[[:space:]]*(#.*)?$' <<<"${l#*:}" && continue
    fail "MASTER SWITCH  $c:${l%%:*} must default Trading__Enabled to false" "${l#*:}" \
      "Allowed: Trading__Enabled: \"\${Trading__Enabled:-false}\" or false."
  done <<<"$lines"
done <<<"$composes"
if [ -f docker-compose.yml ] && ! grep -qiE 'trading__enabled' docker-compose.yml; then
  fail "MASTER SWITCH  docker-compose.yml no longer passes Trading__Enabled at all" "Keep the explicit \${Trading__Enabled:-false} passthrough."
fi

# Railway IaC (.railway/*) and legacy railway.json/railway.toml: every assignment is the literal "false" (gh#758).
railways="$(find . \( -name .git -o -name node_modules -o -name bin -o -name obj \) -prune -o -type f \( -path './.railway/*' -o -name 'railway.json' -o -name 'railway.toml' \) -print)" || env_fail "CANNOT LIST  find failed looking for Railway config"
while IFS= read -r r; do
  [ -n "$r" ] || continue
  r="${r#./}"
  status=0; lines="$(grep -niE 'trading__enabled' "$r")" || status=$?
  [ "$status" -gt 1 ] && env_fail "CANNOT SEARCH  grep exited $status over $r"
  while IFS= read -r l; do
    [ -n "$l" ] || continue
    body="${l#*:}"
    printf '%s\n' "$body" | grep -qE '^[[:space:]]*(//|#|\*|/\*)' && continue
    printf '%s\n' "$body" | grep -qiE '^[[:space:]]*["'"'"']?trading__enabled["'"'"']?[[:space:]]*[:=][[:space:]]*' \
      && printf '%s\n' "$body" | grep -qE '[:=][[:space:]]*"false"[[:space:]]*,?[[:space:]]*(//.*)?$' && continue
    fail "MASTER SWITCH  $r:${l%%:*} must set Trading__Enabled to the literal \"false\"" "$body"
  done <<<"$lines"
done <<<"$railways"
if [ -f .railway/railway.ts ] && ! grep -vE '^[[:space:]]*//' .railway/railway.ts | grep -qiE 'trading__enabled[[:space:]]*:'; then
  fail "MASTER SWITCH  .railway/railway.ts no longer sets Trading__Enabled" "Keep the explicit Trading__Enabled: \"false\" (gh#758)."
fi

# Dockerfile(s) and .env.example: any assignment must be false.
for f in Dockerfile* .env.example; do
  [ -f "$f" ] || continue
  status=0; lines="$(grep -niE 'trading__enabled' "$f")" || status=$?
  [ "$status" -gt 1 ] && env_fail "CANNOT SEARCH  grep exited $status over $f"
  while IFS= read -r l; do
    [ -n "$l" ] || continue
    body="${l#*:}"
    grep -qE '^[[:space:]]*#' <<<"$body" && continue
    grep -qiE 'trading__enabled[[:space:]]*[= ][[:space:]]*"?false"?[[:space:]]*(\\)?$' <<<"$body" && continue
    fail "MASTER SWITCH  $f:${l%%:*} sets Trading__Enabled to something other than false" "$body"
  done <<<"$lines"
done

# --- (5) the gate --------------------------------------------------------------------------------------------
status=0
elsewhere="$(grep -rlwE --include='*.cs' 'class[[:space:]]+TradingRiskGate' "${PRODUCT_DIRS[@]}")" || status=$?
[ "$status" -gt 1 ] && env_fail "CANNOT SEARCH  grep exited $status looking for class TradingRiskGate"
named="$(find . -name 'TradingRiskGate.cs' -not -path '*/obj/*' -not -path '*/bin/*' -print)" || env_fail "CANNOT LIST  find failed looking for TradingRiskGate.cs"
stray="$(printf '%s\n%s\n' "$elsewhere" "${named//.\//}" | grep -v '^$' | grep -vxF "$GATE_FILE" | sort -u || true)"
if [ -n "$stray" ]; then
  fail "GATE LOCATION  TradingRiskGate found at: $(printf '%s' "$stray" | tr '\n' ' ')" \
    "Expected at $GATE_FILE. Move it, or update GATE_FILE (and CODEOWNERS) in the SAME pull request."
fi
if [ ! -f "$GATE_FILE" ]; then
  fail "GATE MISSING  $GATE_FILE does not exist" \
    "ADR-0024 requires TradingRiskGate (gh#711). This check does not skip: it is red until the gate lands."
else
  # Per member: strip comments and string literals, track brace depth, and require RefuseIfDisabledOrHalted(
  # before any probe within the same top-level member of class TradingRiskGate.
  # Portable across mawk and gawk (the runner has gawk, which rejects an -v '\\(' that mawk accepts): '[(]' for a
  # literal paren, no \< \>, so the order-call probe anchors on a non-identifier char.
  # Stage sources: ProjectXMapping.ResolveStage( (internal once #734 lands) or a call on any IAccountStageResolver
  # the gate declares (field, parameter or local) -- e.g. _stages.ResolveAsync(.
  resolver_names="$(grep -oE 'IAccountStageResolver[[:space:]]+[A-Za-z_][A-Za-z0-9_]*' "$GATE_FILE" | awk '{print $2}' | sort -u | paste -sd'|' - || true)"
  res_re='ResolveStage[ \t]*[(]'
  [ -n "$resolver_names" ] && res_re="$res_re|(^|[^A-Za-z0-9_])($resolver_names)[ \\t]*[.][ \\t]*[A-Za-z_][A-Za-z0-9_]*[ \\t]*[(]"
  # Bracket forms only ([[], []], [(]): gawk on the runner mangles backslash escapes passed through -v.
  decl_re='^[ \t]*([[][^]]*[]][ \t]*)*((public|private|internal|protected|static|async|virtual|override|sealed|new|extern|unsafe|partial|readonly|abstract)[ \t]+)*[A-Za-z_(][]A-Za-z0-9_.<>,?[() \t]*[ \t]+[A-Za-z_][A-Za-z0-9_]*[ \t]*(<[^>(]*>)?[ \t]*[(]'
  # Members: a new member starts on a class-level line once the previous member has ENDED (a class-level line
  # ending in ";" or "}"), so a multi-line signature, an attribute line or a next-line expression body all
  # belong to their member. Two rules per member, checked on code with comments and strings stripped:
  #   G  RefuseIfDisabledOrHalted( before any stage resolution, allowlist, denylist or order call;
  #   S  a stage resolution (ResolveStage( / resolver call, or a call to a gate member that makes one) before
  #      any GATEWAY call (".PlaceOrder(" etc.).
  order_report="$(awk -v decl_re="$decl_re" -v res_re="$res_re" \
      -v probe_re="RefuseIfNotAllowlisted[ \t]*[(]|RefuseIfSymbolDenied[ \t]*[(]|(^|[^A-Za-z0-9_])($ORDER_RE)[ \t]*[(]" \
      -v gw_re="[.][ \t]*($ORDER_RE)[ \t]*[(]" '
    BEGIN { depth = 0; classdepth = -1; member = 0; incomment = 0; inraw = 0; ended = 1; n = 0 }
    {
      s = $0
      if (inraw) { if (sub(/^.*"""/, "", s)) inraw = 0; else next }
      gsub(/"""([^"]|"[^"]|""[^"])*"""/, "\"\"", s)
      if (match(s, /"""/)) { s = substr(s, 1, RSTART - 1); inraw = 1 }
      if (incomment) { if (sub(/.*[*][\/]/, "", s)) incomment = 0; else next }
      gsub(/[\/][*].*[*][\/]/, "", s)
      if (match(s, /[\/][*]/)) { s = substr(s, 1, RSTART - 1); incomment = 1 }
      sub(/[\/][\/].*/, "", s)
      gsub(/"([^"\\]|\\.)*"/, "\"\"", s)
      start = depth
      if (classdepth < 0 && s ~ /class[ \t]+TradingRiskGate/) pending = 1
      if (classdepth >= 0 && start == classdepth && s ~ /[^ \t]/) {
        if (ended) { member++; ended = 0 }
        eq = index(s, "="); pre = (eq > 0) ? substr(s, 1, eq - 1) : s
        if (match(pre, decl_re)) {
          ds = RSTART; dl = RLENGTH; m = substr(pre, ds, dl)
          if (match(m, /[A-Za-z_][A-Za-z0-9_]*[ \t]*(<[^>(]*>)?[ \t]*[(]$/)) {
            nm = substr(m, RSTART); sub(/[^A-Za-z0-9_].*$/, "", nm)
            if (!(member in mname)) mname[member] = nm
            m = substr(m, 1, RSTART - 1) "("
          }
          s = substr(s, 1, ds - 1) m substr(s, ds + dl)
        }
      }
      if (classdepth >= 0 && start >= classdepth) { n++; L[n] = NR; S[n] = s; M[n] = member; if (s ~ res_re) res_in[member] = 1 }
      len = length(s)
      for (i = 1; i <= len; i++) {
        ch = substr(s, i, 1)
        if (ch == "{") { depth++; if (pending) { classdepth = depth; pending = 0 } }
        else if (ch == "}") depth--
      }
      if (classdepth >= 0 && depth == classdepth && start >= classdepth && s ~ /[;}][ \t]*$/) ended = 1
    }
    END {
      if (classdepth < 0) { print "NOCLASS"; exit }
      helpers = ""
      for (k in res_in) if (k in mname) helpers = helpers (helpers == "" ? "" : "|") mname[k]
      help_re = (helpers == "") ? "^$x" : "(^|[^A-Za-z0-9_])(" helpers ")[ \t]*[(]"
      cur = -1
      for (j = 1; j <= n; j++) {
        if (M[j] != cur) { cur = M[j]; guarded = 0; staged = 0 }
        t = S[j]; d = index(t, "RefuseIfDisabledOrHalted(")
        if (match(t, probe_re) || match(t, res_re)) {
          if (!guarded && (d == 0 || d > RSTART)) printf "%d:%s\n", L[j], substr(t, RSTART, RLENGTH)
        }
        if (d > 0) guarded = 1
        if (match(t, res_re) || match(t, help_re)) { sp = RSTART; st = 1 } else st = 0
        if (match(t, gw_re)) {
          if (!staged && (!st || sp > RSTART)) printf "STAGE %d:%s\n", L[j], substr(t, RSTART, RLENGTH)
        }
        if (st) staged = 1
      }
    }
  ' "$GATE_FILE")" || env_fail "CANNOT PARSE  awk failed over $GATE_FILE"
  if [ "$order_report" = "NOCLASS" ]; then
    fail "GATE  $GATE_FILE does not declare class TradingRiskGate"
  else
    while IFS= read -r r; do
      [ -n "$r" ] || continue
      case "$r" in
        STAGE\ *)
          r="${r#STAGE }"
          fail "GATE STAGE  $GATE_FILE:${r%%:*} gateway call '${r#*:}' comes before the account stage is resolved in its member" \
            "Resolve the stage (IAccountStageResolver, or ResolveStage) -- directly or via a gate helper -- before any gateway call." ;;
        *)
          fail "GATE ORDER  $GATE_FILE:${r%%:*} '${r#*:}' runs before RefuseIfDisabledOrHalted() in its member" \
            "Every member that touches the stage, allowlist, denylist or gateway checks the master and kill switch first." ;;
      esac
    done <<<"$order_report"
  fi
  code="$(grep -vE '^[[:space:]]*(//|\*|/\*)' "$GATE_FILE" | sed -E 's/"([^"\\]|\\.)*"/""/g; s|//.*$||' || true)"
  # Not a pipe. grep -q exits at the first match and closes its input; under pipefail, printf's SIGPIPE
  # is then a false "never calls". A here-string keeps the same pattern and the same exit status.
  grep -qE 'RefuseIfDisabledOrHalted[[:space:]]*\(' <<<"$code" || fail "GATE  $GATE_FILE never calls RefuseIfDisabledOrHalted()"
  if ! grep -qE "ResolveStage[[:space:]]*\\(${resolver_names:+|(^|[^A-Za-z0-9_])($resolver_names)[[:space:]]*\\.[[:space:]]*[A-Za-z_][A-Za-z0-9_]*[[:space:]]*\\(}" <<<"$code"; then
    fail "GATE  $GATE_FILE never resolves the account stage" \
      "Call an IAccountStageResolver the gate declares (or ProjectXMapping.ResolveStage) on every trading call."
  fi
  # Practice: named in the gate itself, OR delegated -- the gate must CALL (".RefuseIfStageBlocksWrite(", not
  # declare it) and TradingExecutionGate.cs must name AccountStage.Practice INSIDE the body of a method
  # declared RefuseIfStageBlocksWrite(. Text-level, so EXEC_GATE is on CODEOWNERS and in the header.
  # An EXPLICIT refusal: `stage != AccountStage.Practice` / `is not AccountStage.Practice` whose own line, or the
  # next code line (past a lone "{"), returns or throws. Merely naming Practice does not count.
  practice_refusal="$(printf '%s\n' "$code" | awk '
    { line[NR] = $0 }
    END {
      for (i = 1; i <= NR; i++) {
        if (line[i] !~ /(!=|is[ \t]+not)[ \t]*AccountStage[.]Practice|AccountStage[.]Practice[ \t]*!=/) continue
        if (line[i] ~ /(^|[^A-Za-z0-9_])(return|throw)([^A-Za-z0-9_]|$)/) { print "yes"; exit }
        # Only a condition that ENDS its line (")" or "{") may have its return/throw on the next line.
        if (line[i] !~ /[){][ \t]*$/) continue
        for (j = i + 1; j <= NR; j++) {
          if (line[j] ~ /^[ \t]*$/ || line[j] ~ /^[ \t]*[{][ \t]*$/) continue
          if (line[j] ~ /^[ \t]*(return|throw)([^A-Za-z0-9_]|$)/) { print "yes"; exit }
          break
        }
      }
      print "no"
    }')"
  if [ "$practice_refusal" != "yes" ]; then
    if grep -qE '\.[[:space:]]*RefuseIfStageBlocksWrite[[:space:]]*\(' <<<"$code" && [ -f "$EXEC_GATE" ] \
      && in_body="$(awk '
        { s = $0; sub(/\/\/.*/, "", s); gsub(/"([^"\\]|\\.)*"/, "\"\"", s) }
        !inm && s ~ /(public|internal|private|protected)[^=;]*[ \t]RefuseIfStageBlocksWrite[ \t]*[(]/ { inm = 1; d = 0; opened = 0 }
        inm {
          if (s ~ /AccountStage\.Practice/) found = 1
          n = length(s)
          for (i = 1; i <= n; i++) { c = substr(s, i, 1); if (c == "{") { d++; opened = 1 } else if (c == "}") d-- }
          if (opened && d == 0) inm = 0
          if (!opened && s ~ /=>/ && s ~ /;[ \t]*$/) inm = 0
        }
        END { print (found ? "yes" : "no") }' "$EXEC_GATE")" && [ "$in_body" = "yes" ]; then
      practice_via="$EXEC_GATE"
    else
      fail "GATE  $GATE_FILE never refuses a non-Practice stage" \
        "Write an explicit 'if (stage != AccountStage.Practice) return/throw ...', or CALL .RefuseIfStageBlocksWrite(" \
        "with AccountStage.Practice compared inside that method's body in $EXEC_GATE."
    fi
  fi
fi

# --- (6) gate tests exist ---------------------------------------------------------------------------------
trait_files=""
if [ -d "$TEST_DIR" ]; then
  status=0
  trait_files="$(grep -rlE --include='*.cs' "$TRAIT_RE" "$TEST_DIR")" || status=$?
  [ "$status" -gt 1 ] && env_fail "CANNOT SEARCH  grep exited $status over $TEST_DIR"
fi
trait_count="$(printf '%s' "$trait_files" | grep -c . || true)"
if [ "$trait_count" -eq 0 ]; then
  fail "GATE TESTS  no test class in $TEST_DIR is tagged [Trait(\"Gate\", \"ADR-0024\")]"
else
  status=0
  printf '%s\n' "$trait_files" | xargs grep -lw 'TradingRiskGate' >/dev/null 2>&1 || status=$?
  [ "$status" -ne 0 ] && fail "GATE TESTS  no [Trait(\"Gate\", \"ADR-0024\")] test file names TradingRiskGate"
fi

if [ "$failures" -gt 0 ]; then
  echo >&2
  die "$failures violation(s) of the gated order boundary (ADR-0024, gh#718)."
  cat >&2 <<'EXPLAIN'
Orders are reachable only through TradingRiskGate -> IExecutionGateway, behind Trading__Enabled (default
false) and a Practice-only stage check. This script is a tripwire, not a proof: do not widen its allowlists
to get green. If the design genuinely changed, change ADR-0024, this script and CODEOWNERS in one pull request.
EXPLAIN
  exit 1
fi

ok "Gated order boundary tripwire holds: $searched .cs file(s) across ${#PRODUCT_DIRS[@]} discovered product project(s) (${PRODUCT_DIRS[*]}); $order_hits order-name reference(s), all in the seam; $registrations Program.cs registration(s); $trait_count ADR-0024 gate test file(s); gate ordering checked${practice_via:+; Practice checked via $practice_via}."
