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
# TWO RUNS, NOT ONE (the Coding contract, Tests). This file is the first: red on the faults the gate exists to
# catch. The second is `ci.yml`'s `docs` job running the gate against this repository's REAL priced tables —
# the most awkward correct input there is, with fourteen rows across three tables in two files, targets that
# climb two directories out of the file that names them, prose full of em-dashes, and several sections in
# both files carrying no table at all.
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
# swallow the rest of the file in silence. `quoted` puts the fenced example inside a blockquote, which must
# be ignored exactly as the unquoted one is -- the construct that got past `fence_step` in round 2.
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
  local stray_kind="${8:-none}" second_file="${9:-present}"
  mkdir -p "$dir/documentation/agents"
  mkfile "$dir/documentation/alpha.md" 4000
  mkfile "$dir/documentation/beta.md" 8000
  mkfile "$dir/documentation/gamma.md" 2000
  mkfile "$dir/documentation/delta.md" 400
  mkfile "$dir/documentation/agents/epsilon.md" 6000
  mkfile "$dir/documentation/epsilon.md" 400
  if [ "$second_file" = "present" ]; then
    {
      printf '# fixture role contracts\n\n'
      printf '%s\n\n' "$contracts_heading"
      printf '| Contract | ~tok | Loads |\n'
      printf '|---|---:|---|\n'
      printf '%s\n' "$contract_row"
      printf '| [`beta.md`](../beta.md) | 2.0K | Open it yourself. |\n'
    } > "$dir/documentation/agents/README.md"
  fi
  if [ "$stray_kind" != "none" ]; then
    {
      printf '# a document PRICED does not name\n\n'
      q=""
      [ "$stray_kind" != "quoted" ] || q="> "
      if [ "$stray_kind" != "priced" ]; then printf '%s```markdown\n' "$q"; fi
      printf '%s| Document | ~tok | Read it when |\n' "$q"
      printf '%s|---|---:|---|\n' "$q"
      printf '%s| [`delta.md`](delta.md) | 99K | A price table in a file the gate never opens. |\n' "$q"
      if [ "$stray_kind" != "unterminated" ]; then printf '%s```\n' "$q"; fi
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
  } > "$dir/documentation/README.md"
}

SOUND_GAMMA='| [`gamma.md`](gamma.md) | 0.5K | Before starting any work. |'
SOUND_HEADING='## Working agreements'
SOUND_CONTRACT='| [`epsilon.md`](epsilon.md) | 1.5K | Open it yourself. |'
SOUND_CONTRACTS_HEADING='## The contracts'

# Runs the REAL gate against a fixture and requires it to REJECT it, saying why in its own words.
expect_red() {
  local label="$1" dir="$2" needle="$3" out status=0
  cases=$(( cases + 1 ))

  out="$(bash "$GATE" "$dir" 2>&1)" || status=$?

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

# Runs the REAL gate against a SOUND fixture and requires it to ACCEPT it — and to have measured something.
expect_green() {
  local label="$1" dir="$2" needle="$3" out status=0
  cases=$(( cases + 1 ))

  out="$(bash "$GATE" "$dir" 2>&1)" || status=$?

  if [ "$status" -ne 0 ]; then
    red "SELF-TEST FAILED  $label"
    red "  The gate REJECTED a sound fixture (exit $status). It would fail correct pull requests, and the"
    red "  first person it wrongly stops will delete it."
    info "$out"
    failures=$(( failures + 1 ))
    return
  fi
  if [[ "$out" != *"$needle"* ]]; then
    red "SELF-TEST FAILED  $label"
    red "  The gate passed without saying '$needle'. Exit 0 having priced NOTHING is the shape of every"
    red "  dead guard in this repository; a pass has to carry its own evidence."
    info "$out"
    failures=$(( failures + 1 ))
    return
  fi
  ok "  green as required  $label  ($needle)"
}

info "check-doc-sizes.sh self-test — the gate is run against fixtures with known faults."
info ""

# 1. The fault gh#160 was filed for: a row whose number no longer describes its file.
make_fixture "$FIXTURES/drifted" "5.0K" "$SOUND_GAMMA" "$SOUND_HEADING" "plain"
expect_red "a drifted ~tok value (5.0K on a 1.0K file)" "$FIXTURES/drifted" "OUT OF DATE"

# 2. The boundary, both sides. 1.2K on a 1.0K file is 20% out and must be tolerated; 1.3K is 30% and must not.
#    Without the accepted half, TOLERANCE_PCT could be quietly set to 0 and every case above would still pass.
make_fixture "$FIXTURES/within" "1.2K" "$SOUND_GAMMA" "$SOUND_HEADING" "plain"
expect_green "20% out, inside the 25% tolerance" "$FIXTURES/within" "5 priced rows"

make_fixture "$FIXTURES/outside" "1.3K" "$SOUND_GAMMA" "$SOUND_HEADING" "plain"
expect_red "30% out, past the 25% tolerance" "$FIXTURES/outside" "OUT OF DATE"

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

make_fixture "$FIXTURES/fenced-file" "1.0K" "$SOUND_GAMMA" "$SOUND_HEADING" "plain"   "$SOUND_CONTRACT" "$SOUND_CONTRACTS_HEADING" "fenced"
expect_green "a fenced ~tok example in an unswept-for-pricing file" "$FIXTURES/fenced-file" "5 priced rows"

# 18. A FENCE LEFT OPEN AT END OF FILE -- the one fail-open case 17's fence tracking introduces. Every line
#     below an unclosed ``` is skipped, so a price table under it is unseen. In a PRICED file that reddens as
#     NO SECTION only when a priced heading happens to sit below the fence, and passes silently otherwise; in
#     a SWEPT file it is silent always. Both are named instead. Every fence in the corpus was closed when
#     this was added -- several of the openers indented inside a list item -- so it could not fire on
#     correct input, and an unclosed fence is a rendering defect anyway.
make_fixture "$FIXTURES/unterminated-section" "1.0K" "$SOUND_GAMMA" "$SOUND_HEADING" "unterminated"
expect_red "a fence left open in a priced file" "$FIXTURES/unterminated-section" "UNTERMINATED FENCE"

make_fixture "$FIXTURES/unterminated-file" "1.0K" "$SOUND_GAMMA" "$SOUND_HEADING" "plain"   "$SOUND_CONTRACT" "$SOUND_CONTRACTS_HEADING" "unterminated"
expect_red "a fence left open in a swept file" "$FIXTURES/unterminated-file" "UNTERMINATED FENCE"

# 19. THE SAME FENCED EXAMPLE, INSIDE A BLOCKQUOTE (PR #193 review round 2). `fence_step` was handed a line
#     the trim had cleaned of indentation but not of `> `, so a quoted fence opened nothing while the rows
#     inside it still carried `~tok` and two pipes -- and the diagnostic then told the author to put it in a
#     fence, which is what they had done. Case 17 one construct out, and gh#123's rule met a third time: the
#     stateful pass has to know the delimiters the later passes remove. Both code paths again.
make_fixture "$FIXTURES/quoted-section" "1.0K" "$SOUND_GAMMA" "$SOUND_HEADING" "quoted"
expect_green "a quoted, fenced ~tok example under an unlisted heading" "$FIXTURES/quoted-section" "5 priced rows"

make_fixture "$FIXTURES/quoted-file" "1.0K" "$SOUND_GAMMA" "$SOUND_HEADING" "plain"   "$SOUND_CONTRACT" "$SOUND_CONTRACTS_HEADING" "quoted"
expect_green "a quoted, fenced ~tok example in a swept file" "$FIXTURES/quoted-file" "5 priced rows"

# 20. The sound fixture. Five priced rows across three sections in two files, plus an ordinary table with no
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

ok "ok  $cases self-test cases across two priced files — check-doc-sizes.sh rejects each known fault BY NAME, and accepts correct input: one at the tolerance boundary, one whose prose says \"no longer\", four showing a price table inside a fence -- two of them quoted -- and one wholly sound."
