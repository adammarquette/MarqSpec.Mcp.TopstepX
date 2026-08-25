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

# How far a row may sit from its measured value before it is called wrong, as a percentage of the MEASURED
# value.
#
# IT IS A BAND THAT CATCHES INVERSIONS AND MISSES ORDINARY DRIFT, and saying otherwise would be the same
# mistake this script exists to stop. Run against the pre-gh#160 map (`f7bdcae:documentation/README.md`) it
# flags 6 of the 10 rows and lets 4 through:
#
#   flagged   AGENT-MEMORY.md 88% · work-estimate-rubric.md 56% · project-board-workflow.md 44%
#             mcp-tool-catalog.md 43% · architecture.md 35% (the row carrying the inverted recommendation)
#             agents/README.md -- as NOT A SIZE, it was priced `index`
#   tolerated prd.md 14% · data-dictionary.md 10% · CONTRIBUTING.md 7% · AGENTS.md 2%
#
# So four of the values gh#160 corrected would never have been demanded by this gate, `prd.md` among them.
# That is the intended trade: tightening far enough to catch a 14% row means firing on rounding and on a
# paragraph added to a short document, and a gate that reddens a required check on noise is deleted by the
# first person it wrongly stops. The gate is the floor -- it stops the column from reversing its own advice
# -- and the same-PR docs rule is the standard.
TOLERANCE_PCT=25
BYTES_PER_TOKEN=4

# The sections whose rows are priced. Both are required to exist AND to have rows: a heading renamed without
# this script being told is how a gate stops checking in silence.
SECTIONS=("## Start here" "## Working agreements")

# Rule 2's closed vocabulary, word-anchored so "largest" does not fire inside "enlargement". Matched against
# a lowercased cell, so the pattern itself needs no case folding.
#
# SUPERLATIVES, PLUS THE COST WORDS. The two groups are deliberate and were arrived at by measurement, not by
# listing adjectives (PR #175 review):
#
#   - Superlatives ranking one document against the others: `cheapest`, `smallest`, `quickest`. gh#160's
#     acceptance criterion names exactly this shape — "cheapest, smallest or fastest".
#   - Bare cost words — `cheap`, `cheaper`, `pricey`, `costly`. These claim a price in every use, and `cheap`
#     is the word gh#160 actually met: "Cheap; just read it", beside a 0.8K that measured 6.8K.
#
# THE BARE `-er` COMPARATIVES OF DIMENSION WORDS ARE DELIBERATELY ABSENT — `longer`, `larger`, `smaller`,
# `shorter`, `bigger`, `faster`, `slower`. They were in the first draft and they fire on ordinary English:
# **`no longer` appears twelve times in `documentation/` today**, and a row reading "an estimate no longer
# matches the card" was reddened with `SIZE CLAIM … says 'longer'`. `docs` is required on all three rungs, so
# that is a blocked merge accusing an author of a claim they did not make — which is how a gate gets deleted
# by the first person it wrongly stops (the Coding contract's second run: green on the most awkward CORRECT
# input already in the repository). Under-matching is the right direction here; the map states the rule, and
# the gate catches the shape that actually inverted the column.
SIZE_CLAIM='(^|[^[:alnum:]])(cheap|cheaper|cheapest|pricey|priciest|costly|costliest|smallest|largest|biggest|longest|shortest|fastest|quickest|slowest|tiniest|lightest|heaviest)([^[:alnum:]]|$)'

# A row is anything between a table's alignment row and the blank line that ends the table. That is the
# GFM rule, and it is used here INSTEAD OF "the line starts with a pipe" (PR #175 review). GFM makes the
# leading and trailing pipes optional and tolerates up to three spaces of indentation, so
#
#     [`AGENT-MEMORY.md`](AGENT-MEMORY.md) | 0.8K | Before starting any work.
#
# renders as an ordinary row and was silently SKIPPED by the pipe test -- restoring the exact gh#160 defect
# under a green `ok 9 routed rows`. A gate whose coverage shrinks when someone reformats a table is the
# fail-open this script's own header says it does not have. Between the alignment row and the blank line,
# every non-blank line is a data row and must parse.
#
# Detecting the alignment row also removes the need to recognise the HEADER row by its wording: the header
# is whatever sits before the alignment row, so renaming the `Document` column can no longer make a header
# parse as data or make a data row parse as a header.
ALIGNMENT_ROW='^[-:|[:space:]]+$'
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
in_table=0
for line in "${lines[@]}"; do
  if [[ "$line" == '## '* ]]; then
    current=-1
    in_table=0
    for i in "${!SECTIONS[@]}"; do
      if [ "$line" = "${SECTIONS[$i]}" ]; then
        current="$i"
        section_seen[$i]=1
        break
      fi
    done
    continue
  fi

  # Trim once, up front: everything below decides on the trimmed line, so a row indented under a list or a
  # blockquote is read the same as one flush against the margin.
  trimmed="${line#"${line%%[![:space:]]*}"}"; trimmed="${trimmed%"${trimmed##*[![:space:]]}"}"

  # A PRICED TABLE UNDER AN UNLISTED HEADING IS A FAILURE, NOT AN EXEMPTION (PR #175 review). `SECTIONS` is a
  # fixed pair, so before this the parser fail-CLOSED on removal -- delete a section and you get `NO SECTION`,
  # empty one and you get `NO ROWS` -- and fail-OPEN on addition: add one and you got silence. A new
  # `## Operations` table pricing `agents/platform.md` (56,054 B, 14.0K) at 0.2K, with the word "cheap" in its
  # prose, drew `ok 10 routed rows ... every ~tok within 25% and no size claims in prose` and exit 0. That
  # sentence was false about the file it had just read, and the map's own promise -- "re-measures every row
  # below" -- was false with it.
  #
  # Detected on the HEADER row rather than by pricing every table, because most tables in this corpus have no
  # price column and pricing them would be nonsense. A `~tok` header is an unambiguous statement that the
  # table below it is a price list, so the author has to add the heading to `SECTIONS` deliberately. Two pipes
  # are required as well, so a sentence of prose mentioning `~tok` is not mistaken for a table.
  if [ "$current" -lt 0 ]; then
    pipes="${trimmed//[^|]/}"
    if [[ "$trimmed" == *'~tok'* ]] && [ "${#pipes}" -ge 2 ]; then
      die "  UNLISTED TABLE  a ~tok table appears under a heading this gate does not price:"
      die "                  $trimmed"
      die "Its rows are measured by nothing, while the green line below would claim every ~tok was checked."
      die "Add that heading to SECTIONS in this script, in the same pull request that adds the table."
      failures=$(( failures + 1 ))
    fi
    continue
  fi

  # A blank line closes the table. Spelled as `if`, not `[[ ... ]] && continue`: an AND-OR list whose left
  # side fails is exempt from `set -e` only where it is not the last command of the list, and that
  # exemption is exactly the kind of positional subtlety claim.sh already got bitten by.
  if [ -z "$trimmed" ]; then in_table=0; continue; fi

  if [ "$in_table" -eq 0 ]; then
    # The alignment row opens the table. Requiring a pipe as well as a dash keeps a thematic break (`---`)
    # from opening one.
    if [[ "$trimmed" =~ $ALIGNMENT_ROW && "$trimmed" == *-* && "$trimmed" == *"|"* ]]; then
      in_table=1
    fi
    continue
  fi

  # Past this point the line IS a data row of a priced table, whatever it looks like. Optional outer pipes
  # come off; whatever remains must split into cells and parse, or be reported.
  rest="${trimmed#|}"
  rest="${rest%|}"
  cell_doc="${rest%%|*}"
  rest="${rest#*|}"
  cell_tok="${rest%%|*}"
  cell_prose="${rest#*|}"

  # Trim with parameter expansion only — no subshell, no `sed`.
  cell_doc="${cell_doc#"${cell_doc%%[![:space:]]*}"}"; cell_doc="${cell_doc%"${cell_doc##*[![:space:]]}"}"
  cell_tok="${cell_tok#"${cell_tok%%[![:space:]]*}"}"; cell_tok="${cell_tok%"${cell_tok##*[![:space:]]}"}"

  section_rows[$current]=$(( section_rows[current] + 1 ))
  rows_checked=$(( rows_checked + 1 ))

  if [[ "$cell_doc" != *']('* ]]; then
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

# The claim names the scope it actually covered. It used to say "every ~tok" unqualified while a price table
# under an unlisted heading went unread; that is now a failure above, so the sentence is true -- but it still
# says how many rows and which sections, because a green line a reader cannot check is the thing this whole
# script is about.
ok "ok  $rows_checked routed rows under ${#SECTIONS[@]} priced headings in $MAP — every ~tok within ${TOLERANCE_PCT}% of \`wc -c\`÷$BYTES_PER_TOKEN, no size claims in row prose, no unlisted price tables."
