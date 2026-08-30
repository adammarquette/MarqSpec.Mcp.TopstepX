#!/usr/bin/env bash
# check-requirement-ids.sh — fail when a requirement id cited anywhere in the tree is not one the PRD defines.
#
#   scripts/check-requirement-ids.sh [repo-root]
#
# WHY THIS EXISTS (gh#182)
#
# AGENTS.md calls the markdown under documentation/ and the GitHub issues the highest-level source code of
# this system, indexed by a symbol table: requirement ids, ADR numbers and gh#N. check-doc-links.sh proves a
# relative LINK resolves. Nothing proved a requirement id resolves — so a citation could name a requirement
# that this project does not have, and a reader following it to justify or change the thing it annotates
# finds nothing, unable to tell whether the requirement was deleted, renumbered, or never existed here.
#
# gh#172 found two by hand, in Directory.Build.props, where they had sat since the scaffolding commit
# 2dea44a through every review since — including the gh#151 sweep that walked that very file. Both were
# MarqSpec.Client.ProjectX's ids, carried in with the scaffolding; that repository's PRD numbers further and
# differently, and documentation/prd.md says so in its Scope paragraph. Two more of the same class were still
# in the tree when this gate was written, and are cleared by the same pull request:
#
#   .gitignore's Secrets banner       cited the client's id for its secrets rule; the same rule exists HERE,
#                                     as R-7.1, so the citation now names it (option 1)
#   ci.yml's coverage floor           cited the client's id for a 95%/90% coverage target this project has
#                                     never stated. The PRD's Scope paragraph puts build hygiene, the
#                                     pipeline and the release path OUTSIDE the numbered requirements, so
#                                     there is no id here to point at and adding one would contradict it.
#                                     The citation is gone and the floor stands on its own measurement
#                                     (option 3, the same call gh#172 made in Directory.Build.props).
#
# Two by hand, twice, is the argument for a gate rather than a third sweep.
#
# WHAT IT CANNOT DO, SAID HERE SO A GREEN IS NOT OVER-READ
#
# It proves a cited id EXISTS. It cannot prove the citation quotes it correctly. One of gh#172's two never
# said what the comment citing it claimed it said — a dangling id can also be a misquote, and only reading
# the target catches that. A green line below means every symbol resolves, never that every citation is true.
#
# WHAT IS A CITATION
#
#   \bR-<digits>(.<digits>)*   and   \bQ-<digits>(.<digits>)*
#
# THE LEFT WORD BOUNDARY IS LOAD-BEARING. An ADR number CONTAINS a citation-shaped substring — strip the two
# leading letters off `ADR-0007` and what is left has exactly the shape this gate looks for. Without \b this
# repository yields twelve phantom ids, one per ADR, every one of them from a correct ADR reference, and the
# gate is red on every line that names an ADR. Measured both ways on the real tree, and on a one-line probe
# (`ADR-0001 and R-1.1 and xR-2 and -R-3` extracts exactly two ids, the second and the fourth); the
# self-test's `adr-near-miss` fixture keeps it measured on every CI run rather than in this comment.
#
# THE MINOR PART REPEATS, `(\.[0-9]+)*` RATHER THAN `?`, so a malformed depth fails CLOSED. A three-part id
# is extracted whole and resolves against nothing; under `?` it would be truncated to its first two parts,
# which do resolve, and the citation would pass while naming a requirement that does not exist. Verified to
# extract the same id set as `?` on this tree, so the stricter form costs nothing here.
#
# `R-#` IS CORRECT INPUT, NOT A CITATION — thirteen lines across ten files used it as the literal placeholder
# for "the requirement id" on `develop` at `8a2302d`: AGENTS.md, CONTRIBUTING.md, README.md,
# documentation/README.md, the pull request template and wiki/SCHEMA.md among them. (Pinned to a commit
# because this file and its self-test add more of them, so an unpinned count is wrong the moment it is
# written — PR #195 review.) `#` is not a digit, so the pattern above excludes it; a pattern written any
# looser does not, and would redden this repository's most-read files.
#
# BOTH ID CLASSES ARE COVERED, deliberately. `Q-#` is the PRD's open-question class — three today, cited from
# two ADRs and three wiki pages — and it is the same symbol table with the same failure mode. Covering it
# costs one alternation and reddens nothing: all three resolve.
#
# WHAT IS A DEFINITION — documentation/prd.md, and nothing else
#
#   `## R-<n> — …`      a section id. Seven ADR headers, the tool catalogue, the wiki index, two wiki page
#                       headers and .github/ISSUE_TEMPLATE/task.yml's placeholder string cite bare section
#                       ids, so parsing only the bullets would redden fourteen lines of correct text.
#   `- **R-<n>.<m>**`   a requirement.
#   `- **Q-<n>` …       an open question.
#
# Column 0, and OUTSIDE any fenced block or HTML comment. An indented `- **R-…**` is a citation inside some
# other bullet; a fenced one is an example of the PRD's own format; a commented-out one is a retired
# requirement. None of the three is a definition, and reading any of them as one INVENTS a symbol — which is
# this gate's only fail-open direction, since a citation of the invented id then passes. If the rule makes a
# real definition invisible instead, the gate goes red on every citation of it, which is the safe direction,
# and the failure carries a hint saying the PRD mentions the id in a form this script does not read as a
# definition. The fence and comment halves were added by the PR #195 review, which broke the gate with a
# four-line fenced example appended to the real PRD.
#
# THERE IS NO EXCLUSION LIST, and that is why this file spells no id that does not resolve. The gate reads
# its own source and its own self-test like every other file in the corpus. An exclusion list is also how a
# gate stops seeing the file that matters, and the two files most likely to acquire a stale id are these two.
# The self-test assembles its dangling fixtures at run time from a prefix and a number for the same reason.
#
# BLIND SPOTS, stated rather than papered over. Every one of these is a citation the gate does NOT see, so
# each is a way a dangling id could survive — they are listed because a green line that hides its own edges
# is the thing this repository keeps having to un-learn:
#   - **Case.** `R-` and `Q-` are matched case-sensitively. The documented form is upper case, there are
#     zero lower-case citations in the tree, and `-i` would start matching a bare `r-2` in prose — an
#     r-squared, a variant label. Widening a pattern until it reddens correct English is how a required
#     gate gets deleted, so this stays narrow deliberately (PR #195 review measured the miss).
#   - **Non-ASCII look-alikes.** A non-breaking hyphen, an en dash, a minus sign or a full-width R renders
#     like a citation and is not one. Nothing here produces them, and matching them would mean deciding
#     which of several code points *is* the id separator.
#   - **A space after the hyphen** — `R- 4` is not the documented form and is not matched.
#   - **Binary files are skipped** (`grep -I`). This corpus has none; a citation inside one is invisible.
#   - **Nested repositories are not read** — they are a different repository. The count is printed.
#   - **Indent is measured from column 0, not from a container's content column.** CommonMark measures a
#     fence's three-column allowance relative to the list item or blockquote containing it, and doing that
#     properly means parsing containers, which is a markdown parser rather than a gate. What that costs was
#     measured rather than argued, and the entry it replaces is worth keeping as the warning: it named a
#     shape -- a column-0 definition inside a container-indented fence -- and asserted it was a fail-open
#     WITHOUT BUILDING IT. The reviewer built it and the gate agrees with CommonMark, because a column-0
#     line ends the list item, the fence ends with the item, and the heading is top level to both. **An
#     overstated blind spot is the same unmeasured-claim class as a direction argument; it merely fails in
#     the harmless direction.** What is actually true:
#       - a fence a container has pushed past three columns is not recognised as a fence. Its contents are
#         read as ordinary text -- which cannot invent a definition, since those must sit at column 0 and a
#         column-0 line has already left the container. Ids CITED inside such an example are reported
#         unresolved, exactly as they are inside any fence this gate does recognise.
#       - definitions written at column 0 AFTER such a block are read normally. Checked, because the
#         version before the opener carried an indent cap could not close that fence and swallowed them.
#       - **no fail-open of this shape is known.** Neither the author nor the reviewer could construct one:
#         for a column-0 line to stay inside a fence, the fence must be at top level with at most three
#         columns of indent, which this gate opens. That is a failed search, not a proof -- the two
#         fail-opens this machinery has already had were both found by someone building the input.
#   - **Mid-merge, the corpus is the WORKING TREE's conflict-marked file** — text from both sides at once,
#     belonging to no commit, plus the marker lines. Every citation in it is counted, once each, and the
#     green line is true of exactly that; it is not true of either parent, and neither is the tree. This
#     entry used to say something else and stronger: that a conflicted index made the evidence line
#     OVERCOUNT, because `git ls-files --cached` lists a path once per stage, so the file arrived three
#     times and its citations were counted three times. That was real and is measured at the read itself,
#     where `--deduplicate` now closes it (gh#240). What is left is this — a corpus a reader should know is
#     mid-merge, rather than a number they cannot trust.
#   - **The `UNQUOTABLE PATH` branch fires for a REASON no fixture can produce on NTFS** — an embedded
#     quote, backslash or control character in a filename. The branch itself is pinned: a path staged into
#     the INDEX with `update-index --cacheinfo` needs no file on disk, which is how the self-test reaches
#     it. What stays unexercised is the filesystem route, and this entry used to claim the whole branch was
#     unreachable on exactly the wrong grounds — `git ls-files` reads the index, not the disk.
#   - The issue and pull request bodies are not files, so nothing here reads them.
#   - Correctness of the quotation, as above.
#
# WHAT IS **NOT** A BLIND SPOT, because it was measured rather than assumed: **CRLF input.** A PRD written
# with CRLF line endings throughout — a fenced example whose closer carries trailing spaces AND a CR, an HTML comment,
# definitions after both — parses identically: the fence opens and closes, the comment opens and closes, and
# only the ids written INSIDE them come back unresolved (PR #195 round 8). `.gitattributes` pins the working
# tree to LF, so this is defence in depth rather than a path CI takes.
#
# ITS OWN ABILITY TO FAIL IS TESTED: scripts/check-requirement-ids-selftest.sh runs this script against
# fixtures whose faults are known and requires it to reject each one BY NAME, and to accept the awkward
# correct shapes this repository actually contains. Both run in ci.yml's `docs` job, which is already a
# required context on all three rungs — so this is enforcing rather than reporting, and no ruleset was
# written to make it so.

set -euo pipefail

die()  { printf '\033[31m%s\033[0m\n' "$*" >&2; }
ok()   { printf '\033[32m%s\033[0m\n' "$*"; }

REPO_ROOT="${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"
cd "$REPO_ROOT" || { die "  NO SUCH ROOT  $REPO_ROOT"; exit 1; }

PRD="documentation/prd.md"

# One ERE, used for the corpus scan. Kept in one place so the citation side and the header above cannot
# drift apart.
CITATION='\b[RQ]-[0-9]+(\.[0-9]+)*'

DEF_SECTION='^##[[:space:]]+(R-[0-9]+)([^0-9.]|$)'
DEF_REQUIREMENT='^-[[:space:]]+\*\*(R-[0-9]+\.[0-9]+)\*\*'
DEF_QUESTION='^-[[:space:]]+\*\*(Q-[0-9]+)([^0-9.]|$)'

# A fenced block opens on three or more backticks or tildes and closes on the same character (CommonMark
# allows up to three columns of indent, which is why the line is trimmed before this is applied).
FENCE='^(`{3,}|~{3,})'

explain() {
  cat >&2 <<'EXPLAIN'

A requirement id is a symbol. documentation/prd.md is where it is defined, and AGENTS.md says an id that
file does not define is not a requirement of this project however confidently it is cited.

Three ways to clear one, and the choice is per line (gh#172, gh#182):

  1. CITE THE REAL ID. The requirement exists here under a different number — the usual case for an id
     carried in from MarqSpec.Client.ProjectX with the scaffolding.
  2. ADD THE REQUIREMENT to documentation/prd.md, if the thing being annotated really is something this
     SERVER must do. Note the PRD's Scope paragraph: build hygiene, the pipeline and the release path carry
     no requirement id by design, so a build or CI setting is almost never this option.
  3. DROP THE CITATION and let the comment stand on its own reasoning. A comment that explains itself is
     worth more than one that defers to a requirement nobody can find.

And one that is not a way to clear one, because it comes up: if you need to WRITE ABOUT an id that does not
resolve here — a sibling repository's, or one this repository has already cleared — describe it rather than
spelling it ("the id .gitignore used to carry"). There is no exclusion list and there is deliberately no way
to add one, so a tracked file that spells such an id turns this gate red. That is the price of a gate with no
blind spot in it, and it is the price this script and its self-test both pay in their own headers.
EXPLAIN
}

# ---------------------------------------------------------------------------
# 1. The definition side.
# ---------------------------------------------------------------------------
if [ ! -f "$PRD" ]; then
  die "  MISSING  $PRD"
  die "The PRD is where every requirement id is defined. Without it nothing can be resolved, and this gate"
  die "cannot report the tree's citations clean. If the PRD moved, point this script at the new path."
  exit 1
fi

# Read once, on its own line, from a plain redirection — `mapfile < file` does fail the script when the file
# cannot be opened, unlike `mapfile < <(cmd)`, whose process substitution status the shell never examines and
# which is how `commit-hygiene` once reported "no commits to check" and exited 0 (gh#164). The length is
# asserted anyway: an empty array walks zero lines and would fall through to the green line at the bottom.
prd_lines=()
mapfile -t prd_lines < "$PRD"
if [ "${#prd_lines[@]}" -eq 0 ]; then
  die "  EMPTY  $PRD has no lines"
  die "The PRD is not empty in this repository, so this is a broken read rather than a requirement-free PRD."
  exit 1
fi

declare -A DEFINED=()
sections=0
requirements=0
questions=0
inert=0

# A DEFINITION INSIDE A FENCED BLOCK OR AN HTML COMMENT IS NOT A DEFINITION, and this is the one place in
# this script where a mistake fails OPEN (PR #195 review). Every other misreading here makes a real id look
# undefined, which reddens its citations loudly; misreading an EXAMPLE as a definition instead invents a
# symbol, and a citation of it then passes. Demonstrated on the real tree before this existed: a markdown
# fence appended to the PRD showing what a requirement looks like, plus a citation of the id inside it,
# produced `ok … every one resolves` and exit 0.
#
# Neither construct is in documentation/prd.md today (`grep -nE '^\s*```|<!--' documentation/prd.md` is
# empty). Both are one ordinary edit away: a fenced example of the PRD's own format, or a requirement
# retired by commenting it out rather than deleting it.
#
# THE TWO STATES ARE MUTUALLY EXCLUSIVE. Inside a comment a fence marker is text; inside a fence `<!--` is
# text. That is gh#123's lesson in both directions — the pass that carries state across lines has to know
# about the delimiters the other one cares about, because its mistakes are not local. Checking for a fence
# first and unconditionally is not enough and was the first draft's bug: a commented-out block CONTAINING a
# code fence opened one, the fence swallowed the `-->`, and the gate died naming an unterminated comment
# that was in fact closed.
#
# INLINE CODE SPANS ARE **NOT** STEPPED OVER, and the direction is the whole reason. gh#123 stripped spans
# before hunting a citation, where over-stripping merely loses a citation; here, failing to notice a `<!--`
# means a commented-out definition COUNTS — fail-open, the one outcome this gate exists to prevent. So a
# `<!--` written as prose about the marker, even inside backticks, opens a comment and hides every
# definition after it. That fails CLOSED and it is loud rather than confusing: an unterminated comment is
# named below by its own error, and a comment that closes takes its definitions with it, which reddens their
# citations. gh#142's rule, applied rather than inherited — ask which direction over-detection fails in, for
# this specific construct.
in_fence=0
fence_char=""
fence_len=0
in_comment=0

# Advances `in_comment` across one line, left to right, so a line carrying both delimiters ends in the right
# state. Assigns a global and takes no substitution, for the reason check-doc-sizes.sh gives: a fork costs
# microseconds on the runner and milliseconds on a Windows checkout, and this runs once per line of the PRD.
scan_comment_delimiters() {
  local rest="$1"
  while [ -n "$rest" ]; do
    if [ "$in_comment" -eq 0 ]; then
      case "$rest" in
        *'<!--'*) in_comment=1; rest="${rest#*<!--}" ;;
        *) rest="" ;;
      esac
    else
      case "$rest" in
        *'-->'*) in_comment=0; rest="${rest#*-->}" ;;
        *) rest="" ;;
      esac
    fi
  done
}

for line in "${prd_lines[@]}"; do
  # BOTH ENDS are trimmed. A closing fence written with trailing whitespace is ordinary and legal, and
  # leaving it untrimmed means the fence never closes, every definition after it disappears, and their
  # citations go red — the safe direction, but red on correct markdown all the same.
  #
  # BOTH MARKERS FAIL OPEN WHEN OVER-DETECTED, and this comment has now been wrong about that twice, in
  # opposite halves, which is the part worth carrying away rather than the rule.
  #
  #   - The CLOSER was obvious once measured: end a fenced EXAMPLE early and every line of the example
  #     after it becomes a real definition.
  #   - The OPENER was argued to be safe and is not (PR #195 round 3). The mechanism is PARITY, not
  #     inertness: an over-detected opener flips the state, so THE NEXT REAL OPENER IS EATEN AS A CLOSER
  #     and everything after it is read as live text. Three appended lines — a marker at four columns, a
  #     marker at column 0, then a heading — defined that heading and reported `every one resolves`, exit
  #     0, where CommonMark has the heading inside a fence that never closes.
  #
  # So both ends now implement CommonMark's rule: at most three columns of indent, a tab advancing to the
  # next four-column stop. The old reasoning — "CommonMark reads a four-column marker as an indented code
  # block, whose contents are inert too" — was true of the marker's own block and said nothing about what
  # the state flip does to the REST of the file, which is where the symbol was invented.
  #
  # A FAILURE DIRECTION IS A MEASUREMENT, NOT A DEDUCTION. gh#142's rule is to ask which way a construct
  # fails; the mistake both times was answering it by reasoning instead of by building the input and
  # running it. Every direction claim in this file has now been probed rather than argued.
  trimmed="${line#"${line%%[![:space:]]*}"}"; trimmed="${trimmed%"${trimmed##*[![:space:]]}"}"

  # The leading indent in COLUMNS, which the closer test below caps at three. A tab advances to the next
  # four-column stop, which is why it cannot be counted as one character: a single leading tab is four
  # columns and therefore content, not a closer. Only the leading whitespace is walked, so this is a handful
  # of iterations per line rather than a scan of it.
  fence_indent=0
  idx=0
  while [ "$idx" -lt "${#line}" ]; do
    case "${line:$idx:1}" in
      ' ')  fence_indent=$(( fence_indent + 1 )) ;;
      $'\t') fence_indent=$(( fence_indent + 4 - (fence_indent % 4) )) ;;
      *) break ;;
    esac
    idx=$(( idx + 1 ))
  done

  # THE TWO STATES ARE MUTUALLY EXCLUSIVE, and getting that wrong is a real defect rather than a nicety.
  # Inside a comment, a fence marker is text; inside a fence, `<!--` is text. Checking for a fence first and
  # unconditionally meant a commented-out block CONTAINING a code fence opened one, the fence then swallowed
  # the `-->`, and the run died on UNTERMINATED COMMENT about a comment that was closed — a confident wrong
  # answer sending the reader to fix something that is not broken, which is exactly gh#123's complaint about
  # its own diagnostic. Found by probing this loop rather than by reading it.
  if [ "$in_comment" -eq 1 ]; then
    inert=$(( inert + 1 ))
    scan_comment_delimiters "$line"
    continue
  fi

  if [ "$in_fence" -eq 1 ]; then
    inert=$(( inert + 1 ))
    # THE CLOSER IS THE HALF WHERE OVER-DETECTION INVENTS SYMBOLS, so all three of CommonMark's conditions
    # are checked. The first version checked a marker PREFIX and "every character is a backtick or a tilde",
    # which let six shapes close a fence CommonMark keeps open — a marker indented four or eight columns or
    # behind a tab, and a marker mixing the two characters (```` ```~ ````, ``` ~~~` ```). Each of those ends
    # a fenced EXAMPLE early and turns the rest of it into definitions (PR #195 review, sixteen probes).
    #   1. at most three columns of indent, a tab counting as four;
    #   2. the opener's OWN character, not merely one of the two;
    #   3. at least as long as the opener, and nothing else on the line.
    stripped="${trimmed//"$fence_char"/}"
    if [ "$fence_indent" -le 3 ] && [ -n "$trimmed" ] && [ -z "$stripped" ] \
       && [ "${#trimmed}" -ge "$fence_len" ]; then
      in_fence=0
      fence_char=""
      fence_len=0
    fi
    continue
  fi
  # THE OPENER CARRIES BOTH OF COMMONMARK'S RESTRICTIONS, and this script shipped without each of them in
  # turn -- the indent cap in round 3, the info-string rule in round 4, and both times the consequence was
  # the same PARITY fail-open: a marker that is not an opener opens one, the next real opener is eaten as a
  # closer, and everything after it is read live.
  #
  #   1. at most three columns of indent, a tab advancing to the next four-column stop;
  #   2. a BACKTICK fence's info string may not contain a backtick. The spec gives this gate's exact hazard
  #      as its own reason -- otherwise 'some inline code is incorrectly read as the beginning of a fenced
  #      code block' -- and a PRD is prose full of backticked ids. A TILDE fence has no such restriction and
  #      deliberately does not get one here: `~~~a`b` really is an opener, checked in both directions.
  opens_fence=0
  if [ "$fence_indent" -le 3 ] && [[ "$trimmed" =~ $FENCE ]]; then
    marker="${BASH_REMATCH[1]}"
    info="${trimmed:${#marker}}"
    if [ "${marker:0:1}" != '`' ] || [[ "$info" != *'`'* ]]; then
      opens_fence=1
    fi
  fi
  if [ "$opens_fence" -eq 1 ]; then
    in_fence=1
    fence_char="${marker:0:1}"
    fence_len="${#marker}"
    inert=$(( inert + 1 ))
    continue
  fi

  # Outside both, so this line's COLUMN 0 is outside a comment by construction — which is all that matters,
  # because a definition must start there. A `<!--` later on this same line opens the comment for the NEXT
  # line and leaves this one's definition intact, which is correct: the bullet was written before the marker.
  scan_comment_delimiters "$line"

  if [[ "$line" =~ $DEF_SECTION ]]; then
    DEFINED["${BASH_REMATCH[1]}"]=1
    sections=$(( sections + 1 ))
    continue
  fi
  if [[ "$line" =~ $DEF_REQUIREMENT ]]; then
    DEFINED["${BASH_REMATCH[1]}"]=1
    requirements=$(( requirements + 1 ))
    continue
  fi
  if [[ "$line" =~ $DEF_QUESTION ]]; then
    DEFINED["${BASH_REMATCH[1]}"]=1
    questions=$(( questions + 1 ))
  fi
done

# An unterminated fence is legal CommonMark — it closes at the end of the document — so it is counted, not
# refused. An unterminated COMMENT is not: it is malformed, and everything after it has just been skipped,
# so the symbol table this gate is about to resolve against is short by an unknown amount.
if [ "$in_comment" -eq 1 ]; then
  die "  UNTERMINATED COMMENT  $PRD opens an HTML comment that nothing closes"
  die "Every line after it has been skipped, so the symbol table is incomplete by an unknown amount and no"
  die "verdict below would mean anything. Close the comment. Note that a '<!--' written as prose — inside"
  die "backticks included — opens one here on purpose: see the block comment above this check."
  exit 1
fi

# BOTH REQUIREMENT FORMS MUST HAVE PARSED. A renamed heading level or a reformatted bullet list would
# otherwise leave this gate resolving citations against a half-empty symbol table — reporting every id of the
# missing kind as dangling, which is loud, or (if the tree happened to cite none of them) reporting a clean
# run over a PRD it could no longer read. Named separately so the message says which half stopped parsing.
#
# Open questions are NOT required to exist: the PRD may legitimately resolve and retire all of them, and a
# citation left behind afterwards is then correctly dangling.
if [ "$sections" -eq 0 ]; then
  die "  NO SECTION IDS  $PRD has no '## R-<n> — …' headings"
  die "Either the PRD was restructured or this script no longer reads the form it is written in. Until the"
  die "two are reconciled, every bare section id cited in this repository resolves against nothing."
  exit 1
fi
if [ "$requirements" -eq 0 ]; then
  die "  NO REQUIREMENT IDS  $PRD has no '- **R-<n>.<m>**' bullets"
  die "Either the PRD was restructured or this script no longer reads the form it is written in. Until the"
  die "two are reconciled, every requirement cited in this repository resolves against nothing."
  exit 1
fi

# ---------------------------------------------------------------------------
# 2. The corpus.
# ---------------------------------------------------------------------------
# THE FILE LIST IS READ ON ITS OWN LINE AND CHECKED (gh#126). --others --exclude-standard so a document that
# is written but not yet committed is checked too; without it a local run scans only tracked files and
# reports clean on exactly the new text you are about to commit. The same list is both what is searched and
# what the green line below claims to have read, so that number is not an estimate.
#
# `core.quotepath=false` because the DEFAULT IS true, and under it git C-quotes any path with a byte above
# 0x7f — `"documentation/caf\303\251.md"`. grep is then handed a filename that does not exist, exits 2, and
# this gate reports UNREADABLE about a file that is sitting right there (PR #195 review). A path git quotes
# for the OTHER reasons — an embedded quote, backslash or control character — still arrives quoted, and is
# refused below by name rather than mis-read.
#
# `--deduplicate` because `git ls-files --cached` LISTS A PATH ONCE PER STAGE (gh#240). During an unresolved
# merge the conflicted path sits in the index at stages 1, 2 and 3, so it arrives three times, grep is handed
# the same working-tree file three times, and BOTH numbers on the green line below inflate. The number is
# re-derivable rather than quoted: take the flag off and run the self-test, whose case 39 builds its
# conflicted index with a merge that really conflicts. Three files and thirteen citations there came back as
# `17 citations of 6 distinct ids across 5 files`. The verdict does not move — every stage is the same path
# and resolves the same way — so what this repairs is the evidence line, which was the only number printed
# here that could fail to be true of what was read.
#
# THE FLAG RATHER THAN `sort -u`: sort's collation follows the locale, nothing here pins LC_ALL, and it would
# RE-ORDER the list as well as dedupe it — the order being what decides the order of the DANGLING lines an
# author reads. `--deduplicate` suppresses the stage duplicates and leaves git's own ordering alone; on this
# repository's own tree at fecc463 its output is byte-identical to the unflagged form (217 paths, 10561
# bytes, `cmp` clean). It arrived in git 2.31, and an older git rejects the flag, exits non-zero and lands on
# CANNOT LIST below rather than on a quiet miscount — the direction to fail in.
#
# check-doc-links.sh reads the same list by the same idiom in the same `docs` job and has piped it through
# `sort -u` since the template commit `3a1c42d` — checked, not assumed. This was therefore one of two copies
# of one idiom missing a step rather than a decision, and the two gates answered differently about the same
# tree. When a gate here grows a list-building idiom, read what the other copy of it does.
file_list=""
ls_status=0
file_list="$(git -c core.quotepath=false ls-files --cached --others --exclude-standard --deduplicate)" || ls_status=$?
if [ "$ls_status" -ne 0 ]; then
  die "  CANNOT LIST  git ls-files exited $ls_status under $REPO_ROOT"
  die "No file has been read, so this cannot report the tree's citations clean."
  exit 1
fi
if [ -z "$file_list" ]; then
  die "  NOTHING TO CHECK  git ls-files returned no files under $REPO_ROOT"
  die "This tree is not empty, so this is a broken invocation rather than a clean repository."
  exit 1
fi

# A TRAILING SLASH MEANS A NESTED REPOSITORY, NOT A FILE (PR #195 review). `git ls-files --others` will not
# descend into another repository's working tree, so it names the directory instead — and a directory handed
# to grep makes it exit 2, which this gate correctly refuses to treat as "no citations" and therefore
# reported as UNREADABLE. That fired on the maintainer's own checkout, where fourteen agent worktrees sit
# under a path .gitignore does not cover: a required gate red on a correct tree, which is how a gate gets
# deleted by the first person it wrongly stops.
#
# Excluding them is not a fudge. A nested repository is a DIFFERENT repository; its files are not this
# tree's, `git ls-files` is saying exactly that, and nothing in it can be a citation this project owns. It
# is counted and printed rather than dropped in silence, so the pass still says what it did not read.
#
# Note what is NOT filtered: a path that is absent from the working tree but present in the index — a file
# staged and then deleted. That one MUST still reach grep and produce UNREADABLE, because it is a real hole
# in the corpus rather than a different repository. A `[ -f ]` test here would swallow it.
files=()
nested=0
while IFS= read -r entry; do
  [ -n "$entry" ] || continue
  case "$entry" in
    */) nested=$(( nested + 1 )); continue ;;
    '"'*)
      die "  UNQUOTABLE PATH  git ls-files returned $entry"
      die "That path contains a quote, a backslash or a control character, so git escaped it and the name"
      die "above is not the name on disk. Reading it as one would search the wrong file and report the"
      die "result as this repository's; this gate refuses instead."
      exit 1
      ;;
  esac
  files+=("$entry")
done <<FILELIST
$file_list
FILELIST

if [ "${#files[@]}" -eq 0 ]; then
  die "  NOTHING TO CHECK  every entry git ls-files returned was a nested repository ($nested of them)"
  die "This tree is not empty, so this is a broken invocation rather than a clean repository."
  exit 1
fi

# ---------------------------------------------------------------------------
# 3. The citation side.
# ---------------------------------------------------------------------------
# ONE grep OVER THE WHOLE LIST, not one per file: a fork costs microseconds on the runner and milliseconds on
# a Windows checkout, and a per-file loop is what turns a gate that answers in a second into one nobody runs
# before pushing.
#
# THE STATUS IS READ, AND 1 IS TOLD FROM 2 (the platform contract). grep exits 1 for "nothing matched", which
# is handled below as its own failure, and 2-or-above for "I could not read one of these files" — which must
# never be flattened into "no citations found". `|| true` over that distinction is gh#98.
#
# THE OUTPUT GOES TO A FILE, NOT THROUGH `$( )` (gh#164). A command substitution strips trailing newlines, so
# a line-oriented payload whose last line may be empty comes back a line short — and the status being checked
# correctly is no defence, because what is lost is bytes rather than an exit code. The redirection still
# belongs to the command whose status is read, and `mapfile` from a plain file fails the script if the file
# cannot be opened.
hits_file="$(mktemp)"
trap 'rm -f "$hits_file"' EXIT
grep_status=0
grep -I -H -n -o -E "$CITATION" -- "${files[@]}" > "$hits_file" || grep_status=$?
if [ "$grep_status" -gt 1 ]; then
  die "  UNREADABLE  grep exited $grep_status over ${#files[@]} files under $REPO_ROOT"
  die "At least one file was not read, so this cannot report the tree's citations clean."
  exit 1
fi
hits=()
mapfile -t hits < "$hits_file"
if [ "$grep_status" -eq 1 ] || [ "${#hits[@]}" -eq 0 ]; then
  die "  NO CITATIONS  not one requirement id was found in ${#files[@]} files under $REPO_ROOT"
  die "$PRD alone defines $(( sections + requirements + questions )) of them and is normally in this list, so"
  die "a corpus citing none of them means the search did not happen — an ignored PRD, a broken pattern, a"
  die "filtered file list. A gate that finds nothing must not report everything resolved."
  exit 1
fi

citations=0
dangling=0
declare -A SEEN_ID=()
declare -A DANGLING_IN_PRD=()

for hit in "${hits[@]}"; do
  [ -n "$hit" ] || continue
  # Parsed from the RIGHT: `file:line:id`. A path containing a colon would break a left-to-right split and
  # would do it silently, by mis-reporting the location of a real failure.
  id="${hit##*:}"
  loc="${hit%:*}"
  line_no="${loc##*:}"
  file="${loc%:*}"

  citations=$(( citations + 1 ))
  SEEN_ID["$id"]=1

  if [ -z "${DEFINED[$id]+x}" ]; then
    dangling=$(( dangling + 1 ))
    die "  DANGLING  $file:$line_no  $id"
    if [ "$file" = "$PRD" ]; then
      DANGLING_IN_PRD["$id"]=1
    fi
  fi
done

for id in "${!DANGLING_IN_PRD[@]}"; do
  die "  HINT  $PRD mentions $id but not in a form this gate reads as a definition."
  die "        A definition sits at COLUMN 0 and OUTSIDE any fenced block or HTML comment — '## $id — …'"
  die "        for a section, '- **$id** …' for a requirement or an open question. One that is indented,"
  die "        fenced or commented out is an example or a retired line, not a definition, so it is read as"
  die "        a citation — which is what has just been reported dangling."
done

if [ "$citations" -eq 0 ]; then
  die "  NOTHING CHECKED  the search returned output but no citation parsed out of it"
  die "This is a broken gate rather than a clean tree."
  exit 1
fi

if [ "$dangling" -gt 0 ]; then
  echo >&2
  die "$dangling dangling citation(s) of $citations, across ${#files[@]} files."
  explain
  exit 1
fi

# The pass carries its own evidence, the way check-no-order-path.sh prints the number of files it read: a
# green line naming zero of anything is the shape every dead gate in this repository has had. Each number
# below is independently re-derivable from one command, which is the point of printing them.
ok "ok  $citations citations of ${#SEEN_ID[@]} distinct ids across ${#files[@]} files — every one resolves to a $PRD definition ($sections sections, $requirements requirements, $questions open questions; $inert of its lines inert inside fences or comments). Not read: $nested nested repositories. This proves each id EXISTS, not that the citation quotes it correctly."
