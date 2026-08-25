#!/usr/bin/env bash
# check-doc-sizes.sh — fail when a `~tok` column stops describing the files it prices.
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
# WHY THERE IS MORE THAN ONE PRICED TABLE (gh#178)
#
# The routing map is a two-level route: its `agents/` row points at `documentation/agents/README.md`, and that
# index points on at the four role contracts. gh#160 priced the row — correctly, for the index — and the row
# was then the entrance to a read nothing described, `documentation/agents/platform.md` being an order of
# magnitude past the number a reader had just budgeted from.
#
# The prices went into the index's own contract table rather than into four more rows of the map. Four map
# rows would have listed the same four contracts a second time, priced, beside the index's table listing them
# unpriced — one fact in two places with only one copy ever corrected, which is precisely what rule 2 below
# refuses inside a single row. The cost of that choice is this script: "the priced table" became a LIST of
# (file, heading) pairs, and the list is `PRICED` below.
#
# HOW THE NUMBER IS DERIVED
#
# tokens = bytes / 4, bytes from `wc -c` on the working tree. It is the ordinary English-text approximation,
# it is one command anybody can re-run, and being reproducible matters more here than being exact: the
# column's job is to separate an index from a contract an order of magnitude past it, not 6.8K from 6.9K. `wc -c` is spelled out below rather than
# replaced by a cheaper `stat` so that the gate runs the same command the map tells a reader to run.
#
# `wc -c` and `git cat-file -s` disagree wherever a checkout carries CRLF. They cannot here — `.gitattributes`
# pins `* text=auto eol=lf`, so the working tree is LF on Windows too, and the two agree byte for byte across
# every priced file. If that pin is ever removed this gate starts reporting Windows-only failures; fix the
# pin, not this script.
#
# WHAT IT ENFORCES
#
#   1. Every data row under every (file, heading) pair in `PRICED` carries a `~tok` value within
#      TOLERANCE_PCT of its measured size. Every pair must exist AND have rows, so a restructured table
#      cannot turn this gate green by leaving it nothing to read.
#   2. No row's prose makes a size claim. The `~tok` column is the only place a size claim lives, because a
#      superlative in prose is the same fact stated twice and only one of the two copies ever gets corrected
#      — which is exactly how "the cheapest whole-file read here" survived on the largest row.
#   3. No `~tok` table appears under a heading `PRICED` does not name, in a file `PRICED` does read.
#   4. No `~tok` table appears in a markdown file `PRICED` does not read at all.
#
# Rules 3 and 4 are the same rule at two levels, and both are fail-open closed after the fact. Rule 3 came
# from the PR #175 review: `SECTIONS` was a fixed pair, so the parser fail-CLOSED on a REMOVED heading and
# fail-OPEN on an ADDED one — a new `## Operations` table pricing a 14K file at 0.2K drew `ok 10 routed rows`
# and exit 0. Rule 4 is that hole one level up, and gh#178 is what opened it: the moment a second FILE could
# be priced, "which files are priced" became a list too, and a third file with a `~tok` table would have been
# unread exactly as an unlisted heading once was. Adding either has to be as deliberate as the list makes it.
#
# Rule 2 matches a closed vocabulary of size adjectives, so rewording WITHIN that vocabulary cannot silence
# it. Rewording out of it can: "costs less than the others" makes the same claim and passes. Only real
# comprehension closes that, and it is judged not worth it — the failure this actually met was the word
# "cheapest", and the map now states that the column is the only place for such a claim, so the next author
# is told. Same call, and the same reasoning, as the block-comment blind spot in check-paced-paging.sh.
#
# ANYTHING IT CANNOT PARSE IS A FAILURE, NEVER A SKIP. A row with no link, a `~tok` cell that is not a size,
# a target that is not on disk, a renamed heading, a priced file that has moved — each is reported by its own
# name and fails the run. The alternative is a gate that quietly prices fewer rows every time the table is
# edited, which is the shape of every dead guard this repository has had (gh#43, gh#98, gh#114, gh#126,
# gh#164).
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

# EVERY PRICED TABLE, AS `<file>|<heading>`. A pair, not a file: a file may carry a priced table and ordinary
# unpriced ones beside it, and most tables in this corpus are the second kind.
#
# Adding a pair here is the deliberate act rules 3 and 4 above exist to require. Removing one is too — the
# rows under it stop being measured, and nothing else in the repository will notice.
PRICED=(
  "documentation/README.md|## Start here"
  "documentation/README.md|## Working agreements"
  "documentation/agents/README.md|## The contracts"
)

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

A `~tok` column is the price list a reader consults before opening a document, and it is only worth
consulting while it is true. documentation/README.md carries one for the documents it routes to;
documentation/agents/README.md carries one for the four role contracts.

  tokens = bytes / 4, bytes from `wc -c`, rounded to 0.1K. The value to write is printed above each failure.

If you APPENDED to a priced document — AGENT-MEMORY.md especially, or documentation/agents/platform.md —
correct its row in this same pull request. That is the same-PR docs rule in AGENTS.md, and it is one
character of editing.

If you RESTRUCTURED a priced table — renamed a section heading, added a column, added a row with no size,
moved a priced file, added a ~tok table somewhere new — update `PRICED` in this script in the same pull
request. Deleting the gate because the table moved is how an 8.5x drift goes unnoticed a second time.
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

# `PRICED` is pairs; walking it needs the files in order, once each. The membership test is a substring of a
# space-delimited string rather than a nested loop, so nothing expands a possibly-empty array under `set -u`
# and the whole thing stays fork-free.
if [ "${#PRICED[@]}" -eq 0 ]; then
  die "  NOTHING PRICED  PRICED is empty, so this gate would check nothing and say so in green."
  exit 1
fi

FILES=()
files_seen=" "
for pair in "${PRICED[@]}"; do
  pair_file="${pair%%|*}"
  if [[ "$files_seen" != *" $pair_file "* ]]; then
    FILES+=("$pair_file")
    files_seen="$files_seen$pair_file "
  fi
done

# Seen/rows are tracked per PAIR, not per heading: two files may legitimately use the same heading text, and
# a pair that exists in one file and not the other has to be reported against the file it names.
pair_seen=()
pair_rows=()
for _ in "${PRICED[@]}"; do
  pair_seen+=(0)
  pair_rows+=(0)
done

for file in "${FILES[@]}"; do
  file_dir="${file%/*}"
  if [ "$file_dir" = "$file" ]; then file_dir="."; fi

  if [ ! -f "$file" ]; then
    die "  NO SUCH FILE  $file is named in PRICED and is not on disk"
    die "Every row it prices is now measured by nothing. Either a priced file moved — point PRICED at the new"
    die "path in the same pull request — or this gate is looking at the wrong tree."
    exit 1
  fi

  # THE FILE IS READ ONCE, ON ITS OWN LINE, AND THE READ IS CHECKED (gh#126). `mapfile` from a plain file
  # redirection does fail the script when the file cannot be opened — unlike `mapfile < <(cmd)`, whose
  # process substitution status is never examined and which is how `commit-hygiene` once reported "no commits
  # to check" and exited 0. The length is asserted anyway: an empty array here would walk zero lines and fall
  # through to the green line at the bottom.
  lines=()
  mapfile -t lines < "$file"
  if [ "${#lines[@]}" -eq 0 ]; then
    die "  EMPTY  $file has no lines"
    die "It is priced by this gate and is not empty in this repository, so this is a broken read rather than"
    die "a clean file."
    exit 1
  fi

  # The headings priced in THIS file, and the PRICED index each one reports against.
  headings=()
  heading_pair=()
  for i in "${!PRICED[@]}"; do
    if [ "${PRICED[$i]%%|*}" = "$file" ]; then
      headings+=("${PRICED[$i]#*|}")
      heading_pair+=("$i")
    fi
  done

  current=-1
  in_table=0
  for line in "${lines[@]}"; do
    if [[ "$line" == '## '* ]]; then
      current=-1
      in_table=0
      for i in "${!headings[@]}"; do
        if [ "$line" = "${headings[$i]}" ]; then
          current="$i"
          pair_seen[${heading_pair[$i]}]=1
          break
        fi
      done
      continue
    fi

    # Trim once, up front: everything below decides on the trimmed line, so a row indented under a list or a
    # blockquote is read the same as one flush against the margin.
    trimmed="${line#"${line%%[![:space:]]*}"}"; trimmed="${trimmed%"${trimmed##*[![:space:]]}"}"

    # A PRICED TABLE UNDER AN UNLISTED HEADING IS A FAILURE, NOT AN EXEMPTION (PR #175 review). `SECTIONS`
    # was a fixed pair, so before this the parser fail-CLOSED on removal -- delete a section and you get
    # `NO SECTION`, empty one and you get `NO ROWS` -- and fail-OPEN on addition: add one and you got
    # silence. A new `## Operations` table pricing `agents/platform.md` at 0.2K, with the word "cheap" in its
    # prose, drew `ok 10 routed rows ... every ~tok within 25% and no size claims in prose` and exit 0. That
    # sentence was false about the file it had just read, and the map's own promise -- "re-measures every row
    # below" -- was false with it.
    #
    # Detected on the HEADER row rather than by pricing every table, because most tables in this corpus have
    # no price column and pricing them would be nonsense. A `~tok` header is an unambiguous statement that
    # the table below it is a price list, so the author has to add the heading to `PRICED` deliberately. Two
    # pipes are required as well, so a sentence of prose mentioning `~tok` is not mistaken for a table.
    if [ "$current" -lt 0 ]; then
      pipes="${trimmed//[^|]/}"
      if [[ "$trimmed" == *'~tok'* ]] && [ "${#pipes}" -ge 2 ]; then
        die "  UNLISTED TABLE  $file: a ~tok table appears under a heading this gate does not price:"
        die "                  $trimmed"
        die "Its rows are measured by nothing, while the green line below would claim every ~tok was checked."
        die "Add that (file, heading) pair to PRICED in this script, in the same pull request as the table."
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

    where="$file ${headings[$current]}"

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

    pair_index="${heading_pair[$current]}"
    pair_rows[$pair_index]=$(( pair_rows[pair_index] + 1 ))
    rows_checked=$(( rows_checked + 1 ))

    if [[ "$cell_doc" != *']('* ]]; then
      die "  NO LINK  $where: row '$cell_doc' has no [text](target) in its first cell"
      die "Nothing can be measured for it, so its price is unverifiable. If the table gained a column or its"
      die "header was renamed, update this script in the same pull request."
      failures=$(( failures + 1 ))
      continue
    fi

    target="${cell_doc#*](}"
    target="${target%%)*}"
    target="${target%%#*}"

    # RESOLVED AGAINST THE PRICED FILE'S OWN DIRECTORY, not against the routing map's (gh#178). Every link in
    # `documentation/agents/README.md` is relative to `documentation/agents/`, and three of the four climb
    # out of it with `../../`. A port that kept a single `MAP_DIR` would have measured a different file, or
    # none, and reported a confident number either way.
    resolved="$file_dir/$target"

    if [[ ! "$cell_tok" =~ $SIZE_CELL ]]; then
      die "  NOT A SIZE  $where: $target is priced '$cell_tok'"
      die "Every priced row carries a measured size. A placeholder is a row nothing prices and nothing checks."
      failures=$(( failures + 1 ))
      continue
    fi

    if [ ! -f "$resolved" ]; then
      die "  MISSING  $where: $target does not exist at $resolved"
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
      die "  EMPTY  $where: $target measures $bytes bytes"
      die "A priced document with no content is a routing error, not a free read."
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
      die "               write $suggested in that row's ~tok cell, in $file."
      failures=$(( failures + 1 ))
    else
      printf '  ok  %-34s says %-6s measures %-6s (%s%% off)\n' "$target" "$cell_tok" "$suggested" "$dev_pct"
    fi

    if [[ "${cell_prose,,}" =~ $SIZE_CLAIM ]]; then
      die "  SIZE CLAIM  $target's prose says '${BASH_REMATCH[2]}'"
      die "              The ~tok column is the only place a size claim lives. Prose restating it is the same"
      die "              fact in two places, and only the column ever gets corrected — which is how the"
      die "              largest row kept calling itself the cheapest. Delete the claim; the number is there."
      failures=$(( failures + 1 ))
    fi
  done
done

for i in "${!PRICED[@]}"; do
  pair_file="${PRICED[$i]%%|*}"
  pair_heading="${PRICED[$i]#*|}"
  if [ "${pair_seen[$i]}" -ne 1 ]; then
    die "  NO SECTION  $pair_file has no '$pair_heading' heading"
    die "Either the file was restructured or PRICED names a section that no longer exists. Until the two are"
    die "reconciled, the rows under it are priced by nothing."
    failures=$(( failures + 1 ))
  elif [ "${pair_rows[$i]}" -eq 0 ]; then
    die "  NO ROWS  '$pair_heading' in $pair_file contains no priced table rows"
    die "This gate just checked nothing under that heading, and would otherwise have called the file accurate."
    failures=$(( failures + 1 ))
  fi
done

# RULE 4: a `~tok` table in a file PRICED does not read at all (gh#178).
#
# Rule 3 closed this at the heading level and stopped there, because until gh#178 there was one priced file
# and "which file" was not a list. It is now, so the same fail-open exists one level up: add a fifth
# document with a price table of its own and, without this sweep, nothing would read it while the green line
# below still named a row count and so still read as proof.
#
# GLOBSTAR, NOT `find`: zero forks, and on a Windows checkout a fork costs seconds. `**` does not match
# dot-directories unless `dotglob` is set, so `.git/` and — the one that matters locally — `.worktrees/`,
# which holds full copies of this repository for other sessions, are skipped without naming them. The blind
# spot that buys is stated rather than papered over: a `~tok` table under a dot-directory (`.github/`) is not
# swept. Nothing there prices anything today, and the alternative is a sweep that reads three sibling
# worktrees and reports another session's half-finished table as this pull request's fault.
shopt -s globstar nullglob
swept=0
for candidate in **/*.md; do
  swept=$(( swept + 1 ))

  is_priced=0
  for known in "${FILES[@]}"; do
    if [ "$known" = "$candidate" ]; then is_priced=1; break; fi
  done
  if [ "$is_priced" -eq 1 ]; then continue; fi

  # Plain redirection again, so an unopenable file kills the run rather than reading as a clean file.
  candidate_lines=()
  mapfile -t candidate_lines < "$candidate"
  if [ "${#candidate_lines[@]}" -eq 0 ]; then continue; fi

  for line in "${candidate_lines[@]}"; do
    case "$line" in *'~tok'*) ;; *) continue ;; esac
    trimmed="${line#"${line%%[![:space:]]*}"}"; trimmed="${trimmed%"${trimmed##*[![:space:]]}"}"
    pipes="${trimmed//[^|]/}"
    if [ "${#pipes}" -ge 2 ]; then
      die "  UNLISTED FILE  $candidate carries a ~tok table this gate does not read:"
      die "                 $trimmed"
      die "Its rows are measured by nothing. Add the (file, heading) pair to PRICED in this script, in the"
      die "same pull request as the table — or drop the ~tok column, if the table is not a price list."
      failures=$(( failures + 1 ))
      break
    fi
  done
done

if [ "$swept" -eq 0 ]; then
  die "  NOTHING SWEPT  no markdown files were found under $REPO_ROOT"
  die "A priced file was read above, so this is a broken sweep rather than a repository with no documents."
  exit 1
fi

# The pass carries its own evidence, the way check-no-order-path.sh prints the number of files it read: a
# green line naming zero rows is the shape every dead gate in this repository had.
if [ "$rows_checked" -eq 0 ]; then
  die "  NOTHING CHECKED  no priced rows were found in any file named by PRICED"
  die "Those files are not empty, so this is a broken gate rather than a clean set of tables."
  exit 1
fi

if [ "$failures" -gt 0 ]; then
  echo >&2
  die "$failures problem(s) across $rows_checked priced rows in ${#FILES[@]} file(s)."
  explain
  exit 1
fi

# The claim names the scope it actually covered. It used to say "every ~tok" unqualified while a price table
# under an unlisted heading went unread; that is now a failure above, so the sentence is true -- but it still
# says how many rows, how many tables and how many files it swept, because a green line a reader cannot check
# is the thing this whole script is about.
ok "ok  $rows_checked priced rows under ${#PRICED[@]} (file, heading) pairs across ${#FILES[@]} file(s) — every ~tok within ${TOLERANCE_PCT}% of \`wc -c\`÷$BYTES_PER_TOKEN, no size claims in row prose, and no unlisted price table in the $swept markdown files swept."
