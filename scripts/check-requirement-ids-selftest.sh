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
# Rejections alone would all be satisfied by `exit 1`, i.e. by a gate that says no to everything, which is
# exactly as useless as one that says yes to everything and rather harder to notice.
#
# MOST OF THESE CASES ARE HERE BECAUSE A DECISION THIS GATE MAKES WAS HELD BY NOTHING, and that is the
# point of the paragraph rather than a confession. Nine review rounds -- and the ledger above is what
# finally made the question finite, after five of them found rules one at a time; the ones after that came
# from AUDITING THE LEDGER ITSELF against the script, which is the step its own legend prescribes and which
# its author had not run. No count here either, and for the legend's reason: it is a live tally over a set
# that keeps changing, and the three versions of it that stood in this file were each wrong within a round.
#
#   the author's battery      swallowing grep's exit 2 — the hole gh#43, gh#98 and gh#126 each shipped.
#   review round 1            a definition inside a fenced block, and one inside an HTML comment (the gate's
#                             ONLY fail-open: it went green on the real tree over a citation of an invented
#                             id); a nested repository turning a correct tree red; frozen definition
#                             counters; and three ways of destroying the reported LINE NUMBER, all of which
#                             every case survived because the needle was the FILENAME, of which the location
#                             is a superset.
#   mutating that fix         a fence whose closer carries trailing whitespace, and each of the two
#                             constructs nested inside the other.
#   review round 2            three marker lines that are CONTENT to CommonMark and were closing a fence —
#                             over-indented, behind a tab, mixing the two fence characters; the
#                             UNTERMINATED COMMENT check, which nothing exercised; and `core.quotepath`,
#                             likewise.
#   review round 3            the OPENER's two rules — its three-column cap and its three-marker minimum —
#                             and the CLOSER's length rule, which is rule 3 of the three the gate's own
#                             comment enumerates while rules 1 and 2 had just gained cases. Plus, from
#                             sweeping that same question across every rule the comments assert: an
#                             unterminated FENCE is legal and must be counted rather than refused, and
#                             `grep -I` was held by a binary fixture carrying no id.
#
# Read the shape rather than the list: EVERY ROUND FOUND SOMETHING, including the round that was auditing
# the previous round's fix, and the two fail-opens both lived in the newest code. So: mutate the subject
# before believing its self-test, every time. A self-test is a text-matching gate too, and the ones that
# pass on a broken subject are the ones nobody ever ran against one.
#
# NON-ZERO EXIT IS NOT SUFFICIENT AND IS NOT WHAT THIS ASSERTS. check-requirement-ids.sh also exits 1 for "no
# such root" and for a missing PRD, so a self-test satisfied by status alone would go green on a runner where
# the fixtures never got written — reporting the gate sound at precisely the moment nothing had been checked.
# Each case below matches the words that name ITS OWN fault, and every dangling case additionally matches the
# ID ITSELF, so a gate that has stopped printing which symbol failed cannot satisfy it either.
#
# AND THE ACCEPTANCE IS NOT SATISFIED BY EXIT 0 EITHER. Every green assertion matches the COUNTS the gate
# prints, so a gate that resolved nothing cannot pass one. Two of them — the ADR near-miss and the `R-#`
# placeholder — assert a count IDENTICAL to what the same fixture reports with those lines absent, which is
# how they prove those lines contributed no citations rather than merely failing to break anything.
#
# THE FIXTURE IDS ARE ASSEMBLED AT RUN TIME, from a prefix and a number, so this file's own bytes contain no
# citation that does not resolve. That is deliberate: check-requirement-ids.sh has NO exclusion list and
# reads this file like every other one in the corpus. An exclusion list is also how a gate stops seeing the
# file that matters, and the two files most likely to acquire a stale id are the gate and this.
#
# TWO RUNS, NOT ONE (the Coding contract, Tests). This file is the first: red on the faults the gate exists
# to catch. The second is ci.yml's `docs` job running the gate against this repository's REAL tree — the most
# awkward correct input there is, with twelve ADR numbers that contain a citation-shaped substring, fourteen
# lines citing bare section ids, an issue-template placeholder string, a PRD that cites its own ids, and the
# literal placeholder on thirteen lines across ten files as of `develop` at `8a2302d` — pinned to a commit
# because this file and its gate keep adding more of them, and an unpinned count is wrong the next time
# either is edited.
#
# LOCAL RUNTIME. Each case forks `git init` and a shell. That is milliseconds on the CI runner and can be a
# few seconds on a Windows checkout, where process creation is pathologically slow. Run it before pushing a
# change to either script, not on every save.

# WHAT THIS SUITE COULD NOT SEE, found by breaking the gate accidentally while writing the line above (PR #195
# round 8). A stray carriage return split a comment in the gate's HEADER, leaving a bare word that ran as a
# command and printed `command not found` before every invocation. ALL FORTY-TWO CASES STILL PASSED: the
# broken line sits above the gate's own `set -euo pipefail`, so command-not-found was non-fatal, the exit
# code was unchanged, and every needle still matched. CI would have gone green on it too.
#
# Exit status and named substrings are both assertions about output that IS there. Neither can notice output
# that should not be there at all, and no number of further needles would have: the stray line matched none of
# them. So expect_green makes the one assertion that does (gh#239) -- it SPLITS the gate's streams instead of
# merging them with `2>&1`, and requires stderr to be EMPTY.
#
# THAT IS A MEASUREMENT, NOT A PREFERENCE, and it was re-taken on `796b14c` rather than carried over from the
# card that asked for it. A green run of this gate writes exactly ZERO bytes to stderr: 324 B stdout / 0 B
# stderr against the real repository, and 318-319 B / 0 B across all nineteen green fixtures -- the
# nested-repository one, the likeliest to leak because the gate SKIPS a nested repo where `grep` would
# otherwise complain about a directory, at 319 B. Only the stderr half is asserted; the stdout figure is
# quoted as a RANGE because it tracks the digits in the counts the gate prints, and gh#239 recorded a single
# 318 B for a fixture measuring 319 B here. It is the same KIND of claim the cases below already make, not a
# new one -- and it would have caught the carriage return on all nineteen green cases.
#
# THE RED CASES ARE EXEMPT, DELIBERATELY: the gate reports their faults through `die`, which writes to stderr,
# so there stderr is the answer rather than stray output. The reason is recorded again beside expect_red,
# because the asymmetry looks like an oversight and the fix for a supposed oversight is deleting the
# assertion.

set -euo pipefail

# ---------------------------------------------------------------------------
# THE DECISION LEDGER
# ---------------------------------------------------------------------------
# Every decision check-requirement-ids.sh makes, against the case that kills it. Written after FIVE review
# rounds each turned up a rule nothing pinned -- the diagnosis was not any one rule but the method: this file
# pinned what a reviewer had pointed at, not what the script decides. #193 reached the same conclusion on
# `check-doc-sizes.sh` and #202 on `issue-link`; the platform contract carries the rule, and this is the
# third gate to need it. Adding a decision without adding a row is then the same visible omission.
#
# GRADES, and the distinction is the whole point (#193: a ledger is a claim too):
#   mut   individually mutated in the script, and the NAMED cases went red. Re-run before writing the row --
#         a grade that promises more than its evidence is how the first ledger in this repo lied.
#   rev   mutated by the reviewer rather than the author, with the same evidence.
#   case  exercised by the case named, NOT individually mutated. Weaker, and said so.
#   none  no fixture reaches it. **AUDIT THESE ROWS FIRST.** They have been wrong more often than any other
#         row in this table, and in almost every case the stated reason was A TRUE FACT ABOUT A LAYER THE
#         CODE DOES NOT CONSULT: NTFS refuses a quote in a filename, while `git ls-files` reads the index;
#         a stray `C:\.git` makes the drive a work tree, while `GIT_CEILING_DIRECTORIES` stops discovery
#         anyway; "it asserts an absence", while a green case pins an absence perfectly well; "a case would
#         assert the blind spot, not the rule", while a case is simply one fixture. So ask not "is this hard
#         to build" but **"is my reason about the thing the code actually consults?"**
#
#         THERE IS DELIBERATELY NO COUNT HERE. One stood in this legend for three rounds and was wrong in
#         all three, because it is a live tally over a set this file keeps changing -- correcting it is what
#         produced the next wrong version. #190 hit the same thing with its open-item count (seven, six,
#         five, in two hours), deleted the number and kept the invariant; the invariant above stays true
#         however many rows fall next.

#
# CITATION SIDE
#    left word boundary .................... mut   adr-near-miss, sound corpus
#   [RQ] covers both id classes .............. mut   dangling open question; both count assertions
#   [0-9] excludes the `#` placeholder ....... mut   the literal placeholder
#   (\.[0-9]+)* repeats ...................... mut   an id with a third part
#   grep -I skips binaries ................... mut   sound corpus, definition counts
#   grep -o prints only the match ............ case  the dangling cases, whose needles are ids -- which is
#                                                    what the flag produces. Not individually mutated.
#   grep -H prints the filename .............. mut   case 34, a corpus of exactly ONE file. Grep prints the
#                                                    name unasked once it has two, so every other case here
#                                                    passes without it -- measured, not assumed.
#   grep -n reports the line ................. rev   every dangling case, on the file:line needle
#   grep exit 2 is not "no match" ............ mut   a file the corpus lists and grep cannot open
#   grep exit 1 / no hits is NO CITATIONS .... mut   a corpus in which nothing was found
#   hits parsed from the RIGHT ............... rev   dangling cases (line_no split from the left)
#   output to a file, not $( ) ............... none  } THESE TWO ARE NOT INDEPENDENT, and saying so is the
#                                                    } point: both need an EMPTY LAST LINE out of grep, and
#                                                    } the only thing that would produce one -- a match with
#                                                    } an empty id -- is refused two decisions earlier by
#                                                    } `[0-9]+` in CITATION. So they are SAFER than two
#                                                    } independent rows would suggest, and they fall
#                                                    } together if that pattern ever admits an empty match.
# CORPUS
#   the default REPO_ROOT (no argument) ...... mut   case 37, the gate copied in and run with no argument.
#                                                    THE ONLY INVOCATION ci.yml USES, and every other case
#                                                    passes an explicit root -- so this derivation could be
#                                                    broken with all 38 green while the real run died.
#   core.quotepath=false ..................... mut   a filename git would escape
#   ls-files --cached ........................ mut   a file the corpus lists and grep cannot open
#   ls-files --others ........................ mut   case 30, a written-but-uncommitted file -- AND THE ROW
#                                                    IT REPLACES IS WHY THIS COLUMN HAS A LEGEND. Dropping
#                                                    the flag reddens most of the suite, which reads as heavy
#                                                    pinning and is INCIDENTAL: every other fixture is an
#                                                    uncommitted `git init` tree, so removing it empties the
#                                                    sweep whatever the decision means. Case 30 is the only
#                                                    one ABOUT the decision, and the only one that would
#                                                    still redden if every fixture were committed. That
#                                                    distinction is the row's whole point and it holds at any
#                                                    suite size; the ratio did not. Compare `--cached`,
#                                                    pinned at exactly one case.
#   ls-files --exclude-standard .............. case  case 29 (every file ignored) and the ignore-prd case
#   the `--` separator before the file list .. mut   case 33, a root file whose name begins with a dash
#   trailing-slash entries are nested repos .. mut   a nested repository, counted and not read
#   staged-then-deleted still reaches grep ... case  the unreadable case (no `[ -f ]` shortcut to mutate)
#   empty file list is NOTHING TO CHECK ...... mut   case 29, every file ignored
#   UNQUOTABLE PATH .......................... mut   case 35, a quote-bearing path staged into the index.
#                                                    GRADED `none` BECAUSE NTFS REFUSES A QUOTE IN A
#                                                    FILENAME -- true, and about the wrong layer entirely:
#                                                    `git ls-files` reads THE INDEX, and
#                                                    `update-index --cacheinfo` needs no file on disk.
#   ls-files failure is CANNOT LIST .......... mut   case 31. GRADED `none` HERE ON A REASON THAT WAS TRUE
#                                                    AND IRRELEVANT: a stray `C:\.git` does make the drive root
#                                                    a work tree, and `GIT_CEILING_DIRECTORIES` stops the
#                                                    upward discovery anyway. Reachable, portable, same on
#                                                    the runner -- the failure-direction mistake one level up.
#   NO SUCH ROOT ............................. mut   case 32, a root that does not exist. Its own die, its
#                                                    own exit, one command to reach, and no row at all.
# DEFINITION SIDE
#   the PRD exists ........................... mut   a missing PRD
#   the PRD has lines ........................ mut   case 28, a PRD that exists and is empty
#   DEF_SECTION matches `## R-N` ............. mut   headings the gate can no longer read
#   DEF_SECTION's right boundary ............. mut   a mis-levelled heading defining a bare section id
#   DEF_REQUIREMENT matches `- **R-N.M**` .... mut   bullets the gate can no longer read
#   DEF_REQUIREMENT's closing `**` ........... rev   bullets the gate can no longer read
#   DEF_QUESTION matches `- **Q-N`............ case  dangling open question; the count assertions
#   definitions sit at column 0 .............. case  an id the PRD mentions but does not define
#   fence opener: indent cap ................. mut   an over-indented opener
#   fence opener: three-marker minimum ....... mut   prose opening with an inline code span
#   fence opener: backtick info-string rule .. mut   a backtick fence whose info string has a backtick
#   ...and that rule is backtick-ONLY ........ mut   a tilde fence whose info string has a backtick
#   fence closer: indent cap ................. mut   over-indented closer; closer behind a tab
#   fence closer: the opener's own character . mut   a closing marker mixing fence characters
#   fence closer: at least the opener's length mut   a closing marker shorter than its opener
#   fence closer: trailing whitespace is ok .. case  a fence closed with trailing whitespace
#   comment state is read BEFORE fence state . mut   a fence inside a comment, and a comment inside a fence
#   inline code spans are NOT stepped over ... mut   case 38, a `<!--` written as inline code. Called
#                                                    unpinnable because "a case would assert the blind spot,
#                                                    not the rule" -- a case is one fixture, and this one
#                                                    flips the moment a scanner steps over spans.
#   sections==0 is NO SECTION IDS ............ mut   headings the gate can no longer read
#   requirements==0 is NO REQUIREMENT IDS .... mut   bullets the gate can no longer read
#   open questions are NOT required .......... mut   case 36. THE MUTATION'S PLACEMENT IS PART OF THE GRADE:
#                                                    a `questions -eq 0` guard inserted AFTER the
#                                                    `requirements -eq 0` check reddens case 36 alone,
#                                                    because the no-bullets fixture dies earlier on NO
#                                                    REQUIREMENT IDS. Inserted BEFORE it, both redden.
#                                                    Unstated, the grade is unreproducible. The row said
#                                                    this could not be pinned because it asserts an absence;
#                                                    a green case pins one, and every other fixture merely
#                                                    happened to carry the Q-1 bullet.
#   an unterminated COMMENT is fatal ......... mut   an HTML comment the PRD never closes
#   an unterminated FENCE is tolerated ....... case  a fence the document never closes
#   the inert counter ........................ rev   a fence the document never closes
# VERDICT
#   DEFINED membership test .................. mut   every dangling case. NO COUNT HERE: this row said seven,
#                                                    then eight, and measured twelve -- three wrong values in
#                                                    three rounds, on the row with the most cases to drift
#                                                    under it. The legend's rule, one level down.
#   the HINT when the id is in the PRD ....... mut   shadowed, fenced, commented
#   citations==0 is NOTHING CHECKED .......... none  } (the other half of the pair above -- it fires only if
#                                                    } the hits array is non-empty and every entry fails the
#                                                    } `[ -n ]` guard, i.e. grep emitted only blank lines)
#   dangling>0 exits 1 ....................... case  every red case
#   the green line's counts .................. mut   frozen definition counters; frozen section counter
# THE OUTPUT STREAM ITSELF -- not a decision the gate MAKES, which is why it had no row for eight rounds
#   a green run says nothing on STDERR ....... mut   ALL NINETEEN green cases, and the mutation is gh#239's
#                                                    own defect put back: a bare word above the gate's `set
#                                                    -euo pipefail`. Non-fatal, so the exit status is
#                                                    unchanged and EVERY BYTE OF STDOUT is unchanged with it
#                                                    -- which is why status and needles both missed it and
#                                                    all forty-two cases passed. Measured 19 green cases red
#                                                    / 23 red cases still green, so the red half is pinned as
#                                                    EXEMPT rather than merely left untouched.
#                                                    THE GRADE IS THE WHOLE POINT HERE: this row could only
#                                                    ever have been written `mut`, because the assertion's
#                                                    entire claim is about a defect no case could see.
# ---------------------------------------------------------------------------

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
DANGLING_REQUIREMENT_99="${R}9.9"

# Builds one fixture repository.
#
#   $1 dir          fixture root
#   $2 prd_kind     sound | absent | flat-headings | no-bullets | shadowed | fenced | commented | fenced-closed | nested-constructs | fence-bad-closer | unterminated-comment | inline-code-line | unterminated-fence | opener-overindented | backtick-info | tilde-info | misleveled-heading | empty-prd | no-questions | span-comment
#   $3 notes        the body of documentation/notes.md — the file whose citations are under test
#   $4 extra_kind   none | rich | ignore-prd | unreadable | nested | non-ascii | all-ignored
#                   | committed | dash-file | single-file | quoted-path | self-hosted
#   $5 bad_closer   fence-bad-closer only: the marker line that must NOT close the fence
#   $6 opener       fence-bad-closer only: the opening marker, so its LENGTH can be varied
#
# Every case is a one-perturbation change from the sound fixture. A fixture that differs in more than the
# fault under test proves nothing about which fault the gate detected.
#
# THE FIXTURE PRD CITES ONLY IDS THE REAL PRD ALSO DEFINES, so the literals below are correct input to the
# real gate when it reads this file. Its own citation count is fixed and is what the green cases assert:
# two section headings, three requirement bullets, one open question, and one cross-reference in prose.
make_fixture() {
  local dir="$1" prd_kind="$2" notes="$3" extra_kind="$4" bad_closer="${5:-}" opener="${6:-}"
  # Spelled out rather than defaulted inline: a backtick inside ${6:-...} is command substitution,
  # and bash dies on the unterminated one before this file ever runs.
  if [ -z "$opener" ]; then
    opener='```markdown'
  fi
  mkdir -p "$dir/documentation"
  git -c init.defaultBranch=main init -q "$dir"

  # A ZERO-BYTE PRD, distinct from an absent one: the file is there, so the MISSING check passes and the
  # read returns nothing. Graded unreachable in the first draft of the ledger below and reached on the
  # first try -- which is the ledger rule that says to try rather than argue.
  if [ "$prd_kind" = "empty-prd" ]; then
    : > "$dir/documentation/prd.md"
  fi
  if [ "$prd_kind" != "absent" ] && [ "$prd_kind" != "empty-prd" ]; then
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
      # A PRD MAY RETIRE EVERY OPEN QUESTION. The gate does not require any, and the ledger claimed that
      # could not be pinned because it "asserts an absence" -- which is false: a GREEN case pins an absence.
      # Every fixture carried the Q-1 bullet, so adding the guard the row said was unpinnable left all
      # thirty-eight cases green (PR #195 round 6).
      if [ "$prd_kind" != "no-questions" ]; then
        printf '## Open questions\n\n'
        if [ "$prd_kind" != "no-bullets" ]; then
          printf -- '- **Q-1 — A question.** Its text.\n'
        fi
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
      # A fence that CLOSES, with trailing whitespace on the closer — ordinary, legal, and the shape that
      # breaks a naive closer test, since the marker is no longer the whole line. If the fence never closes,
      # the requirement below it is inert and the counts say so; if it never opens, the heading inside it is
      # counted as a section and the counts say that instead. Both directions land on one needle.
      if [ "$prd_kind" = "fenced-closed" ]; then
        printf '\n## Appendix\n\n```markdown\n## R-1 — An inert example heading\n```   \n\n'
        printf -- '- **R-1.3** Defined after the fence closes.\n'
      fi
      # EACH CONSTRUCT INSIDE THE OTHER. The two states have to be mutually exclusive: inside a comment a
      # fence marker is text, and inside a fence `<!--` is text. Checking for a fence first and
      # unconditionally passes every other case here and still gets this wrong — a commented-out block
      # containing a code fence opens one, the fence swallows the `-->`, and the gate dies naming an
      # unterminated comment that is closed three lines up. A confident wrong answer about a file the reader
      # then goes and stares at, which is gh#123's complaint about its own diagnostic.
      # A MARKER LINE THAT MUST NOT CLOSE THE FENCE. This is the half where over-detection INVENTS symbols:
      # a closer accepted too early ends a fenced EXAMPLE, and the rest of the example becomes definitions.
      # Six shapes closed a fence CommonMark keeps open until the PR #195 review measured them — an indent
      # of four or eight columns or a tab, and a marker mixing the two fence characters.
      #
      # TWO inert bullets, not one, and the assertion is on the COUNTS. With one, the two outcomes collide:
      # a wrongly-closed fence would count that bullet and then re-open on the real closer, hiding the
      # requirement below it, and four would come out as four either way.
      # The two inert bullets RE-STATE ids the fixture PRD already defines. They have to: the corpus grep
      # reads inside fences, so a fenced bullet is a citation whatever the definition pass decides, and an
      # id only defined inside the example would dangle in BOTH outcomes and prove nothing about either.
      if [ "$prd_kind" = "fence-bad-closer" ]; then
        # The real closer is the opener's MARKER RUN with any info string stripped off: an info string is
        # legal on an opener and forbidden on a closer, so the opener cannot serve as its own closer.
        # Deriving it keeps a case that varies the opener's LENGTH terminating at the right line.
        local real_closer="${opener//[^\`~]/}"
        printf '\n## Appendix\n\n%s\n%s\n' "$opener" "$bad_closer"
        printf -- '- **R-1.1** Inert: still inside the example.\n'
        printf -- '- **R-1.2** Inert: still inside the example.\n'
        printf '%s\n\n' "$real_closer"
        printf -- '- **R-1.3** Defined after the real closer.\n'
      fi
      # A comment nothing closes. Everything after it is skipped, so the symbol table is short by an unknown
      # amount and no verdict would mean anything -- a named hard failure. NOTHING EXERCISED IT until the
      # PR #195 review deleted the check and watched all nineteen cases stay green: three of the PRD's own
      # requirements then reported dangling, a confident wrong answer at the wrong lines.
      # A `<!--` WRITTEN AS INLINE CODE. This gate deliberately does NOT step over inline spans when it
      # hunts comment delimiters: MISSING one fails open, over-detecting one fails closed, so prose ABOUT
      # the marker opens a comment and the definitions under it go inert. The ledger called that unpinnable
      # because "a case would assert the blind spot, not the rule" -- the same structural move as "asserts
      # an absence", and wrong for the same reason. A case is one fixture, and this one flips the moment a
      # scanner starts stepping over spans (PR #195 round 7, where the mutant ran the real repo green).
      if [ "$prd_kind" = "span-comment" ]; then
        printf '\nProse about the `<!--` marker, which opens one here on purpose.\n\n'
        printf -- '- **R-1.4** Inert: the line above opened a comment.\n\n'
        printf -- '--> and this closes it again.\n'
      fi
      if [ "$prd_kind" = "unterminated-comment" ]; then
        printf '\n<!--\n'
        printf -- '- **R-1.4** Retired, and the comment is never closed.\n'
      fi
      # A LINE THAT OPENS WITH AN INLINE CODE SPAN. A fence needs THREE or more markers; with that minimum
      # dropped to one, this ordinary sentence opens a fence and every definition below it disappears. The
      # PRD is full of prose naming a backticked id, and a wrapped line puts one at column 0.
      if [ "$prd_kind" = "inline-code-line" ]; then
        printf '\n`R-1.1` is the first requirement, referred to in prose.\n\n'
        printf -- '- **R-1.3** Defined after that line.\n'
      fi
      # A FENCE THE DOCUMENT NEVER CLOSES. Legal CommonMark -- it closes at end of document -- so it must
      # be COUNTED, not refused. Nothing held that, and an author who made it a hard failure would redden
      # a correct PRD: every other fence case here closes.
      # THE OPENER'S INDENT CAP, and the shape that proved the old 'an over-detected opener only costs
      # inert lines' argument false. The mechanism is PARITY: an over-indented marker opens a fence, so
      # THE NEXT REAL OPENER IS EATEN AS A CLOSER and the heading below it is read as live text. Two
      # sections if the cap holds, three if it does not -- the heading inside the fence becoming real.
      # A BACKTICK FENCE WHOSE INFO STRING CONTAINS A BACKTICK IS NOT AN OPENER (CommonMark, which gives
      # this gate's own hazard as the reason: otherwise some inline code reads as the start of a fence).
      # Without the rule the first line opens, the second is eaten as its closer, and the heading below is
      # read live -- the same parity fail-open as the indent cap, through a different door.
      if [ "$prd_kind" = "backtick-info" ]; then
        printf '\n## Appendix\n\n```a`b\n'
        printf '```\n'
        printf '## R-2 - inert: CommonMark has this inside a fence that never closes\n'
      fi
      # THE CONTROL, and the reason the rule is written for backticks ALONE: a TILDE fence's info string
      # may contain a backtick, so this IS an opener and the requirement below the closer is real. A rule
      # applied to both characters passes every case above and reddens this one.
      if [ "$prd_kind" = "tilde-info" ]; then
        printf '\n## Appendix\n\n~~~a`b\n'
        printf '## R-2 - inert: this one really is inside a fence\n'
        printf '~~~\n\n'
        printf -- '- **R-1.3** Defined after the tilde fence closes.\n'
      fi
      # A MIS-LEVELLED HEADING MUST NOT DEFINE THE BARE SECTION ID. DEF_SECTION's trailing boundary group is
      # what stops `## R-N.M` from capturing `R-N`, and nothing held it: dropping it passed all 28 cases
      # while a PRD carrying `## R-3.1` and no `## R-3` silently resolved every citation of `R-3`.
      if [ "$prd_kind" = "misleveled-heading" ]; then
        printf '\n'
        printf -- '- **R-3.1** A requirement whose heading below is mis-levelled.\n'
        printf '\n## R-3.1 - a heading that must NOT define the bare section id\n'
      fi
      if [ "$prd_kind" = "opener-overindented" ]; then
        printf '\n## Appendix\n\n    ```\n'
        printf '```\n'
        printf '## R-2 - inert: CommonMark has this inside a fence that never closes\n'
      fi
      if [ "$prd_kind" = "unterminated-fence" ]; then
        printf '\n## Appendix\n\n```markdown\n'
        printf '## R-2 - inert, and this fence is never closed\n'
      fi
      if [ "$prd_kind" = "nested-constructs" ]; then
        printf '\n<!--\n```\n-->\n\n```markdown\n<!-- shown inside a fence, opening nothing -->\n```\n\n'
        printf -- '- **R-1.3** Defined after both.\n'
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
      # THE BINARY CARRIES A DANGLING ID ON PURPOSE. With `grep -I` it is skipped and the count is
      # unchanged; without it the id is found, resolves against nothing, and the run goes red. The blob
      # used to hold no id at all, so dropping `-I` changed nothing and the flag was held by nothing.
      printf 'PK\003\004binary %s99.9 payload\000' "$R" > "$dir/assets/blob.bin"
      ;;
    quoted-path)
      # A PATH GIT ESCAPES FOR A REASON `core.quotepath=false` DOES NOT SUPPRESS. The ledger graded this
      # unreachable because NTFS refuses a quote in a filename -- true, and IRRELEVANT: `git ls-files` reads
      # THE INDEX, not the filesystem, so staging one with `update-index --cacheinfo` needs no file on disk.
      # Fourth `none` row of seven to fall, and the third whose reason was a true fact about another layer.
      local blob
      blob="$(printf 'A note citing %s9.9.\n' "$R" | git -C "$dir" hash-object -w --stdin)"
      git -C "$dir" -c core.protectNTFS=false update-index --add --cacheinfo 100644 "$blob" \
        'documentation/we\"ird.md'
      ;;
    self-hosted)
      # THE GATE, COPIED IN, so it can be run with NO ARGUMENT the way ci.yml runs it. `scripts/` is ignored
      # so the copy is not part of the corpus -- the ids in its own header resolve against the REAL PRD.
      mkdir -p "$dir/scripts"
      cp "$GATE" "$dir/scripts/check-requirement-ids.sh"
      printf 'scripts/\n' > "$dir/.gitignore"
      ;;
    committed)
      # EVERYTHING COMMITTED EXCEPT ONE FILE. `--others` is what puts an uncommitted file in the corpus, and
      # until this case it was pinned only by ACCIDENT: every other fixture is an uncommitted `git init`
      # tree, so dropping the flag emptied the sweep and reddened twenty-odd cases at once. Heavy pinning
      # that is owed to a fixture's incidental SHAPE rather than its intent is coverage the ledger cannot
      # see it lacks -- the legend's own warning, found in this table by the same method (PR #195 round 5).
      git -C "$dir" -c core.autocrlf=false add documentation/prd.md documentation/notes.md
      git -C "$dir" -c user.name=fixture -c user.email=fixture@example.invalid -c commit.gpgsign=false         commit -q -m 'fixture baseline'
      printf 'A note that is written but not yet committed, citing %s9.9.
' "$R" > "$dir/documentation/new.md"
      ;;
    dash-file)
      # A ROOT-LEVEL FILE WHOSE NAME BEGINS WITH A DASH, which is the only thing the `--` separator before
      # the file list is for. Without it grep reads the name as an option -- measured: `invalid argument
      # 'ash.md' for '--directories'`, exit 2, so the run dies as UNREADABLE instead of naming the id.
      printf 'A note citing %s9.9 from a file whose name looks like an option.
' "$R" > "$dir/-dash.md"
      ;;
    single-file)
      # A CORPUS OF EXACTLY ONE FILE, which is the only shape in which `grep -H` matters: grep prints the
      # filename unasked once it has two or more files, so every other case here would pass without it.
      # `.git/info/exclude` rather than `.gitignore`, because a `.gitignore` would itself be a second file.
      printf '*
' > "$dir/.git/info/exclude"
      git -C "$dir" -c core.autocrlf=false add -f documentation/prd.md
      ;;
    all-ignored)
      # EVERY file ignored, so the corpus is empty while the PRD is still on disk and still parses.
      # Also graded unreachable at first, also reached on the first try.
      printf '*
' > "$dir/.gitignore"
      ;;
    ignore-prd)
      # The PRD exists and parses, but nothing in the corpus can see it. Definitions load, the search finds
      # nothing, and without the NO CITATIONS guard the gate would report a clean tree having resolved zero
      # symbols — the exact vacuous green every dead guard in this repository printed.
      printf 'documentation/prd.md\n' > "$dir/.gitignore"
      ;;
    non-ascii)
      # A FILENAME WITH A BYTE ABOVE 0x7F. `core.quotepath` DEFAULTS TO TRUE, and under it git returns
      # `"documentation/caf\303\251.md"` — a name that is not the name on disk. The gate sets the option
      # false so the path arrives raw; without it this file is refused as an UNQUOTABLE PATH and a correct
      # tree goes red. Nothing held that option until the PR #195 review deleted it and all nineteen cases
      # stayed green.
      printf 'A note citing R-1.1, under a name git would escape.\n' \
        > "$dir/documentation/caf$(printf '\303\251').md"
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
# Set around a single call when a case needs the gate to run with one variable set. Spelled as two globals
# rather than a prefix string so nothing has to be re-split or re-quoted at the call.
GATE_ENV_VAR=""
GATE_ENV_VAL=""
# When set, the gate is invoked BY THIS PATH WITH NO ARGUMENT -- the only way ci.yml ever invokes it, and
# the one nothing here exercised: every case passes an explicit root, so the default REPO_ROOT derivation
# could be broken with all thirty-eight green while the real invocation died (PR #195 round 6).
GATE_SELF_HOSTED=""
# THE GATE'S STDERR IS CAPTURED SEPARATELY RATHER THAN MERGED WITH `2>&1` (gh#239), so expect_green can
# assert it is EMPTY. Merged, output that should not be there at all is indistinguishable from output that
# should: a stray line printed before every invocation left all forty-two cases green -- see the header.
# It lives under $FIXTURES so the EXIT trap already removes it, and directly under it rather than inside any
# fixture, since every root the gate is pointed at is $FIXTURES/<name> and it must not read this file.
GATE_STDERR="$FIXTURES/.gate-stderr"
run_gate() {
  local cmd
  if [ -n "$GATE_SELF_HOSTED" ]; then
    cmd=(bash "$GATE_SELF_HOSTED")
  else
    cmd=(bash "$GATE" "$1")
  fi
  # Truncated per call, not appended: each case asserts what ITS OWN run wrote, and a leftover byte from the
  # previous case would redden the next one and name the wrong culprit.
  : > "$GATE_STDERR"
  if [ -n "$GATE_ENV_VAR" ]; then
    env "$GATE_ENV_VAR=$GATE_ENV_VAL" "${cmd[@]}" 2>"$GATE_STDERR"
  else
    "${cmd[@]}" 2>"$GATE_STDERR"
  fi
}

# Both streams as one string, for the callers that must read the gate's own words wherever it chose to put
# them -- `die` writes to stderr, `ok` to stdout. Ordering between the two is lost, which costs nothing: every
# needle in this file is a substring of a single line.
gate_output() { printf '%s\n%s' "$1" "$(cat "$GATE_STDERR")"; }

# THE RED CASES DELIBERATELY DO NOT TAKE expect_green's EMPTY-STDERR ASSERTION, AND THIS IS THE ONLY PLACE
# THAT SAYS WHY (gh#239). The gate reports every one of these faults through `die`, which writes to stderr --
# so on a red case stderr is not stray output, it is THE ANSWER, and the needles below are matched against it.
# "Stderr must be empty" is therefore a claim about a GREEN run only: the gate resolved everything and had
# nothing to say. The asymmetry between the two helpers is the finding, not an oversight in one of them; do
# not tidy it away by pushing the assertion up into run_gate, which would redden all twenty-three cases here.
expect_red() {
  local label="$1" dir="$2"; shift 2
  local out status=0 needle
  cases=$(( cases + 1 ))

  out="$(run_gate "$dir")" || status=$?
  out="$(gate_output "$out")"

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

# Runs the REAL gate against a CORRECT fixture and requires it to ACCEPT it — to say what it resolved, and to
# say NOTHING ELSE ANYWHERE. The third assertion is the one gh#239 added, and it is the only one here that
# looks at output the case did not ask for.
expect_green() {
  local label="$1" dir="$2" needle="$3" out err status=0 stray
  cases=$(( cases + 1 ))

  out="$(run_gate "$dir")" || status=$?
  err="$(cat "$GATE_STDERR")"

  if [ "$status" -ne 0 ]; then
    red "SELF-TEST FAILED  $label"
    red "  The gate REJECTED correct input (exit $status). It would fail correct pull requests, and the"
    red "  first person it wrongly stops will delete it."
    # Both streams: the gate says WHY it rejected through `die`, i.e. on stderr, which $out no longer carries.
    info "$(gate_output "$out")"
    failures=$(( failures + 1 ))
    return
  fi
  if [[ "$out" != *"$needle"* ]]; then
    red "SELF-TEST FAILED  $label"
    red "  The gate passed without saying '$needle'. Exit 0 having resolved NOTHING — or having quietly"
    red "  resolved MORE than the fixture contains — is the shape of every dead guard in this repository."
    info "$(gate_output "$out")"
    failures=$(( failures + 1 ))
    return
  fi
  # A GREEN RUN WRITES EXACTLY ZERO BYTES TO STDERR -- measured on all nineteen green fixtures, not assumed
  # from the two the card measured. See the header for the numbers and for why only the stderr half is
  # asserted.
  #
  # Byte count rather than `[ -n ]`: a lone newline is stray output too, and the number is what an author
  # needs to see. `wc -c` pads on some platforms, hence the strip.
  stray="$(wc -c < "$GATE_STDERR" | tr -d '[:space:]')"
  if [ "$stray" -ne 0 ]; then
    red "SELF-TEST FAILED  $label"
    red "  The gate went green, said '$needle', and still wrote $stray bytes to STDERR. Exit status and"
    red "  needles both survive a gate that is ALSO doing something else — a stray line above its own"
    red "  \`set -euo pipefail\` is non-fatal, changes no exit code and matches every needle (gh#239)."
    info "$err"
    failures=$(( failures + 1 ))
    return
  fi
  ok "  green as required  $label  ($needle; stderr empty)"
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

# 14. A FENCE THAT CLOSES, with trailing whitespace on the closer, and a requirement defined after it. The
#     two red cases above prove a fence SUPPRESSES definitions; this proves it stops doing so at the right
#     line, which they cannot — a fence that never closes satisfies both of them just as well. Two sections
#     and four requirements: three if the closer is missed, three sections if the opener is.
make_fixture "$FIXTURES/fenced-closed" fenced-closed 'Governed by `R-1.3`.' none
expect_green "a fence closed with trailing whitespace" "$FIXTURES/fenced-closed" \
  "(2 sections, 4 requirements, 1 open questions"

# 15. EACH CONSTRUCT INSIDE THE OTHER, which is where the two states stop being independent. A fence checked
#     for first and unconditionally passes every case above and still dies here, on an `UNTERMINATED COMMENT`
#     about a comment that closed three lines earlier. Same counts as the case above, and for the same
#     reason: if either construct leaks, the requirement below both of them stops being defined.
make_fixture "$FIXTURES/nested-constructs" nested-constructs 'Governed by `R-1.3`.' none
expect_green "a fence inside a comment, and a comment inside a fence" "$FIXTURES/nested-constructs" \
  "(2 sections, 4 requirements, 1 open questions"

# 16-18. THE CLOSER'S THREE CONDITIONS, one case each, because the closer is the half where over-detection
#     invents symbols rather than skipping lines. Every one of these marker lines is CONTENT to CommonMark
#     and closed the fence until the PR #195 review: an over-indented marker, a marker behind a tab, and a
#     marker mixing the two fence characters. Same counts as case 14 and for the same reason — if the marker
#     closes, the two bullets inside the example become definitions and the requirement below is hidden.
make_fixture "$FIXTURES/closer-indent" fence-bad-closer 'Governed by `R-1.3`.' none '    ```'
expect_green "a closing marker indented four columns" "$FIXTURES/closer-indent" \
  "(2 sections, 4 requirements, 1 open questions"

make_fixture "$FIXTURES/closer-tab" fence-bad-closer 'Governed by `R-1.3`.' none "$(printf '\t```')"
expect_green "a closing marker behind a tab" "$FIXTURES/closer-tab" \
  "(2 sections, 4 requirements, 1 open questions"

#     The third is the guard for the fail-open half specifically: with it dropped, an info string or a
#     mixed run closes the fence, and NOTHING ELSE in this file would notice. The review dropped that one
#     condition and all nineteen cases stayed green.
make_fixture "$FIXTURES/closer-mixed" fence-bad-closer 'Governed by `R-1.3`.' none '```~'
expect_green "a closing marker mixing fence characters" "$FIXTURES/closer-mixed" \
  "(2 sections, 4 requirements, 1 open questions"

# 19. A COMMENT NOTHING CLOSES. A named hard failure the header advertises, and nothing exercised it: the
#     review replaced the check with `if false` and every case here stayed green, while three of the
#     fixture PRD's own requirements reported dangling at lines their author had not touched.
make_fixture "$FIXTURES/unterminated" unterminated-comment 'Governed by `R-1.1`.' none
expect_red "an HTML comment the PRD never closes" "$FIXTURES/unterminated" "UNTERMINATED COMMENT"

# 20. A NON-ASCII FILENAME, which git escapes under its own default. The gate turns that default off; with
#     the option removed the file is refused by name and a correct tree goes red. Also unheld until review.
make_fixture "$FIXTURES/non-ascii" sound "$SOUND_NOTES" non-ascii
expect_green "a filename git would escape" "$FIXTURES/non-ascii" \
  "12 citations of 6 distinct ids"

# 21. THE CLOSER'S LENGTH RULE, which is rule 3 of the three this gate's own comment enumerates. Rules 1 and
#     2 got a case each last round; this one had none, and dropping it went green on all twenty-four
#     (PR #195 round 3). A marker SHORTER than the opener is content, so the example runs on.
make_fixture "$FIXTURES/closer-short" fence-bad-closer 'Governed by `R-1.3`.' none '```' '`````'
expect_green "a closing marker shorter than its opener" "$FIXTURES/closer-short"   "(2 sections, 4 requirements, 1 open questions"

# 22. THE OPENER'S MINIMUM LENGTH, the other escape. A fence needs three or more markers; at one, an
#     ordinary sentence beginning with a backticked id opens a fence and every definition below it is lost.
#     This PRD is full of such prose, and a wrapped line puts one at column 0 — so the measured cost of that
#     mutation is dozens of correct citations reddening on the real tree.
make_fixture "$FIXTURES/inline-code" inline-code-line 'Governed by `R-1.3`.' none
expect_green "prose opening with an inline code span" "$FIXTURES/inline-code"   "(2 sections, 4 requirements, 1 open questions"

# 23. A FENCE THE DOCUMENT NEVER CLOSES — legal CommonMark, which closes it at end of document. It must be
#     COUNTED, not refused: every other fence case here closes, so nothing would have noticed an author
#     turning this into a hard failure and reddening a correct PRD. The inert count is asserted with the
#     definition counts, because that is what says the fence opened at all.
make_fixture "$FIXTURES/unterminated-fence" unterminated-fence 'Governed by `R-1.1`.' none
expect_green "a fence the document never closes" "$FIXTURES/unterminated-fence"   "(2 sections, 3 requirements, 1 open questions; 2 of its lines inert"

# 24. THE OPENER'S INDENT CAP -- the rule whose absence made the header's own direction argument false
#     (PR #195 round 3). An over-indented marker opened a fence, so the next REAL opener was eaten as a
#     closer and the heading below it became a definition: `every one resolves`, exit 0, over an id
#     CommonMark has inside a fence that never closes. Two sections if the cap holds, three if it does not.
make_fixture "$FIXTURES/opener-overindented" opener-overindented 'Governed by `R-1.1`.' none
expect_green "an over-indented opener, which must not open" "$FIXTURES/opener-overindented"   "(2 sections, 3 requirements, 1 open questions"

# 25. THE OPENER'S OTHER COMMONMARK RESTRICTION: a BACKTICK fence's info string may not contain a backtick.
#     The spec gives this gate's own hazard as the reason -- otherwise some inline code reads as the start of
#     a fenced block -- and without it the parity fail-open is back through a different door. Two sections if
#     the rule holds, three if the heading below escapes the fence (PR #195 round 4).
make_fixture "$FIXTURES/backtick-info" backtick-info 'Governed by `R-1.1`.' none
expect_green "a backtick fence whose info string has a backtick" "$FIXTURES/backtick-info"   "(2 sections, 3 requirements, 1 open questions"

# 26. THE CONTROL FOR IT, and the reason the rule names backticks alone: a TILDE fence's info string MAY
#     contain one, so that line really is an opener. A rule applied to both characters passes case 25 and
#     reddens this -- which is what makes 25 evidence about the spec rather than about a string match.
make_fixture "$FIXTURES/tilde-info" tilde-info 'Governed by `R-1.3`.' none
expect_green "a tilde fence whose info string has a backtick" "$FIXTURES/tilde-info"   "(2 sections, 4 requirements, 1 open questions"

# 27. DEF_SECTION'S RIGHT BOUNDARY -- the trailing group that stops `## R-N.M` from defining bare `R-N`.
#     Dropping it passed all twenty-eight cases (PR #195 round 4), and a mis-levelled heading then invents a
#     section id: the fail-open direction, on a rule the gate's own header enumerates.
make_fixture "$FIXTURES/misleveled" misleveled-heading 'Governed by `R-3`.' none
expect_red "a mis-levelled heading defining a bare section id" "$FIXTURES/misleveled"   "DANGLING" "documentation/notes.md:1" "R-3"

# 28-29. TWO ROWS THE LEDGER BELOW FIRST GRADED UNREACHABLE, AND BOTH WERE REACHED ON THE FIRST TRY. That is
#     #193's rule applied rather than quoted: "unreachable, and here is why" is the weakest row in any such
#     table, because nothing executes it. Re-derive every one by TRYING to reach it.
make_fixture "$FIXTURES/empty-prd" empty-prd 'A note citing `R-1.1`.' none
expect_red "a PRD that exists and is empty" "$FIXTURES/empty-prd" "EMPTY"

make_fixture "$FIXTURES/all-ignored" sound 'A note citing `R-1.1`.' all-ignored
expect_red "a corpus in which every file is ignored" "$FIXTURES/all-ignored" "NOTHING TO CHECK"

# 30-34. FIVE DECISIONS THE LEDGER HAD NO ROW FOR, or a row pinned by accident. Every one was found by
#     auditing the table against the script rather than reading it (PR #195 round 5), which is the audit
#     #193's ledger entry says to run and which I did not run on my own.

#  30. `--others`, pinned for its OWN reason. Everything committed except one file: with the flag that file
#      is in the corpus and its dangling id is named, without it the run is clean. Until this case the flag
#      was "pinned" by twenty-odd cases that all happen to be uncommitted trees -- the legend's own
#      incidental-shape warning, in the legend's own table.
make_fixture "$FIXTURES/uncommitted" sound 'This note cites nothing.' committed
expect_red "a written-but-uncommitted file" "$FIXTURES/uncommitted"   "DANGLING" "documentation/new.md:1" "$DANGLING_REQUIREMENT_99"

#  31. `CANNOT LIST`, which this ledger graded unreachable on the reported grounds that a stray `C:\.git`
#      makes the whole drive a work tree. That is true and it is NOT the reason: `GIT_CEILING_DIRECTORIES`
#      stops the upward discovery, so `git ls-files` fails with 128 here exactly as it would on the runner.
#      Portable, and the third `none` row of six to turn out reachable.
make_fixture "$FIXTURES/no-repo" sound 'A note citing `R-1.1`.' none
rm -rf "$FIXTURES/no-repo/.git"
GATE_ENV_VAR="GIT_CEILING_DIRECTORIES"
GATE_ENV_VAL="$FIXTURES"
expect_red "a root that is not a git repository" "$FIXTURES/no-repo" "CANNOT LIST"
GATE_ENV_VAR=""
GATE_ENV_VAL=""

#  32. `NO SUCH ROOT` -- its own `die`, its own `exit 1`, reachable in one command, and it had neither a row
#      nor a case. The cheapest possible fixture is the one nobody wrote.
expect_red "a root that does not exist" "$FIXTURES/was-never-created" "NO SUCH ROOT"

#  33. The `--` separator before the file list. Only a ROOT-level name beginning with a dash reaches it: grep
#      reads the name as an option and dies with `invalid argument 'ash.md' for '--directories'`, so the run
#      reports UNREADABLE instead of the id. The needle is the ID, which is what tells the two apart.
make_fixture "$FIXTURES/dash-file" sound 'This note cites nothing.' dash-file
expect_red "a root file whose name begins with a dash" "$FIXTURES/dash-file"   "DANGLING" "-dash.md:1" "$DANGLING_REQUIREMENT_99"

#  34. `grep -H`. Grep prints the filename unasked once it has two files, so every other case here passes
#      without the flag -- measured, not assumed. A corpus of exactly ONE file is the only shape that needs
#      it, and without it the location becomes `<line>:<line>` and the file disappears from the report.
make_fixture "$FIXTURES/single-file" shadowed 'This note cites nothing.' single-file
expect_red "a corpus of exactly one file" "$FIXTURES/single-file"   "DANGLING" "documentation/prd.md:" "$DANGLING_REQUIREMENT"

# 35-37. THREE MORE, and the pattern in them is the finding rather than the rules (PR #195 round 6).

#  35. UNQUOTABLE PATH. Graded `none` because NTFS refuses a quote in a filename -- TRUE, AND IRRELEVANT:
#      `git ls-files` reads THE INDEX, not the filesystem. `update-index --cacheinfo` stages one with no
#      file on disk, and git quotes it even under `core.quotepath=false`. That is the FOURTH of seven
#      `none` rows to fall and the THIRD whose stated reason was a true fact about a different layer, so
#      the question to ask a surviving row is not "is this hard to build" but **"is my reason about the
#      thing the code actually consults?"**
make_fixture "$FIXTURES/quoted-path" sound 'A note citing `R-1.1`.' quoted-path
expect_red "a path git escapes for a reason quotepath does not suppress" "$FIXTURES/quoted-path"   "UNQUOTABLE PATH"

#  36. A PRD THAT RETIRES EVERY OPEN QUESTION. The row said this could not be pinned because it "asserts an
#      absence" -- and a GREEN case pins an absence perfectly well. Adding the guard the row said was
#      unpinnable left all thirty-eight green, because every fixture carried the Q-1 bullet.
make_fixture "$FIXTURES/no-questions" no-questions 'A note citing `R-1.1`.' none
expect_green "a PRD with no open questions at all" "$FIXTURES/no-questions"   "(2 sections, 3 requirements, 0 open questions"

# 38. THE INLINE-CODE-SPAN DECISION, which the ledger called unpinnable because "a case would assert the
#     blind spot, not the rule". That is the same structural move as "asserts an absence" and wrong the same
#     way: a case is one fixture, and this one flips the moment the scanner steps over spans. The round-7
#     reviewer built exactly that mutant -- fork-free, demonstrably sane, running the real repository green
#     with the same 233 citations -- and ZERO of the forty-one cases noticed.
#
#     The requirement below the inline `<!--` is INERT, so its citation dangles. Step over the span and it
#     becomes a real definition, the citation resolves, and this case goes green.
make_fixture "$FIXTURES/span-comment" span-comment 'Governed by `R-1.4`.' none
expect_red "a comment marker written as inline code" "$FIXTURES/span-comment"   "DANGLING" "documentation/notes.md:1" "R-1.4"

#  37. THE DEFAULT `REPO_ROOT` -- no row, no case, and the ONLY invocation ci.yml uses. Every other case
#      passes an explicit root, so the derivation could be broken with all thirty-eight green while the
#      gate, run the way CI runs it, died on NO SUCH ROOT. Not a merge-gate hole, because CI fails loudly;
#      it is the self-test reporting all clear on the one invocation it never exercised.
make_fixture "$FIXTURES/self-hosted" sound "$SOUND_NOTES" self-hosted
GATE_SELF_HOSTED="$FIXTURES/self-hosted/scripts/check-requirement-ids.sh"
expect_green "the gate invoked with no argument, as ci.yml invokes it" "$FIXTURES/self-hosted"   "11 citations of 6 distinct ids"
GATE_SELF_HOSTED=""

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
