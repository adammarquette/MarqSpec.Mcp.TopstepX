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
# `R-#` IS CORRECT INPUT, NOT A CITATION — thirteen lines across ten files use it as the literal placeholder
# for "the requirement id", AGENTS.md, CONTRIBUTING.md, README.md, documentation/README.md, the pull request
# template and wiki/SCHEMA.md among them. `#` is not a digit, so the pattern above excludes it; a pattern
# written any looser does not, and would redden six of this repository's most-read files.
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
# Column 0 only: an indented `- **R-…**` is a citation inside some other bullet, not a definition, and
# reading it as one would let a stray mention define an id. If that makes a real definition invisible the
# gate goes red on every citation of it — the safe direction — and the failure carries a hint saying the PRD
# mentions the id in a form this script does not read as a definition.
#
# THERE IS NO EXCLUSION LIST, and that is why this file spells no id that does not resolve. The gate reads
# its own source and its own self-test like every other file in the corpus. An exclusion list is also how a
# gate stops seeing the file that matters, and the two files most likely to acquire a stale id are these two.
# The self-test assembles its dangling fixtures at run time from a prefix and a number for the same reason.
#
# BLIND SPOTS, stated rather than papered over:
#   - Binary files are skipped (`grep -I`). A citation inside one is invisible; none of this corpus is.
#   - The issue and pull request bodies are not files, so nothing here reads them.
#   - Correctness of the quotation, as above.
#
# ITS OWN ABILITY TO FAIL IS TESTED: scripts/check-requirement-ids-selftest.sh runs this script against
# fixtures whose faults are known and requires it to reject each one BY NAME, and to accept the awkward
# correct shapes this repository actually contains. Both run in ci.yml's `docs` job, which is already a
# required context on all three rungs — so this is enforcing rather than reporting, and no ruleset was
# written to make it so.

set -euo pipefail

die()  { printf '\033[31m%s\033[0m\n' "$*" >&2; }
ok()   { printf '\033[32m%s\033[0m\n' "$*"; }
info() { printf '%s\n' "$*"; }

REPO_ROOT="${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"
cd "$REPO_ROOT" || { die "  NO SUCH ROOT  $REPO_ROOT"; exit 1; }

PRD="documentation/prd.md"

# One ERE, used for the corpus scan. Kept in one place so the citation side and the header above cannot
# drift apart.
CITATION='\b[RQ]-[0-9]+(\.[0-9]+)*'

DEF_SECTION='^##[[:space:]]+(R-[0-9]+)([^0-9.]|$)'
DEF_REQUIREMENT='^-[[:space:]]+\*\*(R-[0-9]+\.[0-9]+)\*\*'
DEF_QUESTION='^-[[:space:]]+\*\*(Q-[0-9]+)([^0-9.]|$)'

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

Do not add the id to an exclusion list. There is no exclusion list.
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

for line in "${prd_lines[@]}"; do
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
file_list=""
ls_status=0
file_list="$(git ls-files --cached --others --exclude-standard)" || ls_status=$?
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

files=()
mapfile -t files <<< "$file_list"

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
raw=""
grep_status=0
raw="$(grep -I -H -n -o -E "$CITATION" -- "${files[@]}")" || grep_status=$?
if [ "$grep_status" -gt 1 ]; then
  die "  UNREADABLE  grep exited $grep_status over ${#files[@]} files under $REPO_ROOT"
  die "At least one file was not read, so this cannot report the tree's citations clean."
  exit 1
fi
if [ "$grep_status" -eq 1 ] || [ -z "$raw" ]; then
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

while IFS= read -r hit; do
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
done <<HITS
$raw
HITS

for id in "${!DANGLING_IN_PRD[@]}"; do
  die "  HINT  $PRD mentions $id but not in a form this gate reads as a definition."
  die "        A definition sits at column 0 as '## $id — …' or '- **$id** …'. An indented or reworded one"
  die "        is read as a citation, which is what has just been reported dangling."
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
ok "ok  $citations citations of ${#SEEN_ID[@]} distinct ids across ${#files[@]} files — every one resolves to a $PRD definition ($sections sections, $requirements requirements, $questions open questions). This proves each id EXISTS, not that the citation quotes it correctly."
