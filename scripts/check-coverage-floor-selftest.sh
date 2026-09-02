#!/usr/bin/env bash
# check-coverage-floor-selftest.sh — require that check-coverage-floor.py can still go red (gh#435).
#
#   scripts/check-coverage-floor-selftest.sh
#
# WHY THIS EXISTS. The gate it guards spent its whole life unable to fail on the event it was built for.
# gh#431 merged the unit and integration tiers into one figure and moved the reported number to 91.0%; the
# floor stayed at 40, correctly, because that card forbade touching it. PR #434's reviewer then measured the
# consequence: **a zero-test integration tier still yields merged 56.0% and still passes**, so the full
# gh#387 event cleared the gate built in response to it. A gate 51 points below the measurement it guards
# does not fire before somebody notices by other means, and it is worse than no gate because it is trusted.
#
# The rubric's own worked example is this file's subject almost verbatim: *thirty lines of bash, but it
# enforces the repo's central safety claim -- and a gate that is too permissive is green and proves nothing.*
#
# WHAT A NON-ZERO EXIT IS NOT. `check-coverage-floor.py` exits non-zero for at least six distinct reasons --
# a floor breach, a tier with no report, an unset threshold, a parse that disagrees with its own document,
# tiers that share no source file, and a malformed command line -- and two of those are exit 2 rather than
# exit 1. A self-test satisfied by "it failed" would go green on a runner where the fixtures were never
# written, reporting the gate sound at precisely the moment nothing had been checked. Every case below
# asserts the EXACT exit code and matches the words that name ITS OWN fault (gh#182: assert the whole field
# the reader will use, not a prefix of it).
#
# AND THE ACCEPTANCES ARE NOT SATISFIED BY EXIT 0 EITHER. Rejections alone are all satisfied by `exit 1`,
# i.e. by a gate that says no to everything, which is exactly as useless as one that says yes. The green
# cases additionally require the gate to PRINT the figure it computed, so "green having measured nothing"
# is excluded.
#
# THE TWO GREEN CASES ARE THE HALF THAT KEEPS THIS FIX HONEST. The instruction this card was given names the
# failure mode directly: do not make the gate unable to fail while making it stricter. `sound` proves the
# raised floor still passes a healthy tree and that the parse assertion does not fire on a report that emits
# every line TWICE, which is what real cobertura does. `branch-root-differs` proves the parse assertion was
# deliberately confined to LINES: coverlet's root declares `branches-valid="1905"` where the detail carries
# 1885, so asserting branch equality would make this gate unable to PASS on genuine output -- the same
# defect as one unable to fail, wearing the fix's clothes.
#
# WHAT `floor-is-load-bearing` PINS, AND WHY IT IS THE CENTRAL CASE. It runs the gh#387 shape -- both tiers
# present, the integration tier's report internally consistent and carrying zero hits -- twice: red at the
# floor this card sets, and GREEN at the floor it replaced. That pair is the card. Neither run alone says
# anything: the red one is satisfied by any floor above the merged figure, and the green one is satisfied by
# any floor below it. Together they say the NUMBER is what fires, and they fail the moment someone lowers it
# back toward the merged figure of a broken tree.
#
# THE INTERPRETER IS RESOLVED AND THEN TESTED, not read off PATH (gh#126's rule, in a shape that bites on
# Windows). `python3` on a stock Windows box is the Microsoft Store app-execution alias: it EXISTS on PATH,
# it is executable, and running it prints "Python was not found" and exits 49. `command -v python3` is
# therefore not the read that decides anything -- the read is whether the thing runs.
#
# STDERR IS CAPTURED SEPARATELY rather than merged with `2>&1`, and the green cases require it EMPTY
# (gh#239, narrowed by gh#271). MEASURED, not assumed, and the measurement is what licenses the assertion:
# this gate is a single `python3` process that spawns nothing, so a green run's stderr is a property of the
# script rather than of some child's daemon -- which is the distinction gh#271 drew when the same assertion
# turned out to answer nobody's question for `check-image-entrypoint.sh`. Both green cases below write 0
# bytes to stderr, and so does the real gate over run 33584086404's artifacts. The mutant that proves the
# assertion bites: a stray `print(..., file=sys.stderr)` anywhere on the success path.
#
# DECISION LEDGER -- every decision the gate makes, against the case that kills it.
#
#   | Decision in check-coverage-floor.py | Case that kills it if removed |
#   |---|---|
#   | merged line rate is compared against the floor | `floor-breach` |
#   | the floor's VALUE is close enough to fire on gh#387 | `floor-is-load-bearing` (both halves) |
#   | a tier with no cobertura report is an error, by tier name | `missing-report` |
#   | MINIMUM_LINE_COVERAGE is required, never defaulted to 0 | `unset-floor` |
#   | parsed lines-valid must equal the document's own root | `parse-undercount` |
#   | parsed lines-covered must equal the document's own root | `parse-miscount-covered` |
#   | a root with no such attribute is a fault, not a skip | `parse-root-silent` |
#   | branch totals are NOT asserted against the root | `branch-root-differs` |
#   | tiers sharing no source file are a concatenation | `disjoint-tiers` |
#   | `<tier>=<dir>` is the required argument shape | `usage` |
#   | `(file, line)` is the key, so a doubly-emitted line counts once | `sound` (every fixture line is emitted twice) |
#   | the floor is still reachable when the parse agrees | `sound` + `floor-breach` |
#
# MUTATED BEFORE IT WAS BELIEVED (platform.md: run a text-matching gate before believing its diagnostics).
# Nine mutations of `check-coverage-floor.py`, each run against this suite unchanged, on Python 3.11.15:
#
#   | Mutation | Cases that go red |
#   |---|---|
#   | `if merged_line < floor:` -> `if False:` | floor-breach, floor-is-load-bearing/red (2) |
#   | `problems.extend(check_against_root(...))` -> `extend([])` | the three parse cases + no-verdict-printed (4) |
#   | the `MINIMUM_LINE_COVERAGE not in environ` guard removed | unset-floor, **as exit 1 rather than 2** (1) |
#   | `if not report_count:` -> `if False:` | missing-report (1) |
#   | parsed `lines-valid` counts raw elements (`2 * len`) | 8, `sound` first — the dedup is what makes the assertion true |
#   | the assertion extended to `branches-valid` | branch-root-differs (1) — the tightening this suite forbids |
#   | `main()` replaced by `return 0` after the arg checks | 10 of 13; `usage` and `unset-floor` return earlier |
#   | `if not shared:` -> `if False:` | disjoint-tiers (1) |
#   | a stray `print(..., file=sys.stderr)` on the success path | the three green cases, by BYTE COUNT (3) |
#
# The third row is the one worth reading twice: the mutant still exits non-zero, on the same fixture, for a
# different reason. A suite asserting only "it failed" would have passed it.
#
# FIXTURE ARITHMETIC, so a needle can name a whole figure rather than a substring of one. Two source files
# of 100 lines each, 200 lines per tier. `unit` covers all of `Alpha.cs`; `integration` covers 80 lines of
# `Beta.cs`. Merged is a union: 180/200 = 90.0%, against 50.0% and 40.0% per tier -- the same shape as the
# real 91.0% against 56.0% and 63.2%, small enough to check by hand.
#
# Fixture trees are disposable, live under `mktemp -d`, and are removed on EXIT. Nothing is copied back into
# the repository (documentation/AGENT-MEMORY.md).

set -euo pipefail

red()  { printf '\033[31m%s\033[0m\n' "$*" >&2; }
ok()   { printf '\033[32m%s\033[0m\n' "$*"; }
info() { printf '%s\n' "$*"; }

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GATE="$REPO_ROOT/scripts/check-coverage-floor.py"
if [ ! -f "$GATE" ]; then
  red "  MISSING  $GATE"
  red "NOTHING HAS BEEN CHECKED, and in particular the gate has not been proven able to fail."
  exit 1
fi

# Resolve an interpreter that RUNS, not one that merely resolves. See the header: the Windows Store alias is
# on PATH and exits 49. Assigned on its own line and checked, per documentation/agents/platform.md.
PY=""
for candidate in "${PYTHON:-}" python3 python; do
  [ -n "$candidate" ] || continue
  if "$candidate" -c 'import sys; sys.exit(0)' >/dev/null 2>&1; then
    PY="$candidate"
    break
  fi
done
if [ -z "$PY" ]; then
  red "  NO WORKING PYTHON  tried \${PYTHON:-}, python3, python"
  red "NOTHING HAS BEEN CHECKED. The gate is a python3 script; set PYTHON=<interpreter> and re-run."
  exit 1
fi
info "interpreter: $PY ($("$PY" --version 2>&1))"

FIXTURES="$(mktemp -d)"
trap 'rm -rf "$FIXTURES"' EXIT

failures=0
cases=0

# ---------------------------------------------------------------------------------------------------------
# Fixture construction
# ---------------------------------------------------------------------------------------------------------

# mkreport <path> <root-lines-valid|auto> <root-lines-covered|auto> <spec>...
#   spec = <filename>:<first-line>:<last-line>:<hits>
#
# EVERY LINE IS WRITTEN TWICE -- once under the method that owns it and once under `<class><lines>` -- which
# is what coverlet does and what makes the parse's `(file, line)` key load-bearing. Measured on CI run
# 33584086404: 16 254 `<line>` elements, 8 127 distinct pairs, `lines-valid="8127"` on the root.
#
# The two root totals are separately overridable so a fixture can DISAGREE with its own detail; `auto`
# computes them from the specs, which is what a sound report does.
mkreport() {
  local path="$1" root_valid="$2" root_covered="$3"
  shift 3
  local spec file first last hits count valid=0 covered=0 lines="" number

  for spec in "$@"; do
    IFS=: read -r file first last hits <<<"$spec"
    count=$((last - first + 1))
    valid=$((valid + count))
    if [ "$hits" -gt 0 ]; then covered=$((covered + count)); fi
  done
  # `if`, not `[ … ] && …`: a false test as a whole statement carries exit 1 into `set -e`, which would
  # abort the builder on every fixture that overrides a total. platform.md's first constraint, in miniature.
  if [ "$root_valid" = auto ]; then root_valid="$valid"; fi
  if [ "$root_covered" = auto ]; then root_covered="$covered"; fi

  mkdir -p "$(dirname "$path")"
  {
    printf '<?xml version="1.0" encoding="utf-8"?>\n'
    printf '<coverage line-rate="0.5" branch-rate="0" version="1.9" timestamp="0"'
    printf ' lines-covered="%s" lines-valid="%s"' "$root_covered" "$root_valid"
    printf ' branches-covered="0" branches-valid="0">\n'
    printf '  <sources>\n    <source>/</source>\n  </sources>\n'
    printf '  <packages>\n    <package name="Fixture" line-rate="0.5" branch-rate="0" complexity="0">\n'
    printf '      <classes>\n'
    for spec in "$@"; do
      IFS=: read -r file first last hits <<<"$spec"
      lines=""
      for ((number = first; number <= last; number++)); do
        lines+="          <line number=\"$number\" hits=\"$hits\" branch=\"False\" />"$'\n'
      done
      printf '        <class name="Fixture.%s" filename="%s" line-rate="0.5" branch-rate="0" complexity="0">\n' \
        "${file%.cs}" "$file"
      printf '          <methods>\n            <method name="M" signature="()" line-rate="0.5" branch-rate="0">\n'
      printf '              <lines>\n%s              </lines>\n' "$lines"
      printf '            </method>\n          </methods>\n'
      printf '          <lines>\n%s          </lines>\n' "$lines"
      printf '        </class>\n'
    done
    printf '      </classes>\n    </package>\n  </packages>\n</coverage>\n'
  } >"$path"
}

# Rewrite one attribute of the `<coverage>` root in place, or -- with no value -- delete it. Used only to
# build faults the builder above cannot express, and never on a fixture that is meant to be sound.
set_root_attr() {
  # Split rather than one `local`: bash expands every right-hand side before it assigns any of them, so
  # `local path="$1" tmp="$path.tmp"` reads an unset `path` and dies under `set -u`. Cost one red run here.
  local path="$1" name="$2" value="${3-}"
  local tmp="$path.tmp"
  if [ "$#" -ge 3 ]; then
    sed -e "s/ $name=\"[^\"]*\"/ $name=\"$value\"/" "$path" >"$tmp"
  else
    sed -e "s/ $name=\"[^\"]*\"//" "$path" >"$tmp"
  fi
  mv "$tmp" "$path"
}

# The two tiers of a healthy tree. `unit` walks Alpha, `integration` walks 80 of Beta's 100 lines; both
# report on the same two files, so the union is a union and not a concatenation.
UNIT_SOUND=(Alpha.cs:1:100:1 Beta.cs:1:100:0)
INTEGRATION_SOUND=(Alpha.cs:1:100:0 Beta.cs:1:80:3 Beta.cs:81:100:0)
# A tier that ran no tests at all: the report is present, internally consistent, and carries zero hits.
INTEGRATION_EMPTY=(Alpha.cs:1:100:0 Beta.cs:1:100:0)

# Builds `$FIXTURES/<name>/{unit,integration}` and echoes nothing; the caller passes the two directories.
make_tree() {
  local name="$1" kind="$2"
  local dir="$FIXTURES/$name"
  rm -rf "$dir"
  mkreport "$dir/unit/guid/coverage.cobertura.xml" auto auto "${UNIT_SOUND[@]}"
  case "$kind" in
    sound)
      mkreport "$dir/integration/guid/coverage.cobertura.xml" auto auto "${INTEGRATION_SOUND[@]}" ;;
    empty-integration)
      mkreport "$dir/integration/guid/coverage.cobertura.xml" auto auto "${INTEGRATION_EMPTY[@]}" ;;
    no-integration-report)
      mkdir -p "$dir/integration"
      printf 'not a coverage report\n' >"$dir/integration/integration-tests.trx" ;;
    *)
      red "make_tree: unknown kind '$kind'"; exit 1 ;;
  esac
}

# ---------------------------------------------------------------------------------------------------------
# Running the gate
# ---------------------------------------------------------------------------------------------------------

# Directly under $FIXTURES and never inside a tier directory: the gate globs its tier roots for reports, so
# a scratch file written in one would join the corpus under test.
GATE_STDOUT="$FIXTURES/.gate-stdout"
GATE_STDERR="$FIXTURES/.gate-stderr"
GATE_STATUS=0

# run_gate <floor|unset> <arg>...
#
# `env -u` IS LOAD-BEARING, not tidiness. In `ci.yml` the `coverage` job sets MINIMUM_LINE_COVERAGE at JOB
# level, so it is in this script's own environment; without unsetting it the `unset-floor` case would
# inherit CI's real floor and pass while asserting nothing.
run_gate() {
  local floor="$1"
  shift
  # Truncated per call, not appended: each case asserts what ITS OWN run wrote.
  : >"$GATE_STDOUT"
  : >"$GATE_STDERR"
  GATE_STATUS=0
  if [ "$floor" = unset ]; then
    env -u MINIMUM_LINE_COVERAGE "$PY" "$GATE" "$@" >"$GATE_STDOUT" 2>"$GATE_STDERR" || GATE_STATUS=$?
  else
    MINIMUM_LINE_COVERAGE="$floor" "$PY" "$GATE" "$@" >"$GATE_STDOUT" 2>"$GATE_STDERR" || GATE_STATUS=$?
  fi
}

# Both streams as one string, for needles that must find the gate's own words wherever it put them.
gate_output() { cat "$GATE_STDOUT" "$GATE_STDERR"; }

show_output() {
  info "    --- the gate said ---"
  gate_output | sed -e 's/^/    /'
  info "    ---------------------"
}

# expect_red <case> <expected-exit> <needle>...
expect_red() {
  local name="$1" want="$2"
  shift 2
  cases=$((cases + 1))
  local out
  out="$(gate_output)"
  if [ "$GATE_STATUS" -eq 0 ]; then
    red "  FAIL  $name: the gate ACCEPTED a tree it must reject (exit 0)."
    show_output
    failures=$((failures + 1))
    return
  fi
  if [ "$GATE_STATUS" -ne "$want" ]; then
    red "  FAIL  $name: exit $GATE_STATUS, expected $want. A different fault fired than the one under test."
    show_output
    failures=$((failures + 1))
    return
  fi
  local needle
  for needle in "$@"; do
    case "$out" in
      *"$needle"*) ;;
      *)
        red "  FAIL  $name: rejected with exit $want but never said: $needle"
        show_output
        failures=$((failures + 1))
        return ;;
    esac
  done
  ok "  ok    $name — rejected (exit $want), naming its own fault"
}

# expect_not_said <case> <needle>: the gate must NOT have printed this. Used where the danger is a figure
# that reads like a verdict.
expect_not_said() {
  local name="$1" needle="$2" out
  cases=$((cases + 1))
  out="$(gate_output)"
  case "$out" in
    *"$needle"*)
      red "  FAIL  $name: the gate printed '$needle', which reads as a measurement it had already refused."
      show_output
      failures=$((failures + 1)) ;;
    *)
      ok "  ok    $name — did not print '$needle'" ;;
  esac
}

# expect_green <case> <needle>...
expect_green() {
  local name="$1"
  shift
  cases=$((cases + 1))
  local out
  out="$(gate_output)"
  if [ "$GATE_STATUS" -ne 0 ]; then
    red "  FAIL  $name: the gate REJECTED a sound tree (exit $GATE_STATUS)."
    show_output
    failures=$((failures + 1))
    return
  fi
  local needle
  for needle in "$@"; do
    case "$out" in
      *"$needle"*) ;;
      *)
        red "  FAIL  $name: accepted, but never printed the figure it measured: $needle"
        show_output
        failures=$((failures + 1))
        return ;;
    esac
  done
  local stderr_bytes
  stderr_bytes="$(wc -c <"$GATE_STDERR")"
  if [ "$stderr_bytes" -ne 0 ]; then
    red "  FAIL  $name: a green run wrote $stderr_bytes bytes to stderr. A needle cannot see output that"
    red "        should not be there at all (gh#239); the stream is what catches it."
    info "    --- stderr ---"
    sed -e 's/^/    /' "$GATE_STDERR"
    failures=$((failures + 1))
    return
  fi
  ok "  ok    $name — accepted, stated its figure, and wrote nothing to stderr"
}

# ---------------------------------------------------------------------------------------------------------
# Cases
# ---------------------------------------------------------------------------------------------------------

info "check-coverage-floor-selftest.sh — proving scripts/check-coverage-floor.py can still fail"
info ""

# 1. A healthy tree is ACCEPTED, and says what it measured. Also pins the `(file, line)` key: every fixture
#    line is written twice, so a parse that counted raw elements would report 400 lines here and be caught
#    by the assertion this card adds, not by a needle.
make_tree sound sound
run_gate 85 "unit=$FIXTURES/sound/unit" "integration=$FIXTURES/sound/integration"
expect_green "sound" \
  "Line coverage 90.0% (floor 85%)" \
  "| **merged** | **90.0%** | **0.0%** | 180/200 |" \
  "| \`unit\` | 50.0% | 0.0% | 100/200 |" \
  "| \`integration\` | 40.0% | 0.0% | 80/200 |"

# 2. Below the floor, and the diagnostic carries BOTH numbers -- the measurement and the floor it missed.
run_gate 95 "unit=$FIXTURES/sound/unit" "integration=$FIXTURES/sound/integration"
expect_red "floor-breach" 1 \
  "::error::Merged line coverage 90.0% is below the 95% floor."

# 3. THE CARD. The gh#387 shape: both tiers present, the integration tier internally consistent and having
#    executed nothing. Red at the floor gh#435 sets...
make_tree gh387 empty-integration
run_gate 85 "unit=$FIXTURES/gh387/unit" "integration=$FIXTURES/gh387/integration"
expect_red "floor-is-load-bearing/red" 1 \
  "| \`integration\` | 0.0% | 0.0% | 0/200 |" \
  "::error::Merged line coverage 50.0% is below the 85% floor."

# ...and GREEN at the floor it replaced, on the byte-identical tree. Neither half measures anything alone:
#    together they say the VALUE is what fires, and they redden if it is walked back.
run_gate 40 "unit=$FIXTURES/gh387/unit" "integration=$FIXTURES/gh387/integration"
expect_green "floor-is-load-bearing/green-at-the-old-floor" \
  "Line coverage 50.0% (floor 40%)"

# 4. A tier whose directory holds no cobertura report is an ERROR NAMED FOR THAT TIER, never a tier dropped
#    -- dropping one is the gh#387 regression itself, and it drops toward a number that still passes. The
#    listing is asserted too: without it the diagnostic cannot be acted on.
make_tree noreport no-integration-report
run_gate 85 "unit=$FIXTURES/noreport/unit" "integration=$FIXTURES/noreport/integration"
expect_red "missing-report" 1 \
  "::error::No cobertura report for the 'integration' tier under" \
  "integration artifact contained:" \
  "integration-tests.trx"

# 5. The threshold is required. A floor that defaults to 0 when the variable is missing is a gate that
#    cannot fail, reached by deleting one line of YAML.
run_gate unset "unit=$FIXTURES/sound/unit" "integration=$FIXTURES/sound/integration"
expect_red "unset-floor" 2 \
  "::error::MINIMUM_LINE_COVERAGE is not set."

# 6. THE UNDER-READ, in the direction that flatters. The unit report's detail carries only Alpha's 100
#    covered lines while its root still declares the 200 it was written with -- which is what a parser that
#    silently skips lines produces. Numerator and denominator shrink together, so the tier reads 100.0%
#    instead of 50.0% and the merged figure RISES. Every version of this gate before gh#435 accepted it.
rm -rf "$FIXTURES/underread"
mkreport "$FIXTURES/underread/unit/guid/coverage.cobertura.xml" 200 100 Alpha.cs:1:100:1
mkreport "$FIXTURES/underread/integration/guid/coverage.cobertura.xml" auto auto "${INTEGRATION_SOUND[@]}"
run_gate 85 "unit=$FIXTURES/underread/unit" "integration=$FIXTURES/underread/integration"
expect_red "parse-undercount" 1 \
  "::error::Parse mismatch in the 'unit' tier." \
  "parsed lines-valid=100, but its own <coverage> root declares lines-valid=200." \
  "the reported rate RISES while coverage falls"
# And it refuses BEFORE reporting a figure, so the flattering number is never printed as a verdict.
expect_not_said "parse-undercount/no-verdict-printed" "Line coverage"

# 7. The other attribute, on its own case -- otherwise deleting the `lines-covered` arm passes every case
#    above. Detail and root agree on how many lines exist and disagree on how many ran.
rm -rf "$FIXTURES/miscovered"
mkreport "$FIXTURES/miscovered/unit/guid/coverage.cobertura.xml" auto 150 "${UNIT_SOUND[@]}"
mkreport "$FIXTURES/miscovered/integration/guid/coverage.cobertura.xml" auto auto "${INTEGRATION_SOUND[@]}"
run_gate 85 "unit=$FIXTURES/miscovered/unit" "integration=$FIXTURES/miscovered/integration"
expect_red "parse-miscount-covered" 1 \
  "parsed lines-covered=100, but its own <coverage> root declares lines-covered=150."

# 8. A root that declares no total at all is a FAULT, not a check quietly skipped. Without this case the
#    `None` arm is dead code and the assertion can be defeated by deleting an attribute.
rm -rf "$FIXTURES/silentroot"
mkreport "$FIXTURES/silentroot/unit/guid/coverage.cobertura.xml" auto auto "${UNIT_SOUND[@]}"
set_root_attr "$FIXTURES/silentroot/unit/guid/coverage.cobertura.xml" lines-valid
mkreport "$FIXTURES/silentroot/integration/guid/coverage.cobertura.xml" auto auto "${INTEGRATION_SOUND[@]}"
run_gate 85 "unit=$FIXTURES/silentroot/unit" "integration=$FIXTURES/silentroot/integration"
expect_red "parse-root-silent" 1 \
  "its <coverage> root declares no lines-valid, so this parse is checked against nothing (parsed 200)."

# 9. BRANCH TOTALS ARE NOT ASSERTED, and this case is why. Real coverlet declares `branches-valid="1905"`
#    on a document whose `<line branch="True">` detail sums to 1885 -- twenty branch points counted in the
#    summary and absent from the detail. Extending the assertion to branches would make this gate unable to
#    PASS on genuine output, which is the same defect as one unable to fail. The fixture reproduces the
#    shape: a root claiming branch points the detail does not carry, on an otherwise sound tree.
make_tree branchroot sound
set_root_attr "$FIXTURES/branchroot/unit/guid/coverage.cobertura.xml" branches-valid 7
set_root_attr "$FIXTURES/branchroot/unit/guid/coverage.cobertura.xml" branches-covered 4
run_gate 85 "unit=$FIXTURES/branchroot/unit" "integration=$FIXTURES/branchroot/integration"
expect_green "branch-root-differs" \
  "Line coverage 90.0% (floor 85%)"

# 10. Two tiers that share no source file did not merge, they concatenated -- and a concatenation inflates
#     the denominator, turning the union back into an average wearing a union's name.
rm -rf "$FIXTURES/disjoint"
mkreport "$FIXTURES/disjoint/unit/guid/coverage.cobertura.xml" auto auto Alpha.cs:1:100:1
mkreport "$FIXTURES/disjoint/integration/guid/coverage.cobertura.xml" auto auto Gamma.cs:1:100:1
run_gate 85 "unit=$FIXTURES/disjoint/unit" "integration=$FIXTURES/disjoint/integration"
expect_red "disjoint-tiers" 1 \
  "::error::The tiers' reports share no source file"

# 11. The argument shape. Naming each tier on the command line is what makes a missing report an error FOR
#     THAT TIER BY NAME, so an argument list the gate cannot read is a usage error, not a run over nothing.
run_gate 85 "$FIXTURES/sound/unit"
expect_red "usage" 2 \
  "::error::usage: check-coverage-floor.py <tier>=<directory> [<tier>=<directory> ...]"

info ""
if [ "$failures" -ne 0 ]; then
  red "SELF-TEST FAILED — $failures of $cases assertions. check-coverage-floor.py is not proven able to fail."
  exit 1
fi
ok "check-coverage-floor.py proven able to fail and to pass — $cases assertions, all green."
