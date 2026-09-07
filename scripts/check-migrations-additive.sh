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
#   No operation in the ENUMERATED SET below appears UNACKNOWLEDGED in the `Up()` body of a migration this
#   diff adds or changes.
#
# It does NOT say a migration is safe. It does not judge whether an ACKNOWLEDGED destructive operation is
# right -- that is the reviewer's, and the marker is the sentence they judge. It says nothing about
# migrations already on the base branch, about `Down()`, or about data.
#
# WHAT IT COVERS
#
#   In the `Up()` body, on a line that is not a `//` comment:
#     .DropTable(         .DropColumn(        .RenameColumn(      .RenameTable(
#     .AlterColumn<T>( or .AlterColumn(       .DropSchema(        .DropSequence(
#     .DropIndex(         -- ONLY when the same Up() body also calls .DropColumn(; see below
#   And, over the whole text of a `migrationBuilder.Sql(...)` call (multi-line literals included), or on any
#   other non-comment body line, case-insensitively:
#     DROP <space>        TRUNCATE            ALTER TABLE … <space>TYPE<space>
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
#     them would put a marker on the ordinary rename-and-recreate dance EF scaffolds.
#   - The `Down()` body. Every additive migration's Down() drops what its Up() added -- reading Down() would
#     redden all five migrations on `develop` and the gate would be deleted the first day.
#   - Migrations already on the base branch. They predate the rule (ADR-0023 is later than all five) and are
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
# line" this gate exists for, and two operations always need two markers because each one's block is broken
# by the other. The reason must be non-empty; a bare `// destructive-migration:` is a rubber stamp and is
# refused. In the file rather than in the PR body so it is reviewable, greppable and survives the merge --
# a PR body is read once.
#
# HOW THE DIFF IS TAKEN
#
# `git merge-base BASE HEAD`, then `git diff --diff-filter=ACMR <merge-base>` -- so it reads what THIS branch
# added or changed, committed or not, and never a migration the base branch grew after the branch point.
# Deletions are filtered out (a deleted migration is a history rewrite, not a schema change) and
# `*.Designer.cs` and the model snapshot are excluded: they are generated mirrors carrying no operations.
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

  2. ACKNOWLEDGE IT, on the line immediately above the operation:

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

# A capture strips trailing newlines and this list is read back as lines (gh#164), so it goes through a file.
mapfile -t DIFFED < "$DIFF_LIST"

MIGRATIONS=()
for path in ${DIFFED[@]+"${DIFFED[@]}"}; do
  [ -n "$path" ] || continue
  case "$path" in
    *.Designer.cs)     continue ;;   # generated mirror of the model; carries no operations
    *ModelSnapshot.cs) continue ;;   # ditto
    "$MIG_DIR"/*.cs)   MIGRATIONS+=("$path") ;;
    *)                 continue ;;
  esac
done

# ---------------------------------------------------------------------------------------------------------
# The needles. Held in variables and matched UNQUOTED with bash's own `=~`, which is how a pattern carrying
# brackets and parentheses survives `[[ ]]`'s word parsing -- and it forks nothing, so there is no grep exit
# status to misread (gh#126).
# ---------------------------------------------------------------------------------------------------------

OPS=(DropTable DropColumn RenameColumn RenameTable AlterColumn DropSchema DropSequence)
CALL='[[:space:]]*[<(]'                       # `.AlterColumn<string>(` and `.DropColumn(` alike
DROP_COLUMN_RE="\\.DropColumn${CALL}"
DROP_INDEX_RE='\.DropIndex[[:space:]]*\('
SQL_CALL_RE='\.Sql[[:space:]]*\('
STATEMENT_END_RE='\)[[:space:]]*;'
COMMENT_RE='^[[:space:]]*//'
MARKER_RE='^[[:space:]]*//[[:space:]]*destructive-migration:[[:space:]]*[^[:space:]]'
UP_RE='void[[:space:]]+Up[[:space:]]*\(MigrationBuilder'
MEMBER_RE='^[[:space:]]+(public|private|protected|internal)[[:space:]]'

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

# The marker must OPEN a line comment somewhere in the CONTIGUOUS `//` block immediately above, and carry a
# non-empty reason. The walk stops at the first line that is not a line comment -- a blank line, the opening
# brace, or the previous operation -- which is what keeps one marker from covering a whole file. It also
# stops at line 1: a `$(( line - 2 ))` of -1 would index the array from the END, which is how a marker at
# the BOTTOM of a file would come to silence an operation at the top.
report() {
  local file="$1" line="$2" label="$3" i above
  ops_found=$(( ops_found + 1 ))
  for (( i = line - 1; i >= 1; i-- )); do
    above="${LINES[$(( i - 1 ))]:-}"
    if [[ ! "$above" =~ $COMMENT_RE ]]; then break; fi
    if [[ "$above" =~ $MARKER_RE ]]; then
      acknowledged=$(( acknowledged + 1 ))
      return 0
    fi
  done
  die "  DESTRUCTIVE  $file:$line  $label  -- no '// $MARKER' in the comment block above"
  failures=$(( failures + 1 ))
}

for file in ${MIGRATIONS[@]+"${MIGRATIONS[@]}"}; do
  if [ ! -f "$file" ]; then
    die "  UNREADABLE  $file is in the diff and not on disk. This gate did not read it."
    failures=$(( failures + 1 ))
    continue
  fi
  files_read=$(( files_read + 1 ))

  mapfile -t LINES < "$file"
  n=${#LINES[@]}
  # .gitattributes pins LF, but a stray CR would defeat every `$`-anchored match below in silence.
  for (( i = 0; i < n; i++ )); do LINES[$i]="${LINES[$i]%$'\r'}"; done

  up_start=0
  for (( i = 0; i < n; i++ )); do
    if [[ "${LINES[$i]}" =~ $UP_RE ]]; then
      up_start=$(( i + 1 ))
      break
    fi
  done
  if [ "$up_start" -eq 0 ]; then
    die "  NO Up()  $file  -- the migration's Up(MigrationBuilder) could not be located."
    die "This gate read nothing in that file. It is not a pass."
    failures=$(( failures + 1 ))
    continue
  fi

  # The body runs to the next member declaration at class indentation (Down(), normally), or to EOF.
  up_end=$(( n + 1 ))
  for (( i = up_start; i < n; i++ )); do
    if [[ "${LINES[$i]}" =~ $MEMBER_RE ]]; then
      up_end=$(( i + 1 ))
      break
    fi
  done

  # Pass 1 -- is there a DropColumn anywhere in this Up()? That is what arms the DropIndex rule.
  has_drop_column=0
  for (( ln = up_start + 1; ln < up_end; ln++ )); do
    line="${LINES[$(( ln - 1 ))]}"
    if [[ "$line" =~ $COMMENT_RE ]]; then continue; fi
    if [[ "$line" =~ $DROP_COLUMN_RE ]]; then has_drop_column=1; fi
  done

  # Pass 2 -- the migrationBuilder.Sql(...) regions. A literal may span many lines, so the needles run over
  # the WHOLE region and the finding is anchored at the line that OPENS it, which is where the marker goes.
  # A region left open at the end of Up() is closed there and still scanned: unread is not a pass.
  declare -A in_region=()
  sql_open=0
  sql_text=''
  for (( ln = up_start + 1; ln < up_end; ln++ )); do
    line="${LINES[$(( ln - 1 ))]}"
    if [ "$sql_open" -eq 0 ]; then
      if [[ "$line" =~ $COMMENT_RE ]]; then continue; fi
      if [[ ! "$line" =~ $SQL_CALL_RE ]]; then continue; fi
      sql_open=$ln
      sql_text="$line"
      in_region[$ln]=1
    else
      in_region[$ln]=1
      sql_text+=$'\n'"$line"
    fi
    if [[ "$line" =~ $STATEMENT_END_RE ]]; then
      if sql_label_for "$sql_text"; then report "$file" "$sql_open" "$SQL_LABEL"; fi
      sql_open=0
      sql_text=''
    fi
  done
  if [ "$sql_open" -ne 0 ]; then
    if sql_label_for "$sql_text"; then report "$file" "$sql_open" "$SQL_LABEL"; fi
  fi

  # Pass 3 -- the operation calls, and any SQL needle on a body line outside a Sql( region.
  for (( ln = up_start + 1; ln < up_end; ln++ )); do
    if [ -n "${in_region[$ln]:-}" ]; then continue; fi
    line="${LINES[$(( ln - 1 ))]}"
    if [[ "$line" =~ $COMMENT_RE ]]; then continue; fi

    for op in "${OPS[@]}"; do
      op_re="\\.${op}${CALL}"
      if [[ "$line" =~ $op_re ]]; then report "$file" "$ln" "$op"; fi
    done
    if [ "$has_drop_column" -eq 1 ] && [[ "$line" =~ $DROP_INDEX_RE ]]; then
      report "$file" "$ln" "DropIndex (paired with a DropColumn in the same Up())"
    fi
    if sql_label_for "$line"; then
      report "$file" "$ln" "$SQL_LABEL, outside a recognised migrationBuilder.Sql( call"
    fi
  done
  unset in_region
done

if [ "$failures" -ne 0 ]; then
  echo >&2
  die "$failures unacknowledged destructive operation(s) in $files_read migration file(s) added or changed"
  die "against $BASE."
  explain
  exit 1
fi

if [ "$files_read" -eq 0 ]; then
  ok "ok  no migration added or changed against $BASE (${#DIFFED[@]} path(s) under $MIG_DIR in the diff)."
  exit 0
fi

ok "ok  $files_read migration file(s) added or changed against $BASE; $ops_found destructive operation(s), $acknowledged acknowledged, 0 unacknowledged."
