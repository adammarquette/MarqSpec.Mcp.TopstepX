#!/usr/bin/env bash
# check-requirement-ids-selftest.sh — require that check-requirement-ids.sh can still go red.
#
#   scripts/check-requirement-ids-selftest.sh
#
# THE GATE IT GUARDS HAS TO BE ABLE TO FAIL (gh#182; the rule gh#98 wrote down, gh#108 applied and gh#160
# applied again). This repository has shipped six guards that could not fire — gh#43 (green on an unpaced
# loop), gh#98 (`|| true` resetting `PIPESTATUS`), gh#114 and gh#126 (swallowed reads reporting "nothing
# found"), gh#164, and check-no-order-path.sh once reporting "no order path" having read zero files — and
# gh#160's brand-new size gate shipped with two more that its reviewers found by mutation.
# check-requirement-ids.sh has the same shape as every one of them: a shell script that decides a verdict by
# matching text it does not own. One demoted heading, one reformatted bullet, one `|| true` left after a
# debugging session, and it resolves zero citations and prints a green line saying so.
#
# So the REAL gate is run against fixtures whose faults are known, on every CI run, and is required to reject
# each one — AND against the awkward CORRECT shapes this repository actually contains, which it must accept.
#
# FIVE OF THE SEVENTEEN CASES ARE HERE BECAUSE MUTATION FOUND THEM MISSING, and that is the point of the
# paragraph rather than a confession. The author's own battery found one — swallowing grep's exit 2, the
# same hole gh#43, gh#98 and gh#126 each shipped. The PR #195 review found four more: a definition inside a
# fenced block and one inside an HTML comment (the gate's ONLY fail-open, and it went green on the real tree
# with a citation of an invented id), a nested repository turning a correct tree red, and three separate
# ways of destroying the reported LINE NUMBER that every case survived because the needle was the filename,
# of which the location is a superset.
#
# So: mutate the subject before believing its self-test, every time. A self-test is a text-matching gate
# too, and the ones that pass on a broken subject are the ones nobody ever ran against one.
# Rejections alone would all be satisfied by `exit 1`, i.e. by a gate that says no to everything, which is
# exactly as useless as one that says yes to everything and rather harder to notice.
#
# NON-ZERO EXIT IS NOT SUFFICIENT AND IS NOT WHAT THIS ASSERTS. check-requirement-ids.sh also exits 1 for "no
# such root" and for a missing PRD, so a self-test satisfied by status alone would go green on a runner where
# the fixtures never got written — reporting the gate sound at precisely the moment nothing had been checked.
# Each case below matches the words that name ITS OWN fault, and every dangling case additionally matches the
# ID ITSELF, so a gate that has stopped printing which symbol failed cannot satisfy it either.
#
# AND THE ACCEPTANCE IS NOT SATISFIED BY EXIT 0 EITHER. Every green case matches the COUNTS the gate prints,
# so a gate that resolved nothing cannot pass one. Two of them — the ADR near-miss and the `R-#` placeholder
# — assert a count IDENTICAL to what the same fixture reports with those lines absent, which is how they
# prove those lines contributed no citations rather than merely failing to break anything.
#
# THE FIXTURE IDS ARE ASSEMBLED AT RUN TIME, from a prefix and a number, so this file's own bytes contain no
# citation that does not resolve. That is deliberate: check-requirement-ids.sh has NO exclusion list and
# reads this file like every other one in the corpus. An exclusion list is also how a gate stops seeing the
# file that matters, and the two files most likely to acquire a stale id are the gate and this.
#
# TWO RUNS, NOT ONE (the Coding contract, Tests). This file is the first: red on the faults the gate exists
# to catch. The second is ci.yml's `docs` job running the gate against this repository's REAL tree — the most
# awkward correct input there is, with twelve ADR numbers that contain a citation-shaped substring, fourteen
# lines citing bare section ids, thirteen lines using the literal placeholder across ten files, an
# issue-template placeholder string, and a PRD that cites its own ids.
#
# LOCAL RUNTIME. Each case forks `git init` and a shell. That is milliseconds on the CI runner and can be a
# few seconds on a Windows checkout, where process creation is pathologically slow. Run it before pushing a
# change to either script, not on every save.

set -euo pipefail

red()  { printf '\033[31m%s\033[0m\n' "$*" >&2; }
ok()   { printf '\033[32m%s\033[0m\n' "$*"; }
info() { printf '%s\n' "$*"; }

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GATE="$REPO_ROOT/scripts/check-requirement-ids.sh"
if [ ! -f "$GATE" ]; then
  red "  MISSING  $GATE"
  red "NOTHING HAS BEEN CHECKED, and in particular the gate has not been proven able to fail."
  exit 1
fi

FIXTURES="$(mktemp -d)"
trap 'rm -rf "$FIXTURES"' EXIT

failures=0
cases=0

# Assembled, never spelled — see the header. The fixture PRD below DOES spell ids, deliberately: every one
# of them is an id the REAL PRD also defines, so those literals are correct input when the gate reads this
# file. What is never spelled is an id that does NOT resolve, which is the whole reason these four are built
# at run time. (The earlier wording here claimed no `R-` or `Q-` was followed by a digit anywhere in this
# file, which was simply false — PR #195 review counted twelve.)
R='R-'
Q='Q-'
DANGLING_REQUIREMENT="${R}9.3"   # gh#172's class exactly: MarqSpec.Client.ProjectX's id for the secrets rule
DANGLING_SECTION="${R}42"
DANGLING_QUESTION="${Q}9"
OVER_DEEP="${R}1.2.3"            # first two parts DO resolve; the whole id must not

# Builds one fixture repository.
#
#   $1 dir          fixture root
#   $2 prd_kind     sound | absent | flat-headings | no-bullets | shadowed | fenced | commented
#   $3 notes        the body of documentation/notes.md — the file whose citations are under test
#   $4 extra_kind   none | rich | ignore-prd | unreadable | nested
#
# Every case is a one-perturbation change from the sound fixture. A fixture that differs in more than the
# fault under test proves nothing about which fault the gate detected.
#
# THE FIXTURE PRD CITES ONLY IDS THE REAL PRD ALSO DEFINES, so the literals below are correct input to the
# real gate when it reads this file. Its own citation count is fixed and is what the green cases assert:
# two section headings, three requirement bullets, one open question, and one cross-reference in prose.
make_fixture() {
  local dir="$1" prd_kind="$2" notes="$3" extra_kind="$4"
  mkdir -p "$dir/documentation"
  git -c init.defaultBranch=main init -q "$dir"

  if [ "$prd_kind" != "absent" ]; then
    local heading='##'
    [ "$prd_kind" != "flat-headings" ] || heading='###'
    {
      printf '# fixture requirements\n\n'
      printf '%s R-1 — First section\n\n' "$heading"
      if [ "$prd_kind" != "no-bullets" ]; then
        printf -- '- **R-1.1** The first requirement.\n'
        printf -- '- **R-1.2** The second requirement.\n\n'
      fi
      printf '%s R-2 — Second section\n\n' "$heading"
      if [ "$prd_kind" != "no-bullets" ]; then
        printf -- '- **R-2.1** Another requirement, which does not weaken R-1.\n'
        # An INDENTED bullet is a citation inside another bullet, never a definition. The gate must go on
        # calling this id dangling, and must say why rather than leaving the author staring at a PRD that
        # visibly contains the id it has just refused.
        [ "$prd_kind" != "shadowed" ] || printf -- '  - **%s** an indented sub-bullet, which defines nothing.\n' "$DANGLING_REQUIREMENT"
        printf '\n'
      fi
      printf '## Open questions\n\n'
      if [ "$prd_kind" != "no-bullets" ]; then
        printf -- '- **Q-1 — A question.** Its text.\n'
      fi
      # An EXAMPLE of the PRD's own format, and a requirement RETIRED by commenting it out. Both look
      # exactly like definitions to a line-at-a-time parser and neither is one, and this is the ONLY place
      # in the gate where a misreading fails OPEN: it would invent the symbol and then resolve a citation of
      # it. Found by the PR #195 review, on the real tree, with a green `ok … every one resolves`.
      if [ "$prd_kind" = "fenced" ]; then
        printf '\n## Appendix\n\n```markdown\n## %s — Example\n\n- **%s** Example.\n```\n' \
          "$DANGLING_SECTION" "$DANGLING_REQUIREMENT"
      fi
      if [ "$prd_kind" = "commented" ]; then
        printf '\n<!--\n- **%s** Retired, kept for the record.\n-->\n' "$DANGLING_REQUIREMENT"
      fi
    } > "$dir/documentation/prd.md"
  fi

  printf '%s\n' "$notes" > "$dir/documentation/notes.md"

  case "$extra_kind" in
    rich)
      # A NON-MARKDOWN file carrying a citation, the shape .github/ISSUE_TEMPLATE/task.yml really has, and a
      # BINARY file the gate must step over without erroring rather than without noticing.
      mkdir -p "$dir/.github/ISSUE_TEMPLATE"
      printf '      placeholder: "R-2, ADR-0002, adammarquette/trading-copilot#589"\n' \
        > "$dir/.github/ISSUE_TEMPLATE/task.yml"
      mkdir -p "$dir/assets"
      printf 'PK\003\004binary\000payload\000' > "$dir/assets/blob.bin"
      ;;
    ignore-prd)
      # The PRD exists and parses, but nothing in the corpus can see it. Definitions load, the search finds
      # nothing, and without the NO CITATIONS guard the gate would report a clean tree having resolved zero
      # symbols — the exact vacuous green every dead guard in this repository printed.
      printf 'documentation/prd.md\n' > "$dir/.gitignore"
      ;;
    nested)
      # ANOTHER REPOSITORY inside this one. `git ls-files --others` will not descend into it, so it names
      # the DIRECTORY with a trailing slash — and a directory handed to grep makes it exit 2, which this
      # gate correctly refuses to read as "no citations". Before the PR #195 review it therefore reported
      # UNREADABLE on the maintainer's own checkout, where fourteen agent worktrees sit under a path
      # .gitignore does not cover: a required gate red on a correct tree, which is how a gate gets deleted
      # by the first person it wrongly stops. It must be counted, skipped, and the run must still be green.
      #
      # The nested repository carries a citation that does NOT resolve, so this case also proves the skip is
      # real rather than the directory happening to contain nothing.
      mkdir -p "$dir/nested"
      git -c init.defaultBranch=main init -q "$dir/nested"
      printf 'A note in another repository citing %s99.9.\n' "$R" > "$dir/nested/notes.md"
      ;;
    unreadable)
      # A file the corpus LISTS and grep cannot OPEN: staged in the index, then removed from the working
      # tree, so `git ls-files --cached` still names it. That makes grep exit 2 while other files match, and
      # 2 is the code this whole family of gates keeps flattening into "no match" (gh#43, gh#98, gh#126).
      #
      # Staged-then-deleted rather than `chmod 000` or a broken symlink on purpose: the runner's job runs as
      # a user root-like enough to read a mode-000 file, which would make the fixture INERT in CI while
      # passing locally, and Git Bash does not make real symlinks without a non-default MSYS setting.
      printf 'A note citing R-1.1.\n' > "$dir/documentation/gone.md"
      # -c core.autocrlf=false so a Windows checkout does not print a line-ending warning into the
      # middle of this self-test's output. The fixture has no .gitattributes to pin it.
      git -C "$dir" -c core.autocrlf=false add documentation/gone.md
      rm "$dir/documentation/gone.md"
      ;;
  esac
}

# Runs the REAL gate against a fixture and requires it to REJECT it, saying why in its own words. Every
# needle must appear; a gate that rejects for some other reason fails this.
expect_red() {
  local label="$1" dir="$2"; shift 2
  local out status=0 needle
  cases=$(( cases + 1 ))

  out="$(bash "$GATE" "$dir" 2>&1)" || status=$?

  if [ "$status" -eq 0 ]; then
    red "SELF-TEST FAILED  $label"
    red "  The gate ACCEPTED a fixture it must reject. Everything it reports elsewhere is now worthless."
    info "$out"
    failures=$(( failures + 1 ))
    return
  fi
  for needle in "$@"; do
    if [[ "$out" != *"$needle"* ]]; then
      red "SELF-TEST FAILED  $label"
      red "  The gate rejected the fixture but never said '$needle', so it failed for some OTHER reason —"
      red "  or it failed without naming the symbol, which is the half an author acts on."
      info "$out"
      failures=$(( failures + 1 ))
      return
    fi
  done
  ok "  red as required  $label  ($*)"
}

# Runs the REAL gate against a CORRECT fixture and requires it to ACCEPT it — and to say what it resolved.
expect_green() {
  local label="$1" dir="$2" needle="$3" out status=0
  cases=$(( cases + 1 ))

  out="$(bash "$GATE" "$dir" 2>&1)" || status=$?

  if [ "$status" -ne 0 ]; then
    red "SELF-TEST FAILED  $label"
    red "  The gate REJECTED correct input (exit $status). It would fail correct pull requests, and the"
    red "  first person it wrongly stops will delete it."
    info "$out"
    failures=$(( failures + 1 ))
    return
  fi
  if [[ "$out" != *"$needle"* ]]; then
    red "SELF-TEST FAILED  $label"
    red "  The gate passed without saying '$needle'. Exit 0 having resolved NOTHING — or having quietly"
    red "  resolved MORE than the fixture contains — is the shape of every dead guard in this repository."
    info "$out"
    failures=$(( failures + 1 ))
    return
  fi
  ok "  green as required  $label  ($needle)"
}

# The fixture PRD alone carries seven citations across six distinct ids. Every green case below is measured
# against that baseline, so a case whose extra lines were meant to contribute nothing is PROVEN to have
# contributed nothing rather than merely observed not to have broken anything.
BASELINE='7 citations of 6 distinct ids'

# Correct citations of all four shapes: a bare section id, a requirement, an open question, and (in the
# fixture PRD itself) a cross-reference. Used by the sound case and by the unreadable one, which has to
# have real citations resolving beside the file grep could not open.
SOUND_NOTES='Bare section id: `R-1`. A requirement: `R-1.1`. Another: `R-2.1`. An open question: `Q-1`.'

info "check-requirement-ids.sh self-test — the gate is run against fixtures with known faults."
info ""

# ---------------------------------------------------------------------------
# RED — the faults the gate exists to catch. Each names the symbol, not just the file.
# ---------------------------------------------------------------------------

# 1. The fault gh#182 was filed for, in the class gh#172 found by hand: another repository's requirement id.
#
#    THE NEEDLE IS THE FULL `file:line`, NOT THE FILENAME (PR #195 review). `documentation/notes.md` is a
#    substring of `documentation/notes.md:1`, so a gate that had stopped reporting the line — `-n` dropped
#    from its grep, or the field split reversed — satisfied this case with a location an author cannot act
#    on. Three separate mutations escaped all thirteen cases on exactly that.
make_fixture "$FIXTURES/dangling-req" sound "The rule is stated as $DANGLING_REQUIREMENT." none
expect_red "a dangling requirement id" "$FIXTURES/dangling-req" \
  "DANGLING" "documentation/notes.md:1" "$DANGLING_REQUIREMENT"

# 2. A bare SECTION id that does not resolve. Bare ids are cited on fourteen lines of this repository, so
#    they have to be resolved rather than skipped — and skipping them is the cheapest way to make this gate
#    look green.
make_fixture "$FIXTURES/dangling-section" sound "See $DANGLING_SECTION for the boundary." none
expect_red "a dangling section id" "$FIXTURES/dangling-section" "DANGLING" "$DANGLING_SECTION"

# 3. The open-question class. It is covered deliberately, so it must be covered in both directions.
make_fixture "$FIXTURES/dangling-question" sound "Resolved by $DANGLING_QUESTION." none
expect_red "a dangling open question" "$FIXTURES/dangling-question" "DANGLING" "$DANGLING_QUESTION"

# 4. A three-part id. Its first two parts DO resolve, so a pattern that stopped at one minor part would
#    truncate it, resolve the truncation and pass — a citation naming a requirement that does not exist,
#    accepted because the gate read only the front of it.
make_fixture "$FIXTURES/over-deep" sound "As set out in $OVER_DEEP." none
expect_red "an id with a third part that resolves against nothing" "$FIXTURES/over-deep" \
  "DANGLING" "$OVER_DEEP"

# 5. The PRD MENTIONS the id, in a form that is not a definition. Without the hint this is the most
#    confusing red the gate can produce: the author greps the PRD, finds the id, and concludes the gate is
#    broken. The mention is an indented sub-bullet — ordinary markdown, and not a definition.
make_fixture "$FIXTURES/shadowed" shadowed "Governed by $DANGLING_REQUIREMENT." none
expect_red "an id the PRD mentions but does not define" "$FIXTURES/shadowed" \
  "DANGLING" "HINT" "$DANGLING_REQUIREMENT"

# 6. No PRD. There is no symbol table, so nothing can be resolved and nothing may be reported clean.
make_fixture "$FIXTURES/no-prd" absent "A note citing R-1.1." none
expect_red "a missing PRD" "$FIXTURES/no-prd" "MISSING"

# 7. The headings stop being headings. Half the symbol table silently disappears; the gate must say which
#    half rather than report fourteen lines of correct text dangling and leave the reader to work it out.
make_fixture "$FIXTURES/flat-headings" flat-headings "A note citing R-1.1." none
expect_red "section headings the gate can no longer read" "$FIXTURES/flat-headings" "NO SECTION IDS"

# 8. The requirement bullets stop being bullets. The other half.
make_fixture "$FIXTURES/no-bullets" no-bullets "A note citing R-1." none
expect_red "requirement bullets the gate can no longer read" "$FIXTURES/no-bullets" "NO REQUIREMENT IDS"

# 9. THE VACUOUS GREEN ITSELF. The PRD parses, the corpus is real, and the search finds nothing — here
#    because the PRD is ignored and so is not among the files searched. "I found no citations" must never be
#    reported as "every citation resolves".
make_fixture "$FIXTURES/no-citations" sound "This note cites nothing at all." ignore-prd
expect_red "a corpus in which nothing was found" "$FIXTURES/no-citations" "NO CITATIONS"

# 10. "I COULD NOT LOOK", which must never read as "I looked and found nothing". grep exits 2 when it cannot
#     open a file it was given, and every other citation in the fixture still matches — so the run has a full
#     set of resolved symbols sitting beside a file it never read. This case was ADDED because the mutation
#     battery found it missing: swallowing the 2 was the one change to the gate that its self-test did not
#     notice, which is exactly the hole gh#43, gh#98 and gh#126 each shipped.
make_fixture "$FIXTURES/unreadable" sound "$SOUND_NOTES" unreadable
expect_red "a file the corpus lists and grep cannot open" "$FIXTURES/unreadable" "UNREADABLE"

# 11. A DEFINITION INSIDE A FENCED BLOCK — an example of the PRD's own format. The only fail-OPEN this gate
#     has: the parser read it as a definition, invented the symbol, and then resolved a citation of it. Found
#     by the PR #195 review against the real tree, which returned `ok … every one resolves` and exit 0.
make_fixture "$FIXTURES/fenced" fenced "Governed by $DANGLING_REQUIREMENT and section $DANGLING_SECTION." none
expect_red "a definition inside a fenced block" "$FIXTURES/fenced" \
  "DANGLING" "documentation/notes.md:1" "$DANGLING_REQUIREMENT" "$DANGLING_SECTION" "HINT"

# 12. THE SAME FAIL-OPEN THROUGH THE OTHER CONSTRUCT: a requirement retired by commenting it out rather than
#     deleting it, which is the likelier of the two to be written by hand.
make_fixture "$FIXTURES/commented" commented "Governed by $DANGLING_REQUIREMENT." none
expect_red "a definition inside an HTML comment" "$FIXTURES/commented" \
  "DANGLING" "$DANGLING_REQUIREMENT" "HINT"

# ---------------------------------------------------------------------------
# GREEN — correct input this repository really contains, which the gate must not redden.
# ---------------------------------------------------------------------------

# 11. THE ADR NEAR-MISS. An ADR number contains a citation-shaped substring, so a pattern without the left
#     word boundary extracts one phantom id per ADR — twelve of them on the real tree today, every one from
#     a correct reference, and every one dangling. The count asserted is the fixture PRD's own, unchanged:
#     these twelve lines must contribute exactly zero citations.
adr_notes=""
for n in 1 2 3 4 5 6 7 8 9 10 11 12; do
  adr_notes+="$(printf 'Decided by ADR-%04d, which supersedes nothing.' "$n")"$'\n'
done
make_fixture "$FIXTURES/adr-near-miss" sound "$adr_notes" none
expect_green "twelve ADR references and no citations" "$FIXTURES/adr-near-miss" "$BASELINE"

# 12. THE LITERAL PLACEHOLDER, on thirteen lines across ten of this repository's most-read files — AGENTS.md,
#     CONTRIBUTING.md, README.md, documentation/README.md, the pull request template, wiki/SCHEMA.md. A
#     pattern one character looser reddens all of them, which is a required check on all three rungs
#     blocking every pull request over text that is exactly right.
placeholder_notes='Docs in lockstep — the affected section of the PRD (`R-#`), the architecture doc, the ADRs.
`R-#`, ADR numbers and gh#N are the symbol table.
> **Informs:** <the R-# / Q-# it grounds>'
make_fixture "$FIXTURES/placeholder" sound "$placeholder_notes" none
expect_green "the literal placeholder, which is not a citation" "$FIXTURES/placeholder" "$BASELINE"

# 13. THE SOUND CORPUS: a bare section id, a requirement, an open question, a cross-reference inside the PRD
#     itself, a citation in a NON-MARKDOWN file, and a binary file that must be stepped over rather than
#     erred on. Four notes citations plus one in the yaml, on top of the baseline's seven.
make_fixture "$FIXTURES/sound" sound "$SOUND_NOTES" rich
expect_green "a sound corpus, yaml and binary included" "$FIXTURES/sound" "12 citations of 6 distinct ids"

#     …and the DEFINITION side's own counters, which nothing else asserts. Frozen at 1 each, every case
#     above still passed: the green needle stopped short of this tuple and the red cases never reach it
#     (PR #195 review). The fixture PRD has two headings, three requirement bullets and one open question.
expect_green "the definition counts the gate reports" "$FIXTURES/sound" \
  "(2 sections, 3 requirements, 1 open questions"

#     A NESTED REPOSITORY, which must be skipped and SAID, not errored on. It carries a citation that does
#     not resolve, so a gate that read into it would go red rather than merely counting differently.
make_fixture "$FIXTURES/nested" sound "$SOUND_NOTES" nested
expect_green "a nested repository, counted and not read" "$FIXTURES/nested" \
  "Not read: 1 nested repositories"

info ""
if [ "$failures" -gt 0 ]; then
  red "$failures of $cases self-test case(s) failed."
  red "check-requirement-ids.sh is NOT known to be able to fail, so its green runs prove nothing. Fix it"
  red "before trusting anything it reports."
  exit 1
fi

if [ "$cases" -eq 0 ]; then
  red "  NOTHING CHECKED  no self-test cases ran."
  red "This file exists to prove the gate can fail and has just proven nothing."
  exit 1
fi

ok "ok  $cases self-test cases — check-requirement-ids.sh rejects each known fault BY NAME, BY SYMBOL and BY LOCATION, and accepts the awkward correct shapes this repository contains: ADR numbers, the literal placeholder, bare section ids, a yaml citation, a binary file and a nested repository."
