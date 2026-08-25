#!/usr/bin/env bash
# check-doc-sizes.sh — fail when the routing map's ~tok column stops describing the files it routes to.
#
#   scripts/check-doc-sizes.sh [repo-root]
#
# WHY THIS EXISTS (gh#160)
#
# `documentation/README.md` prices every document it routes to, so a reader can see what a read costs BEFORE
# paying for it, and the file has always told its own maintainers to "keep them roughly accurate — a size
# column nobody updates is worse than none, because it is trusted". Nothing kept them.
#
# By gh#160 the column had drifted far enough to INVERT the advice built on top of it. `AGENT-MEMORY.md` was
# priced at 0.8K beside the words "Cheap; just read it" while measuring 6.8K — 8.5x understated — and
# `architecture.md` was recommended as "the cheapest whole-file read here" while being the second most
# expensive row in its own table. A stale number is ordinary. A number that reverses the recommendation
# standing next to it is the routing map sending every reader the wrong way, in the file whose only job is
# routing.
#
# The drift is STRUCTURAL, which is why it is checked rather than remembered. `AGENT-MEMORY.md` is under
# standing instructions to grow — "append, don't overwrite" is its own rule — so its row is wrong again a few
# entries after anyone corrects it, and nothing in an ordinary pull request ever looks at the row that prices
# it. A rule whose violation is produced by obeying another rule cannot be left to memory.
#
# HOW THE NUMBER IS DERIVED
#
# tokens = bytes / 4, bytes from `wc -c` on the working tree. It is the ordinary English-text approximation,
# it is one command anybody can re-run, and being reproducible matters more here than being exact: the
# column's job is to separate 0.6K from 8.9K, not 6.8K from 6.9K. `wc -c` is spelled out below rather than
# replaced by a cheaper `stat` so that the gate runs the same command the map tells a reader to run.
#
# `wc -c` and `git cat-file -s` disagree wherever a checkout carries CRLF. They cannot here — `.gitattributes`
# pins `* text=auto eol=lf`, so the working tree is LF on Windows too, and the two agree byte for byte across
# all ten routed files. If that pin is ever removed this gate starts reporting Windows-only failures; fix the
# pin, not this script.
#
# WHAT IT ENFORCES
#
#   1. Every data row under `## Start here` and `## Working agreements` carries a `~tok` value within
#      TOLERANCE_PCT of its measured size. Both headings must exist and both must have rows, so a
#      restructured table cannot turn this gate green by leaving it nothing to read.
#   2. No row's prose makes a size claim. The `~tok` column is the only place a size claim lives, because a
#      superlative in prose is the same fact stated twice and only one of the two copies ever gets corrected
#      — which is exactly how "the cheapest whole-file read here" survived on the largest row.
#
# Rule 2 matches a closed vocabulary of size adjectives, so rewording WITHIN that vocabulary cannot silence
# it. Rewording out of it can: "costs less than the others" makes the same claim and passes. Only real
# comprehension closes that, and it is judged not worth it — the failure this actually met was the word
# "cheapest", and the map now states that the column is the only place for such a claim, so the next author
# is told. Same call, and the same reasoning, as the block-comment blind spot in check-paced-paging.sh.
#
# ANYTHING IT CANNOT PARSE IS A FAILURE, NEVER A SKIP. A row with no link, a `~tok` cell that is not a size,
# a target that is not on disk, a renamed heading — each is reported by its own name and fails the run. The
# alternative is a gate that quietly prices fewer rows every time the table is edited, which is the shape of
# every dead guard this repository has had (gh#43, gh#98, gh#114, gh#126, gh#164).
#
# ITS OWN ABILITY TO FAIL IS TESTED: scripts/check-doc-sizes-selftest.sh runs this script against fixtures
# whose faults are known and requires it to reject each one BY NAME, and to accept a sound one. Both run in
# ci.yml's `docs` job, which is already a required context on all three rungs — so this is enforcing rather
# than reporting, and no ruleset was written to make it so.

set -euo pipefail

die()  { printf '\033[31m%s\033[0m\n' "$*" >&2; }
ok()   { printf '\033[32m%s\033[0m\n' "$*"; }
info() { printf '%s\n' "$*"; }

REPO_ROOT="${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"
cd "$REPO_ROOT" || { die "  NO SUCH ROOT  $REPO_ROOT"; exit 1; }

MAP="documentation/README.md"
MAP_DIR="$(dirname "$MAP")"

# How far a row may sit from its measured value before it is called wrong. Wide enough that an ordinary
# append does not fail an unrelated pull request; narrow enough that it would have caught every drift gh#160
# found, including the 26%-over `architecture.md` row that carried the inverted recommendation.
TOLERANCE_PCT=25
BYTES_PER_TOKEN=4

# The sections whose rows are priced. Both are required to exist AND to have rows: a heading renamed without
# this script being told is how a gate stops checking in silence.
SECTIONS=("## Start here" "## Working agreements")

# Rule 2's closed vocabulary, word-anchored so "largest" does not fire inside "enlargement". Matched against
# a lowercased cell, so the pattern itself needs no case folding.
SIZE_CLAIM='(^|[^[:alnum:]])(cheap(est|er)?|small(est|er)?|larg(est|er)|big(gest|ger)|fast(est|er)|slow(est|er)|short(est|er)|long(est|er)|tiniest|lightest|heaviest)([^[:alnum:]]|$)'

# The two table lines that carry no price. Anything else under a priced heading must parse.
SEPARATOR_ROW='^\|[-:| ]+\|$'
HEADER_ROW='^\| *Document *\|'
SIZE_CELL='^[0-9]+(\.[0-9]+)?K$'

failures=0
rows_checked=0

explain() {
  cat >&2 <<'EXPLAIN'

documentation/README.md's ~tok column is the price list a reader consults before opening a document, and it
is only worth consulting while it is true.

  tokens = bytes / 4, bytes from `wc -c`, rounded to 0.1K. The value to write is printed above each failure.

If you APPENDED to a routed document — AGENT-MEMORY.md especially — correct its row in this same pull
request. That is the same-PR docs rule in AGENTS.md, and it is one character of editing.

If you RESTRUCTURED the map — renamed a section heading, added a column, added a row with no size — update
this script in the same pull request. Deleting the gate because the table moved is how an 8.5x drift goes
unnoticed a second time.
EXPLAIN
}

# BOTH HELPERS ASSIGN A GLOBAL RATHER THAN PRINTING, and are called bare rather than in `$( )`. A command
# substitution forks, and on a Windows checkout a fork costs seconds rather than microseconds -- the printing
# form of these two turned a check that answers in under a second on the CI runner into an eighty-second wait
# locally, which is how a doc gate stops being run before pushing.
#
# "5.5K" -> 5500, "5K" -> 5000, "0.8K" -> 800. Integer arithmetic throughout: bash has no floats, and an
# `awk` per row would put the forks straight back.
STATED=0
stated_tokens() {
  local v="${1%K}" whole frac
  whole="${v%%.*}"
  if [ "$v" = "$whole" ]; then frac=""; else frac="${v#*.}"; fi
  frac="${frac}000"
  frac="${frac:0:3}"
  STATED=$(( 10#$whole * 1000 + 10#$frac ))
}

# 6825 -> "6.8K", 577 -> "0.6K". Rounds to the nearest 0.1K, the granularity the column is written at, so the
# value reported is the value to paste.
SUGGESTED=""
as_k() {
  local tenths=$(( ($1 + 50) / 100 ))
  SUGGESTED="$(( tenths / 10 )).$(( tenths % 10 ))K"
}

if [ ! -f "$MAP" ]; then
  die "  MISSING  $MAP"
  die "The routing map moved. This gate no longer checks anything — point it at the new path."
  exit 1
fi

# THE MAP IS READ ONCE, ON ITS OWN LINE, AND THE READ IS CHECKED (gh#126). `mapfile` from a plain file
# redirection does fail the script when the file cannot be opened — unlike `mapfile < <(cmd)`, whose process
# substitution status is never examined and which is how `commit-hygiene` once reported "no commits to check"
# and exited 0. The length is asserted anyway: an empty array here would walk zero lines and fall through to
# the green line at the bottom.
lines=()
mapfile -t lines < "$MAP"
if [ "${#lines[@]}" -eq 0 ]; then
  die "  EMPTY  $MAP has no lines"
  die "The map is not empty in this repository, so this is a broken read rather than a clean map."
  exit 1
fi

# Parallel arrays rather than an associative one: SECTIONS is fixed and tiny, and this keeps the ordering of
# the report the same as the ordering of the map.
section_seen=()
section_rows=()
for _ in "${SECTIONS[@]}"; do
  section_seen+=(0)
  section_rows+=(0)
done

current=-1
for line in "${lines[@]}"; do
  if [[ "$line" == '## '* ]]; then
    current=-1
    for i in "${!SECTIONS[@]}"; do
      if [ "$line" = "${SECTIONS[$i]}" ]; then
        current="$i"
        section_seen[$i]=1
        break
      fi
    done
    continue
  fi

  [ "$current" -ge 0 ] || continue
  [[ "$line" == '|'* ]] || continue
  # Spelled as `if`, not `[[ ... ]] && continue`: an AND-OR list whose left side fails is exempt from
  # `set -e` only where it is not the last command of the list, and that exemption is exactly the kind of
  # positional subtlety claim.sh already got bitten by. An `if` has no such caveat.
  if [[ "$line" =~ $SEPARATOR_ROW ]]; then continue; fi
  if [[ "$line" =~ $HEADER_ROW ]]; then continue; fi

  rest="${line#|}"
  cell_doc="${rest%%|*}"
  rest="${rest#*|}"
  cell_tok="${rest%%|*}"
  cell_prose="${rest#*|}"

  # Trim with parameter expansion only — no subshell, no `sed`.
  cell_doc="${cell_doc#"${cell_doc%%[![:space:]]*}"}"; cell_doc="${cell_doc%"${cell_doc##*[![:space:]]}"}"
  cell_tok="${cell_tok#"${cell_tok%%[![:space:]]*}"}"; cell_tok="${cell_tok%"${cell_tok##*[![:space:]]}"}"

  section_rows[$current]=$(( section_rows[current] + 1 ))
  rows_checked=$(( rows_checked + 1 ))

  if [[ "$cell_doc" != *'](['* && "$cell_doc" != *']('* ]]; then
    die "  NO LINK  ${SECTIONS[$current]}: row '$cell_doc' has no [text](target) in its first cell"
    die "Nothing can be measured for it, so its price is unverifiable. If the table gained a column or its"
    die "header was renamed, update this script in the same pull request."
    failures=$(( failures + 1 ))
    continue
  fi

  target="${cell_doc#*](}"
  target="${target%%)*}"
  target="${target%%#*}"
  resolved="$MAP_DIR/$target"

  if [[ ! "$cell_tok" =~ $SIZE_CELL ]]; then
    die "  NOT A SIZE  ${SECTIONS[$current]}: $target is priced '$cell_tok'"
    die "Every routed row carries a measured size. A placeholder is a row nothing prices and nothing checks."
    failures=$(( failures + 1 ))
    continue
  fi

  if [ ! -f "$resolved" ]; then
    die "  MISSING  ${SECTIONS[$current]}: $target does not exist at $resolved"
    die "It cannot be measured, so this cannot report its price accurate. (check-doc-links.sh reports the"
    die "same row as a broken link; fix the link and this clears with it.)"
    failures=$(( failures + 1 ))
    continue
  fi

  bytes=""
  wc_status=0
  bytes="$(wc -c < "$resolved")" || wc_status=$?
  if [ "$wc_status" -ne 0 ]; then
    die "  UNREADABLE  $resolved (wc exited $wc_status)"
    die "It has NOT been measured, so this cannot report its price accurate."
    exit 1
  fi
  bytes="${bytes//[[:space:]]/}"

  measured=$(( bytes / BYTES_PER_TOKEN ))
  if [ "$measured" -le 0 ]; then
    die "  EMPTY  ${SECTIONS[$current]}: $target measures $bytes bytes"
    die "A routed document with no content is a routing error, not a cheap read."
    failures=$(( failures + 1 ))
    continue
  fi

  stated_tokens "$cell_tok"; stated="$STATED"
  as_k "$measured";            suggested="$SUGGESTED"

  delta=$(( stated - measured ))
  [ "$delta" -ge 0 ] || delta=$(( -delta ))
  dev_pct=$(( delta * 100 / measured ))

  if [ "$dev_pct" -gt "$TOLERANCE_PCT" ]; then
    die "  OUT OF DATE  $target says $cell_tok, measures $suggested ($bytes bytes / $BYTES_PER_TOKEN) — ${dev_pct}% off, tolerance ${TOLERANCE_PCT}%"
    die "               write $suggested in that row's ~tok cell."
    failures=$(( failures + 1 ))
  else
    printf '  ok  %-34s says %-6s measures %-6s (%s%% off)
' "$target" "$cell_tok" "$suggested" "$dev_pct"
  fi

  if [[ "${cell_prose,,}" =~ $SIZE_CLAIM ]]; then
    die "  SIZE CLAIM  $target's prose says '${BASH_REMATCH[2]}'"
    die "              The ~tok column is the only place a size claim lives. Prose restating it is the same"
    die "              fact in two places, and only the column ever gets corrected — which is how the"
    die "              largest row kept calling itself the cheapest. Delete the claim; the number is there."
    failures=$(( failures + 1 ))
  fi
done

for i in "${!SECTIONS[@]}"; do
  if [ "${section_seen[$i]}" -ne 1 ]; then
    die "  NO SECTION  $MAP has no '${SECTIONS[$i]}' heading"
    die "Either the map was restructured or this script names a section that no longer exists. Until the two"
    die "are reconciled, the rows under it are priced by nothing."
    failures=$(( failures + 1 ))
  elif [ "${section_rows[$i]}" -eq 0 ]; then
    die "  NO ROWS  '${SECTIONS[$i]}' contains no priced table rows"
    die "This gate just checked nothing under that heading, and would otherwise have called the map accurate."
    failures=$(( failures + 1 ))
  fi
done

# The pass carries its own evidence, the way check-no-order-path.sh prints the number of files it read: a
# green line naming zero rows is the shape every dead gate in this repository had.
if [ "$rows_checked" -eq 0 ]; then
  die "  NOTHING CHECKED  no priced rows were found anywhere in $MAP"
  die "The map is not empty, so this is a broken gate rather than a clean map."
  exit 1
fi

if [ "$failures" -gt 0 ]; then
  echo >&2
  die "$failures problem(s) across $rows_checked routed rows in $MAP."
  explain
  exit 1
fi

ok "ok  $rows_checked routed rows in $MAP, every ~tok within ${TOLERANCE_PCT}% and no size claims in prose."
