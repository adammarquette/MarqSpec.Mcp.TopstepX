#!/usr/bin/env bash
# check-migrations-additive-selftest.sh — require that check-migrations-additive.sh can still go red, and
# that it goes red for the RIGHT reason, at the right file and line.
#
#   scripts/check-migrations-additive-selftest.sh
#
# THE GATE IT GUARDS HAS TO BE ABLE TO FAIL (gh#98's rule, applied again by gh#108, gh#160, gh#293 and
# gh#438). A gate that cannot fail is worse than none, because it is trusted -- and this one is trusted
# about the one thing nothing else in the repository reads: whether a migration a pull request adds can be
# rolled back over. Its whole surface is line matching in shell, so one loosened needle, one `|| true` left
# after a debugging session, or a terminator regex that stops finding `Down()` turns it into a step that
# passes on anything while the run page stays green.
#
# NON-ZERO EXIT IS NOT SUFFICIENT AND IS NOT WHAT THIS ASSERTS. check-migrations-additive.sh also exits 1
# for "the Migrations directory is missing", for an unresolvable base ref and for a file it could not read,
# so a self-test satisfied by status alone would go green on a runner where the fixtures never got written
# -- reporting the gate as sound precisely when nothing had been checked. Every red case matches on the
# WHOLE FIELD a reader would act on: `<path>:<line>  <operation>`, never the operation name alone (gh#182 --
# a needle that is a PREFIX of the right answer is satisfied by an answer missing the rest of it, and the
# line number is exactly the part a shortened needle would stop pinning).
#
# AND NEITHER EXIT STATUS NOR A NEEDLE CAN SEE OUTPUT THAT SHOULD NOT BE THERE AT ALL (gh#239). Both are
# assertions about output that IS there. So expect_green SPLITS the streams rather than merging them with
# `2>&1` and requires a green run to write ZERO BYTES to stderr.
#
# MEASURED BEFORE IT WAS ASSERTED, not inherited from the sibling gates' notes. On this branch, streams
# split and the exit status captured directly:
#
#     the real repository, no argument      1 line stdout   0 B stderr   exit 0
#       ok  no migration added or changed against origin/develop (0 path(s) under …/Migrations in the diff).
#     the real repository, base f49dd5b~1   0 B stdout      red + explain on stderr   exit 1
#       DESTRUCTIVE  …/20260827071708_DropPriceLevels.cs:14  DropTable  -- no '// destructive-migration:' …
#
# The second is the measurement that matters and it is not a fixture: pointed at a base BEFORE the one
# genuinely destructive migration this repository has ever merged, the gate reads SIX migration files, finds
# it in real EF-generated code at the real line, and passes the other FIVE -- whose Up() bodies are additive
# and whose Down() bodies drop everything they added. It also reads the SIX generated partials beside them
# and reports nothing from any of them, which is the measurement that the round-three widening costs correct
# work nothing. `develop` is green because of the DIFF SCOPING, not because the detector is inert.
# (`develop` carries SEVEN migrations; six of them have a destructive Down(), `DropPriceLevels` being the
# one whose Down() re-creates its table. Counted, not remembered -- every earlier draft of this paragraph
# said five, and two more landed while the branch was open.)
#
# AND THE STDERR CLAIM AGREES WITH THE CODE. `grep -n '>&2' scripts/check-migrations-additive.sh` finds the
# writers on failing paths only (`die`, the `explain` heredoc, the blank separator); the green line goes to
# stdout through `ok`. The children the gate spawns are `git rev-parse --verify --quiet`, `git merge-base`,
# `git diff` and `mktemp` -- all four silent on success, re-derived by the run above rather than assumed.
# Re-derive that list if the gate grows a command; the assertion is unavailable for a gate whose children
# talk on success, which is why check-image-entrypoint.sh declined it.
#
# THE RED CASES ARE EXEMPT FROM THE EMPTY-STDERR ASSERTION and say so here: `die` and `explain` report
# through stderr, so there stderr is the answer rather than stray output.
#
# EVERY CASE RUNS THE REAL SCRIPT, copied into a disposable repository under `mktemp -d`. It is HERMETIC:
# no network, no token, no `gh`, and every fixture repository is built by `git init` with an explicit
# identity, so a developer's global git config cannot change a verdict.
#
# TWO CASES INVOKE THE GATE WITH NO ARGUMENT, against a `refs/remotes/origin/develop` the fixture writes by
# hand. That is the ONLY invocation ci.yml makes, and without them the default-base resolution would be a
# line no case executes -- the shape gh#435 names: a self-test whose fixtures supply the value the
# deployment actually gets wrong.
#
# DECISION LEDGER -- see the table at the bottom of this file (gh#178's remedy). Every decision the gate
# makes is listed beside the case that kills it, and the table is SPLIT: five MEASURED sweeps of mutants,
# and the rest listed as exercised-but-not-mutated. Adding a decision to the gate without adding a row is
# the same visible omission the ledger exists to catch. Read the split before trusting a row -- "a ledger is
# a claim too", and the ways one lies are a grade promising more than its evidence, a decision pinned by a
# fixture's incidental shape, and -- found by review on this very file -- **a row whose mechanism has moved
# underneath it, so the number is stale and the sentence describes code that is gone.** THREE MUTANTS have
# survived a sweep and each is named as such; the first sweep's rows 1 and 7 have now been re-run FOUR
# times, and row 1 has moved on every single one.
#
# RUNTIME, AND WHERE THE FIFTH SWEEP WAS RUN. Each case forks `git init`, a few commits and a shell, so what
# it costs is process creation and nothing else. On a Windows checkout that is brutal: **16m15s at 53 cases**
# on the machine gh#601 was written on (`real` 16m15s, `user` 0m48s, `sys` 5m52s), against the **5m35s at
# 49 cases** an earlier card measured on different hardware. One order of magnitude between two machines, so
# read either as *minutes, dominated by forking* rather than as a figure to plan against. THE SAME SUITE RUNS
# IN **11.8s** inside `mcr.microsoft.com/dotnet/sdk:10.0` -- bash 5.2.21 and git 2.43, which is the
# `ubuntu-24.04` runner's own family -- and that is where gh#601's seventeen sweep runs were taken. Not a
# shortcut around the Windows number: it is the CLOSER environment to the one CI uses, and it is what makes
# a full per-decision sweep affordable on a single card at all. **Sweep in the container; run the gate
# wherever you are.**

set -euo pipefail

red()  { printf '\033[31m%s\033[0m\n' "$*" >&2; }
ok()   { printf '\033[32m%s\033[0m\n' "$*"; }
info() { printf '%s\n' "$*"; }

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GATE="$REPO_ROOT/scripts/check-migrations-additive.sh"
if [ ! -f "$GATE" ]; then
  red "  MISSING  $GATE"
  red "NOTHING HAS BEEN CHECKED, and in particular the gate has not been proven able to fail."
  exit 1
fi

FIXTURES="$(mktemp -d)"
trap 'rm -rf "$FIXTURES"' EXIT

GATE_STDERR="$FIXTURES/gate.err"
MIG_REL="MarqSpec.Mcp.TopstepX.Data/Migrations"
failures=0
cases=0

git_ident=(-c user.name=fixture -c user.email=fixture@example.invalid -c commit.gpgsign=false
  -c core.autocrlf=false -c core.filemode=false)

# ---------------------------------------------------------------------------------------------------------
# Fixture construction. Every migration carries a DESTRUCTIVE Down() -- that is the shape EF scaffolds for
# an ordinary additive migration, so a gate that started reading Down() would redden the whole suite.
# ---------------------------------------------------------------------------------------------------------

emit_head() {  # $1 file  $2 class
  mkdir -p "$(dirname "$1")"
  cat > "$1" <<EOF
using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.Mcp.TopstepX.Data.Migrations
{
    /// <inheritdoc />
    public partial class $2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
EOF
}

emit_tail() {  # $1 file
  cat >> "$1" <<'EOF'
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "Added", table: "Bars");
            migrationBuilder.DropTable(name: "Thing");
        }
    }
}
EOF
}

emit_body() { cat >> "$1"; }  # body lines on stdin

# The needle is built from the file the fixture actually wrote, so a case cannot drift from its own line
# number and a hardcoded number cannot silently stop describing the fixture.
line_of() {  # $1 file  $2 fixed string
  local n
  n="$(grep -n -F -- "$2" "$1" | head -n1 | cut -d: -f1)"
  if [ -z "$n" ]; then
    red "  FIXTURE BROKEN  '$2' is not in $1 — its case would assert a line nothing is on."
    exit 1
  fi
  printf '%s' "$n"
}

init_repo() {  # $1 dir
  mkdir -p "$1/scripts"
  git -c init.defaultBranch=main "${git_ident[@]}" init -q "$1"
  cp "$GATE" "$1/scripts/check-migrations-additive.sh"
  mkdir -p "$1/$MIG_REL"
  # TWO baseline migrations, so the directory exists on the BASE commit and nothing in the diff is an
  # artefact of the directory itself appearing. Two rather than one because git tracks files, not
  # directories: the deletion case would otherwise take the whole directory with it and be answered by the
  # MISSING guard instead of by the `--diff-filter=ACMR` it exists to pin.
  local b
  for b in 20260101000000_Baseline 20260101000001_BaselineTwo; do
    emit_head "$1/$MIG_REL/$b.cs" "${b#*_}"
    emit_body "$1/$MIG_REL/$b.cs" <<'EOF'
            migrationBuilder.AddColumn<string>(name: "Added", table: "Bars", nullable: true);
EOF
    emit_tail "$1/$MIG_REL/$b.cs"
  done
  git -C "$1" "${git_ident[@]}" add -A
  git -C "$1" "${git_ident[@]}" commit -q -m 'fixture baseline'
  git -C "$1" "${git_ident[@]}" branch -f basebranch HEAD
  # The ref ci.yml's argument-free invocation resolves. Written by hand: no remote, no network.
  git -C "$1" update-ref refs/remotes/origin/develop HEAD
}

commit_case() {  # $1 dir
  git -C "$1" "${git_ident[@]}" add -A
  git -C "$1" "${git_ident[@]}" commit -q -m 'fixture case'
}

run_gate() {  # $1 dir  [base ref…]
  local dir="$1"; shift
  : > "$GATE_STDERR"
  bash "$dir/scripts/check-migrations-additive.sh" "$@" 2>"$GATE_STDERR"
}

gate_output() { printf '%s\n%s' "$1" "$(cat "$GATE_STDERR")"; }

expect_red() {  # $1 label  $2 dir  $3 needle  [base…]
  local label="$1" dir="$2" needle="$3" out status=0
  shift 3
  cases=$(( cases + 1 ))

  out="$(run_gate "$dir" "$@")" || status=$?
  out="$(gate_output "$out")"

  if [ "$status" -eq 0 ]; then
    red "SELF-TEST FAILED  $label"
    red "  The gate ACCEPTED a fixture it must reject. Everything it reports elsewhere is now worthless."
    info "$out"
    failures=$(( failures + 1 ))
    return 0
  fi
  if [[ "$out" != *"$needle"* ]]; then
    red "SELF-TEST FAILED  $label"
    red "  The gate rejected the fixture but never said '$needle', so it failed for some OTHER reason."
    red "  Exit 1 is also what a missing directory and an unresolvable base produce."
    info "$out"
    failures=$(( failures + 1 ))
    return 0
  fi
  ok "  red as required  $label  ($needle)"
}

# $4, when given, is a string the output must NOT contain — that is how "the FIRST DropColumn was marked,
# only the second is reported" is pinned as a fact about which line, rather than as a fact about failing.
expect_red_without() {  # $1 label  $2 dir  $3 needle  $4 forbidden  [base…]
  local label="$1" dir="$2" needle="$3" forbidden="$4" out status=0
  shift 4
  cases=$(( cases + 1 ))

  out="$(run_gate "$dir" "$@")" || status=$?
  out="$(gate_output "$out")"

  if [ "$status" -eq 0 ]; then
    red "SELF-TEST FAILED  $label"
    red "  The gate ACCEPTED a fixture it must reject."
    info "$out"
    failures=$(( failures + 1 ))
    return 0
  fi
  if [[ "$out" != *"$needle"* ]]; then
    red "SELF-TEST FAILED  $label"
    red "  The gate rejected the fixture but never said '$needle'."
    info "$out"
    failures=$(( failures + 1 ))
    return 0
  fi
  if [[ "$out" == *"$forbidden"* ]]; then
    red "SELF-TEST FAILED  $label"
    red "  The gate ALSO reported '$forbidden', which carries an acknowledgement. A marker that is ignored"
    red "  is a gate nobody can satisfy, and it will be deleted by the first person it wrongly stops."
    info "$out"
    failures=$(( failures + 1 ))
    return 0
  fi
  ok "  red as required  $label  ($needle, and not $forbidden)"
}

expect_green() {  # $1 label  $2 dir  $3 needle  [base…]
  local label="$1" dir="$2" needle="$3" out err status=0 stray
  shift 3
  cases=$(( cases + 1 ))

  out="$(run_gate "$dir" "$@")" || status=$?
  err="$(cat "$GATE_STDERR")"

  if [ "$status" -ne 0 ]; then
    red "SELF-TEST FAILED  $label"
    red "  The gate REJECTED a sound fixture (exit $status). It would fail correct pull requests, and the"
    red "  first person it wrongly stops will delete it."
    info "$(gate_output "$out")"
    failures=$(( failures + 1 ))
    return 0
  fi
  if [[ "$out" != *"$needle"* ]]; then
    red "SELF-TEST FAILED  $label"
    red "  The gate passed without saying '$needle'. Exit 0 having read NOTHING is the shape this suite"
    red "  exists to refuse — the green line carries the file count for exactly that reason."
    info "$(gate_output "$out")"
    failures=$(( failures + 1 ))
    return 0
  fi
  stray="$(wc -c < "$GATE_STDERR" | tr -d '[:space:]')"
  if [ "$stray" -ne 0 ]; then
    red "SELF-TEST FAILED  $label"
    red "  The gate went green, said '$needle', and still wrote $stray bytes to STDERR. Exit status and"
    red "  needles both survive a gate that is ALSO doing something else (gh#239)."
    info "$err"
    failures=$(( failures + 1 ))
    return 0
  fi
  ok "  green as required  $label  ($needle; stderr empty)"
}

info "check-migrations-additive.sh self-test — the real gate, run against fixtures with known faults."
info ""

# ---------------------------------------------------------------------------------------------------------
# GREEN: the shapes a correct pull request really contains.
# ---------------------------------------------------------------------------------------------------------

D="$FIXTURES/nothing-changed"; init_repo "$D"
expect_green "a branch that touches no migration at all" "$D" "no migration added or changed" basebranch

D="$FIXTURES/default-base-green"; init_repo "$D"
F="$D/$MIG_REL/20260201000000_AddThing.cs"; emit_head "$F" AddThing
emit_body "$F" <<'EOF'
            migrationBuilder.CreateTable(name: "Thing", columns: table => new { Id = table.Column<Guid>() });
            migrationBuilder.AddColumn<int>(name: "Rank", table: "Bars", nullable: true);
            migrationBuilder.CreateIndex(name: "IX_Thing_Id", table: "Thing", column: "Id");
EOF
emit_tail "$F"; commit_case "$D"
# NO BASE ARGUMENT — the invocation ci.yml makes, resolving refs/remotes/origin/develop.
expect_green "an additive migration, on the argument-free invocation CI uses" "$D" \
  "1 migration file(s) added or changed against origin/develop; 0 destructive operation(s)"

D="$FIXTURES/marked"; init_repo "$D"
F="$D/$MIG_REL/20260201000000_DropLegacy.cs"; emit_head "$F" DropLegacy
emit_body "$F" <<'EOF'
            // destructive-migration: not rollback-safe before v0.4.0 — Bars.Legacy has been dual-written
            // since v0.3.0 and no code path reads it.
            migrationBuilder.DropColumn(name: "Legacy", table: "Bars");
EOF
emit_tail "$F"; commit_case "$D"
expect_green "a DropColumn acknowledged by a TWO-LINE reason in the block above it" "$D" \
  "1 destructive operation(s), 1 acknowledged, 0 unacknowledged" basebranch

D="$FIXTURES/down-only"; init_repo "$D"
F="$D/$MIG_REL/20260201000000_AddOnly.cs"; emit_head "$F" AddOnly
emit_body "$F" <<'EOF'
            migrationBuilder.AddColumn<int>(name: "Rank", table: "Bars", nullable: true);
EOF
emit_tail "$F"; commit_case "$D"
expect_green "a Down() that drops everything Up() added — every real migration's shape" "$D" \
  "0 destructive operation(s)" basebranch

D="$FIXTURES/generated-only"; init_repo "$D"
cat > "$D/$MIG_REL/20260201000000_AddThing.Designer.cs" <<'EOF'
// <auto-generated />
// migrationBuilder.DropColumn(name: "Legacy", table: "Bars");
EOF
cat > "$D/$MIG_REL/TopstepXDbContextModelSnapshot.cs" <<'EOF'
// <auto-generated />
// migrationBuilder.DropTable(name: "Bars");
EOF
commit_case "$D"
# The generated mirrors are READ since round two's review (F3) and neither is required to declare an `Up()`.
# What still applies to them is the comment rule, and what they must not do is inflate the migration count --
# the green line's `N migration file(s)` is a count of MIGRATIONS, and these are reported separately.
expect_green "a Designer.cs and a model snapshot are read, and neither is required to declare an Up()" "$D" \
  "no migration added or changed" basebranch

D="$FIXTURES/commented-out"; init_repo "$D"
F="$D/$MIG_REL/20260201000000_Reconsidered.cs"; emit_head "$F" Reconsidered
emit_body "$F" <<'EOF'
            // We considered dropping it here and did not:
            // migrationBuilder.DropColumn(name: "Legacy", table: "Bars");
            migrationBuilder.AddColumn<int>(name: "Rank", table: "Bars", nullable: true);
EOF
emit_tail "$F"; commit_case "$D"
expect_green "a commented-out DropColumn, which drops nothing" "$D" "0 destructive operation(s)" basebranch

D="$FIXTURES/lone-drop-index"; init_repo "$D"
F="$D/$MIG_REL/20260201000000_Reindex.cs"; emit_head "$F" Reindex
emit_body "$F" <<'EOF'
            migrationBuilder.DropIndex(name: "IX_Bars_Old", table: "Bars");
            migrationBuilder.CreateIndex(name: "IX_Bars_New", table: "Bars", column: "Bucket");
EOF
emit_tail "$F"; commit_case "$D"
expect_green "a DropIndex with no DropColumn beside it — recreatable, so not flagged" "$D" \
  "0 destructive operation(s)" basebranch

D="$FIXTURES/deleted"; init_repo "$D"
git -C "$D" "${git_ident[@]}" rm -q "$MIG_REL/20260101000000_Baseline.cs"
commit_case "$D"
expect_green "a DELETED migration — filtered out, and not read as a file that vanished" "$D" \
  "no migration added or changed" basebranch

D="$FIXTURES/marked-sql"; init_repo "$D"
F="$D/$MIG_REL/20260201000000_SqlMarked.cs"; emit_head "$F" SqlMarked
emit_body "$F" <<'EOF'
            // destructive-migration: not rollback-safe before v0.5.0 — the index is rebuilt by the same
            // release and nothing plans against it.
            migrationBuilder.Sql(@"
                DROP INDEX IF EXISTS ""IX_Bars_Legacy"";
            ");
EOF
emit_tail "$F"; commit_case "$D"
expect_green "a multi-line Sql() whose DROP is on a later line, marked above the CALL" "$D" \
  "1 destructive operation(s), 1 acknowledged" basebranch

D="$FIXTURES/env-base"; init_repo "$D"
F="$D/$MIG_REL/20260201000000_AddThing.cs"; emit_head "$F" AddThing
emit_body "$F" <<'EOF'
            migrationBuilder.AddColumn<int>(name: "Rank", table: "Bars", nullable: true);
EOF
emit_tail "$F"; commit_case "$D"
# Exported rather than run in a subshell: expect_green counts into `cases` and `failures`, and a subshell
# would drop both — a case that cannot report its own failure is the shape this whole file refuses.
export MIGRATION_GATE_BASE=basebranch
expect_green "MIGRATION_GATE_BASE naming the base, with no argument" "$D" "against basebranch"
unset MIGRATION_GATE_BASE

# ---------------------------------------------------------------------------------------------------------
# RED: one property per fixture. A fixture that trips two rules pins neither (gh#438).
# ---------------------------------------------------------------------------------------------------------

D="$FIXTURES/bare-dropcolumn"; init_repo "$D"
F="$D/$MIG_REL/20260201000000_DropLegacy.cs"; emit_head "$F" DropLegacy
emit_body "$F" <<'EOF'
            migrationBuilder.DropColumn(name: "Legacy", table: "Bars");
EOF
emit_tail "$F"; commit_case "$D"
N="$(line_of "$F" 'DropColumn(name: "Legacy"')"
# NO BASE ARGUMENT — the red half of the pair that pins the default-base resolution.
expect_red "an unacknowledged DropColumn, on the argument-free invocation CI uses" "$D" \
  "$MIG_REL/20260201000000_DropLegacy.cs:$N  DropColumn"

D="$FIXTURES/untracked"; init_repo "$D"
F="$D/$MIG_REL/20260201000000_ScaffoldedNotAdded.cs"; emit_head "$F" ScaffoldedNotAdded
emit_body "$F" <<'EOF'
            migrationBuilder.DropColumn(name: "Legacy", table: "Bars");
EOF
emit_tail "$F"
# DELIBERATELY NOT COMMITTED — `dotnet ef migrations add` leaves the file untracked, and `git diff` cannot
# see it. A green run on the very file the author is about to commit is the confident wrong answer.
N="$(line_of "$F" 'DropColumn(name: "Legacy"')"
expect_red "a migration scaffolded and never added — invisible to git diff alone" "$D" \
  "$MIG_REL/20260201000000_ScaffoldedNotAdded.cs:$N  DropColumn" basebranch

D="$FIXTURES/bare-droptable"; init_repo "$D"
F="$D/$MIG_REL/20260201000000_DropThing.cs"; emit_head "$F" DropThing
emit_body "$F" <<'EOF'
            migrationBuilder.DropTable(name: "Thing");
EOF
emit_tail "$F"; commit_case "$D"
N="$(line_of "$F" 'DropTable(name: "Thing")')"
expect_red "an unacknowledged DropTable" "$D" \
  "$MIG_REL/20260201000000_DropThing.cs:$N  DropTable" basebranch

D="$FIXTURES/bare-renamecolumn"; init_repo "$D"
F="$D/$MIG_REL/20260201000000_RenameIt.cs"; emit_head "$F" RenameIt
emit_body "$F" <<'EOF'
            migrationBuilder.RenameColumn(name: "Old", table: "Bars", newName: "New");
EOF
emit_tail "$F"; commit_case "$D"
N="$(line_of "$F" 'RenameColumn(name: "Old"')"
expect_red "an unacknowledged RenameColumn" "$D" \
  "$MIG_REL/20260201000000_RenameIt.cs:$N  RenameColumn" basebranch

D="$FIXTURES/bare-renametable"; init_repo "$D"
F="$D/$MIG_REL/20260201000000_RenameTbl.cs"; emit_head "$F" RenameTbl
emit_body "$F" <<'EOF'
            migrationBuilder.RenameTable(name: "Thing", newName: "Things");
EOF
emit_tail "$F"; commit_case "$D"
N="$(line_of "$F" 'RenameTable(name: "Thing"')"
expect_red "an unacknowledged RenameTable" "$D" \
  "$MIG_REL/20260201000000_RenameTbl.cs:$N  RenameTable" basebranch

D="$FIXTURES/bare-altercolumn"; init_repo "$D"
F="$D/$MIG_REL/20260201000000_Narrow.cs"; emit_head "$F" Narrow
emit_body "$F" <<'EOF'
            migrationBuilder.AlterColumn<string>(name: "Instrument", table: "Bars", maxLength: 16);
EOF
emit_tail "$F"; commit_case "$D"
N="$(line_of "$F" 'AlterColumn<string>(name: "Instrument"')"
expect_red "an unacknowledged AlterColumn — a narrowing is not distinguishable statically" "$D" \
  "$MIG_REL/20260201000000_Narrow.cs:$N  AlterColumn" basebranch

D="$FIXTURES/sql-drop"; init_repo "$D"
F="$D/$MIG_REL/20260201000000_SqlDrop.cs"; emit_head "$F" SqlDrop
emit_body "$F" <<'EOF'
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_Bars_Legacy\";");
EOF
emit_tail "$F"; commit_case "$D"
N="$(line_of "$F" 'Sql("DROP INDEX')"
expect_red "a single-line Sql() carrying DROP" "$D" \
  "$MIG_REL/20260201000000_SqlDrop.cs:$N  raw SQL: DROP" basebranch

D="$FIXTURES/sql-drop-multiline"; init_repo "$D"
F="$D/$MIG_REL/20260201000000_SqlDropML.cs"; emit_head "$F" SqlDropML
emit_body "$F" <<'EOF'
            migrationBuilder.Sql(@"
                -- rebuild the projection
                DROP TABLE ""IndicatorValues"";
            ");
EOF
emit_tail "$F"; commit_case "$D"
N="$(line_of "$F" 'migrationBuilder.Sql(@"')"
expect_red "a multi-line Sql() whose DROP is three lines below the call" "$D" \
  "$MIG_REL/20260201000000_SqlDropML.cs:$N  raw SQL: DROP" basebranch

D="$FIXTURES/sql-truncate"; init_repo "$D"
F="$D/$MIG_REL/20260201000000_SqlTrunc.cs"; emit_head "$F" SqlTrunc
emit_body "$F" <<'EOF'
            migrationBuilder.Sql("truncate table \"TradeTape\";");
EOF
emit_tail "$F"; commit_case "$D"
N="$(line_of "$F" 'Sql("truncate table')"
expect_red "a LOWERCASE truncate — SQL keywords are matched case-insensitively" "$D" \
  "$MIG_REL/20260201000000_SqlTrunc.cs:$N  raw SQL: TRUNCATE" basebranch

D="$FIXTURES/sql-alter-type"; init_repo "$D"
F="$D/$MIG_REL/20260201000000_SqlAlter.cs"; emit_head "$F" SqlAlter
emit_body "$F" <<'EOF'
            migrationBuilder.Sql("ALTER TABLE \"Bars\" ALTER COLUMN \"Volume\" TYPE integer;");
EOF
emit_tail "$F"; commit_case "$D"
N="$(line_of "$F" 'Sql("ALTER TABLE')"
expect_red "a raw ALTER TABLE … TYPE, which narrows without naming a Drop" "$D" \
  "$MIG_REL/20260201000000_SqlAlter.cs:$N  raw SQL: ALTER TABLE … TYPE" basebranch

D="$FIXTURES/blanket-marker"; init_repo "$D"
F="$D/$MIG_REL/20260201000000_Blanket.cs"; emit_head "$F" Blanket
emit_body "$F" <<'EOF'
            // destructive-migration: everything below is fine, trust me.
            migrationBuilder.AddColumn<int>(name: "Rank", table: "Bars", nullable: true);
            migrationBuilder.DropColumn(name: "Legacy", table: "Bars");
EOF
emit_tail "$F"; commit_case "$D"
N="$(line_of "$F" 'DropColumn(name: "Legacy"')"
expect_red "a BLANKET marker at the top of the body, which must silence only the next line" "$D" \
  "$MIG_REL/20260201000000_Blanket.cs:$N  DropColumn" basebranch

D="$FIXTURES/empty-reason"; init_repo "$D"
F="$D/$MIG_REL/20260201000000_NoReason.cs"; emit_head "$F" NoReason
emit_body "$F" <<'EOF'
            // destructive-migration:
            migrationBuilder.DropColumn(name: "Legacy", table: "Bars");
EOF
emit_tail "$F"; commit_case "$D"
N="$(line_of "$F" 'DropColumn(name: "Legacy"')"
expect_red "a marker with NO reason — a rubber stamp is not an acknowledgement" "$D" \
  "$MIG_REL/20260201000000_NoReason.cs:$N  DropColumn" basebranch

D="$FIXTURES/marker-gap"; init_repo "$D"
F="$D/$MIG_REL/20260201000000_Gap.cs"; emit_head "$F" Gap
emit_body "$F" <<'EOF'
            // destructive-migration: not rollback-safe before v0.4.0 — nothing reads it.

            migrationBuilder.DropColumn(name: "Legacy", table: "Bars");
EOF
emit_tail "$F"; commit_case "$D"
N="$(line_of "$F" 'DropColumn(name: "Legacy"')"
expect_red "a marker separated from the operation by a blank line" "$D" \
  "$MIG_REL/20260201000000_Gap.cs:$N  DropColumn" basebranch

D="$FIXTURES/second-unmarked"; init_repo "$D"
F="$D/$MIG_REL/20260201000000_Two.cs"; emit_head "$F" Two
emit_body "$F" <<'EOF'
            // destructive-migration: not rollback-safe before v0.4.0 — Legacy was dual-written.
            migrationBuilder.DropColumn(name: "Legacy", table: "Bars");
            migrationBuilder.DropColumn(name: "Scratch", table: "Bars");
EOF
emit_tail "$F"; commit_case "$D"
N_BAD="$(line_of "$F" 'DropColumn(name: "Scratch"')"
N_OK="$(line_of "$F" 'DropColumn(name: "Legacy"')"
expect_red_without "two DropColumns, only the first acknowledged" "$D" \
  "$MIG_REL/20260201000000_Two.cs:$N_BAD  DropColumn" \
  "$MIG_REL/20260201000000_Two.cs:$N_OK  DropColumn" basebranch

D="$FIXTURES/paired-drop-index"; init_repo "$D"
F="$D/$MIG_REL/20260201000000_Paired.cs"; emit_head "$F" Paired
emit_body "$F" <<'EOF'
            migrationBuilder.DropIndex(name: "IX_Bars_Legacy", table: "Bars");
            // destructive-migration: not rollback-safe before v0.4.0 — Legacy was dual-written.
            migrationBuilder.DropColumn(name: "Legacy", table: "Bars");
EOF
emit_tail "$F"; commit_case "$D"
N="$(line_of "$F" 'DropIndex(name: "IX_Bars_Legacy"')"
expect_red "a DropIndex beside an acknowledged DropColumn — half an acknowledgement" "$D" \
  "$MIG_REL/20260201000000_Paired.cs:$N  DropIndex" basebranch

# ---------------------------------------------------------------------------------------------------------
# The four shapes review found, each of which COMPILED and passed `dotnet format --verify-no-changes` while
# the gate said green. A gate an ordinary formatting choice walks past is the failure this card exists to
# prevent, so each one is a case rather than a fix and a note.
# ---------------------------------------------------------------------------------------------------------

D="$FIXTURES/two-ops-one-line"; init_repo "$D"
F="$D/$MIG_REL/20260201000000_TwoOnOneLine.cs"; emit_head "$F" TwoOnOneLine
emit_body "$F" <<'EOF'
            // destructive-migration: only Legacy is dual-written, v0.4.0.
            migrationBuilder.DropColumn(name: "Legacy", table: "Bars"); migrationBuilder.DropTable(name: "Thing");
EOF
emit_tail "$F"; commit_case "$D"
N="$(line_of "$F" 'DropColumn(name: "Legacy"')"
# The marker's reason names Legacy and nothing else; it must not reach across to a DropTable nobody wrote a
# sentence about. "One marker acknowledges one operation" is stated in five documents and was false here.
expect_red "TWO operations on one line under one marker" "$D" \
  "$MIG_REL/20260201000000_TwoOnOneLine.cs:$N  2 destructive operations on one line (DropTable, DropColumn)" \
  basebranch

D="$FIXTURES/twin-ops-one-line"; init_repo "$D"
F="$D/$MIG_REL/20260201000000_Twins.cs"; emit_head "$F" Twins
emit_body "$F" <<'EOF'
            // destructive-migration: only Legacy is dual-written, v0.4.0.
            migrationBuilder.DropColumn(name: "Legacy", table: "Bars"); migrationBuilder.DropColumn(name: "Scratch", table: "Bars");
EOF
emit_tail "$F"; commit_case "$D"
N="$(line_of "$F" 'DropColumn(name: "Legacy"')"
# The COUNT is the assertion here, not the refusal. A boolean `=~` reported these two as ONE, so the green
# line's evidence undercounted in the quiet direction; `(DropColumn, DropColumn)` is what proves it counts
# occurrences rather than distinct operation names.
expect_red "two IDENTICAL operations on one line, which a boolean match counts once" "$D" \
  "$MIG_REL/20260201000000_Twins.cs:$N  2 destructive operations on one line (DropColumn, DropColumn)" \
  basebranch

D="$FIXTURES/wrapped-paren"; init_repo "$D"
F="$D/$MIG_REL/20260201000000_Wrapped.cs"; emit_head "$F" Wrapped
emit_body "$F" <<'EOF'
            migrationBuilder.DropColumn
                (name: "Legacy", table: "Bars");
EOF
emit_tail "$F"; commit_case "$D"
N="$(line_of "$F" 'migrationBuilder.DropColumn')"
# Legal C#, and `dotnet format --verify-no-changes` reports NOTHING for it -- so unlike
# `migrationBuilder . DropColumn (`, which Format catches with whitespace errors, this one had no backstop.
expect_red "an operation whose ( sits on the NEXT line, which dotnet format does not object to" "$D" \
  "$MIG_REL/20260201000000_Wrapped.cs:$N  DropColumn" basebranch

D="$FIXTURES/sibling-member"; init_repo "$D"
F="$D/$MIG_REL/20260201000000_Helper.cs"
mkdir -p "$(dirname "$F")"
cat > "$F" <<'EOF'
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.Mcp.TopstepX.Data.Migrations
{
    /// <inheritdoc />
    public partial class Helper : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            RetireLegacy(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(name: "Legacy", table: "Bars", nullable: true);
        }

        private static void RetireLegacy(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "PriceLevels");
        }
    }
}
EOF
commit_case "$D"
N="$(line_of "$F" 'DropTable(name: "PriceLevels")')"
# The helper sits AFTER Down(), so this pins the END of the Down() body as well as the start: if the member
# boundary stopped being found, Down() would run to EOF and swallow the helper again.
expect_red "an operation in a SIBLING member, reached from Up() and written outside it" "$D" \
  "$MIG_REL/20260201000000_Helper.cs:$N  DropTable" basebranch

D="$FIXTURES/lone-semicolon"; init_repo "$D"
F="$D/$MIG_REL/20260201000000_LoneSemi.cs"; emit_head "$F" LoneSemi
emit_body "$F" <<'EOF'
            migrationBuilder.Sql(@"
                UPDATE ""Bars"" SET ""Legacy2"" = NULL
            ")
            ;
            migrationBuilder.DropColumn(name: "Legacy2", table: "Bars");
EOF
emit_tail "$F"; commit_case "$D"
N="$(line_of "$F" 'DropColumn(name: "Legacy2"')"
# The Sql( region used to close on the FIRST `);` it saw. With the `;` on a line of its own that was the
# DropColumn's own closer, so the region swallowed the call and pass 3 skipped it as region-interior.
expect_red "an operation swallowed by a Sql( region whose ; sits on its own line" "$D" \
  "$MIG_REL/20260201000000_LoneSemi.cs:$N  DropColumn" basebranch

# gh#529 review N2. These two rode the same loop as the five operations above and appeared in this file only
# as ledger PROSE, so removing them from OPS reddened nothing -- an unpinned entry in a list is how the next
# edit quietly shortens the list.
D="$FIXTURES/bare-dropschema"; init_repo "$D"
F="$D/$MIG_REL/20260201000000_DropSch.cs"; emit_head "$F" DropSch
emit_body "$F" <<'EOF'
            migrationBuilder.DropSchema(name: "archive");
EOF
emit_tail "$F"; commit_case "$D"
N="$(line_of "$F" 'DropSchema(name: "archive")')"
expect_red "an unacknowledged DropSchema" "$D" \
  "$MIG_REL/20260201000000_DropSch.cs:$N  DropSchema" basebranch

D="$FIXTURES/bare-dropsequence"; init_repo "$D"
F="$D/$MIG_REL/20260201000000_DropSeq.cs"; emit_head "$F" DropSeq
emit_body "$F" <<'EOF'
            migrationBuilder.DropSequence(name: "BarIds");
EOF
emit_tail "$F"; commit_case "$D"
N="$(line_of "$F" 'DropSequence(name: "BarIds")')"
expect_red "an unacknowledged DropSequence" "$D" \
  "$MIG_REL/20260201000000_DropSeq.cs:$N  DropSequence" basebranch

# ---------------------------------------------------------------------------------------------------------
# ROUND TWO's shapes. The first review widened WHAT IS SCANNED; the hole it opened was in WHAT DECIDES THE
# BOUNDARY of that scan -- `DOWN_RE` was matched against raw text, so a `//` comment or a string literal
# naming the method picked the excluded span. Every case below is a boundary, a mirror file or a call shape,
# and each one compiles and passes `dotnet format --verify-no-changes`.
# ---------------------------------------------------------------------------------------------------------

D="$FIXTURES/down-decoy-comment"; init_repo "$D"
F="$D/$MIG_REL/20260201000000_Decoy.cs"; emit_head "$F" Decoy
emit_body "$F" <<'EOF'
            // Irreversible; there is no void Down(MigrationBuilder migrationBuilder) worth writing.
            migrationBuilder.DropColumn(name: "Legacy", table: "Bars");
EOF
emit_tail "$F"; commit_case "$D"
N="$(line_of "$F" 'DropColumn(name: "Legacy"')"
# The decoy sets down_start at the COMMENT and down_end at the real declaration, so the whole rest of Up()
# falls out of every pass -- and, because the fixture's Down() is EF's ordinary destructive one, the gate
# then reports the Down() body's drop instead. It fails in both directions from one root.
expect_red "a COMMENT inside Up() naming void Down(MigrationBuilder — the boundary decoy" "$D" \
  "$MIG_REL/20260201000000_Decoy.cs:$N  DropColumn" basebranch

D="$FIXTURES/down-decoy-literal"; init_repo "$D"
F="$D/$MIG_REL/20260201000000_DecoyLit.cs"; emit_head "$F" DecoyLit
emit_body "$F" <<'EOF'
            migrationBuilder.Sql("-- void Down(MigrationBuilder migrationBuilder)");
            migrationBuilder.DropColumn(name: "Legacy", table: "Bars");
EOF
emit_tail "$F"; commit_case "$D"
N="$(line_of "$F" 'DropColumn(name: "Legacy"')"
# The same decoy inside a STRING LITERAL. TWO independent conditions refuse this one -- the line is a Sql(
# region's interior AND it declares no member -- so it pins the pair and neither half; the two cases below
# separate them, which is what a conjunction owes its ledger row.
expect_red "the same decoy inside a Sql( string literal" "$D" \
  "$MIG_REL/20260201000000_DecoyLit.cs:$N  DropColumn" basebranch

D="$FIXTURES/down-decoy-plain-string"; init_repo "$D"
F="$D/$MIG_REL/20260201000000_DecoyStr.cs"; emit_head "$F" DecoyStr
emit_body "$F" <<'EOF'
            System.Console.WriteLine("protected override void Down(MigrationBuilder migrationBuilder)");
            migrationBuilder.DropColumn(name: "Legacy", table: "Bars");
EOF
emit_tail "$F"; commit_case "$D"
N="$(line_of "$F" 'DropColumn(name: "Legacy"')"
# A decoy in a string that is NOT a Sql( region, so the region guard cannot see it -- and it quotes the
# WHOLE signature, `override` included, so the override test cannot see it either. Only the requirement
# that a boundary line DECLARE A MEMBER refuses this one, which is what pins that requirement alone.
expect_red "the decoy in a plain string literal, outside any Sql( region" "$D" \
  "$MIG_REL/20260201000000_DecoyStr.cs:$N  DropColumn" basebranch

D="$FIXTURES/down-decoy-verbatim"; init_repo "$D"
F="$D/$MIG_REL/20260201000000_DecoyVerb.cs"; emit_head "$F" DecoyVerb
emit_body "$F" <<'EOF'
            migrationBuilder.Sql(@"
        protected override void Down(MigrationBuilder migrationBuilder)
            ");
            migrationBuilder.DropColumn(name: "Legacy", table: "Bars");
EOF
emit_tail "$F"; commit_case "$D"
N="$(line_of "$F" 'DropColumn(name: "Legacy"')"
# And the mirror of it: a decoy written INSIDE a verbatim SQL literal, indented and modified so that it
# passes the member-declaration test on its face. Only the region guard refuses this one, so it pins that
# guard alone. The `//` test in `declares()` is pinned by neither and is SUBSUMED by the member test today
# -- a `//` line can never carry a leading modifier -- kept because the two would have to be wrong together.
expect_red "the decoy written to LOOK like a declaration, inside a verbatim Sql( literal" "$D" \
  "$MIG_REL/20260201000000_DecoyVerb.cs:$N  DropColumn" basebranch

D="$FIXTURES/wrapped-down"; init_repo "$D"
F="$D/$MIG_REL/20260201000000_WrappedDown.cs"
mkdir -p "$(dirname "$F")"
cat > "$F" <<'EOF'
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.Mcp.TopstepX.Data.Migrations
{
    /// <inheritdoc />
    public partial class WrappedDown : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(name: "Rank", table: "Bars", nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(
            MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "Rank", table: "Bars");
        }
    }
}
EOF
commit_case "$D"
# A GREEN case, and the only one here that pins a FALSE POSITIVE. A wrapped signature left `down_start` at 0,
# so the whole file was scanned and an ordinary additive migration was reddened by its own Down(). A gate that
# reddens correct work is deleted by the first person it wrongly stops.
expect_green "a WRAPPED Down( signature — correct additive work must not be reddened by its own Down()" "$D" \
  "0 destructive operation(s)" basebranch

# THE THIRD BOUNDARY-BLIND PATTERN, found by auditing rather than by review. `MEMBER_RE` also decides where
# the Down() body ENDS, and it wanted an ACCESS MODIFIER -- so a sibling helper written `void Helper(…)` or
# `static void Helper(…)`, both legal C# that `dotnet format` will not touch, was never reached: Down() ran
# to end of file and swallowed it. Quiet, and one keyword away from the shape review already found. Two
# rules close it, a closing brace in the declaration's own column and the next member declaration, and there
# is a fixture for EACH because either alone catches the obvious shape.

D="$FIXTURES/no-modifier-sibling"; init_repo "$D"
F="$D/$MIG_REL/20260201000000_NoModifier.cs"
mkdir -p "$(dirname "$F")"
cat > "$F" <<'EOF'
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.Mcp.TopstepX.Data.Migrations
{
    /// <inheritdoc />
    public partial class NoModifier : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            RetireLegacy(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(name: "Legacy", table: "Bars", nullable: true);
        }

        void RetireLegacy(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "PriceLevels");
        }
    }
}
EOF
commit_case "$D"
N="$(line_of "$F" 'DropTable(name: "PriceLevels")')"
# NO modifier keyword at all, so no widening of MEMBER_RE can reach it. Only the CLOSING BRACE rule ends
# Down() here, which is what pins that rule alone.
expect_red "a sibling helper with no modifier at all — only the closing-brace rule ends Down() there" "$D" \
  "$MIG_REL/20260201000000_NoModifier.cs:$N  DropTable" basebranch

D="$FIXTURES/inline-down-sibling"; init_repo "$D"
F="$D/$MIG_REL/20260201000000_InlineDown.cs"
mkdir -p "$(dirname "$F")"
cat > "$F" <<'EOF'
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.Mcp.TopstepX.Data.Migrations
{
    /// <inheritdoc />
    public partial class InlineDown : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            RetireLegacy(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) { }

        static void RetireLegacy(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "PriceLevels");
        }
    }
}
EOF
commit_case "$D"
N="$(line_of "$F" 'DropTable(name: "PriceLevels")')"
# And the mirror: a Down() whose body is `{ }` on the declaration line, so the closing brace in that column
# is the HELPER's, below the drop. Only `MEMBER_RE` knowing `static` is a modifier ends Down() here, which
# pins the widening alone.
expect_red "an inline Down() { } above a static sibling — only the member rule ends Down() there" "$D" \
  "$MIG_REL/20260201000000_InlineDown.cs:$N  DropTable" basebranch

# ROUND THREE's two shapes, and they are the SAME finding as round two arriving through the fixes for it:
# each of round two's boundary widenings opened a defeat of its own. `declares()` asked whether a line is a
# comment, whether it is inside a Sql( region, and whether it carries a modifier -- and NONE of those asks
# what the line DECLARES. A modifier keyword is evidence of a declaration; it is not evidence of WHICH
# declaration, nor that the declaration is a member rather than a local. Both compile and both pass
# `dotnet format --verify-no-changes`, measured in the real Migrations/ directory rather than in a fixture.

D="$FIXTURES/local-function-down"; init_repo "$D"
F="$D/$MIG_REL/20260201000000_LocalFn.cs"
mkdir -p "$(dirname "$F")"
cat > "$F" <<'EOF'
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.Mcp.TopstepX.Data.Migrations
{
    /// <inheritdoc />
    public partial class LocalFn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            static void Down(MigrationBuilder b) { }
            Down(migrationBuilder);
            migrationBuilder.DropColumn(name: "Legacy", table: "Bars");
            migrationBuilder.DropTable(name: "PriceLevels");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
EOF
commit_case "$D"
N="$(line_of "$F" 'DropTable(name: "PriceLevels")')"
# `static` was added to MEMBER_RE to close the audit find, and `static` is also the modifier a C# LOCAL
# FUNCTION may carry -- so a local function named Down inside Up() satisfied all three conditions, took
# `down_start`, and the real declaration below took `down_end`. Everything between was excluded. (An
# EXPRESSION-bodied one is caught, because `=> b.Sql(…)` puts the line in a region; it is the block-bodied
# form that got through, which is the region guard doing real work rather than the shape being exotic.)
expect_red "a STATIC LOCAL FUNCTION named Down inside Up(), which a modifier test cannot tell from a member" \
  "$D" "$MIG_REL/20260201000000_LocalFn.cs:$N  DropTable" basebranch

D="$FIXTURES/decoy-overload-down"; init_repo "$D"
F="$D/$MIG_REL/20260201000000_Overload.cs"
mkdir -p "$(dirname "$F")"
cat > "$F" <<'EOF'
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.Mcp.TopstepX.Data.Migrations
{
    /// <inheritdoc />
    public partial class Overload : Migration
    {
        private static void Down(
            int unused)
        { }

        void RetireLegacy(MigrationBuilder b)
        {
            b.DropColumn(name: "Legacy", table: "Bars");
            b.DropTable(name: "PriceLevels");
        }

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            RetireLegacy(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
EOF
commit_case "$D"
N="$(line_of "$F" 'b.DropTable(name: "PriceLevels")')"
# And the mirror, through the OTHER widening: the end-of-line arm added so a WRAPPED `Down(` signature would
# stop reddening correct work also matches an ordinary OVERLOAD whose parameter is not a MigrationBuilder at
# all, and an overload wins the first-match race against the real declaration. `down_end` then lands on the
# closing brace of a helper written with NO modifier at all -- which the widened MEMBER_RE still misses,
# because there is no list entry for *nothing* -- so the helper's body falls inside the excluded span.
expect_red "a decoy Down( OVERLOAD above a no-modifier sibling, which wins the first-match race" \
  "$D" "$MIG_REL/20260201000000_Overload.cs:$N  DropTable" basebranch

# ROUND FOUR's two shapes (gh#601), and they are round three's finding once more: the condition added for it
# was itself a TEXT property. `OVERRIDE_RE` was matched UNANCHORED against the whole line, so the WORD
# `override` anywhere on it -- a trailing comment, a string -- satisfied the one condition that was supposed
# to test what the line DECLARES. And a genuine override is not necessarily the MIGRATION's: a nested type
# has members of its own. Both are `down_start` shapes, which is what the header's asymmetry predicts. All
# four fixtures below compile and pass `dotnet format --verify-no-changes`.

D="$FIXTURES/commented-local-fn"; init_repo "$D"
F="$D/$MIG_REL/20260201000000_CommentedLocalFn.cs"
mkdir -p "$(dirname "$F")"
cat > "$F" <<'EOF'
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.Mcp.TopstepX.Data.Migrations
{
    /// <inheritdoc />
    public partial class CommentedLocalFn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            static void Down(MigrationBuilder b) { } // no override needed on a local shim
            Down(migrationBuilder);
            migrationBuilder.DropColumn(name: "Legacy", table: "Bars");
            migrationBuilder.DropTable(name: "PriceLevels");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
EOF
commit_case "$D"
N="$(line_of "$F" 'DropTable(name: "PriceLevels")')"
# THE SHAPE gh#601 IS NAMED FOR: round three's local function, with five words of ordinary comment on it. An
# unanchored `override` needle reads the comment, so the condition that closed A1 was satisfied by prose.
# TWO conditions refuse it now -- the indent it sits at is not the class's, and its `override` is not in the
# declaration's own modifier run -- so it PINS THE PAIR AND NEITHER HALF, exactly like the Sql( decoy above.
# The two cases below separate them, which is what a conjunction owes its ledger row.
expect_red "a local function named Down whose trailing COMMENT carries the word override" "$D" \
  "$MIG_REL/20260201000000_CommentedLocalFn.cs:$N  DropTable" basebranch

D="$FIXTURES/commented-sibling"; init_repo "$D"
F="$D/$MIG_REL/20260201000000_CommentedSibling.cs"
mkdir -p "$(dirname "$F")"
cat > "$F" <<'EOF'
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.Mcp.TopstepX.Data.Migrations
{
    /// <inheritdoc />
    public partial class CommentedSibling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            RetireLegacy(migrationBuilder);
        }

        static void Down(MigrationBuilder b, bool force) { } // no override needed on this shim

        void RetireLegacy(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "PriceLevels");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
EOF
commit_case "$D"
N="$(line_of "$F" 'DropTable(name: "PriceLevels")')"
# The same forgery at the CLASS'S OWN MEMBER INDENT, on an ordinary overload. The indent test cannot see it
# and there is no `override` in the modifier run, so ONLY the requirement that the override sit in the
# declaration itself refuses it -- which is what pins the anchoring alone.
expect_red "a sibling Down( overload whose trailing COMMENT carries the word override" "$D" \
  "$MIG_REL/20260201000000_CommentedSibling.cs:$N  DropTable" basebranch

D="$FIXTURES/commented-up"; init_repo "$D"
F="$D/$MIG_REL/20260201000000_CommentedUp.cs"
mkdir -p "$(dirname "$F")"
cat > "$F" <<'EOF'
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.Mcp.TopstepX.Data.Migrations
{
    /// <inheritdoc />
    public partial class CommentedUp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) // no void Down(MigrationBuilder) here
        {
            migrationBuilder.DropTable(name: "PriceLevels");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
EOF
commit_case "$D"
N="$(line_of "$F" 'DropTable(name: "PriceLevels")')"
# THE MIRROR OF IT, and the reason the fix is one CONTIGUOUS pattern rather than a stricter `override` needle
# beside a signature needle: here the `override` is real, the indent is the class's, and the SIGNATURE is the
# part written in the comment. `Up()`'s own declaration then takes `down_start`, and Up()'s whole body is the
# excluded span. Only the requirement that the modifier run and the signature be one uninterrupted
# declaration refuses this, so it pins that alone.
expect_red "a real Up() declaration whose trailing COMMENT names the Down( signature" "$D" \
  "$MIG_REL/20260201000000_CommentedUp.cs:$N  DropTable" basebranch

D="$FIXTURES/nested-override"; init_repo "$D"
F="$D/$MIG_REL/20260201000000_NestedOverride.cs"
mkdir -p "$(dirname "$F")"
cat > "$F" <<'EOF'
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.Mcp.TopstepX.Data.Migrations
{
    /// <inheritdoc />
    public partial class NestedOverride : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            RetireLegacy(migrationBuilder);
        }

        private class Shim
        {
            public virtual void Down(MigrationBuilder b) { }
        }

        private sealed class Fake : Shim
        {
            public override void Down(MigrationBuilder b) { }
        }

        void RetireLegacy(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "PriceLevels");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
EOF
commit_case "$D"
N="$(line_of "$F" 'DropTable(name: "PriceLevels")')"
# NOTHING IS FORGED HERE. `Fake.Down` is a real override of a real virtual method -- it is simply not the
# MIGRATION's, and no test of the line's TEXT can tell the two apart. Written as a ONE-LINER on purpose: the
# three-line form is already caught, because `down_end`'s brace rule ends the body at the nested method's own
# closing brace. Its indent is the nested type's, so only the indent-equality test refuses it, and that is
# what pins the indent test alone.
expect_red "a one-line GENUINE override of a nested type's Down(, which is not the migration's" "$D" \
  "$MIG_REL/20260201000000_NestedOverride.cs:$N  DropTable" basebranch

D="$FIXTURES/designer-partial"; init_repo "$D"
F="$D/$MIG_REL/20260201000000_Hidden.cs"
mkdir -p "$(dirname "$F")"
cat > "$F" <<'EOF'
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.Mcp.TopstepX.Data.Migrations
{
    /// <inheritdoc />
    public partial class Hidden : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            RetireLegacy(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(name: "Legacy", table: "Bars", nullable: true);
        }
    }
}
EOF
G="$D/$MIG_REL/20260201000000_Hidden.Designer.cs"
cat > "$G" <<'EOF'
// <auto-generated />
using Microsoft.EntityFrameworkCore.Migrations;

namespace MarqSpec.Mcp.TopstepX.Data.Migrations
{
    public partial class Hidden
    {
        private static void RetireLegacy(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "PriceLevels");
        }
    }
}
EOF
commit_case "$D"
N="$(line_of "$G" 'DropTable(name: "PriceLevels")')"
# `Foo.Designer.cs` declares `partial class Foo`, so a helper written there is a member of the migration
# class. The name-based skip was harmless while no helper was read anywhere; round two started reading them,
# which made this the one place a helper was still invisible.
expect_red "a helper hidden in the *.Designer.cs partial of the migration class" "$D" \
  "$MIG_REL/20260201000000_Hidden.Designer.cs:$N  DropTable" basebranch

D="$FIXTURES/snapshot-op"; init_repo "$D"
S="$D/$MIG_REL/TopstepXDbContextModelSnapshot.cs"
cat > "$S" <<'EOF'
// <auto-generated />
using Microsoft.EntityFrameworkCore.Migrations;

namespace MarqSpec.Mcp.TopstepX.Data.Migrations
{
    public partial class TopstepXDbContextModelSnapshot
    {
        private static void Fix(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "Bars");
        }
    }
}
EOF
commit_case "$D"
N="$(line_of "$S" 'DropTable(name: "Bars")')"
# The other generated mirror, on the same argument. Skipping ONE of the two by name and reading the other
# would be a rule nobody could state; no file under the migrations directory goes unread now.
expect_red "a destructive call in the model snapshot, the other generated mirror" "$D" \
  "$MIG_REL/TopstepXDbContextModelSnapshot.cs:$N  DropTable" basebranch

D="$FIXTURES/spaced-call"; init_repo "$D"
F="$D/$MIG_REL/20260201000000_Spaced.cs"; emit_head "$F" Spaced
emit_body "$F" <<'EOF'
            migrationBuilder . DropColumn (name: "Legacy", table: "Bars");
EOF
emit_tail "$F"; commit_case "$D"
N="$(line_of "$F" 'DropColumn (name: "Legacy"')"
# `dotnet format` DOES catch this one, with three `error WHITESPACE` -- and that was the argument for leaving
# it. It is a LATER STEP IN THE SAME JOB, and this gate's whole placement argument is that it runs BEFORE
# `setup-dotnet`; splitting it into a job of its own would take the backstop away silently. One character
# class costs less than the paragraph defending its absence.
expect_red "a receiver, dot and name separated by spaces — a backstop in a later step is not this gate's" "$D" \
  "$MIG_REL/20260201000000_Spaced.cs:$N  DropColumn" basebranch

D="$FIXTURES/marked-and-unmarked"; init_repo "$D"
F="$D/$MIG_REL/20260201000000_Mixed.cs"; emit_head "$F" Mixed
emit_body "$F" <<'EOF'
            // destructive-migration: not rollback-safe before v0.4.0 — Legacy was dual-written.
            migrationBuilder.DropColumn(name: "Legacy", table: "Bars");
            migrationBuilder.DropTable(name: "Thing");
EOF
emit_tail "$F"; commit_case "$D"
# The SUMMARY is the assertion, not the refusal. `1 refusal(s) over 2 destructive operation(s)` read as two
# problems where there is one, because the second number counts the ACKNOWLEDGED operation too. It still has
# to be printed -- one refusal can cover two acts on an ambiguous line -- so it now says what it counts.
expect_red "the red summary's two numbers, with one operation marked and one not" "$D" \
  "1 refusal(s), 2 destructive operation(s) read, in 1 migration file(s)" basebranch

# A SECOND assertion on the twin-operations fixture built above: the refusal's actionable half must lead, and
# its text must fit the case where there is NO marker at all -- which is most of them.
expect_red "the ambiguous-line refusal leads with the act, not with a marker that may not be there" \
  "$FIXTURES/twin-ops-one-line" "Put them on separate lines. A marker acknowledges ONE operation" basebranch

D="$FIXTURES/no-up"; init_repo "$D"
F="$D/$MIG_REL/20260201000000_Handwritten.cs"
mkdir -p "$(dirname "$F")"
cat > "$F" <<'EOF'
using Microsoft.EntityFrameworkCore.Migrations;

public partial class Handwritten : Migration
{
    protected override void Apply(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "Legacy", table: "Bars");
    }
}
EOF
commit_case "$D"
expect_red "a migration whose Up(MigrationBuilder) cannot be located — unread is not a pass" "$D" \
  "NO Up()  $MIG_REL/20260201000000_Handwritten.cs" basebranch

D="$FIXTURES/bad-base"; init_repo "$D"
expect_red "a base ref that names no commit — it must not read that as an empty diff" "$D" \
  "UNRESOLVABLE BASE" origin/does-not-exist

D="$FIXTURES/no-migrations-dir"; init_repo "$D"
git -C "$D" "${git_ident[@]}" rm -q -r "$MIG_REL"
commit_case "$D"
expect_red "the Migrations directory gone — the gate would otherwise pass forever having read nothing" "$D" \
  "MISSING  $MIG_REL" basebranch

info ""
if [ "$failures" -gt 0 ]; then
  red "$failures of $cases self-test case(s) failed."
  red "check-migrations-additive.sh is NOT known to be able to fail, so its green runs prove nothing about"
  red "any migration in this pull request. Fix it before trusting anything it reports."
  exit 1
fi

if [ "$cases" -eq 0 ]; then
  red "  NOTHING CHECKED  no self-test cases ran."
  red "This file exists to prove the gate can fail and has just proven nothing."
  exit 1
fi

ok "ok  $cases self-test cases — check-migrations-additive.sh rejects each destructive shape BY FILE, LINE AND OPERATION, honours a marker only in the unbroken comment block directly above the operation, and accepts the additive shapes WITHOUT WRITING A BYTE TO STDERR."

# ---------------------------------------------------------------------------------------------------------
# DECISION LEDGER (gh#178's remedy, and this is the fourth gate here to carry one).
#
# THE TABLE IS SPLIT, because "a ledger is a claim too, and it lies in two specific ways". The first part is
# MEASURED: four sweeps of mutants, each deleting or inverting exactly one decision, each run against the
# whole suite, and the row records WHICH CASES WENT RED rather than merely that the suite did (gh#178 -- "a
# mutation that reddens for the WRONG reason reads as caught"). The last part is NOT MUTATED and says so; it
# is not a claim of coverage.
#
# WHAT THE FIRST SWEEP RAN ON, stated because gh#184's rule is that a run is cited against the code it ran
# on. It ran on this branch's gate BEFORE the untracked-file read was added -- that blob differs from the
# shipping one only by the `git ls-files --others` block, two header paragraphs and one line of `explain`
# prose. The suite was 27 cases then; case 8 (untracked) is the 28th and is the one thing the sweep could not
# see. That decision was measured separately and on the REAL TREE rather than on a fixture, which is
# stronger: with the probe migration written and never `git add`ed, the gate printed `ok  no migration added
# or changed against origin/develop`; with the read added, it printed
# `DESTRUCTIVE  ...29990101000000_ProbeDropColumn.cs:13  DropColumn`.
#
# **TWO OF ITS ROWS HAVE SINCE BEEN RE-RUN AND BOTH MOVED** -- rows 1 and 7, whose numbers below are from the
# SHIPPING blob `1bf26eb` at 49 cases and whose history is in the fourth sweep's note. The rest of the first table is
# where the provenance caveat still lives, and the caveat is now the smaller half of the point: **a ledger
# row names a mechanism, and a mechanism that moves takes the row's meaning with it, silently.** Each full
# run costs about 5m35s on a Windows checkout, so a sweep is the better part of an hour. Re-run it rather than
# re-reading this table -- that is where every row below came from, and auditing a ledger by reading it is
# how the first one in this repository got two rows wrong.
#
# FIRST SWEEP (baseline at the time: 27 of 27 green; rows 1 and 7 re-run at 47)
#
# | # | Decision deleted or inverted                        | Cases that went red                              |
# |---|-----------------------------------------------------|--------------------------------------------------|
# | 1 | `$MEMBER_RE` (made to never match)                  | **1 of 53** at gate blob `fba4730`. RE-MEASURED    |
# |   |                                                     | FOUR TIMES, and it has moved on every one -- see   |
# |   |                                                     | the fifth sweep's re-run table; the row said "7"   |
# |   |                                                     | and described a mechanism four rounds of review    |
# |   |                                                     | have since moved out from under.                   |
# | 2 | `--diff-filter=ACMR` (D allowed back in)            | **1.** "a DELETED migration"                       |
# | 3 | `has_drop_column` (forced to 1, always armed)       | **1.** "a DropIndex with no DropColumn beside it"  |
# | 4 | the comment-block boundary in `report`'s upward      | **3.** "a BLANKET marker at the top of the body",  |
# |   | walk (so the walk scans the whole file)             | "a marker separated ... by a blank line", and      |
# |   |                                                     | "two DropColumns, only the first acknowledged" --  |
# |   |                                                     | the third is the one that matters: without the     |
# |   |                                                     | boundary, ONE marker acknowledges a whole file.    |
# | 5 | the `[ ! -d "$MIG_DIR" ]` guard                     | **1.** "the Migrations directory gone"             |
# | 6 | `BASE` default `origin/develop` (made `HEAD`)       | **2.** both argument-free cases, and only those --  |
# |   |                                                     | which is why two cases pass no base at all.        |
# | 7 | `in_region` suppression of pass 3's SQL-NEEDLE arm  | **4 of 53** at gate blob `fba4730`. RE-MEASURED,   |
# |   | (pass 3's operation needles are no longer           | and the row also said the wrong thing: pass 3 is   |
# |   | suppressed by it at all -- see the second sweep)    | not suppressed by `in_region`, only its SQL arm    |
# |   |                                                     | is. See the note below the third sweep.            |
# | 8 | `DropSchema` and `DropSequence` removed from `OPS`  | **0 at the time. SURVIVOR, now CLOSED** -- see    |
# |   |                                                     | "an unacknowledged DropSchema" and "... DropSequence" |
#
# **WHICH ROWS HAVE BEEN RE-RUN, because inferring it from the two that mention a blob is exactly the reading
# the third-way rule refuses.** Rows **1 and 7** were re-run again at gh#601 against blob `fba4730` at 53
# cases; row 4 was re-run by review at round two and measured 3, as stated. Rows **2, 3, 5, 6 and 8 have NOT
# been re-run since the first sweep**, and their mechanisms -- `--diff-filter`, `has_drop_column`, the
# directory guard, the `BASE` default and the `OPS` list -- are the ones no round has touched. That is a
# reason to expect them to hold, not evidence that they do. **A row nobody has re-run is a row whose number
# is as old as its mechanism's last edit, and only the row can say which.**
#
# `1bf26eb` and `fba4730` are BLOB hashes (`git hash-object scripts/check-migrations-additive.sh`), not
# commits. Deliberate:
# a run is cited against the code it ran on (gh#184), and this branch is rebased -- a commit SHA naming these
# numbers would be stale before they were read, while the blob is the thing that was actually executed.
#
# **MUTANT 8 SURVIVED THE FIRST SWEEP, and the gap it named is now closed.** Those two list entries were
# pinned by nothing: they ride the same loop and the same `$CALL` suffix as the five operations that five
# cases do pin, so the argument was that a case each would pin the LIST ENTRY and nothing else. Review's
# answer is the right one — **a list entry is exactly the thing that needs pinning**, because an unpinned
# entry in a list is how the next edit quietly shortens the list, and the two cases cost six lines each.
# The row is kept rather than deleted: **a surviving mutant is the honest output of a sweep**, and the
# record of one having survived is worth more than a table that looks as though none ever did.
#
# SECOND SWEEP (gh#529 review round two). Four fixes, each reverted on the SHIPPING blob and the suite
# re-run, so these rows have none of the first sweep's provenance caveat. Every one of the four shapes
# compiles and passes `dotnet format --verify-no-changes`, which is what makes them worth a fixture apiece:
#
# | Fix reverted                                          | Cases that went red                                |
# |-------------------------------------------------------|----------------------------------------------------|
# | `$CALL`'s end-of-line arm (B2)                        | "an operation whose ( sits on the NEXT line"       |
# | the `Sql(` region pair (B4) -- lone-`;` close AND     | "an operation swallowed by a Sql( region whose ;   |
# |   the operation needles running inside a region        |   sits on its own line"                            |
# | `scanned()`'s after-`Down()` arm (B3)                 | "an operation in a SIBLING member"                 |
# | `adjudicate`'s `count -gt 1` refusal (B1)             | "TWO operations on one line" AND "two IDENTICAL …" |
# | `count_matches` made boolean again (B1's other half)  | "two IDENTICAL operations on one line"             |
#
# **The B4 row reverts TWO changes together and is labelled a conjunction deliberately.** Either one alone
# catches that shape — the region now closes on a lone `;`, *and* the operation needles run inside a region
# — so neither half is pinned by that case on its own. That is platform.md's "refused by the conjunction,
# pinned by neither", named here rather than left for the next reader to discover. The last two rows are the
# opposite and are worth the contrast: B1's refusal and B1's counting are separable, and the two cases
# separate them — delete the refusal and both one-line cases go red, make the match boolean again and only
# the identical-operations case does.
#
# THIRD SWEEP (gh#529 review round two's verdict, N8 and F1-F7). Nine reverts, each on the SHIPPING blob,
# each the whole suite re-run. The pattern review named is what shapes it: **round one widened WHAT IS
# SCANNED; the hole that opened was in WHAT DECIDES THE BOUNDARY of that scan.** Every guard below that
# could be pinned alone HAS a fixture written to defeat exactly it — a decoy in a plain string, a decoy
# written to look like a declaration inside a verbatim literal, a helper with no modifier, a helper under an
# inline `Down() { }` — because the pair-of-guards shape is precisely how a conjunction hides a dead half.
#
# | Fix reverted                                          | Cases that went red                                |
# |-------------------------------------------------------|----------------------------------------------------|
# | `declares()`'s MEMBER_RE requirement (N8) -- the     | **1.** "the decoy in a plain string literal"       |
# |   condition is GONE as of gh#601; see the fifth       |   (the same case is now pinned by the fifth        |
# |   sweep's row 5                                       |   sweep's ANCHOR row)                              |
# | `declares()`'s `in_region` requirement (N8)           | **1.** "the decoy written to LOOK like a           |
# |                                                       |   declaration, inside a verbatim Sql( literal"     |
# | `$DOWN_RE`'s end-of-line arm (F4)                     | **1.** "a WRAPPED Down( signature" — the only      |
# |                                                       |   FALSE-POSITIVE case in the suite                 |
# | the generated partials read rather than skipped (F3)  | **2.** "a helper hidden in the *.Designer.cs       |
# |                                                       |   partial" and "... in the model snapshot"         |
# | `$MEMBER_RE`'s non-access modifiers (the third        | **1.** "an inline Down() { } above a static        |
# |   boundary-blind pattern, found by audit)             |   sibling"                                         |
# | `down_end`'s closing-brace rule (same finding)        | **1.** "a sibling helper with no modifier at all"  |
# | `$DOT`'s whitespace class (F7)                        | **1.** "a receiver, dot and name separated by      |
# |                                                       |   spaces"                                          |
# | the red summary's labelled numbers (F5)               | **1.** "the red summary's two numbers"             |
# | the ambiguous-line refusal's wording                  | **1.** "the ambiguous-line refusal leads with the  |
# |                                                       |   act"                                             |
#
# FOURTH SWEEP (gh#529 review round three, A1 and A2). ONE revert, and one condition closes both shapes:
#
# | Fix reverted                                          | Cases that went red                                |
# |-------------------------------------------------------|----------------------------------------------------|
# | `declares()`'s `$OVERRIDE_RE` requirement -- GONE as  | **2.** "a STATIC LOCAL FUNCTION named Down inside  |
# |   of gh#601, which split it into the fifth sweep's     |   Up()" AND "a decoy Down( OVERLOAD above a        |
# |   rows 2 and 3 because it was answering two questions  |   no-modifier sibling"                             |
#
# **BOTH SHAPES ARRIVED THROUGH THE PREVIOUS ROUND'S FIXES, and that is the finding rather than the bug.**
# `static` went into `MEMBER_RE` to close the sibling-helper find, and `static` is what a C# local function
# may carry. `DOWN_RE`'s end-of-line arm went in so a wrapped signature would stop reddening correct work,
# and it also matches an ordinary `Down(int)` overload. Three rounds, three boundary bugs, each one living
# in the fix for the last: **a widening is where the next defeat lives, so say which END of a boundary you
# widened.** This row then said `OVERRIDE_RE` was the one condition testing what the line DECLARES, which is
# the property that was always the point. **IT WAS NOT, and gh#601 is that correction**: the needle was
# matched UNANCHORED against the whole line, so the word `override` in a trailing comment satisfied it -- a
# fifth property of the TEXT, and a fourth round with its bug living in the third round's fix. What tests
# what the line declares is where the declaration SITS and what it is CONTIGUOUS with, and both are in the
# fifth sweep's rows.
#
# The member test is still separately pinned after that addition, and only because its fixture was
# strengthened in the same change: the plain-string decoy now quotes the WHOLE signature, `override`
# included, so `OVERRIDE_RE` cannot refuse it and `MEMBER_RE` is the only condition left. **Adding a
# condition can silently un-pin an existing one, and the way to find out is to re-run its mutant** -- which
# is this file's own instruction, applied to a fixture rather than to a row. (gh#601 removed `MEMBER_RE`
# from `declares()` entirely; that fixture is now pinned by the ANCHOR instead -- fifth sweep, row 5 -- and
# the strengthening is what still makes it a single-property fixture there.)
#
# **`declares()`'s `//` test is pinned by NOTHING and is subsumed today.** A `//` line can never open with a
# modifier keyword, so the declaration pattern already refuses every comment; deleting the comment test
# reddens nothing, measured twice now (fourth sweep, and fifth sweep row 7). It is kept anyway, and stated
# here rather than left to be discovered -- the two would have to be wrong together, and the subsumption is
# structural (`^[[:space:]]*//` and `^<indent>(modifier )` cannot both match a line) rather than incidental,
# so it will not rot quietly.
#
# **THAT ROW CARRIED A PREDICTION, THE PREDICTION WAS TESTED, AND IT FAILED -- TWICE.** It said *the day
# MEMBER_RE is widened is the day the `//` test starts carrying weight*. `MEMBER_RE` WAS widened, in the very
# next round, and that is not what happened: the widening did not give the `//` test weight, it took weight
# away from the MEMBER test -- `static` made a local function eligible to set a boundary, and what was needed
# was a FOURTH condition rather than one of the three already there. Then gh#601 removed `MEMBER_RE` from
# `declares()` altogether, which is the strongest form of the event the prediction described, and the `//`
# test picked up no weight from that either. **A prediction about which guard will matter next is a guess
# about where the next bug is, and this one has now pointed at the wrong guard twice.** Kept and marked
# rather than quietly rewritten, because a wrong prediction with its outcome recorded is worth more than a
# tidy row that never risked anything.
#
# **AND THE FIRST SWEEP'S ROWS 1 AND 7 WERE RE-RUN, because every round of review moved the code under them**
# (review's F1, which measured row 1 itself rather than reading it). This is the ledger's own instruction
# turned on the ledger:
#
#   - **Row 1 said 7, then 1, then 39 of 47, and now measures 41 of 49 at gate blob `1bf26eb`.** It was a
#     claim about `$MEMBER_RE` bounding the END of the `Up()` body. Round two made it bound the end of
#     `Down()`, at which point review measured **1** -- the row overstated its own evidence, and the case it
#     NAMED stayed green. Round three made MEMBER_RE part of locating `Down()` at all. **And the mechanism
#     moved AGAIN on the next round, which is the row's point rather than a footnote to it:** `declares()`
#     now gates `up_start` as well, so a MEMBER_RE that never matches means no migration's `Up()` can be
#     located and every operation case dies on `NO Up()` -- they redden for a reason this row's own sentence
#     does not give. **A mutation that reddens for the wrong reason reads as caught**, and this row is now
#     its own example. Four numbers, four mechanisms, one mutant.
#   - **Row 7 said 1 and measures 4 of 49 at the same blob**, and it also named the wrong thing: pass 3 is
#     not suppressed by `in_region` at all any more, only its SQL-needle arm is. The three single-line
#     `Sql()` cases join the multi-line one because the region finding and the outside-a-region finding then
#     land on the SAME line, which the ambiguity rule refuses -- so they fail on a diagnostic that is right
#     about the code and wrong about them. That is why a row records which cases moved rather than that the
#     suite did.
#
# FIFTH SWEEP (gh#601). SEVENTEEN runs, each deleting or inverting exactly ONE decision on a COPY of the
# gate, each refusing to start unless the copy's blob moved and printing its own
# `APPLIED <before> -> <after>` line, each the whole suite re-run. Baseline blob `fba4730`: **53 of 53
# green.** Taken in the container named at the top of this file, not on the Windows checkout.
#
# | # | Decision deleted or inverted                          | Cases that went red                             |
# |---|-------------------------------------------------------|-------------------------------------------------|
# | 0 | THE WHOLE CHANGE, reverted to the shipping gate blob  | **4.** the four gh#601 fixtures and only those. |
# |   | `1bf26eb`. Not a decision -- the before/after pair    |   Every other case green on BOTH blobs.         |
# | 1 | the INDENT EQUALITY (the `Down()` search handed       | **1.** "a one-line GENUINE override of a nested |
# |   | `[[:space:]]+` instead of the class's member indent)  |   type's Down("                                 |
# | 2 | the requirement that an `override` be present AT ALL  | **2.** "a decoy Down( OVERLOAD above a          |
# |   | (a modifier run required, none of it `override`)      |   no-modifier sibling" and "a sibling Down(     |
# |   |                                                       |   overload whose trailing COMMENT carries …"    |
# | 3 | the requirement that the `override` sit in the        | **1.** "a sibling Down( overload whose trailing |
# |   | declaration's OWN run (round four's unanchored needle |   COMMENT carries the word override"            |
# |   | restored beside a modifier run)                       |                                                 |
# | 4 | the requirement that the SIGNATURE follow that run    | **1.** "a real Up() declaration whose trailing  |
# |   | IMMEDIATELY (matched as its own question again)       |   COMMENT names the Down( signature"            |
# | 5 | the `^<indent>` ANCHOR (the declaration allowed to    | **2.** "the decoy in a plain string literal"    |
# |   | match anywhere on the line)                           |   and the nested-type one                       |
# | 6 | `declares()`'s `in_region` requirement                | **1.** "the decoy written to LOOK like a        |
# |   |                                                       |   declaration, inside a verbatim Sql( literal"  |
# | 7 | `declares()`'s `//` test                              | **0. SURVIVOR, and expected** -- see below      |
# | 8 | "no locatable `Up()` means no `Down()` boundary" (the | **0. SURVIVOR** -- unreachable from a fixture,  |
# |   | file searched at any indent instead)                  |   and that was checked, not argued. See below.  |
#
# Rows 1 through 5 are five decisions inside ONE regex (row 6 is a separate condition), and they are listed
# as five because that is what they are: each was mutated alone, and no two of the six redden the same set.
# **WHAT SEPARATES THEM IS THE SET, NOT A UNIQUE CASE.** Here are the sets, so that the containment can be
# READ rather than taken on trust -- a claim derivable from the table beside it should be derived from it,
# and the summary sentence that used to stand here was wrong three drafts running:
#
#     row 1   indent equality ............  { nested }
#     row 2   an override at all .........  { decoy overload, commented overload }
#     row 3   override in its own run ....  { commented overload }
#     row 4   signature contiguous .......  { mirror Up() }
#     row 5   the `^<indent>` anchor .....  { plain-string, nested }
#     row 6   `in_region` ................  { verbatim literal }
#
# Exactly two are strict SUBSETS of another -- row 1 of row 5, and row 3 of row 2 -- so **only rows 1 and 3
# fail to own a case outright**, and rows 2, 4, 5 and 6 each do. Every row is still pinned: deleting its
# decision reddens a case, and the six sets are distinct, so the table tells them apart. What it does not
# give is a unique case per row, and *pinned by a set difference* is a weaker claim than *pinned by a unique
# case*.
#
# THREE DRAFTS OF THAT ONE SENTENCE WERE WRONG, WHICH IS WHY THE SETS ARE WRITTEN OUT ABOVE IT. The first
# claimed a unique case for every row. The second named the two exceptions correctly as a pair and attached
# them to the wrong side -- "only rows 4 and 6 own a case outright", when rows 2 and 5 own one too. Each was
# a summary nobody could check without re-measuring, which is precisely the shape this ledger exists to warn
# about, committed three times by the pull request that re-audits it and caught three times by a reviewer who
# measured instead of reading. A compound pattern still does not get one row for being written on one line.
#
# FIFTH SWEEP, PART TWO -- THE RE-RUNS. `declares()` changed, so every earlier row whose separating fixture
# that change could have un-pinned was RE-RUN rather than re-read. Same baseline blob, same container.
#
# | Earlier row re-run                                     | Then  | Now, of 53                                 |
# |--------------------------------------------------------|-------|--------------------------------------------|
# | first sweep row 1 -- `$MEMBER_RE` made never to match  | 41/49 | **1.** "an inline Down() { } above a static |
# |                                                        |       |   sibling". FOURTH mechanism, fifth number. |
# | first sweep row 7 -- `in_region` suppressing pass 3's  | 4/49  | **4.** the same four; denominator only.     |
# |   SQL-needle arm                                       |       |                                             |
# | third sweep -- `$DOWN_RE`'s end-of-line arm            | 1     | **1.** "a WRAPPED Down( signature"          |
# | third sweep -- the generated partials read             | 2     | **2.** unchanged, both mirrors              |
# | third sweep -- `$MEMBER_RE`'s non-access modifiers     | 1     | **1.** "an inline Down() { } above a static |
# |   (`down_end`)                                         |       |   sibling"                                  |
# | third sweep -- `down_end`'s closing-brace rule         | 1     | **1.** "a sibling helper with no modifier"  |
# | third sweep -- `$DOT`'s whitespace class               | 1     | **1.** "a receiver, dot and name separated" |
# | third sweep -- `declares()`'s MEMBER_RE requirement    | 1     | **GONE.** The condition no longer exists;   |
# |                                                        |       |   its fixture is pinned by row 5 above.     |
# | fourth sweep -- `declares()`'s `$OVERRIDE_RE`          | 2     | **GONE.** Replaced by rows 2 and 3, which   |
# |                                                        |       |   is gh#601's whole point: ONE condition    |
# |                                                        |       |   was answering TWO questions.              |
#
# **ROW 1 MOVED AGAIN, AND THAT IS FIVE NUMBERS FOR ONE MUTANT.** 7, then 1, then 39 of 47, then 41 of 49,
# now **1 of 53** -- because `MEMBER_RE` has stopped gating `up_start`, so killing it no longer kills every
# migration's `NO Up()` check. Each number was true of a different mechanism. **A ledger row names a
# mechanism, and a mechanism that moves takes the row's meaning with it, silently.**
#
# **ROW 7 SURVIVES AND IS SUPPOSED TO** -- the `//` test's subsumption is discussed above the fourth sweep.
#
# **ROW 8 SURVIVES BECAUSE NO FIXTURE CAN REACH IT, and that was checked rather than asserted.** A file with
# no locatable `Up()` is only ever a generated partial: a migration without one dies on `NO Up()` first.
# Neither `*.Designer.cs` nor the model snapshot declares a `Down()`, and neither can -- the migration class
# already declares it and a partial class cannot declare it twice. The fixture would have to be a file
# nobody can write. Recorded as a measured 0 rather than as "unreachable, and here is why", which is this
# ledger's own weakest row shape.
#
# **AND THE TWO LOCAL-FUNCTION FIXTURES ARE NOW REFUSED TWICE OVER AND PINNED BY NEITHER HALF.** "a STATIC
# LOCAL FUNCTION named Down inside Up()" and "a local function named Down whose trailing COMMENT carries the
# word override" went red under **NOTHING** in this sweep: delete the indent test and the absent `override`
# still refuses them, delete the `override` requirement and the indent still does. That is gh#438's
# `flips-on: NOTHING`, and here it **cannot** be fixed by stripping the fixture to one property -- a local
# function sits deeper than its class's members by construction, so there is no way to write one at the
# member indent that `dotnet format` leaves alone. **No coverage was lost**: rows 1, 2 and 3 each keep a
# fixture no other mutant reddens. What was lost is these two fixtures' ability to BE that fixture, and the
# honest record is this paragraph rather than a row implying otherwise. They stay -- row 0 is their
# evidence, red on the shipping blob and green on this one, and they are the cases that notice second the
# day either half is relaxed.
#
# WHAT THIS STILL DOES NOT CLOSE, stated so the next reader does not have to find it. The indent test says
# *at the migration class's member indent*, and a type declared BESIDE the migration class -- same
# namespace, same nesting level -- has its members at exactly that indent too. A genuine
# `public override void Down(MigrationBuilder b)` there, above the migration class, would still take
# `down_start`. gh#612 is where that is decided; it carries a worked bypass, and the part this note did not
# name is the FILE-SCOPED NAMESPACE, which puts the class declaration at column 0 where `MEMBER_RE` cannot
# see it, so nothing ends the excluded span either.
#
# **AND THE FIRST DRAFT OF THIS NOTE SAID CLOSING IT NEEDS A PARSER. THAT IS FALSE, AND THIS FILE WROTE IT.**
# Bounding the `Down()` search to the migration class's own brace span needs no machinery `down_end` does not
# already have -- the closing brace in the declaration's own column is exactly the rule that ends the `Down()`
# body today. It is a scoping question, not a language question, and calling it a parser is the kind of claim
# that retires a defect by describing it as impossible. **A newly-written false claim is worse than an
# inherited one**, because nothing above it is stale: it is the very defect class this card exists to fight,
# committed in the paragraph recording that card. What is TRUE is the second half, and it is the whole reason
# gh#601 stopped here: this is one more construct whose only function is to move the boundary, and an author
# willing to write one can lie in a `// destructive-migration:` marker instead -- which the design accepts by
# construction and hands to the reviewer.
#
# NOT MUTATED -- claimed as exercised, never as pinned
#
#   Each of these has a case whose NEEDLE names it, so a silent regression would have to also keep the
#   diagnostic byte-identical. That is weaker than a mutation and is listed separately for that reason.
#
#   - the base-ref read and its status check ......... "a base ref that names no commit" (needle: UNRESOLVABLE BASE)
#   - `MIGRATION_GATE_BASE` fallback ................. "MIGRATION_GATE_BASE naming the base" (needle: against basebranch)
#   - `git merge-base` and its status check .......... every case; UNREACHABLE as a failure from a fixture,
#                                                      since every fixture history is related by construction
#   - the generated mirrors needing no `Up()` ........ "a Designer.cs and a model snapshot are read"
#   - `$UP_RE` ....................................... "a migration whose Up(MigrationBuilder) cannot be located"
#   - `declares()`'s `//` test ....................... MOVED. It is MUTATED now (fifth sweep, row 7) and
#                                                      survives: subsumed by the anchored declaration
#                                                      pattern, since a `//` line cannot open with a
#                                                      modifier. A measured survivor is a stronger record
#                                                      than a line in this list, which claims nothing
#   - the `partials_read` clause in the summaries .... NOTHING asserts its TEXT. Three cases execute it (the
#                                                      two generated-mirror reds and the green pair), so a
#                                                      broken expansion would be caught by `set -u`; the
#                                                      wording itself is unpinned and this row says so
#   - `$COMMENT_RE` skip ............................. "a commented-out DropColumn"
#   - `$SQL_CALL_RE` / `$STATEMENT_END_RE` ........... "a multi-line Sql() whose DROP is three lines below"
#   - the three SQL needles, individually ............ one case each, matching the printed label
#   - `nocasematch` .................................. "a LOWERCASE truncate"
#   - `OPS`, every entry ........................... one case each, matching the printed operation --
#                                                      `DropSchema` and `DropSequence` included since the
#                                                      first sweep found them pinned by nothing
#   - `$MARKER_RE`'s non-empty-reason requirement .... "a marker with NO reason"
#   - the `files_read -eq 0` early green ............. "a branch that touches no migration at all"
#   - the untracked read ............................. case 8, plus the real-tree pair quoted above
# ---------------------------------------------------------------------------------------------------------
