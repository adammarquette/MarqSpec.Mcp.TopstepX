#!/usr/bin/env bash
# check-doc-sizes-selftest.sh — require that check-doc-sizes.sh can still go red.
#
#   scripts/check-doc-sizes-selftest.sh
#
# THE GATE IT GUARDS HAS TO BE ABLE TO FAIL (gh#160, the rule gh#98 wrote down and gh#108 applied). This
# repository has shipped five guards that could not fire — gh#43 (green on an unpaced loop), gh#98 (`|| true`
# resetting `PIPESTATUS`), gh#114 and gh#126 (swallowed reads reporting "nothing found"), gh#164 — and
# `check-doc-sizes.sh` has the same shape as all of them: a shell script that decides a verdict by parsing
# text it does not own. One renamed heading, one extra column, one `|| true` left after a debugging session,
# and it prices zero rows and prints a green line saying so.
#
# So the REAL gate is run against fixtures whose faults are known, on every CI run, and is required to reject
# each one — AND against a sound fixture it is required to ACCEPT. Two of the faults are the SAME drift
# written the two other ways GFM permits, because that is how this gate's coverage was found to shrink:
# in review (PR #175), not in the fault it was written for. Rejections alone would all be satisfied by
# `exit 1`, i.e. by a gate that says no to everything, which is exactly as useless as one that says yes.
#
# NON-ZERO EXIT IS NOT SUFFICIENT AND IS NOT WHAT THIS ASSERTS. `check-doc-sizes.sh` also exits 1 for "no such
# root" and for a priced file that is not on disk, so a self-test satisfied by status alone would go green on a
# runner where the fixtures never got written — reporting the gate sound at precisely the moment nothing had
# been checked. Each case below matches the words that name ITS OWN fault.
#
# AND THE ACCEPTANCE IS NOT SATISFIED BY EXIT 0 EITHER. The sound case additionally requires the gate to say
# how many rows it priced, because "green having measured nothing" is the exact failure every guard above had.
#
# AND NEITHER OF THOSE CAN SEE OUTPUT THAT SHOULD NOT BE THERE AT ALL (gh#271, extending gh#239). Exit status
# and named substrings are both assertions about output that IS there; a stray non-fatal line above the gate's
# own `set -euo pipefail` matches no needle, and matching no needle is precisely what a needle cannot detect.
# So expect_green makes the one assertion that does: it SPLITS the gate's streams instead of merging them with
# `2>&1`, and requires a green run to write ZERO BYTES to stderr.
#
# MEASURED BEFORE IT WAS ASSERTED, on `1d558eb`, and per gate rather than by analogy with gh#239's measurement
# of check-requirement-ids.sh:
#
#     the real repository (no argument)  1331 B stdout   0 B stderr   exit 0
#     all 18 green fixtures below         604-605 B      0 B stderr   exit 0
#     all 29 red fixtures below           0-390 B        194-2223 B   exit 1
#
# The stdout figures are a RANGE and are not asserted -- they track the digits in the counts the gate prints.
# The stderr half IS asserted, on every green case, so a passing run has just re-measured all eighteen and a
# nineteenth green case extends the measurement by construction rather than quietly escaping it.
#
# AND THE MEASUREMENT AGREES WITH THE CODE, which is what makes it safe to assert on a runner nobody measured
# on. `grep -n '>&2' scripts/check-doc-sizes.sh` finds exactly three writers -- `die`, `explain`, and one bare
# `echo >&2`.
#
# THAT GREP ONLY SEES THIS SCRIPT'S OWN REDIRECTIONS, so it has to be paired with a reading of what the gate
# SPAWNS -- a child's stderr is not this repository's to promise, and gh#271 declined the assertion on
# check-image-entrypoint.sh for exactly that reason (see its self-test's header: a green `docker run` there
# writes 152 B of platform WARNING). This gate spawns THREE external commands in total, and no others:
# `dirname`, once, inside the no-argument REPO_ROOT derivation; `wc -c < "$resolved"`, once per priced row;
# and `cat`, inside `explain`, which is a red path. All three are silent when they succeed. Everything else
# the gate runs is a shell builtin. Re-derive that list if the gate grows a command; it is the half the grep
# cannot show you.
#
# THE ONE OF THE THREE THAT CAN WRITE TO STDERR STILL LEAVES THE RUN RED, and this was RUN rather than
# reasoned, because the obvious reasoning is wrong. `dirname` sits inside
# `REPO_ROOT="${1:-$(cd "$(dirname ...)/.." && pwd)}"`, and a bare assignment carries the LAST substitution's
# status -- but the last one is the OUTER `$(cd ... && pwd)`, whose `pwd` succeeds regardless. Measured:
# with that `dirname` replaced by a name that does not exist, the assignment SURVIVES under `set -euo
# pipefail`, prints 62 B of `command not found` to stderr -- path-dependent for the same reason as the 84 B
# BELOW, bash putting `$0` in its own error line -- and yields `REPO_ROOT=/`, because an empty substitution
# makes the path `"/.."`, which `cd` resolves to `/`.
# The run is not silently green, though:
# rooted at `/`, the gate exits **1** with 0 B stdout and 285 B stderr, `NO SUCH FILE
# documentation/README.md`. So the stderr write and the non-zero exit arrive together, which is what the
# assertion needs. An earlier draft of this paragraph claimed `set -e` aborted the assignment; it does not.
#
# THE SHAPE IS NOT check-requirement-ids.sh's, either, and the difference is the load-bearing part: there
# every writer sits on a path ending `exit 1`, whereas `die` HERE does not exit. Most call sites report a row
# and then `failures=$(( failures + 1 ))`, carrying on so an author sees every bad row at once. What closes
# it is the pair of them: every one of the fifty-three `die` calls reaches either an immediate `exit 1` or
# that counter, and `failures > 0 -> exit 1` at the bottom turns it into a non-zero exit. "Green implies
# silent" is therefore structural here TOO, but it rests on that final check as well as on the writers'
# placement -- re-derive BOTH halves if either stops being true.
#
# PROVEN ABLE TO FAIL, by putting gh#239's own defect into this gate (a bare word above its `set -euo
# pipefail`, verified with `cmp` to differ from the shipped file before it was scored). It is non-fatal, so
# the exit status is unchanged and EVERY BYTE OF STDOUT is unchanged with it -- 1331 B, byte-identical to the
# shipped gate's on the real tree, with 84 B of `command not found` on stderr. That 84 is PATH-DEPENDENT and
# NOTHING ASSERTS IT: bash puts `$0` in its own error line, so the same mutant reports 83, 69, 85 or 88 B
# depending on where the copy was run from -- five values across three people measuring the one mutant, none
# of which changed a verdict. What `expect_green` reads is the byte COUNT being non-zero, and all five
# reddened all eighteen. Against that mutant:
#
#     this suite BEFORE gh#271   47 of 47 cases green    the blindness, reproduced
#     this suite AFTER  gh#271   18 green cases RED, 29 red cases still green
#
# THE RED CASES ARE EXEMPT, DELIBERATELY: the gate reports their faults through `die`, which writes to stderr,
# so there stderr is the answer rather than stray output. The reason is recorded again beside expect_red,
# because the asymmetry looks like an oversight and the fix for a supposed oversight is deleting the
# assertion.
#
# ONE HAZARD THIS ASSERTION MAKES VISIBLE, recorded so a red run is not misdiagnosed: editing
# check-doc-sizes.sh WHILE this suite is running can hand bash a torn read of it, which prints a
# `command not found` naming a fragment of the line being rewritten. Before gh#271 that ran green; now the
# green cases go red naming stray bytes. The run really was untrustworthy — re-run it, do not chase the line
# number.
#
# TWO RUNS, NOT ONE (the Coding contract, Tests). This file is the first: red on the faults the gate exists to
# catch. The second is `ci.yml`'s `docs` job running the gate against this repository's REAL priced tables —
# the most awkward correct input there is, with fourteen rows across three tables in two files, targets that
# climb two directories out of the file that names them, prose full of em-dashes, and several sections in
# both files carrying no table at all.
#
# THE DECISION LEDGER (PR #193 rounds 6-7). Seven review rounds each found something the round before it
# had not thought to look for, and the diagnosis was structural rather than about any one rule: **the
# suite pinned what a reviewer pointed at, not what the script decides.** So every decision
# check-doc-sizes.sh makes is listed here beside the case that kills it, and a decision with no case
# carries the reason it needs none. **Adding a decision to that script without adding a row here is the
# same omission this ledger exists to catch.**
#
# EVIDENCE:  mut  = individually mutated, and exactly the cases named here failed.
#            rev  = same, measured by the reviewer rather than here (PR #193 round 5).
#            case = exercised by the named case, but not separately mutated. Weaker, and marked so.
#
# TWO WAYS THIS TABLE LIES. Both happened in round 6, and both were caught by AUDITING it against the
# script rather than by re-reading it:
#
#   A GRADE CAN CLAIM MORE THAN ITS EVIDENCE. FENCE_QUOTED was marked rev against one named case;
#   re-measured, six fail. The decision was pinned; the row was not true. A marker that promises
#   "exactly these cases" has to be re-run before it is written down.
#
#   A DECISION CAN BE PINNED BY ACCIDENT. The .github fixture sat at depth 1, where ** degrading to *
#   also misses it -- so removing globstar reddened that case and this table read as though globstar
#   were covered. It was not: that case was written for the ROOT. A decision pinned by the incidental
#   SHAPE of a fixture rather than by its intent is coverage this table cannot see it lacks. The
#   fixture now sits at depth 2 and globstar has a case of its own. One case, one decision.
#
#   THE FOUR gh#196 ROWS CARRY `mut` BECAUSE THEY WERE RE-RUN, not because the change looked well covered.
#   Each mutant is one substituted line, verified to differ from the shipped gate first -- a sed that fails
#   to match produces a byte-identical "mutant" that reports as equivalent, which is a false all-clear:
#
#     verdict back to `dev_pct > 25`      -> onestep, finer, compound1, compound3 fail
#     `cell_tok != suggested` (strings)   -> bareK fails
#     `ROUNDED=$1` (no rounding)          -> rounds fails
#     precision branch removed            -> finer fails
#
#   No new case is redundant and no new decision is unpinned. Note that `finer` dies under two different
#   mutants, on its VERDICT under the first and on its NEEDLE under the last -- which is why its needle is
#   the precision wording rather than `OUT OF DATE`.
#
#   DECISION in check-doc-sizes.sh               PINNED BY (case label)                             EV
#   -------------------------------------------  -------------------------------------------------  ----
#   PRICED pair 1 (README / Start here)          a drifted ~tok value                               case
#   PRICED pair 2 (README / Working agreements)  a renamed section heading                          case
#   PRICED pair 3 (agents / The contracts)       a drifted row in the second priced file            case
#   ROUNDING_STEP = 100 (the 0.1K grid)          off-grid green; one 0.1K step off red              case
#   BYTES_PER_TOKEN = 4                          every sized fixture (sizes exact at 4)             case
#   SIZE_CLAIM vocabulary                        size claim; quickest; no-longer green              case
#   ALIGNMENT_ROW regex                          a sound pair of priced files                       case
#   SIZE_CELL regex                              a placeholder instead of a size                    case
#   stated_tokens fraction handling              1K and 1.0K are the same price; 1.05K refused      case
#   FILES de-duplication                         a sound pair (two pairs, one file)                 case
#   NO SUCH FILE (priced file absent)            a priced file that is not on disk                  case
#   EMPTY (priced file has no lines)             a priced file that exists and has no lines         mut
#   per-file heading list (pair keying)          a renamed heading in the second priced file        mut
#   heading match on the normalised line         a renamed section heading                          case
#   UNLISTED TABLE (unlisted heading)            a price table under an unlisted heading            case
#   blank line closes a table                    a priced section with no rows                      case
#   alignment row must contain a PIPE            a thematic break under a priced heading            mut
#   NO LINK                                      a row with no link in its first cell               case
#   #fragment stripped from the target           a row whose link carries a #fragment               mut
#   resolved against the PRICED file dir         a sound pair (epsilon shadowed one level up)       mut
#   NOT A SIZE                                   a placeholder instead of a size                    case
#   MISSING (target absent)                      a row whose target is absent                       case
#   measured <= 0 (zero-byte target)             a row pointing at a zero-byte document             mut
#   as_k ROUNDED (the verdict's arithmetic)      a measurement off the 0.1K grid                    mut
#   stated != ROUNDED  ->  OUT OF DATE           one step off; finer; both compounding growths      mut
#   dev_pct == 0  ->  precision wording          a cell finer than the column has (needle IS it)    mut
#   SIZE CLAIM in row prose                      prose making a size claim; no-longer green         case
#   NO SECTION / NO ROWS, per pair               renamed heading; priced section with no rows       case
#   shopt globstar                               a price table three directories down               mut
#   shopt nullglob                               18 cases (every fixture with no .github dir)       mut
#   sweep root  **/*.md                          a price table in a file the gate does not read     case
#   sweep root  .github/**/*.md                  a price table under .github/                       mut
#   sweep skips priced files                     a sound pair (else they self-report)               case
#   UNLISTED FILE (unswept-for-pricing)          a price table in a file the gate does not read     case
#   fenced content skipped (both loops)          twelve fence cases                                 mut
#   fence opener  ```                            every fenced case                                  case
#   fence opener  ~~~                            a ~~~-fenced price table is still fenced           mut
#   closer character must match opener           a ~~~ line inside a backtick fence closes nothing  mut
#   closer at least as long as opener            a shorter fence run closes nothing                 mut
#   closer carries nothing but fence chars       a fence run with an info string closes nothing     mut
#   FENCE_QUOTED recorded at open                six quoted cases                                   mut
#   normalize_line leading/trailing trim         a drifted row indented under the table             case
#   normalize_line  >  strip                     six quoted cases                                   mut
#   QUOTED reset per line                        quoted cases (sticky QUOTED breaks two)            rev
#   close_fence_if_quote_ended                   a real price table below a quoted fence            mut
#   per-file fence_reset (sweep)                 a real table in the file AFTER one mid-fence       mut
#   UNTERMINATED FENCE report (both loops)       a fence left open in a priced / swept file         mut
#   quoted-fence-at-EOF exemption (both)         a quoted fence still open at end of file           mut
#   NOTHING CHECKED (rows_checked == 0)          every priced table empty                           mut
#   failures > 0  ->  exit 1                     every red case                                     case
#   green line names rows / pairs / files        every green case (needle N priced rows)            case
#
# THE OUTPUT STREAM ITSELF — not a decision the gate MAKES, which is why it had no row until gh#271.
#
#   DECISION in check-doc-sizes.sh               PINNED BY (case label)                             EV
#   -------------------------------------------  -------------------------------------------------  ----
#   a green run says NOTHING on stderr           EVERY green case (18 of them)                      mut
#
#   The mutant is gh#239's own defect put into this gate: a bare word above its `set -euo pipefail`,
#   `cmp`-verified to differ from the shipped file before it was scored. Non-fatal, so the exit status is
#   unchanged and every byte of stdout is unchanged with it — which is why status and needles both miss it.
#   Scored twice: 47 of 47 green on the suite as it stood BEFORE this row existed, and 18 green cases red /
#   29 red cases still green after. A `mut` row names the cases that die, so growing the suite without
#   re-running it makes the grade a lie; the count is written here for exactly that reason.
#
# NO CASE, AND WHY. Every one RE-DERIVED by trying to reach it (round 7), after two of the original
# seven turned out to be reachable -- EMPTY and NOTHING CHECKED, both now cases above. The EMPTY entry
# had been wrong in BOTH halves: its stated reason was that NO SECTION reports first, and NO SECTION
# never reports at all, because the EMPTY branch exits from inside the per-file loop.
#
#   NOTHING PRICED (PRICED empty)    structural: PRICED is a literal array, so no fixture can empty it.
#                                    Reachable only by mutating the script itself.
#   file_dir . fallback              structural: needs a PRICED entry with no slash, again a literal;
#                                    and no priced file sits at the repository root.
#   UNREADABLE (wc non-zero)         MEASURED, not assumed: chmod 000 still reads back on this platform,
#                                    and a directory named *.md fails the -f test first, so MISSING
#                                    reports instead. Fail-closed either way.
#   empty candidate skipped (sweep)  MEASURED equivalent mutant: removing it fails 0 of 43 cases. On
#                                    bash >= 4.4 the loop iterates an empty array, which is a no-op.
#   NOTHING SWEPT (swept == 0)       structural: a priced file is itself markdown at a path both globs
#                                    reach, and if it is absent the run has already exited NO SUCH FILE.
#   die writes to STDERR             MEASURED equivalent mutant, and RUN rather than argued because the
#                                    empty-stderr row above looks like it covers this and does not:
#                                    dropping ` >&2` from `die` (cmp-verified to differ, one line) fails
#                                    0 of 47. Both helpers survive it -- expect_red matches its needles
#                                    against BOTH streams through gate_output, and expect_green requires
#                                    stderr to be EMPTY, which a `die` that never writes there satisfies
#                                    trivially. Pinning it needs a DIFFERENT assertion, that a RED run
#                                    writes something to stderr; gh#271 declined to ship one it had not
#                                    also mutated. Stated so the row above is not read as covering it.
#
# LOCAL RUNTIME. Each case forks a shell and a `wc` per row. That is milliseconds on the CI runner and can be
# a couple of minutes on a Windows checkout, where process creation is pathologically slow; the fixtures
# themselves are written with builtins only, so what remains is irreducible without giving up running the real
# gate. Run it before pushing a change to either script, not on every save.

set -euo pipefail

red()  { printf '\033[31m%s\033[0m\n' "$*" >&2; }
ok()   { printf '\033[32m%s\033[0m\n' "$*"; }
info() { printf '%s\n' "$*"; }

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GATE="$REPO_ROOT/scripts/check-doc-sizes.sh"
if [ ! -f "$GATE" ]; then
  red "  MISSING  $GATE"
  red "NOTHING HAS BEEN CHECKED, and in particular the gate has not been proven able to fail."
  exit 1
fi

FIXTURES="$(mktemp -d)"
trap 'rm -rf "$FIXTURES"' EXIT

failures=0
cases=0

# Writes a file of exactly $2 bytes using builtins only — no `head`, no `tr`, no fork. The content is
# irrelevant; only `wc -c` ever looks at these.
mkfile() {
  local path="$1" bytes="$2" pad
  printf -v pad '%*s' "$bytes" ''
  printf '%s' "${pad// /x}" > "$path"
}

# Builds one fixture root. The priced documents are sized so their correct prices are exact:
#   alpha.md 4000 B -> 1000 tok -> 1.0K, beta.md 8000 B -> 2000 tok -> 2.0K, gamma.md 2000 B -> 500 tok -> 0.5K
#   agents/epsilon.md 6000 B -> 1500 tok -> 1.5K
#
# `delta.md` sits under a THIRD heading the gate does not price. Which table it gets is the fifth argument:
#
#   plain   a table with NO ~tok column. The gate must ignore it — most tables in the corpus are this shape,
#           and pricing them would be nonsense.
#   priced  a ~tok table under that unlisted heading. The gate must REFUSE it. Until the PR #175 review this
#           was the `plain` case's opposite in name only: an unlisted priced table was silently unread, and
#           the fixture asserted that it should be.
#
# THE SECOND PRICED FILE IS THE gh#178 HALF, and it is present in every fixture because the real `PRICED`
# names it: a `documentation/agents/README.md` with a `~tok` table of its own, standing in for the role
# contracts. Two things about it are load-bearing and neither is decoration:
#
#   - `documentation/epsilon.md` is a 400 B SHADOW of `documentation/agents/epsilon.md`. The second file's
#     row prices `epsilon.md` at 1.5K, which is right relative to `documentation/agents/` and 15x wrong
#     relative to `documentation/`. So a gate that kept ONE map-relative directory — the obvious way to
#     port this script and the way that silently measures a different file — turns the SOUND case red. The
#     green case is the measurement; nothing else has to assert it.
#   - the `../beta.md` row climbs out of the nested file, which is the shape three of the four real rows
#     have (`../../MarqSpec.Mcp.TopstepX/AGENTS.md`).
#
# `stray_kind` is the file-level twin of `reference_kind`, and both take a third value. `priced` drops a
# `~tok` table where the gate must refuse it — under an unlisted heading, or in a file `PRICED` does not name
# at all. `fenced` puts the same table inside a ```markdown fence, where the gate must IGNORE it: a document
# explaining a price table shows one, and reddening a required check on that is how a gate gets deleted.
# `unterminated` opens that fence and never closes it, which must be REPORTED rather than allowed to
# swallow the rest of the file in silence. `closer-char`, `closer-len`, `closer-info` and `tilde` each lean
# on one of `fence_step`'s four opener/closer rules, and `reset-leak` on the per-file reset -- the one of the
# five that fails OPEN. `quoted` puts the fenced example inside a blockquote, which must
# be ignored exactly as the unquoted one is -- the construct that got past `fence_step` in round 2.
# `quote-closed` withholds the closing fence and ends the blockquote instead, which closes it in CommonMark
# and must not be read as unterminated. `quote-eof` ends the FILE instead, which closes the quote and the
# fence with it. `quote-then-table` puts a REAL price table after the quote -- the only shape that can tell
# "the fence closed with its container" from "the fence ran to EOF and was forgiven there".
# `second_file` set to `absent` deletes the second priced file outright — a priced file that has moved must
# be reported, not skipped.
#
# Every argument is one editable part of an otherwise known-good pair of files, so every case is a one-line
# perturbation. A fixture that differs from the sound one in more than the fault under test proves nothing
# about which fault the gate detected. The last four default to the sound values, so the twelve calls that
# predate gh#178 are unchanged.
make_fixture() {
  local dir="$1" alpha_tok="$2" gamma_row="$3" agreements_heading="$4" reference_kind="$5"
  local contract_row="${6:-$SOUND_CONTRACT}" contracts_heading="${7:-$SOUND_CONTRACTS_HEADING}"
  local stray_kind="${8:-none}" second_file="${9:-present}" map_tail="${10:-none}"
  # gh#196. `alpha.md`'s SIZE is now variable as well as its price, because the rule under test is no longer
  # "how far apart are these two numbers" but "does the row state what the file rounds to" -- and that can
  # only be exercised by moving the FILE under a fixed row, which is precisely the drift gh#196 is about.
  # Defaulted, so every call that predates it is unchanged.
  local alpha_bytes="${11:-4000}"
  local fk_kind=no fk_open="" fk_inner="" fk_close=""
  mkdir -p "$dir/documentation/agents"
  mkfile "$dir/documentation/alpha.md" "$alpha_bytes"
  mkfile "$dir/documentation/beta.md" 8000
  mkfile "$dir/documentation/gamma.md" 2000
  mkfile "$dir/documentation/delta.md" 400
  mkfile "$dir/documentation/agents/epsilon.md" 6000
  mkfile "$dir/documentation/epsilon.md" 400
  mkfile "$dir/documentation/zero.md" 0
  if [ "$second_file" = "empty" ]; then
    : > "$dir/documentation/agents/README.md"
  elif [ "$second_file" = "present" ]; then
    {
      printf '# fixture role contracts\n\n'
      printf '%s\n\n' "$contracts_heading"
      printf '| Contract | ~tok | Loads |\n'
      printf '|---|---:|---|\n'
      printf '%s\n' "$contract_row"
      printf '| [`beta.md`](../beta.md) | 2.0K | Open it yourself. |\n'
    } > "$dir/documentation/agents/README.md"
  fi
  # THE SWEEP HAS TWO ROOTS AND EVERY OTHER FIXTURE LIVES UNDER documentation/, so until this one nothing
  # in the suite could tell them apart -- `.github/**/*.md` could be deleted from the glob with all 36 cases
  # green, and a price list in `copilot-instructions.md` would simply stop being read. That root was added
  # because a reviewer found it missing; this is what proves it is load-bearing.
  # AT DEPTH 2 DELIBERATELY. At depth 1 this file also pinned `globstar` by accident -- `.github/**/*.md`
  # with globstar off degrades to `.github/*/*.md`, which misses depth 1 and finds depth 2 -- so removing
  # globstar reddened this case and the ledger read as though globstar were covered. One case, one decision:
  # this one pins the ROOT, and `stray_kind=deep` below pins globstar. Two of the five real markdown files
  # under that root live at this depth (.github/workflows/), three at depth 1.
  if [ "$stray_kind" = "github-root" ]; then
    mkdir -p "$dir/.github/workflows"
    {
      printf '# a checklist under a DOT-directory\n\n'
      printf '| Document | ~tok | Read it when |\n'
      printf '|---|---:|---|\n'
      printf '| [`delta.md`](../documentation/delta.md) | 99K | Priced by nothing, in the second root. |\n'
    } > "$dir/.github/workflows/notes.md"
  fi

  # GLOBSTAR, PINNED BY INTENT. Three levels down from the repository root, which `**/*.md` reaches only
  # while globstar is on: with it off the pattern degrades to `*/*.md` and stops at depth 2, so this table
  # is never swept and the run goes green on a price list nothing read.
  if [ "$stray_kind" = "deep" ]; then
    {
      printf '# a document three levels down\n\n'
      printf '| Document | ~tok | Read it when |\n'
      printf '|---|---:|---|\n'
      printf '| [`epsilon.md`](epsilon.md) | 99K | Only globstar reaches this depth. |\n'
    } > "$dir/documentation/agents/deep.md"
  fi

  case "$stray_kind" in closer-char|closer-len|closer-info|tilde|reset-leak) fk_kind=yes ;; esac
  if [ "$fk_kind" = yes ]; then
    # ONE PRICE TABLE, INSIDE ONE FENCE, and the only thing that varies is which of `fence_step`'s four
    # opener/closer rules the fixture leans on. Each `fk_inner` line is a NEAR-closer that the shipped gate
    # must NOT treat as a closer; drop the corresponding rule and it closes early, the table falls outside
    # the fence, and the run reddens. So each of these is green here and red under exactly one mutation.
    case "$stray_kind" in
      closer-char) fk_open='```markdown' ; fk_inner='~~~'         ; fk_close='```'  ;;
      closer-len)  fk_open='````'        ; fk_inner='```'         ; fk_close='````' ;;
      closer-info) fk_open='```text'     ; fk_inner='```markdown' ; fk_close='```'  ;;
      tilde)       fk_open='~~~markdown' ; fk_inner=''            ; fk_close='~~~'  ;;
      reset-leak)  fk_open='```markdown' ; fk_inner=''            ; fk_close=''     ;;
    esac
    {
      printf '# a document PRICED does not name\n\n'
      printf '%s\n' "$fk_open"
      [ -z "$fk_inner" ] || printf '%s\n' "$fk_inner"
      printf '| Document | ~tok | Read it when |\n'
      printf '|---|---:|---|\n'
      printf '| [`delta.md`](delta.md) | 99K | Inside the fence, so the gate must not read it. |\n'
      [ -z "$fk_close" ] || printf '%s\n' "$fk_close"
    } > "$dir/documentation/stray.md"
    # `stray2.md` sorts immediately after `stray.md` in the sweep's glob order, with nothing between them to
    # reset the fence -- and THAT ADJACENCY IS THE PRECONDITION, not decoration. A fixture whose files are
    # not adjacent lets an intervening fence clear the leak, and then the mutation looks like a no-op: a
    # fixture that fails to establish its condition reports exactly as a rule that is not needed.
    if [ "$stray_kind" = "reset-leak" ]; then
      {
        printf '# a SECOND unlisted document, carrying a REAL price table\n\n'
        printf '| Document | ~tok | Read it when |\n'
        printf '|---|---:|---|\n'
        printf '| [`delta.md`](delta.md) | 99K | Not an example. The sweep must still reach this. |\n'
      } > "$dir/documentation/stray2.md"
    fi
  elif [ "$stray_kind" != "none" ]; then
    {
      printf '# a document PRICED does not name\n\n'
      q=""
      case "$stray_kind" in quoted|quote-closed|quote-eof|quote-then-table) q="> " ;; esac
      if [ "$stray_kind" != "priced" ]; then printf '%s```markdown\n' "$q"; fi
      printf '%s| Document | ~tok | Read it when |\n' "$q"
      printf '%s|---|---:|---|\n' "$q"
      printf '%s| [`delta.md`](delta.md) | 99K | A price table in a file the gate never opens. |\n' "$q"
      case "$stray_kind" in
        unterminated|quote-closed|quote-eof|quote-then-table) ;;
        *) printf '%s```\n' "$q" ;;
      esac
      if [ "$stray_kind" = "quote-then-table" ]; then
        printf '\nThen a REAL price table, outside the quote and outside the fence:\n\n'
        printf '| Document | ~tok | Read it when |\n'
        printf '|---|---:|---|\n'
        printf '| [`delta.md`](delta.md) | 99K | This one is not an example. |\n'
      fi
      if [ "$stray_kind" = "quote-closed" ]; then printf '\nOrdinary prose, outside the quote.\n'; fi
    } > "$dir/documentation/stray.md"
  fi
  {
    printf '# fixture routing map\n\n'
    printf '## Start here\n\n'
    printf '| Document | ~tok | Read it when |\n'
    printf '|---|---:|---|\n'
    printf '| [`alpha.md`](alpha.md) | %s | You need alpha. |\n' "$alpha_tok"
    printf '| [`beta.md`](beta.md) | 2.0K | You need beta. |\n'
    printf '\n%s\n\n' "$agreements_heading"
    printf '| Document | ~tok | Read it when |\n'
    printf '|---|---:|---|\n'
    [ -z "$gamma_row" ] || printf '%s\n' "$gamma_row"
    # A `---` THEMATIC BREAK, with prose on the very next line so no blank line can close the table the
    # mutant opens. The alignment-row test requires a pipe as well as a dash precisely so this does not open
    # a table; drop that requirement and this prose parses as a row and reddens with NO LINK.
    #
    # ITS POSITION IS LOAD-BEARING, and nothing else in this file says so (PR #193 round 7, a near-miss the
    # reviewer disclosed rather than filed). It sits INSIDE a priced section -- between the agreements table
    # and `## Reference`. Move it below the unpriced `## The contracts` heading and `current` is -1 there,
    # the table logic never runs at all, and the case passes green under the very mutation it exists to
    # catch. A fixture that stops establishing its precondition reports exactly as a rule that is not
    # needed, so do not relocate this block without re-running the mutation.
    if [ "$map_tail" = "thematic-break" ]; then
      printf '\n---\nOrdinary prose directly under a thematic break.\n'
    fi
    printf '\n## Reference\n\n'
    if [ "$reference_kind" = "priced" ]; then
      printf '| Document | ~tok | Read it when |\n'
      printf '|---|---:|---|\n'
      printf '| [`delta.md`](delta.md) | 99K | A price table under a heading the gate does not know. |\n'
    elif [ "$reference_kind" = "fenced" ]; then
      printf '```markdown\n'
      printf '| Document | ~tok | Read it when |\n'
      printf '|---|---:|---|\n'
      printf '| [`delta.md`](delta.md) | 99K | An EXAMPLE of a price table, shown not declared. |\n'
      printf '```\n'
    elif [ "$reference_kind" = "quote-then-table" ]; then
      printf '> An example, in a quote, with no closing fence:\n\n'
      printf '> ```markdown\n'
      printf '> | Document | ~tok | Read it when |\n'
      printf '\nThen a REAL price table, outside the quote and outside the fence:\n\n'
      printf '| Document | ~tok | Read it when |\n'
      printf '|---|---:|---|\n'
      printf '| [`delta.md`](delta.md) | 99K | This one is not an example. |\n'
    elif [ "$reference_kind" = "quote-closed" ]; then
      printf '> Shown as the map writes it:\n\n'
      printf '> ```markdown\n'
      printf '> | Document | ~tok | Read it when |\n'
      printf '> |---|---:|---|\n'
      printf '> | [`delta.md`](delta.md) | 99K | Closed by the end of the quote, not by a fence. |\n'
      printf '\nOrdinary prose, outside the quote.\n'
    elif [ "$reference_kind" = "quoted" ]; then
      printf '> Quoting the map, which is what a document explaining this column does:\n\n'
      printf '> ```markdown\n'
      printf '> | Document | ~tok | Read it when |\n'
      printf '> |---|---:|---|\n'
      printf '> | [`delta.md`](delta.md) | 99K | Quoted AND fenced, which must still be ignored. |\n'
      printf '> ```\n'
    elif [ "$reference_kind" = "unterminated" ]; then
      printf '```markdown\n'
      printf '| Document | ~tok | Read it when |\n'
      printf '|---|---:|---|\n'
      printf '| [`delta.md`](delta.md) | 99K | A fence nobody closed swallows every line below it. |\n'
    else
      printf '| Document | Notes |\n'
      printf '|---|---|\n'
      printf '| [`delta.md`](delta.md) | No price column, so the gate must leave all of it alone. |\n'
    fi
    printf '\n## The contracts\n\n'
    printf '| Document | Notes |\n'
    printf '|---|---|\n'
    printf '| [`delta.md`](delta.md) | The SECOND priced file uses this heading; here it is unpriced. |\n'
    if [ "$reference_kind" = "quote-eof" ]; then
      printf '\n> Quoted, and then the file simply ends:\n\n'
      printf '> ```markdown\n'
      printf '> | Document | ~tok | Read it when |\n'
    fi
  } > "$dir/documentation/README.md"
}

# Every priced heading present, every priced table EMPTY. Its own builder rather than five more positional
# arguments on make_fixture, because it varies nothing else: this is the one shape in which `rows_checked`
# reaches zero, and it is the fixture that proved NOTHING CHECKED reachable after the ledger had recorded
# it as unreachable (PR #193 round 7).
make_empty_fixture() {
  local dir="$1"
  mkdir -p "$dir/documentation/agents"
  mkfile "$dir/documentation/alpha.md" 4000
  {
    printf '# fixture routing map\n\n'
    printf '## Start here\n\n'
    printf '| Document | ~tok | Read it when |\n'
    printf '|---|---:|---|\n\n'
    printf '## Working agreements\n\n'
    printf '| Document | ~tok | Read it when |\n'
    printf '|---|---:|---|\n\n'
  } > "$dir/documentation/README.md"
  {
    printf '# fixture role contracts\n\n'
    printf '## The contracts\n\n'
    printf '| Contract | ~tok | Loads |\n'
    printf '|---|---:|---|\n\n'
  } > "$dir/documentation/agents/README.md"
}
SOUND_GAMMA='| [`gamma.md`](gamma.md) | 0.5K | Before starting any work. |'
SOUND_HEADING='## Working agreements'
SOUND_CONTRACT='| [`epsilon.md`](epsilon.md) | 1.5K | Open it yourself. |'
SOUND_CONTRACTS_HEADING='## The contracts'

# THE GATE'S STDERR IS CAPTURED SEPARATELY RATHER THAN MERGED WITH `2>&1` (gh#271, extending gh#239), so
# expect_green can assert it is EMPTY. Merged, output that should not be there at all is indistinguishable
# from output that should -- see the header for the measurement and for the mutant that proves it.
#
# It lives directly under $FIXTURES, which the EXIT trap already removes, and NOT inside any fixture: every
# root the gate is pointed at is $FIXTURES/<name>, and this gate SWEEPS its root for markdown, so a file
# written inside one would be read as part of the corpus under test.
GATE_STDERR="$FIXTURES/.gate-stderr"

run_gate() {
  # Truncated per call, not appended: each case asserts what ITS OWN run wrote, and a leftover byte from the
  # previous case would redden the next one and name the wrong culprit.
  : > "$GATE_STDERR"
  bash "$GATE" "$1" 2>"$GATE_STDERR"
}

# Both streams as one string, for the callers that must read the gate's own words wherever it chose to put
# them -- `die` writes to stderr, `ok` to stdout. Ordering between the two is lost, which costs nothing: every
# needle in this file is a substring of a single line.
gate_output() { printf '%s\n%s' "$1" "$(cat "$GATE_STDERR")"; }

# Runs the REAL gate against a fixture and requires it to REJECT it, saying why in its own words.
#
# THE RED CASES DELIBERATELY DO NOT TAKE expect_green's EMPTY-STDERR ASSERTION, AND THIS IS THE ONLY PLACE
# BESIDE THE HEADER THAT SAYS WHY (gh#271). The gate reports every one of these faults through `die`, which
# writes to stderr -- measured at 194 to 2223 bytes across the twenty-nine cases below -- so here stderr is
# not stray output, it is THE ANSWER, and the needles are matched against it. "Stderr must be empty" is a
# claim about a GREEN run only. The asymmetry between the two helpers is the finding, not an oversight in
# one of them; pushing the assertion up into run_gate would redden all twenty-nine.
expect_red() {
  local label="$1" dir="$2" needle="$3" out status=0
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
  if [[ "$out" != *"$needle"* ]]; then
    red "SELF-TEST FAILED  $label"
    red "  The gate rejected the fixture but never said '$needle', so it failed for some OTHER reason."
    red "  A gate that rejects everything is as useless as one that accepts everything."
    info "$out"
    failures=$(( failures + 1 ))
    return
  fi
  ok "  red as required  $label  ($needle)"
}

# Runs the REAL gate against a SOUND fixture and requires it to ACCEPT it — to have measured something, and
# to have said NOTHING ELSE ANYWHERE. The third assertion is the one gh#271 added, and it is the only one
# here that looks at output the case did not ask for.
expect_green() {
  local label="$1" dir="$2" needle="$3" out err status=0 stray
  cases=$(( cases + 1 ))

  out="$(run_gate "$dir")" || status=$?
  err="$(cat "$GATE_STDERR")"

  if [ "$status" -ne 0 ]; then
    red "SELF-TEST FAILED  $label"
    red "  The gate REJECTED a sound fixture (exit $status). It would fail correct pull requests, and the"
    red "  first person it wrongly stops will delete it."
    # Both streams: the gate says WHY it rejected through `die`, i.e. on stderr, which $out no longer carries.
    info "$(gate_output "$out")"
    failures=$(( failures + 1 ))
    return
  fi
  if [[ "$out" != *"$needle"* ]]; then
    red "SELF-TEST FAILED  $label"
    red "  The gate passed without saying '$needle'. Exit 0 having priced NOTHING is the shape of every"
    red "  dead guard in this repository; a pass has to carry its own evidence."
    info "$(gate_output "$out")"
    failures=$(( failures + 1 ))
    return
  fi
  # A GREEN RUN WRITES EXACTLY ZERO BYTES TO STDERR -- measured on every green fixture in this file and on
  # the real tree, and re-measured HERE, by this line, on every run. See the header for the numbers, for the
  # code reading that agrees with them, and for the mutant that proves this line can fail.
  #
  # Byte count rather than `[ -n ]`: a lone newline is stray output too, and the number is what an author
  # needs to see. `wc -c` pads on some platforms, hence the strip.
  stray="$(wc -c < "$GATE_STDERR" | tr -d '[:space:]')"
  if [ "$stray" -ne 0 ]; then
    red "SELF-TEST FAILED  $label"
    red "  The gate went green, said '$needle', and still wrote $stray bytes to STDERR. Exit status and"
    red "  needles both survive a gate that is ALSO doing something else — a stray line above its own"
    red "  \`set -euo pipefail\` is non-fatal, changes no exit code, leaves stdout byte-identical and"
    red "  matches every needle (gh#239, gh#271)."
    info "$err"
    failures=$(( failures + 1 ))
    return
  fi
  ok "  green as required  $label  ($needle; stderr empty)"
}

info "check-doc-sizes.sh self-test — the gate is run against fixtures with known faults."
info ""

# 1. The fault gh#160 was filed for: a row whose number no longer describes its file.
make_fixture "$FIXTURES/drifted" "5.0K" "$SOUND_GAMMA" "$SOUND_HEADING" "plain"
expect_red "a drifted ~tok value (5.0K on a 1.0K file)" "$FIXTURES/drifted" "OUT OF DATE"

# 2. THE GRANULARITY RULE, BOTH SIDES (gh#196). This pair replaced a 20%-green / 30%-red pair that pinned
#    `TOLERANCE_PCT=25`. There is no percentage any more: a row is right when it states the measurement
#    ROUNDED TO 0.1K, which is the value the gate prints.
#
#    `alpha.md` at 4180 B measures 1045 tok, which is deliberately NOT on a 0.1K boundary -- it rounds to
#    1.0K and is 4% away from it. The green half is what stops the rule collapsing into "stated must equal
#    the raw measurement", which would redden nearly every real row on this repository and be deleted the
#    same afternoon; the red half is 1.1K, ONE STEP off, which the old 25% band accepted in silence. That
#    red case is gh#196's regression test: it is the shape of `code-reviewer.md` sitting at 1.7K against a
#    measured 1.8K, green for as long as nobody re-derived it by hand.
make_fixture "$FIXTURES/rounds" "1.0K" "$SOUND_GAMMA" "$SOUND_HEADING" "plain" \
  "$SOUND_CONTRACT" "$SOUND_CONTRACTS_HEADING" "none" "present" "none" 4180
expect_green "a measurement off the 0.1K grid, priced at what it rounds to" "$FIXTURES/rounds" "5 priced rows"

make_fixture "$FIXTURES/onestep" "1.1K" "$SOUND_GAMMA" "$SOUND_HEADING" "plain" \
  "$SOUND_CONTRACT" "$SOUND_CONTRACTS_HEADING" "none" "present" "none" 4180
expect_red "one 0.1K step off, which the old 25% band accepted" "$FIXTURES/onestep" "OUT OF DATE"

#    The comparison is arithmetic, not a string match. `SIZE_CELL` admits `1K` as well as `1.0K` and they are
#    the same price, so a gate comparing the two CELLS would redden a row for its spelling -- a blocked merge
#    on a correct number, which is how a gate gets deleted by the first person it wrongly stops.
make_fixture "$FIXTURES/bareK" "1K" "$SOUND_GAMMA" "$SOUND_HEADING" "plain"
expect_green "1K and 1.0K are the same price" "$FIXTURES/bareK" "5 priced rows"

#    And a cell FINER than the column is refused, with its own wording. 1.05K on a 1045-token file is nearer
#    the truth than the 1.0K demanded of it, so the deviation truncates to 0% and the ordinary diagnostic
#    would refuse the row while printing a reason saying nothing is wrong. The needle is that wording, not
#    `OUT OF DATE`, because what is being pinned here is the branch that explains the refusal.
make_fixture "$FIXTURES/finer" "1.05K" "$SOUND_GAMMA" "$SOUND_HEADING" "plain" \
  "$SOUND_CONTRACT" "$SOUND_CONTRACTS_HEADING" "none" "present" "none" 4180
expect_red "a cell carrying more precision than the column has" "$FIXTURES/finer" \
  "finer than the 0.1K this column is written at"

# 2a. THE COMPOUNDING CASE, WHICH IS gh#196'S ACCEPTANCE CRITERION AND WHICH NO SINGLE OUT-OF-BAND ROW
#     COVERS. The failure that card documents is not one wrong row; it is a row going wrong by increments
#     nobody's pull request is responsible for, each one individually tolerated. Three merges each growing a
#     priced file by ~6%, with the row never edited:
#
#       start   4000 B  1000 tok  row says 1.0K, correct
#       +6%     4240 B  1060 tok  ->  1.1K.   5% off -- GREEN under the old 25% band
#       +6%     4494 B  1123 tok  ->  1.1K.  11% off -- GREEN under the old 25% band
#       +6%     4764 B  1191 tok  ->  1.2K.  16% off -- GREEN under the old 25% band
#
#     So the old gate tolerated the whole sequence and would have tolerated a fourth, because a proportional
#     band's absolute width grows with the file it is measuring. Two of the four points are cases, and they
#     make DIFFERENT claims: the first that the sequence cannot begin, the last that its endpoint -- the row
#     16% wrong, which is the state gh#160 was filed for, reassembled out of individually tolerated steps --
#     is refused. The middle step is arithmetic between them and is not a third decision.
make_fixture "$FIXTURES/compound1" "1.0K" "$SOUND_GAMMA" "$SOUND_HEADING" "plain" \
  "$SOUND_CONTRACT" "$SOUND_CONTRACTS_HEADING" "none" "present" "none" 4240
expect_red "the first ~6% growth of a priced file, row unedited" "$FIXTURES/compound1" "OUT OF DATE"

make_fixture "$FIXTURES/compound3" "1.0K" "$SOUND_GAMMA" "$SOUND_HEADING" "plain" \
  "$SOUND_CONTRACT" "$SOUND_CONTRACTS_HEADING" "none" "present" "none" 4764
expect_red "three ~6% growths compounded to 16%, still inside the old band" "$FIXTURES/compound3" "OUT OF DATE"

# 3. THE SAME DRIFT, WRITTEN THE TWO OTHER WAYS GFM ALLOWS. Both of these render as ordinary rows, and both
#    were silently skipped by the "the line starts with a pipe" test this gate used until the PR #175
#    review, which restored the exact gh#160 defect through them and got back a green `ok  9 routed rows`
#    with the bad row simply absent from the listing. A gate whose coverage shrinks when somebody reformats
#    a table is worse than no gate, because its green line still names a row count and so reads as proof.
make_fixture "$FIXTURES/pipeless" "1.0K" \
  '[`gamma.md`](gamma.md) | 5.0K | Before starting any work.' "$SOUND_HEADING" "plain"
expect_red "a drifted row written without outer pipes" "$FIXTURES/pipeless" "OUT OF DATE"

make_fixture "$FIXTURES/indented" "1.0K" \
  '  | [`gamma.md`](gamma.md) | 5.0K | Before starting any work. |' "$SOUND_HEADING" "plain"
expect_red "a drifted row indented under the table" "$FIXTURES/indented" "OUT OF DATE"

# 4. The inversion itself: prose that makes the size claim the column already makes.
make_fixture "$FIXTURES/claim" "1.0K" \
  '| [`gamma.md`](gamma.md) | 0.5K | Before starting any work. Cheap; just read it. |' "$SOUND_HEADING" "plain"
expect_red "prose making a size claim" "$FIXTURES/claim" "SIZE CLAIM"

# 5. `quickest` -- named in documentation/README.md as an example of what this refuses, and missing from
#    the vocabulary until the PR #175 review. The word the map uses to TEACH the rule was the one word the
#    rule did not catch, so the author most likely to trip it was the one who read the sentence and reached
#    for its own example.
make_fixture "$FIXTURES/quickest" "1.0K" \
  '| [`gamma.md`](gamma.md) | 0.5K | Before starting. The quickest read in this table. |' "$SOUND_HEADING" "plain"
expect_red "prose claiming the quickest read" "$FIXTURES/quickest" "SIZE CLAIM"

# 6. THE SECOND OF THE TWO RUNS, in fixture form: correct prose the gate must NOT reject. `no longer` appears
#    twelve times in documentation/ today, and the first draft's bare `-er` comparatives reddened it -- a
#    required check on all three rungs blocking a merge over a size claim nobody made. A gate is deleted by
#    the first person it wrongly stops, so this case is what keeps the comparatives out of the vocabulary.
make_fixture "$FIXTURES/nolonger" "1.0K" \
  '| [`gamma.md`](gamma.md) | 0.5K | Read it when an estimate no longer matches the card. |' "$SOUND_HEADING" "plain"
expect_green "ordinary prose containing 'no longer'" "$FIXTURES/nolonger" "5 priced rows"

# 7. A row pointing at a file that is not there. It cannot be measured, so the gate must not call it priced.
make_fixture "$FIXTURES/missing" "1.0K" \
  '| [`nope.md`](nope.md) | 0.5K | A document that does not exist. |' "$SOUND_HEADING" "plain"
expect_red "a row whose target is absent" "$FIXTURES/missing" "MISSING"

# 8. A placeholder where a size belongs — the `index` this map carried on its `agents/` row until gh#160.
make_fixture "$FIXTURES/placeholder" "1.0K" \
  '| [`gamma.md`](gamma.md) | index | Before starting any work. |' "$SOUND_HEADING" "plain"
expect_red "a placeholder instead of a size" "$FIXTURES/placeholder" "NOT A SIZE"

# 9. A row with no link at all: nothing to measure, and silently skipping it is how the column shrinks.
make_fixture "$FIXTURES/nolink" "1.0K" \
  '| Some prose, not a link | 0.5K | Before starting any work. |' "$SOUND_HEADING" "plain"
expect_red "a row with no link in its first cell" "$FIXTURES/nolink" "NO LINK"

# 10. A priced heading with a table but no data rows. This is the vacuous pass — the gate must call it out
#    rather than report the section clean.
make_fixture "$FIXTURES/norows" "1.0K" "" "$SOUND_HEADING" "plain"
expect_red "a priced section with no rows" "$FIXTURES/norows" "NO ROWS"

# 11. A renamed heading. The gate stops seeing the section, so it must say so instead of pricing what is left.
make_fixture "$FIXTURES/renamed" "1.0K" "$SOUND_GAMMA" "## Agreements" "plain"
expect_red "a renamed section heading" "$FIXTURES/renamed" "NO SECTION"

# 12. A ~tok table under a heading SECTIONS does not name. The parser fail-CLOSED on a removed section
#     (`NO SECTION`) and on an emptied one (`NO ROWS`), and fail-OPEN on an added one: before the PR #175
#     review this drew `ok 10 routed rows ... every ~tok within 25%` while a row 70x wrong sat unread beneath
#     it. Adding a section is the one edit that produced exactly the shrinking coverage the header refuses.
make_fixture "$FIXTURES/unlisted" "1.0K" "$SOUND_GAMMA" "$SOUND_HEADING" "priced"
expect_red "a price table under an unlisted heading" "$FIXTURES/unlisted" "UNLISTED TABLE"

# 13. THE SECOND PRICED FILE IS ACTUALLY READ (gh#178). Everything above perturbs the routing map, so every
#     one of them would still pass on a gate that had been pointed at a second file and never opened it --
#     which is the whole failure mode this card was filed about, in the gate rather than in the map. The
#     drift is put in the second file's OWN row, so only a gate that read it can report this.
make_fixture "$FIXTURES/contract-drift" "1.0K" "$SOUND_GAMMA" "$SOUND_HEADING" "plain" \
  '| [`epsilon.md`](epsilon.md) | 5.0K | Open it yourself. |'
expect_red "a drifted row in the second priced file" "$FIXTURES/contract-drift" "OUT OF DATE"

# 14. The same renamed-heading fault as case 11, in the second file -- and the one case that asserts `PRICED`
#     is keyed on PAIRS rather than on heading text. The map fixture carries a `## The contracts` heading of
#     its own, deliberately and unpriced, so a gate keeping one flat list of headings finds that occurrence,
#     marks the section seen, and never says `NO SECTION` about the file whose heading actually moved.
make_fixture "$FIXTURES/contract-renamed" "1.0K" "$SOUND_GAMMA" "$SOUND_HEADING" "plain" \
  "$SOUND_CONTRACT" "## Contracts"
expect_red "a renamed heading in the second priced file" "$FIXTURES/contract-renamed" "NO SECTION"

# 15. A ~tok table in a FILE `PRICED` does not name. Case 12 is this fault at the heading level and was the
#     PR #175 review's finding; gh#178 opened it again one level up by making "which files" a list at all.
#     Without this the next author adds a fifth priced document, nothing reads it, and the green line below
#     still names a row count -- so it still reads as proof.
make_fixture "$FIXTURES/stray" "1.0K" "$SOUND_GAMMA" "$SOUND_HEADING" "plain" \
  "$SOUND_CONTRACT" "$SOUND_CONTRACTS_HEADING" "priced"
expect_red "a price table in a file the gate does not read" "$FIXTURES/stray" "UNLISTED FILE"

# 16. A priced file that is not on disk. `check-doc-links.sh` says nothing about it -- nothing links to a
#     table, only to documents -- so if this gate skipped it instead, moving `documentation/agents/README.md`
#     would silently stop pricing four contracts with every other check still green.
make_fixture "$FIXTURES/nofile" "1.0K" "$SOUND_GAMMA" "$SOUND_HEADING" "plain" \
  "$SOUND_CONTRACT" "$SOUND_CONTRACTS_HEADING" "none" "absent"
expect_red "a priced file that is not on disk" "$FIXTURES/nofile" "NO SUCH FILE"

# 17. FENCED EXAMPLES ARE NOT PRICE TABLES, and both halves of that must be green (PR #193 review). A
#     document that EXPLAINS a `~tok` table shows one, and a gate matching `~tok` plus two pipes then blocks
#     a merge on correct prose -- `docs` is required on all three rungs, so that is the shape a gate gets
#     deleted for. It very nearly landed: this card's own `platform.md` edit added two `~tok` sentences to a
#     swept file, each one `|` short of tripping it.
#
#     Two cases because the two rules are separate code paths: the fence must be honoured in a file the gate
#     WALKS (rule 3, below a heading it does not price) and in one it merely SWEEPS (rule 4). The unfenced
#     twins are cases 12 and 15, which stay red.
make_fixture "$FIXTURES/fenced-section" "1.0K" "$SOUND_GAMMA" "$SOUND_HEADING" "fenced"
expect_green "a fenced ~tok example under an unlisted heading" "$FIXTURES/fenced-section" "5 priced rows"

make_fixture "$FIXTURES/fenced-file" "1.0K" "$SOUND_GAMMA" "$SOUND_HEADING" "plain" \
  "$SOUND_CONTRACT" "$SOUND_CONTRACTS_HEADING" "fenced"
expect_green "a fenced ~tok example in an unswept-for-pricing file" "$FIXTURES/fenced-file" "5 priced rows"

# 18. A FENCE LEFT OPEN AT END OF FILE -- the one fail-open case 17's fence tracking introduces. Every line
#     below an unclosed ``` is skipped, so a price table under it is unseen. In a PRICED file that reddens as
#     NO SECTION only when a priced heading happens to sit below the fence, and passes silently otherwise; in
#     a SWEPT file it is silent always. Both are named instead. Every fence in the corpus was closed when
#     this was added -- several of the openers indented inside a list item -- so it could not fire on
#     correct input, and an unclosed fence is a rendering defect anyway.
make_fixture "$FIXTURES/unterminated-section" "1.0K" "$SOUND_GAMMA" "$SOUND_HEADING" "unterminated"
expect_red "a fence left open in a priced file" "$FIXTURES/unterminated-section" "UNTERMINATED FENCE"

make_fixture "$FIXTURES/unterminated-file" "1.0K" "$SOUND_GAMMA" "$SOUND_HEADING" "plain" \
  "$SOUND_CONTRACT" "$SOUND_CONTRACTS_HEADING" "unterminated"
expect_red "a fence left open in a swept file" "$FIXTURES/unterminated-file" "UNTERMINATED FENCE"

# 19. THE SAME FENCED EXAMPLE, INSIDE A BLOCKQUOTE (PR #193 review round 2). `fence_step` was handed a line
#     the trim had cleaned of indentation but not of `> `, so a quoted fence opened nothing while the rows
#     inside it still carried `~tok` and two pipes -- and the diagnostic then told the author to put it in a
#     fence, which is what they had done. Case 17 one construct out, and gh#123's rule met a third time: the
#     stateful pass has to know the delimiters the later passes remove. Both code paths again.
make_fixture "$FIXTURES/quoted-section" "1.0K" "$SOUND_GAMMA" "$SOUND_HEADING" "quoted"
expect_green "a quoted, fenced ~tok example under an unlisted heading" "$FIXTURES/quoted-section" "5 priced rows"

make_fixture "$FIXTURES/quoted-file" "1.0K" "$SOUND_GAMMA" "$SOUND_HEADING" "plain" \
  "$SOUND_CONTRACT" "$SOUND_CONTRACTS_HEADING" "quoted"
expect_green "a quoted, fenced ~tok example in a swept file" "$FIXTURES/quoted-file" "5 priced rows"

# 20. A QUOTED FENCE CLOSED BY THE END OF ITS BLOCKQUOTE, with no closing ``` anywhere -- valid CommonMark,
#     because the container closes and the fence closes with it (PR #193 review round 3, the residual). This
#     is the INTERACTION of two round-2/3 fixes, neither wrong alone: stripping `>` so `fence_step` can see
#     a quoted fence let the fence state walk out of the quote into ordinary prose, and the unterminated-
#     fence report then reddened correct markdown. One case pins all three mechanisms, because it goes red
#     three different ways: UNLISTED FILE / UNLISTED TABLE if the `>` strip goes (the fence never opens),
#     UNTERMINATED FENCE if the container close goes (the fence never closes), and green only when both are
#     right.
make_fixture "$FIXTURES/quote-closed-section" "1.0K" "$SOUND_GAMMA" "$SOUND_HEADING" "quote-closed"
expect_green "a quoted fence closed by the end of its blockquote" "$FIXTURES/quote-closed-section" "5 priced rows"

make_fixture "$FIXTURES/quote-closed-file" "1.0K" "$SOUND_GAMMA" "$SOUND_HEADING" "plain" \
  "$SOUND_CONTRACT" "$SOUND_CONTRACTS_HEADING" "quote-closed"
expect_green "the same, in a swept file" "$FIXTURES/quote-closed-file" "5 priced rows"

# 21. A QUOTED FENCE STILL OPEN AT END OF FILE (PR #193 review round 4). Case 20's rule has two halves --
#     "a fence opened under a `>` ends at the first line carrying none" AND a blockquote also ends at EOF --
#     and only the first was implemented, so this reddened `UNTERMINATED FENCE` on correct markdown. Worse
#     than noisy: that message says "a price table under it would be unseen" where there is nothing under it
#     at all, which is a confident wrong diagnostic of exactly the kind gh#108/gh#140 is about. Half a rule
#     is the shape that produces one.
make_fixture "$FIXTURES/quote-eof-section" "1.0K" "$SOUND_GAMMA" "$SOUND_HEADING" "quote-eof"
expect_green "a quoted fence still open at end of a priced file" "$FIXTURES/quote-eof-section" "5 priced rows"

make_fixture "$FIXTURES/quote-eof-file" "1.0K" "$SOUND_GAMMA" "$SOUND_HEADING" "plain" \
  "$SOUND_CONTRACT" "$SOUND_CONTRACTS_HEADING" "quote-eof"
expect_green "the same, at the end of a swept file" "$FIXTURES/quote-eof-file" "5 priced rows"

# 22. THE CASE THAT PINS `close_fence_if_quote_ended`, and it exists because case 21 stopped pinning it.
#     Adding the quoted-at-EOF exemption MASKED the container rule: with `close_fence_if_quote_ended`
#     deleted outright, a quoted fence simply stayed open to EOF, where the new exemption then forgave it --
#     so all 29 cases went green on a gate that had lost the feature four of them were written for. Found by
#     a mutation disagreeing with the model, not by reading the file.
#
#     What discriminates is a REAL price table BELOW the quote. If the container rule works, the fence
#     closed with the blockquote and that table is read -- red, by its own name. If it does not, the table
#     sits inside a fence that runs to EOF and is forgiven there -- green, wrongly. No other fixture can
#     tell those apart, which is exactly why the gap was invisible.
make_fixture "$FIXTURES/quote-then-table-section" "1.0K" "$SOUND_GAMMA" "$SOUND_HEADING" "quote-then-table"
expect_red "a real price table below a quoted fence" "$FIXTURES/quote-then-table-section" "UNLISTED TABLE"

make_fixture "$FIXTURES/quote-then-table-file" "1.0K" "$SOUND_GAMMA" "$SOUND_HEADING" "plain" \
  "$SOUND_CONTRACT" "$SOUND_CONTRACTS_HEADING" "quote-then-table"
expect_red "the same, in a swept file" "$FIXTURES/quote-then-table-file" "UNLISTED FILE"

# 23. THE FOUR OPENER/CLOSER RULES `fence_step`'s header STATES IN PROSE, and which nothing tested (PR #193
#     review round 5). Every one could be deleted with all 31 cases green, and not one is an equivalent
#     mutant: each makes the gate redden on ordinary markdown -- a document that shows fence syntax, or one
#     that fences with tildes. That is the same false positive rounds 1 through 4 were spent removing.
#
#     Prose is not a fixture. This is precisely the gap `check-doc-sizes.sh` exists to close for the `~tok`
#     column -- a claim nothing re-measures -- reappearing inside the script that closes it.
make_fixture "$FIXTURES/closer-char" "1.0K" "$SOUND_GAMMA" "$SOUND_HEADING" "plain" \
  "$SOUND_CONTRACT" "$SOUND_CONTRACTS_HEADING" "closer-char"
expect_green "a ~~~ line inside a backtick fence closes nothing" "$FIXTURES/closer-char" "5 priced rows"

make_fixture "$FIXTURES/closer-len" "1.0K" "$SOUND_GAMMA" "$SOUND_HEADING" "plain" \
  "$SOUND_CONTRACT" "$SOUND_CONTRACTS_HEADING" "closer-len"
expect_green "a shorter fence run closes nothing" "$FIXTURES/closer-len" "5 priced rows"

make_fixture "$FIXTURES/closer-info" "1.0K" "$SOUND_GAMMA" "$SOUND_HEADING" "plain" \
  "$SOUND_CONTRACT" "$SOUND_CONTRACTS_HEADING" "closer-info"
expect_green "a fence run with an info string closes nothing" "$FIXTURES/closer-info" "5 priced rows"

make_fixture "$FIXTURES/tilde" "1.0K" "$SOUND_GAMMA" "$SOUND_HEADING" "plain" \
  "$SOUND_CONTRACT" "$SOUND_CONTRACTS_HEADING" "tilde"
expect_green "a ~~~-fenced price table is still fenced" "$FIXTURES/tilde" "5 priced rows"

# 24. THE FAIL-OPEN OF THE FIVE, and the sharpest defect in this batch. `fence_reset` runs once per file in
#     the sweep; remove it and fence state LEAKS from one file into the next. Two adjacent files -- the
#     first ending mid-fence, the second opening with a real undeclared price table -- and the second
#     file's table is swallowed. The report for it simply disappears, buried under a cascade of
#     UNTERMINATED FENCE noise about innocent files.
#
#     THE MUTANT STILL EXITS 1, which is why this case matches on `UNLISTED FILE` and not on redness. A
#     harness asking only "did the suite go red" passes a gate that has stopped reporting the very thing
#     rule 4 exists for -- this file's own "non-zero exit is not sufficient" rule, one level up.
make_fixture "$FIXTURES/reset-leak" "1.0K" "$SOUND_GAMMA" "$SOUND_HEADING" "plain" \
  "$SOUND_CONTRACT" "$SOUND_CONTRACTS_HEADING" "reset-leak"
expect_red "a real table in the file AFTER one ending mid-fence" "$FIXTURES/reset-leak" "UNLISTED FILE"

# 25. THE SWEEP'S SECOND ROOT. `.github/**/*.md` could be deleted from the glob with all 36 cases green,
#     because every other fixture this file builds lives under `documentation/` -- so nothing in the suite
#     could tell the two roots apart. Not an equivalent mutant: a price table in
#     `.github/copilot-instructions.md` is exit 1 `UNLISTED FILE` shipped and exit 0 without the root, i.e.
#     a price list silently stops being read. That root was added because a REVIEWER noticed it missing
#     (PR #193 round 1); nothing proved it load-bearing until this case.
make_fixture "$FIXTURES/github-root" "1.0K" "$SOUND_GAMMA" "$SOUND_HEADING" "plain" \
  "$SOUND_CONTRACT" "$SOUND_CONTRACTS_HEADING" "github-root"
expect_red "a price table under .github/" "$FIXTURES/github-root" "UNLISTED FILE"

# 26. THE ALIGNMENT ROW MUST CONTAIN A PIPE, and the comment beside it says so while nothing tested it.
#     Remove the pipe test and a `---` thematic break under a priced heading opens a table, so the very next
#     line of ordinary prose is read as a data row and reddens with NO LINK -- a required check failing on
#     correct markdown, which is the failure mode five of the last seven findings share. The prose sits on
#     the line IMMEDIATELY after the break, because a blank line would close the mutant's table before it
#     could parse anything and the fixture would prove nothing.
make_fixture "$FIXTURES/thematic-break" "1.0K" "$SOUND_GAMMA" "$SOUND_HEADING" "plain" \
  "$SOUND_CONTRACT" "$SOUND_CONTRACTS_HEADING" "none" "present" "thematic-break"
expect_green "a thematic break under a priced heading" "$FIXTURES/thematic-break" "5 priced rows"

# 27. A LINK CARRYING A #FRAGMENT. `target="${target%%#*}"` strips it so the row resolves; delete that and
#     the row reports MISSING against a file that is plainly there. No priced row in this repository uses a
#     fragment today, which is exactly why nothing exercised the strip -- and why the first row that does
#     would have reddened a required check on a legitimate anchor link.
make_fixture "$FIXTURES/fragment" "1.0K" \
  '| [`gamma.md`](gamma.md#a-section) | 0.5K | Before starting any work. |' "$SOUND_HEADING" "plain"
expect_green "a row whose link carries a #fragment" "$FIXTURES/fragment" "5 priced rows"

# 28. A ROW POINTING AT A ZERO-BYTE FILE. The `measured <= 0` guard exists so the deviation arithmetic never
#     divides by zero. `dev_pct` decides nothing since gh#196 — it is the wording of a failure, not the
#     verdict — but it is still computed on every row, so the guard is load-bearing exactly as it was.
#     Without it the run dies on a bash division error instead of naming the row: it still fails, but stops
#     saying which document is empty. A priced document with no content is a routing error and the gate
#     should say so.
make_fixture "$FIXTURES/zero-byte" "1.0K" \
  '| [`zero.md`](zero.md) | 0.5K | Before starting any work. |' "$SOUND_HEADING" "plain"
expect_red "a row pointing at a zero-byte document" "$FIXTURES/zero-byte" "EMPTY"

# 29. GLOBSTAR, PINNED BY INTENT rather than by a fixture's incidental depth (PR #193 round 7). `**/*.md`
#     degrades to `*/*.md` without it and stops at depth 2, so a price table three levels down is never
#     swept and the run goes green. The .github case above USED to pin this by accident, which is worse
#     than not pinning it at all: the ledger then read as covered.
make_fixture "$FIXTURES/globstar" "1.0K" "$SOUND_GAMMA" "$SOUND_HEADING" "plain" \
  "$SOUND_CONTRACT" "$SOUND_CONTRACTS_HEADING" "deep"
expect_red "a price table three directories down" "$FIXTURES/globstar" "UNLISTED FILE"

# 30. A PRICED FILE THAT EXISTS AND HAS NO LINES. The ledger recorded this branch as unreachable because
#     "NO SECTION reports first" -- BOTH HALVES WERE FALSE. The EMPTY branch sits inside the per-file loop
#     and exits long before the NO SECTION loop is reached, so NO SECTION never reports at all; and the
#     case was one argument away, since `mkfile ... 0` already existed in the builder. An
#     unreachable-with-a-reason row is a claim, and this is the one that was wrong in both halves.
make_fixture "$FIXTURES/empty-priced" "1.0K" "$SOUND_GAMMA" "$SOUND_HEADING" "plain" \
  "$SOUND_CONTRACT" "$SOUND_CONTRACTS_HEADING" "none" "empty"
expect_red "a priced file that exists and has no lines" "$FIXTURES/empty-priced" "has no lines"

# 31. NOTHING CHECKED, also recorded as unreachable and also reachable: every priced heading present and
#     every priced table empty. NO ROWS fires three times AND `rows_checked` reaches zero, and the
#     NOTHING CHECKED branch sits before the `failures > 0` check, so it is what actually ends the run.
make_empty_fixture "$FIXTURES/nothing-checked"
expect_red "every priced table empty" "$FIXTURES/nothing-checked" "NOTHING CHECKED"

# 32. The sound fixture. Five priced rows across three sections in two files, plus an ordinary table with no
#     price column under a third heading -- so this also asserts the gate leaves un-priced tables alone,
#     which is what nearly every table in the corpus is.
#
#     It is ALSO the only assertion that link targets resolve against the file that names them: the second
#     file prices `epsilon.md` at 1.5K, and `documentation/epsilon.md` is 400 B sitting where a map-relative
#     resolution would look. Green here means the gate measured `documentation/agents/epsilon.md`; a gate
#     that kept one directory for every file goes red on this case with `OUT OF DATE`.
make_fixture "$FIXTURES/sound" "1.0K" "$SOUND_GAMMA" "$SOUND_HEADING" "plain"
expect_green "a sound pair of priced files" "$FIXTURES/sound" "5 priced rows"

info ""
if [ "$failures" -gt 0 ]; then
  red "$failures of $cases self-test case(s) failed."
  red "check-doc-sizes.sh is NOT known to be able to fail, so its green runs prove nothing. Fix it before"
  red "trusting anything it reports."
  exit 1
fi

if [ "$cases" -eq 0 ]; then
  red "  NOTHING CHECKED  no self-test cases ran."
  red "This file exists to prove the gate can fail and has just proven nothing."
  exit 1
fi

ok "ok  $cases self-test cases across two priced files — check-doc-sizes.sh rejects each known fault BY NAME, and accepts correct input WITHOUT WRITING A BYTE TO STDERR: one priced off the 0.1K grid at what it rounds to, one spelling that price \`1K\`, one whose prose says \"no longer\", twelve showing a price table inside a fence, one placing a thematic break under a priced heading, and one wholly sound."
