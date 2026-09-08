#!/usr/bin/env bash
# check-migrations-additive.sh — refuse an UNACKNOWLEDGED destructive operation in a NEW EF Core migration.
#
#   scripts/check-migrations-additive.sh [BASE_REF]        # BASE_REF defaults to origin/develop
#
# WHY THIS EXISTS (gh#529, ADR-0023 §4 and §12)
#
# The deploy shape ADR-0023 chose is desired 1, minimum 0%, maximum 100%: the old task stops, the new one
# runs `MigrateAsync` alone, and if the new version then fails, the ECS circuit breaker restores the
# PREVIOUS task definition OVER THE MIGRATED SCHEMA. So a migration that lands before a code rollback must
# be additive -- a dropped column is a crashed old version with no path back, over a store holding bars,
# tape and indicator projections that re-running nothing will bring back.
#
# That rule was prose in an ADR, and "a defence stated in four places is not four defences" (gh#415). It is
# only ever tested at the worst moment: production, mid-rollback. Nothing in CI read a migration --
# SchemaTests pins what the schema IS, not how it got there.
#
# WHAT A GREEN RUN LICENSES, EXACTLY
#
#   No operation in the ENUMERATED SET below appears UNACKNOWLEDGED anywhere in a migration this diff adds
#   or changes, EXCEPT inside its `Down()` body.
#
# It does NOT say a migration is safe. It does not judge whether an ACKNOWLEDGED destructive operation is
# right -- that is the reviewer's, and the marker is the sentence they judge. It says nothing about
# migrations already on the base branch, about `Down()`, or about data.
#
# WHAT IT COVERS
#
#   Everywhere in the file EXCEPT the `Down()` body, on a line that is not a `//` comment:
#     .DropTable(         .DropColumn(        .RenameColumn(      .RenameTable(
#     .AlterColumn<T>( or .AlterColumn(       .DropSchema(        .DropSequence(
#     .DropIndex(         -- ONLY when the same migration also calls .DropColumn(; see below
#   with the `(` allowed to sit on the FOLLOWING line and whitespace allowed after the `.`, both of which are
#   legal C# that `dotnet format` accepts or repairs in a LATER step of the same job.
#   And, over the whole text of a `migrationBuilder.Sql(...)` call (multi-line literals included), or on any
#   other non-comment line outside a region, case-insensitively:
#     DROP <space>        TRUNCATE            ALTER TABLE … <space>TYPE<space>
#
#   IN THE MIGRATION AND IN THE GENERATED PARTIALS BESIDE IT. `Foo.Designer.cs` declares `partial class Foo`,
#   so a helper written there is a member of the migration class; skipping it by name was the one place a
#   helper stayed invisible once the scan widened past `Up()`. The model snapshot is read on the same terms.
#   They are reported separately from the migration count, because they are not migrations.
#
# WHERE THE BOUNDARY COMES FROM, WHICH IS A DIFFERENT KIND OF PATTERN
#
# `Up()` and `Down()` are located through `declares()`, never by matching a raw line: the line must not be a
# `//` comment, must not be the interior of a `Sql(` string literal, must carry a modifier keyword, and must
# carry `override`. A comment inside `Up()` reading "there is no void Down(MigrationBuilder …) worth
# writing" otherwise set the excluded span from that comment to the real declaration -- switching off the
# rest of `Up()` while reporting the `Down()` body's own drop as if it were in `Up()`. A finding needle that
# is too loose costs a false positive; a BOUNDARY needle that is too loose excludes code, and nothing
# reports what was never read.
#
# ONLY ONE BOUNDARY HERE FAILS SILENTLY, AND IT IS WHERE THE EXCLUDED SPAN BEGINS. That asymmetry is what
# says where to look, and three rounds of review have found bugs at exactly one end:
#
#   - `down_start` too EARLY excludes real code and reports nothing about it. SILENT. Every boundary bug
#     this gate has had lives here, and each arrived through the previous round's fix -- which is why the
#     condition added for it tests what the line DECLARES rather than another property of the line.
#   - `down_end` (`MEMBER_RE`, the brace rule) matching falsely ends the body EARLY, so MORE is scanned. LOUD.
#   - `SQL_CALL_RE` opening a bogus region in pass 0 puts lines in `in_region`, which makes `declares()`
#     REFUSE the declarations inside it -- so the run dies on `NO Up()`, or `down_start` falls to 0 and the
#     whole file including `Down()` is read. LOUD, by construction rather than by luck.
#   - `STATEMENT_END_RE` closing a region early takes lines OUT of `in_region`, where pass 3's line-level
#     SQL needle picks them up. LOUD.
#
# So those three stay un-predicated on purpose. Making them string-aware would let an unterminated literal
# run a region to end of file and take a sibling member with it, which is the quiet direction.
#
# AND EVERY WIDENING IN THIS FILE IS AUDITED AGAINST THAT ASYMMETRY, because a widening is how each of the
# last two rounds' bugs arrived. `$DOT`, the `$CALL` end-of-line arm and reading the generated partials
# widen what is FOUND, so they can only add findings -- loud. Pass 0's whole-file region map widens
# `in_region`, which can only make `declares()` refuse more -- loud. `MEMBER_RE`'s modifier list and the
# brace rule widen `down_end`, which can only end the body earlier -- loud. The two that widened
# `down_start` -- `MEMBER_RE`'s list, again, and `DOWN_RE`'s end-of-line arm -- are the two that were
# defeated, and both are now behind `OVERRIDE_RE`. **Widen a boundary and say which end you widened.**
#
# NOT THE `Up()` BODY ALONE, and that is a correction rather than a design (gh#529 review). Scanning only
# `Up()` meant an operation in a SIBLING MEMBER of the same class -- a private helper `Up()` calls -- was
# never read, because the member declaration that ends the body also ended the scan. It compiled, it
# formatted clean, and the gate said `0 destructive operation(s)`. Everything outside `Down()` is read now.
#
# ONE OPERATION PER LINE. Two destructive calls sharing a line are REFUSED outright rather than matched
# against the marker above them: there is one sentence and two acts, and nothing says which act it
# describes. A marker naming only `Legacy` used to acknowledge a `DropTable` beside it, and two IDENTICAL
# calls on one line were counted as one, so the green line's own evidence undercounted. The remedy is to put
# them on separate lines and mark each.
#
# `AlterColumn` is covered WHOLESALE because a narrowing is not distinguishable statically: `numeric(18,8)`
# to `numeric(12,4)`, `text` to `varchar(32)` and a nullable-to-required flip are the same call shape as a
# widening, and only the type arguments differ. Marking a widening costs one line; missing a narrowing costs
# the store.
#
# `DropIndex` ALONE IS NOT FLAGGED, and that is a decision rather than an omission: an index carries no data
# a rolled-back binary needs, and CREATE INDEX is the recovery. Paired with a `DropColumn` it is part of the
# destructive act rather than a separate one, so it needs its own marker -- otherwise the acknowledgement
# names half of what the migration does. The pairing is SYNTACTIC: a DropColumn in the body arms it whether
# or not that DropColumn is itself marked.
#
# WHAT IT DELIBERATELY DOES NOT COVER
#
#   - `DropPrimaryKey`, `DropForeignKey`, `DropUniqueConstraint`, `DropCheckConstraint`. Dropping a
#     constraint destroys no rows and a restored old binary still reads and writes the same data. Flagging
#     them would put a marker on the ordinary rename-and-recreate dance EF scaffolds. **"Destroys no rows"
#     is not quite "a rollback is unaffected"**: a lone DropPrimaryKey, or a lone DropIndex on a UNIQUE
#     index, lets the restored old binary insert duplicates, and re-adding the key in a later migration can
#     then fail. That is a FORWARD-FIX hazard rather than a rollback one, so it sits outside ADR-0023
#     decision 4 -- recorded here because the shorter claim reads stronger than it is.
#   - The `Down()` body. Every additive migration's Down() drops what its Up() added -- six of the seven
#     migrations on `develop` have a destructive Down(), so reading it would redden almost the whole tree
#     and the gate would be deleted the first day. THE COST IS NAMED: a helper called only from Down() is
#     still read, because nothing here says which of the two calls it. Mark it, or inline it into Down().
#   - Migrations already on the base branch. They predate the rule (ADR-0023 is later than all seven) and are
#     not re-read. The scoping is the diff, so this is a property of what the gate LOOKS AT rather than an
#     exclusion list that can drift.
#   - Data. `migrationBuilder.UpdateData`/`DeleteData` and a `Sql("DELETE FROM …")` are not flagged; the rule
#     ADR-0023 states is about SCHEMA a rolled-back binary must still fit.
#   - `/* block comments */`. Line comments are skipped (a commented-out DropColumn drops nothing, and the
#     marker itself is a comment); a destructive call inside a block comment is still FLAGGED, which is the
#     loud direction. Same stance as check-paced-paging.sh, opposite reason: there a commented-out call faked
#     a call that must be present, here it would fake one that must be absent.
#   - Whether an acknowledged destructive migration is CORRECT. Out of scope by the issue.
#
# THE ESCAPE HATCH IS IN THE FILE, NOT THE PULL REQUEST
#
#     // destructive-migration: <the release after which rollback is no longer possible, and why>
#     migrationBuilder.DropColumn(name: "Legacy", table: "Bars");
#
# In the CONTIGUOUS BLOCK OF LINE COMMENTS immediately above the operation -- so a reason may run over
# several lines, which a real one does. The block ends at the first line that is not a `//` comment: a blank
# line, or the previous operation. ADJACENCY IS THE WHOLE MECHANISM. A blanket marker at the top of a file
# would otherwise silence every operation below it, which is exactly the "generated file nobody read line by
# line" this gate exists for, and two operations on two lines always need two markers because each one's
# block is broken by the other. Two on ONE line are refused instead -- see ONE OPERATION PER LINE above, and
# note that adjacency alone did NOT deliver this: the walk starts at `line - 1` for both calls and lands on
# the same marker, so the rule had to be enforced rather than assumed. The reason must be non-empty; a bare
# `// destructive-migration:` is a rubber stamp and is refused. In the file rather than in the PR body so it
# is reviewable, greppable and survives the merge -- a PR body is read once.
#
# HOW THE DIFF IS TAKEN
#
# `git merge-base BASE HEAD`, then `git diff --diff-filter=ACMR <merge-base>` -- so it reads what THIS branch
# added or changed, committed or not, and never a migration the base branch grew after the branch point.
# Deletions are filtered out (a deleted migration is a history rewrite, not a schema change). Nothing under
# the migrations directory is excluded by NAME any more: `*.Designer.cs` and the model snapshot are read on
# the same passes, and only their COUNT is kept separate, because the green line's `N migration file(s)` is
# an evidence number about migrations.
# `git ls-files --others` is read on top of it, because a migration `dotnet ef` has just scaffolded and
# nobody has added yet is invisible to `git diff` -- and going green on the file the author is about to
# commit is worse than saying nothing.
#
# IT CANNOT PASS VACUOUSLY. Every read that decides the verdict is assigned on its own line and its status
# checked (gh#126): an unresolvable base ref, no merge base, a failed diff, a missing Migrations directory,
# a file in the diff that is not on disk, and a migration whose `Up()` cannot be located are each a FAILURE,
# never a skip. No `grep` decides anything here -- every match is bash's own `=~`, so there is no exit 2 to
# be mistaken for "no match". The green line prints how many migration files were read, so a pass carries
# its own evidence (the shape gh#126 gave check-no-order-path.sh).
#
# WIRED INTO `build & unit tests`, which is a required context on all three rungs -- so this enforces by
# wiring rather than by a ruleset write, the same argument as everything in the `docs` job. If this gate is
# moved to a job of its own, the new context has to be added to `protect-develop`, `-staging` and `-main` in
# the same change, or it only ever reports (gh#26).

set -euo pipefail

die()  { printf '\033[31m%s\033[0m\n' "$*" >&2; }
ok()   { printf '\033[32m%s\033[0m\n' "$*"; }

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

MIG_DIR="MarqSpec.Mcp.TopstepX.Data/Migrations"
MARKER='destructive-migration:'
BASE="${1:-${MIGRATION_GATE_BASE:-origin/develop}}"

explain() {
  cat >&2 <<EXPLAIN

A migration that lands before a code rollback must be ADDITIVE (ADR-0023 §4, §12).

The deploy runs the new task's MigrateAsync alone. If that task then fails, the circuit breaker restores the
PREVIOUS task definition over the schema you just migrated -- so a dropped column, a renamed one or a
narrowed type is an old binary crashing against a store nothing can put back.

Two ways forward:

  1. MAKE IT ADDITIVE. Add the new column, write both, and drop the old one in a LATER migration, after the
     release you would roll back to is no longer a rollback target. This is almost always the answer.

  2. ACKNOWLEDGE IT, in the comment block directly above the operation:

         // ${MARKER} not rollback-safe before v0.4.0 -- Bars.Legacy was dual-written
         // since v0.3.0 and nothing reads it.
         migrationBuilder.DropColumn(name: "Legacy", table: "Bars");

     The reason may run over several comment lines. It must sit in the UNBROKEN comment block directly
     above the operation -- a blank line between them ends the block, and a marker at the top of the file
     acknowledges only the operation it is actually touching. One marker per operation; the reason is
     required, and is what the reviewer judges.

EXPLAIN
}

# ---------------------------------------------------------------------------------------------------------
# The base, the merge base and the diff. Three reads, three statuses, and every one of them fatal.
# ---------------------------------------------------------------------------------------------------------

if [ ! -d "$MIG_DIR" ]; then
  die "  MISSING  $MIG_DIR"
  die "The migrations moved. This gate reads nothing and would pass forever -- point it at the new path."
  exit 1
fi

base_status=0
BASE_SHA="$(git rev-parse --verify --quiet "${BASE}^{commit}")" || base_status=$?
if [ "$base_status" -ne 0 ] || [ -z "$BASE_SHA" ]; then
  die "  UNRESOLVABLE BASE  '$BASE' does not name a commit here (git rev-parse exit $base_status)."
  die "Nothing was read. In CI, fetch it first:"
  die "    git fetch --no-tags origin +refs/heads/develop:refs/remotes/origin/develop"
  die "Locally, pass a base explicitly: scripts/check-migrations-additive.sh <ref>"
  exit 1
fi

mb_status=0
MERGE_BASE="$(git merge-base "$BASE_SHA" HEAD)" || mb_status=$?
if [ "$mb_status" -ne 0 ] || [ -z "$MERGE_BASE" ]; then
  die "  NO MERGE BASE  between '$BASE' ($BASE_SHA) and HEAD (git merge-base exit $mb_status)."
  die "A shallow clone or an unrelated history. Nothing was read, so nothing is known."
  exit 1
fi

DIFF_LIST="$(mktemp)"
trap 'rm -f "$DIFF_LIST"' EXIT

diff_status=0
git -c core.quotepath=false diff --name-only --diff-filter=ACMR "$MERGE_BASE" -- "$MIG_DIR" \
  > "$DIFF_LIST" || diff_status=$?
if [ "$diff_status" -ne 0 ]; then
  die "  DIFF FAILED  git diff exited $diff_status against $MERGE_BASE."
  die "Nothing was read. A gate that cannot take its diff has checked nothing."
  exit 1
fi

# AND THE UNTRACKED ONES. `git diff` cannot see a file that has never been added, so a migration `dotnet ef`
# has just scaffolded is invisible to it -- and a green run on the very file the author is about to commit
# is the confident wrong answer this gate exists to avoid. Irrelevant in CI, where actions/checkout produces
# a clean tree, and it is the LOCAL run that decides whether anyone keeps running it before pushing.
untracked_status=0
git -c core.quotepath=false ls-files --others --exclude-standard -- "$MIG_DIR" \
  >> "$DIFF_LIST" || untracked_status=$?
if [ "$untracked_status" -ne 0 ]; then
  die "  LIST FAILED  git ls-files --others exited $untracked_status under $MIG_DIR."
  die "A migration scaffolded and not yet added would go unread. Nothing here is a pass."
  exit 1
fi

# A capture strips trailing newlines and this list is read back as lines (gh#164), so it goes through a file.
mapfile -t DIFFED < "$DIFF_LIST"

# THE GENERATED MIRRORS ARE READ, and they used to be skipped by name. `Foo.Designer.cs` declares
# `partial class Foo`, so a helper written there is a member of the migration class -- which was harmless
# while no helper was read anywhere, and became the one place a helper was invisible the moment the scan
# widened past `Up()`. They go in their own list because they are NOT migrations: `files_read` is the count
# the green line offers as evidence, and inflating it by two generated files per migration would make that
# number describe something else. Neither is required to declare an `Up()` -- they legitimately have none.
MIGRATIONS=()
PARTIALS=()
for path in ${DIFFED[@]+"${DIFFED[@]}"}; do
  [ -n "$path" ] || continue
  case "$path" in
    "$MIG_DIR"/*.Designer.cs)     PARTIALS+=("$path") ;;
    "$MIG_DIR"/*ModelSnapshot.cs) PARTIALS+=("$path") ;;
    "$MIG_DIR"/*.cs)              MIGRATIONS+=("$path") ;;
    *)                            continue ;;
  esac
done

# ---------------------------------------------------------------------------------------------------------
# The needles. Held in variables and matched UNQUOTED with bash's own `=~`, which is how a pattern carrying
# brackets and parentheses survives `[[ ]]`'s word parsing -- and it forks nothing, so there is no grep exit
# status to misread (gh#126).
# ---------------------------------------------------------------------------------------------------------

OPS=(DropTable DropColumn RenameColumn RenameTable AlterColumn DropSchema DropSequence)
# THE DOT MAY CARRY WHITESPACE. `migrationBuilder . DropColumn (` was left to `dotnet format`, which does
# catch it with three `error WHITESPACE` -- recorded once as "defence in depth that exists by luck". Review
# is right that the luck is thinner than it reads: `Format` is a LATER STEP IN THE SAME JOB, and this gate's
# whole placement argument is that it runs BEFORE `setup-dotnet`. A future job split would take that backstop
# away silently, with nothing going red. One character class costs less than the paragraph defending it.
DOT='\.[[:space:]]*'
# `.AlterColumn<string>(`, `.DropColumn(` -- and `.DropColumn` at END OF LINE with the `(` on the next one,
# which is legal C#, survives `dotnet format --verify-no-changes` untouched, and defeated every needle here
# until review found it. The mirror shape (`migrationBuilder` alone, `.DropColumn(` below) was already
# caught, so this closes the one-sided half.
CALL='([[:space:]]*[<(]|[[:space:]]*$)'
DROP_COLUMN_RE="${DOT}DropColumn${CALL}"
DROP_INDEX_RE="${DOT}DropIndex${CALL}"
SQL_CALL_RE="${DOT}Sql[[:space:]]*\\("
# A statement can also end on a line carrying nothing but its `;`, which is how an unterminated region used
# to run on and swallow the next call whole.
STATEMENT_END_RE='(\)[[:space:]]*;|^[[:space:]]*\)?[[:space:]]*;[[:space:]]*$)'
COMMENT_RE='^[[:space:]]*//'
MARKER_RE='^[[:space:]]*//[[:space:]]*destructive-migration:[[:space:]]*[^[:space:]]'
# THE BOUNDARY NEEDLES. These decide which REGION of a file is read rather than reporting a finding, and
# that makes them a different kind of pattern: a finding needle that is too loose costs a false positive,
# while a boundary needle that is too loose EXCLUDES CODE. Round two's hole was exactly here -- `DOWN_RE`
# was matched against raw text, so a `//` comment inside `Up()` naming the method it was talking about
# ("there is no void Down(MigrationBuilder …) worth writing") set `down_start` at the comment and `down_end`
# at the real declaration, and the whole rest of `Up()` fell out of every pass. A string literal did the
# same. They are therefore matched through `declares()` below, never against a line directly.
#
# The `(` may sit at END OF LINE, because a wrapped signature is legal C#: without that, `down_start` stayed
# 0, the whole file was scanned, and an ordinary additive migration was reddened by its own `Down()`.
UP_RE='void[[:space:]]+Up[[:space:]]*\([[:space:]]*(MigrationBuilder|$)'
DOWN_RE='void[[:space:]]+Down[[:space:]]*\([[:space:]]*(MigrationBuilder|$)'
# AND AN ACCESS MODIFIER IS NOT REQUIRED TO DECLARE A MEMBER. `static void RetireLegacy(MigrationBuilder)`
# is legal C#, defaults to private, and `dotnet format` will not add a modifier -- so a `MEMBER_RE` wanting
# one never found the end of the `Down()` body, `down_end` ran to end of file, and a sibling helper written
# that way was swallowed whole. Same shape as the decoy above, found by auditing rather than by review.
MEMBER_RE='^[[:space:]]+(public|private|protected|internal|static|override|virtual|abstract|sealed|partial|async|extern|unsafe)[[:space:]]'
# AND THE THING THAT DECIDES A BOUNDARY MUST BE AN OVERRIDE. Everything above tests a property of the LINE,
# and the property that matters is WHAT THE LINE DECLARES: a modifier keyword is evidence of a declaration,
# never of WHICH declaration, nor that it is a member rather than a local. Both of round three's defeats came
# through that gap, and each arrived through a widening made for the round before it:
#
#   - `static` was added to MEMBER_RE for the sibling-helper find, and `static` is also the modifier a C#
#     LOCAL FUNCTION may carry -- so `static void Down(MigrationBuilder b) { }` written inside `Up()` took
#     `down_start`, and the rest of `Up()` fell between it and the real declaration.
#   - the END-OF-LINE arm was added to DOWN_RE so a wrapped signature would stop reddening correct work, and
#     it also matches an ordinary OVERLOAD -- `private static void Down(\n int unused)` -- which wins the
#     first-match race against the real one.
#
# `Migration.Up` and `Migration.Down` are `protected virtual`, so a migration's are ALWAYS overrides; EF can
# scaffold nothing else, and all seven on `develop` are. A local function cannot be an override and neither
# is `Down(int)`, so one condition refuses both without narrowing either widening back. The cost is named:
# a hand-written migration declaring `Up` without `override` fails `NO Up()`, which is the loud direction.
OVERRIDE_RE='(^|[^[:alnum:]_])override[[:space:]]'

# Matched case-insensitively. The left boundary is what keeps `.DropColumn(` -- no space after Drop -- out
# of the DROP arm, so the two families cannot be confused for one another in a diagnostic.
SQL_NEEDLES=(
  '(^|[^[:alnum:]_])DROP[[:space:]]'
  '(^|[^[:alnum:]_])TRUNCATE([[:space:]]|$)'
  '(^|[^[:alnum:]_])ALTER[[:space:]]+TABLE[^;]*[[:space:]]TYPE[[:space:]]'
)
SQL_LABELS=('raw SQL: DROP' 'raw SQL: TRUNCATE' 'raw SQL: ALTER TABLE … TYPE')

failures=0
files_read=0
partials_read=0
ops_found=0
acknowledged=0
SQL_LABEL=''

# Sets SQL_LABEL and returns 0 on the first needle that matches. `[^;]*` and `[[:space:]]` both match a
# newline in bash's ERE, which is what lets the ALTER … TYPE arm span a verbatim string literal.
sql_label_for() {
  local text="$1" i needle rc=1
  shopt -s nocasematch
  for i in "${!SQL_NEEDLES[@]}"; do
    needle="${SQL_NEEDLES[$i]}"
    if [[ "$text" =~ $needle ]]; then
      SQL_LABEL="${SQL_LABELS[$i]}"
      rc=0
      break
    fi
  done
  shopt -u nocasematch
  return "$rc"
}

# COUNTS OCCURRENCES, because a boolean `=~` reports two calls on one line as one and the green line's
# operation count is the evidence a reader acts on. `${rest#*"${BASH_REMATCH[0]}"}` drops the leftmost
# occurrence of the text that matched, and `=~` matches leftmost, so that occurrence IS the match rather
# than an earlier lookalike. A zero-width match would never shrink `rest`, so it breaks rather than spins.
count_matches() {  # $1 text  $2 regex -> COUNT
  local rest="$1" re="$2"
  COUNT=0
  while [[ "$rest" =~ $re ]]; do
    [ -n "${BASH_REMATCH[0]}" ] || break
    COUNT=$(( COUNT + 1 ))
    rest="${rest#*"${BASH_REMATCH[0]}"}"
  done
}

# Findings are RECORDED against a line and adjudicated afterwards, rather than reported as they are found.
# That is what makes "one marker acknowledges one operation" true: the marker sits above a LINE, so a line
# carrying two destructive operations cannot be acknowledged at all -- there is one sentence and two acts,
# and no way to tell which act it describes. Review found both halves of that: two different operations
# sharing a line came back `2 acknowledged` under one marker naming only the first, and two IDENTICAL ones
# came back as `1 destructive operation(s)` because the match was a boolean. Both compile and both survive
# `dotnet format --verify-no-changes`.
record() {  # $1 line  $2 label
  local line="$1" label="$2"
  found_count[$line]=$(( ${found_count[$line]:-0} + 1 ))
  if [ -n "${found_labels[$line]:-}" ]; then
    found_labels[$line]+=", $label"
  else
    found_labels[$line]="$label"
  fi
}

# The marker must OPEN a line comment somewhere in the CONTIGUOUS `//` block immediately above, and carry a
# non-empty reason. The walk stops at the first line that is not a line comment -- a blank line, the opening
# brace, or the previous operation -- which is what keeps one marker from covering a whole file. It also
# stops at line 1: walking past it would index the array from the END, which is how a marker at the BOTTOM
# of a file would come to silence an operation at the top.
adjudicate() {  # $1 file  $2 line
  local file="$1" line="$2" i above count labels
  count="${found_count[$line]}"
  labels="${found_labels[$line]}"
  ops_found=$(( ops_found + count ))

  if [ "$count" -gt 1 ]; then
    # THE ACTIONABLE HALF LEADS, and the sentence has to fit the case where there is NO marker at all --
    # which is most of them. "One marker acknowledges ONE operation" as an opener describes a marker the
    # author may never have written.
    die "  AMBIGUOUS LINE  $file:$line  $count destructive operations on one line ($labels)"
    die "                  Put them on separate lines. A marker acknowledges ONE operation, and one"
    die "                  sentence cannot say which of two acts it describes."
    failures=$(( failures + 1 ))
    return 0
  fi

  for (( i = line - 1; i >= 1; i-- )); do
    above="${LINES[$(( i - 1 ))]:-}"
    if [[ ! "$above" =~ $COMMENT_RE ]]; then break; fi
    if [[ "$above" =~ $MARKER_RE ]]; then
      acknowledged=$(( acknowledged + 1 ))
      return 0
    fi
  done
  die "  DESTRUCTIVE  $file:$line  $labels  -- no '// $MARKER' in the comment block above"
  failures=$(( failures + 1 ))
}

# ---------------------------------------------------------------------------------------------------------
# READING ONE FILE. Called for a migration, where `Up(MigrationBuilder)` must be locatable, and for the
# generated partials beside it, where it must not be.
# ---------------------------------------------------------------------------------------------------------

scan_file() {  # $1 path  $2 1 if Up(MigrationBuilder) must be locatable
  local file="$1" require_up="$2"
  local sql_open=0 sql_text='' indent='' brace_re=''

  mapfile -t LINES < "$file"
  n=${#LINES[@]}
  # .gitattributes pins LF, but a stray CR would defeat every `$`-anchored match below in silence.
  for (( i = 0; i < n; i++ )); do LINES[$i]="${LINES[$i]%$'\r'}"; done

  # PASS 0 -- the migrationBuilder.Sql( regions, over the WHOLE file and BEFORE any boundary is decided.
  # THE ORDER IS THE POINT. The boundaries below are read off raw text, and a boundary a string literal can
  # pick is a boundary an attacker or an unlucky comment picks -- so they consult this map, which means the
  # map cannot in turn depend on them. A region opened inside `Down()` is tracked here for that reason
  # alone; nothing is REPORTED from one until `scanned()` has had its say, below.
  declare -A in_region=()
  declare -A region_text=()
  local -a region_opens=()
  for (( ln = 1; ln <= n; ln++ )); do
    line="${LINES[$(( ln - 1 ))]}"
    if [ "$sql_open" -eq 0 ]; then
      if [[ "$line" =~ $COMMENT_RE ]]; then continue; fi
      if [[ ! "$line" =~ $SQL_CALL_RE ]]; then continue; fi
      sql_open=$ln
      sql_text="$line"
      in_region[$ln]=1
      region_opens+=("$ln")
    else
      in_region[$ln]=1
      sql_text+=$'\n'"$line"
    fi
    if [[ "$line" =~ $STATEMENT_END_RE ]]; then
      region_text[$sql_open]="$sql_text"
      sql_open=0
      sql_text=''
    fi
  done
  # A region left open at the end of the file is closed there and still scanned: unread is not a pass.
  if [ "$sql_open" -ne 0 ]; then region_text[$sql_open]="$sql_text"; fi

  # A LINE MAY ONLY DECIDE A BOUNDARY IF IT DECLARES AN OVERRIDE OF `Up`/`Down` ON THE MIGRATION CLASS. Not
  # a `//` comment, not the interior of a string literal, carrying a modifier keyword, AND carrying
  # `override`. The first three test properties of the LINE; the fourth is the only one that tests what the
  # line DECLARES, and two rounds of boundary bugs are what it took to notice the difference.
  #
  # Each condition has a fixture written to defeat EXACTLY it -- except the `//` test, which is SUBSUMED by
  # MEMBER_RE (a `//` line can carry no leading modifier) and pinned by nothing. That is stated in the
  # ledger rather than papered over: an unpinnable guard whose redundancy is written down cannot mislead
  # anyone about what is proven, and an unlabelled one is indistinguishable from a guard that works.
  declares() {  # $1 line number  $2 the signature regex
    local ln="$1" re="$2" text="${LINES[$(( $1 - 1 ))]}"
    if [ -n "${in_region[$ln]:-}" ]; then return 1; fi
    if [[ "$text" =~ $COMMENT_RE ]]; then return 1; fi
    if [[ ! "$text" =~ $MEMBER_RE ]]; then return 1; fi
    if [[ ! "$text" =~ $OVERRIDE_RE ]]; then return 1; fi
    if [[ ! "$text" =~ $re ]]; then return 1; fi
    return 0
  }

  up_start=0
  for (( ln = 1; ln <= n; ln++ )); do
    if declares "$ln" "$UP_RE"; then up_start=$ln; break; fi
  done
  if [ "$require_up" -eq 1 ] && [ "$up_start" -eq 0 ]; then
    die "  NO Up()  $file  -- the migration's Up(MigrationBuilder) could not be located."
    die "This gate read nothing in that file. It is not a pass."
    failures=$(( failures + 1 ))
    unset -f declares
    return 0
  fi

  # WHAT IS SCANNED IS THE WHOLE FILE EXCEPT THE Down() BODY -- not the Up() body alone. Reading only Up()
  # meant an operation in a SIBLING MEMBER of the same class, a private helper `Up()` calls, was never read
  # at all: the member pattern that ends the body also ends the scan. Review wrote one, it compiled, it
  # formatted clean, and the gate said `0 destructive operation(s)`. Everything outside Down() is now read,
  # so a helper is covered wherever the class puts it.
  #
  # THE COST IS NAMED RATHER THAN HIDDEN: a helper called only from Down() is read too, because nothing
  # here says which of the two calls it. That is the loud direction -- mark it, or inline it into Down().
  down_start=0
  for (( ln = 1; ln <= n; ln++ )); do
    if declares "$ln" "$DOWN_RE"; then down_start=$ln; break; fi
  done

  # WHERE THE Down() BODY ENDS, by TWO rules, and the EARLIER of the two wins. A body ends at its closing
  # brace, in the declaration's own column, and it ends at the next member declaration -- and each rule has
  # a blind spot the other covers. `MEMBER_RE` alone missed `static void Helper(...)`, and Down() then ran
  # to end of file and swallowed it. The brace rule alone would miss a `Down(...) { }` written on one line.
  #
  # Neither consults `in_region`, and that is deliberate rather than an oversight: a string literal that
  # falsely matches ends the body EARLY, so more is scanned -- the loud direction, and the one this gate
  # argues for everywhere else. Skipping region interiors here would let an unterminated literal inside
  # Down() push `down_end` to end of file and hide a sibling helper below it, which is the quiet one.
  down_end=$(( n + 1 ))
  if [ "$down_start" -ne 0 ]; then
    if [[ "${LINES[$(( down_start - 1 ))]}" =~ ^([[:space:]]*) ]]; then indent="${BASH_REMATCH[1]}"; fi
    brace_re="^${indent}\\}[[:space:]]*$"
    for (( ln = down_start + 1; ln <= n; ln++ )); do
      line="${LINES[$(( ln - 1 ))]}"
      if [[ "$line" =~ $MEMBER_RE ]] || [[ "$line" =~ $brace_re ]]; then
        down_end=$ln
        break
      fi
    done
  fi

  declare -A found_count=()
  declare -A found_labels=()

  scanned() {  # $1 line number -- 0 if it belongs to Down()
    [ "$down_start" -eq 0 ] && return 0
    [ "$1" -lt "$down_start" ] && return 0
    [ "$1" -ge "$down_end" ] && return 0
    return 1
  }

  # Pass 1 -- is there a DropColumn anywhere outside Down()? That is what arms the DropIndex rule.
  has_drop_column=0
  for (( ln = 1; ln <= n; ln++ )); do
    if ! scanned "$ln"; then continue; fi
    line="${LINES[$(( ln - 1 ))]}"
    if [[ "$line" =~ $COMMENT_RE ]]; then continue; fi
    if [[ "$line" =~ $DROP_COLUMN_RE ]]; then has_drop_column=1; fi
  done

  # Pass 2 -- the findings from the regions pass 0 mapped. A literal may span many lines, so the SQL needles
  # run over the WHOLE region and the finding is anchored at the line that OPENS it, which is where the
  # marker goes -- and a region whose opener sits inside Down() reports nothing, like any other line there.
  for ln in ${region_opens[@]+"${region_opens[@]}"}; do
    if ! scanned "$ln"; then continue; fi
    if sql_label_for "${region_text[$ln]}"; then record "$ln" "$SQL_LABEL"; fi
  done

  # Pass 3 -- the operation calls on EVERY scanned line, region or not, and a SQL needle on any line outside
  # a region. The operation needles deliberately run INSIDE a Sql( region as well: a region whose statement
  # ends on a line of its own used to run on and swallow the next call, and `in_region` then skipped it. A
  # false positive on an operation name inside a SQL literal is the loud direction this gate already argues
  # for everywhere else; a swallowed DropColumn is the quiet one.
  for (( ln = 1; ln <= n; ln++ )); do
    if ! scanned "$ln"; then continue; fi
    line="${LINES[$(( ln - 1 ))]}"
    if [[ "$line" =~ $COMMENT_RE ]]; then continue; fi

    for op in "${OPS[@]}"; do
      op_re="${DOT}${op}${CALL}"
      count_matches "$line" "$op_re"
      for (( k = 0; k < COUNT; k++ )); do record "$ln" "$op"; done
    done
    if [ "$has_drop_column" -eq 1 ]; then
      count_matches "$line" "$DROP_INDEX_RE"
      for (( k = 0; k < COUNT; k++ )); do
        record "$ln" "DropIndex (paired with a DropColumn in the same migration)"
      done
    fi
    if [ -z "${in_region[$ln]:-}" ] && sql_label_for "$line"; then
      record "$ln" "$SQL_LABEL, outside a recognised migrationBuilder.Sql( call"
    fi
  done

  # Adjudicate in line order, so a reader's eye moves down the file rather than around it.
  for (( ln = 1; ln <= n; ln++ )); do
    if [ -n "${found_count[$ln]:-}" ]; then adjudicate "$file" "$ln"; fi
  done
  unset -f scanned declares
}

for file in ${MIGRATIONS[@]+"${MIGRATIONS[@]}"}; do
  if [ ! -f "$file" ]; then
    die "  UNREADABLE  $file is in the diff and not on disk. This gate did not read it."
    failures=$(( failures + 1 ))
    continue
  fi
  files_read=$(( files_read + 1 ))
  scan_file "$file" 1
done

# The generated partials, read on the same passes and counted separately -- see the PARTIALS list above.
for file in ${PARTIALS[@]+"${PARTIALS[@]}"}; do
  if [ ! -f "$file" ]; then
    die "  UNREADABLE  $file is in the diff and not on disk. This gate did not read it."
    failures=$(( failures + 1 ))
    continue
  fi
  partials_read=$(( partials_read + 1 ))
  scan_file "$file" 0
done

PARTIAL_NOTE=''
if [ "$partials_read" -ne 0 ]; then
  PARTIAL_NOTE="; $partials_read generated partial(s) also read"
fi

if [ "$failures" -ne 0 ]; then
  echo >&2
  # TWO numbers, because one refusal can cover two operations: an ambiguous line is a single site and more
  # than one act. Reporting only the refusals would undercount exactly what the ambiguity rule exists to
  # stop being undercounted. AND BOTH ARE LABELLED, because the second counts the ACKNOWLEDGED ones too:
  # `1 refusal(s) over 2 destructive operation(s)` for one marked drop beside one unmarked read as two
  # problems where there is one, and this is the one number the gate asks a reader to act on.
  die "$failures refusal(s), $ops_found destructive operation(s) read, in $files_read migration file(s)"
  die "added or changed against $BASE$PARTIAL_NOTE."
  explain
  exit 1
fi

if [ "$files_read" -eq 0 ]; then
  ok "ok  no migration added or changed against $BASE (${#DIFFED[@]} path(s) under $MIG_DIR in the diff)$PARTIAL_NOTE."
  exit 0
fi

ok "ok  $files_read migration file(s) added or changed against $BASE; $ops_found destructive operation(s), $acknowledged acknowledged, 0 unacknowledged$PARTIAL_NOTE."
