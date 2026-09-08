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
# makes is listed beside the case that kills it, and the table is SPLIT: three MEASURED sweeps of mutants,
# and the rest listed as exercised-but-not-mutated. Adding a decision to the gate without adding a row is
# the same visible omission the ledger exists to catch. Read the split before trusting a row -- "a ledger is
# a claim too", and the ways one lies are a grade promising more than its evidence, a decision pinned by a
# fixture's incidental shape, and -- found by review on this very file -- **a row whose mechanism has moved
# underneath it, so the number is stale and the sentence describes code that is gone.** ONE MUTANT SURVIVED
# the first sweep and is named as such; TWO of that sweep's rows have since been re-run and both moved.
#
# LOCAL RUNTIME. Each case forks `git init`, a few commits and a shell. Seconds on the CI runner; **about
# 7m20s on a Windows checkout** at 47 cases, where process spawning dominates -- `sys` is five times `user`.
# That is what bounds how much of the ledger below can be measured on one card, so it is written down rather
# than left for the next reader to discover mid-sweep.

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
            System.Console.WriteLine("void Down(MigrationBuilder migrationBuilder)");
            migrationBuilder.DropColumn(name: "Legacy", table: "Bars");
EOF
emit_tail "$F"; commit_case "$D"
N="$(line_of "$F" 'DropColumn(name: "Legacy"')"
# A decoy in a string that is NOT a Sql( region, so the region guard cannot see it. Only the requirement
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
# MEASURED: three sweeps of mutants, each deleting or inverting exactly one decision, each run against the
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
# SHIPPING blob at 47 cases and whose history is in the third sweep's note. The rest of the first table is
# where the provenance caveat still lives, and the caveat is now the smaller half of the point: **a ledger
# row names a mechanism, and a mechanism that moves takes the row's meaning with it, silently.** Each full
# run costs about 7m20s on a Windows checkout, so a sweep is an hour or more. Re-run it rather than
# re-reading this table -- that is where every row below came from, and auditing a ledger by reading it is
# how the first one in this repository got two rows wrong.
#
# FIRST SWEEP (baseline at the time: 27 of 27 green; rows 1 and 7 re-run at 47)
#
# | # | Decision deleted or inverted                        | Cases that went red                              |
# |---|-----------------------------------------------------|--------------------------------------------------|
# | 1 | `$MEMBER_RE` (made to never match)                  | **39 of 47** on the code that ships. RE-MEASURED   |
# |   |                                                     | TWICE -- see the note below the third sweep; the   |
# |   |                                                     | row said "7" and described a mechanism two rounds  |
# |   |                                                     | of review have since moved out from under it.      |
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
# | 7 | `in_region` suppression of pass 3's SQL-NEEDLE arm  | **4 of 47** on the code that ships. RE-MEASURED,   |
# |   | (pass 3's operation needles are no longer           | and the row also said the wrong thing: pass 3 is   |
# |   | suppressed by it at all -- see the second sweep)    | not suppressed by `in_region`, only its SQL arm    |
# |   |                                                     | is. See the note below the third sweep.            |
# | 8 | `DropSchema` and `DropSequence` removed from `OPS`  | **0 at the time. SURVIVOR, now CLOSED** -- see    |
# |   |                                                     | "an unacknowledged DropSchema" and "... DropSequence" |
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
# | `declares()`'s MEMBER_RE requirement (N8)             | **1.** "the decoy in a plain string literal"       |
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
# **`declares()`'s `//` test is pinned by NOTHING and is subsumed today.** A `//` line can never carry a
# leading modifier keyword, so the MEMBER_RE requirement already refuses every comment; deleting the comment
# test reddens nothing. It is kept anyway, and stated here rather than left to be discovered: the two would
# have to be wrong together, and the day MEMBER_RE is widened is the day it starts carrying weight.
#
# **AND THE FIRST SWEEP'S ROWS 1 AND 7 WERE RE-RUN, because two rounds of review moved the code under them**
# (review's F1, which measured row 1 itself rather than reading it). This is the ledger's own instruction
# turned on the ledger:
#
#   - **Row 1 said 7 and now measures 39 of 47.** It was a claim about `$MEMBER_RE` bounding the END of the
#     `Up()` body. Round two made it bound the end of `Down()`, at which point review measured **1** — the
#     row overstated its own evidence, and the case it NAMED stayed green. Round three then made MEMBER_RE
#     the requirement for locating `Down()` at all, so a MEMBER_RE that never matches leaves `down_start` at
#     0, the whole file is read including a destructive `Down()`, and almost the whole suite moves. Same
#     mutant, three different numbers, three different mechanisms. **A row that names a mechanism has to be
#     re-run when the mechanism moves, and nothing but re-running it says so.**
#   - **Row 7 said 1 and now measures 4 of 47**, and it also named the wrong thing: pass 3 is not suppressed
#     by `in_region` at all any more, only its SQL-needle arm is. The three single-line `Sql()` cases join it
#     because the region finding and the outside-a-region finding then land on the SAME line, which the
#     ambiguity rule refuses — so they fail on a diagnostic that is right about the code and wrong about
#     them. **A mutation that reddens for the wrong reason reads as caught**, which is why the row records
#     which cases moved rather than that the suite did.
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
#   - `declares()`'s `//` test ....................... NOTHING. Subsumed by its MEMBER_RE test today, since a
#                                                      `//` line carries no leading modifier -- kept because
#                                                      the two would have to be wrong together, and named
#                                                      here rather than left to be found
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
