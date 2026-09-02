#!/usr/bin/env python3
"""Enforce the line-coverage floor over the MERGED coverage of every test tier (gh#431).

Usage:  check-coverage-floor.py <tier>=<directory> [<tier>=<directory> ...]
        MINIMUM_LINE_COVERAGE=<int> in the environment.

WHY THIS EXISTS AS A FILE RATHER THAN AS AN INLINE `shell: python3 {0}` STEP
---------------------------------------------------------------------------
It was inline until gh#431. A local check that disagrees with CI is worse than no local check, and an
inline gate can only be run locally by COPYING it -- which is measuring a copy and reporting it as the
shipped text. gh#142 had to extract `branch-policy.yml`'s stripper for exactly that reason before its
numbers meant anything. This file is what CI runs and what a developer runs.

WHY IT MERGES RATHER THAN GATING EACH TIER SEPARATELY
-----------------------------------------------------
gh#387 moved the write-path suites from the unit tier to the integration tier and deleted the EF-InMemory
second implementation that let the unit tier run them at all. The tests kept running; the gate read only
the unit job's report, so the lines they exercise stopped being counted and the reported figure fell from
64.4%/80.7% to 56.0%/71.1% for no change in what is tested. The number stopped describing the repository.

Two ways to fix that, and the choice is recorded beside the floor in `ci.yml` as well:

  MERGE (chosen). One figure, meaning "the fraction of this repository's lines that some test executes".
  That is the sentence a reader of the number acts on, and it is the sentence that was false. Two numbers
  do not repair it -- a reader cannot combine them, because the union of two line sets is not a function
  of the two rates over them, so "unit 56% + integration 38%" is not an answer to "what is tested".

  A FLOOR PER TIER (declined), and its argument answered rather than ignored. The worry is real: a slow
  tier's coverage can mask a gap in a fast one. But (a) a per-tier floor needs a second threshold invented
  from nothing, and this repository's one floor is a measurement ratcheted upward, not a target; (b) the
  integration tier's standalone figure is low BY DESIGN -- it does not exercise the indicator library and
  should not, so a floor under it would punish a tier for the shape it was given in gh#387; and (c) line
  coverage cannot see a deleted redundant test WITHIN a tier either, so per-tier floors do not actually buy
  the property the worry asks for. What does buy it is visibility, which costs no threshold: this gate
  PRINTS each tier's own figure beside the merged one, so a fast tier going thin is legible in the output
  of every run. Reported per tier, gated on the merge.

WHAT LINE COVERAGE CAN AND CANNOT SEE -- read this before writing a mutation that "should" move it
-------------------------------------------------------------------------------------------------
It is an execution counter, not an assertion. Changing what a covered line DOES moves it by exactly zero:
`UpsertBarsSql`'s `ON CONFLICT ... DO UPDATE` is a `private const string`, which the C# compiler inlines
and which carries no sequence point at all, so it contributes no line to any report and sabotaging it is
invisible here in both directions. MEASURED, not argued: `BarCacheService.cs` lines 522-542, the whole
raw-string literal, appear in NEITHER tier's report -- the instrumented set for that file skips them
entirely. What catches a mutation there is the integration test going red.

What this gate sees, and what gh#387 broke, is whether the lines that EXECUTE that statement are counted
at all. Same run, same file: line 680, the `ExecuteSqlRawAsync(UpsertBarsSql, ...)` call site, reads
**0 hits** in the unit tier's report and **85** in the integration tier's; `BarCacheService.cs` as a whole
goes 21.5% -> 95.9% and `FootprintProjector.cs` 3.0% -> 89.8%. Those are the lines the old unit-only gate
was silently scoring as untested.

MERGING RULES, AND THE ONE APPROXIMATION IN THEM
------------------------------------------------
Lines are merged EXACTLY: a `(file, line)` is covered when any tier's report gives it a non-zero hit
count, and the denominator is the distinct `(file, line)` pairs across all tiers. Rates are never
averaged -- averaging two rates double-counts every line both tiers walk, which is most of them.

Branches are merged as a per-line MAXIMUM of cobertura's `condition-coverage="p% (c/t)"`, and that is a
LOWER BOUND rather than the true union: if two tiers each take a different one of a line's two arms,
cobertura records `(1/2)` on both sides and the format carries nothing that could tell the arms apart.
ReportGenerator merging cobertura has the same ceiling, which is why no tool is installed to do this.
Stated rather than papered over -- and it costs nothing that decides anything, because the FLOOR IS ON
LINE COVERAGE ONLY. Branch is reported, never gated.

A SECOND, SEPARATE BRANCH DISCREPANCY, MEASURED RATHER THAN REASONED (gh#431). The per-tier LINE figures
here reproduce coverlet's own `<coverage>` root exactly -- 4554/8127 unit, 5133/8127 integration on the
container run below, 4550/8127 and 5133/8127 on CI run 33584086404, the same 56.0% the gh#431 issue
quotes. SINCE gh#435 THAT SENTENCE IS ASSERTED ON EVERY RUN rather than claimed here: it was this file's
strongest claim and nothing observed it, which is why the two unit figures in this file could differ by
four without anything noticing. See check_against_root(). The per-tier BRANCH figures do not, and cannot:
coverlet's root
declares `branches-valid="1905"` while the sum over every `<line branch="True" condition-coverage=...>`
it writes is **1885**, covered 1353 against 1349. Twenty branch points, four of them covered, are counted
in the summary and appear nowhere in the detail, so no parser can recover them; reading the detail is the
only route that can union two tiers at all. Effect on the unit tier: 71.6% here against the root's 71.0%,
+0.5pp. Both counted on the container run of 2026-09-01 (unit 1065 tests, integration 196). Do not "fix"
this by reading the root -- that gives back a number that cannot be merged, which is the whole defect.

THE TIERS MUST BE REPORTS OF THE SAME SOURCE TREE, and that is an assumption rather than a check. The
merge key is `(file, line number)`, so two reports built from trees whose line numbers differ do not
merge -- they concatenate the shifted file twice and inflate the denominator. Measured while proving the
mutation below: deleting eleven lines from `BarCacheService.cs` and re-running only the integration tier
took the merged denominator from 8127 to 8177 against the old unit report. CI cannot reach that state --
both jobs check out the same SHA inside one workflow run, and artifacts do not survive a run -- but a
developer comparing a fresh report against a stale directory can, so mind the provenance of what you
point this at. The "tiers share no file" guard below does not catch it: the shifted file still matches.

FAIL-CLOSED, deliberately. A tier named on the command line whose directory holds no cobertura report is
an error, not a tier skipped: silently dropping the integration tier is precisely the gh#387 regression
this gate exists to stop, and it would drop it toward a NUMBER THAT STILL PASSES. The tiers are also
required to share at least one source file, which is the cheap guard against the two reports disagreeing
about how a path is spelled -- disjoint file sets would make the "merge" a sum, inflating the denominator
and quietly turning the union back into an average.

AND THE FLOOR HAS TO BE CLOSE ENOUGH TO THE MEASUREMENT TO FIRE (gh#435). gh#431 left the floor at 40
against a merged 91.0%, correctly -- that card forbade touching it -- and a gate 51 points below what it
guards will not fire before somebody notices by other means, which is worse than no gate because it is
trusted. Its reviewer measured the exact hole: a zero-test integration tier still yields merged 56.0% and
still passes, so THE gh#387 EVENT CLEARS THE GATE BUILT IN RESPONSE TO IT. The floor is 85 since gh#435;
the measurement, the headroom and what ordinary variance costs are recorded beside MINIMUM_LINE_COVERAGE
in `.github/workflows/ci.yml`, which is where the number is configured.

PROVEN ABLE TO FAIL, by `scripts/check-coverage-floor-selftest.sh`, which runs in the `coverage` job
directly after this gate. Every guard above -- the floor, the missing report, the unset threshold, the
disjoint file sets, the parse assertion -- has a fixture there that it is required to REJECT IN ITS OWN
WORDS, plus a sound fixture it is required to ACCEPT at a stated figure. Rejections alone are all
satisfied by `exit 1`; an acceptance alone is satisfied by `exit 0`; neither half is the gate.
"""

from __future__ import annotations

import glob
import hashlib
import os
import sys
import xml.etree.ElementTree as ET


def normalise(path: str) -> str:
    """Spell a cobertura `filename` the same way whichever runner produced it."""
    return path.replace("\\", "/").lstrip("/")


def check_against_root(report: str, root, lines) -> list[str]:
    """Require the parse of ONE document to reproduce that document's own `<coverage>` root.

    Returns a list of human-readable problems; empty means the parse agrees with the report.

    THIS IS THE ONLY THING THAT OBSERVES THE HEADER'S STRONGEST CLAIM, and an under-read is
    invisible without it: dropping lines shrinks the numerator and the denominator TOGETHER, so
    the reported rate MOVES IN THE FLATTERING DIRECTION while coverage falls. Measured on the
    fixture `check-coverage-floor-selftest.sh` builds for exactly this: a report whose uncovered
    lines stop being read reports **higher** than the document it came from, and every earlier
    version of this gate accepted it silently. The same class as the `branch="True"` bug two
    functions below -- a parser reading a document it does not own, with nobody checking.

    LINES ONLY, DELIBERATELY, and not because branches are less important. Cobertura's branch
    detail CANNOT reproduce its own root and the header records the measurement: coverlet declares
    `branches-valid="1905"` while the sum over the `<line branch="True">` elements it writes is
    1885, twenty branch points counted in the summary and absent from the detail. Asserting that
    equality would make this gate unable to PASS, which is the same defect as one unable to fail
    wearing the fix's clothes. The floor is on line coverage only; so is this.

    THE COMPARISON IS AGAINST THE DEDUPLICATED PARSE, which is the parse the merge uses. Cobertura
    emits every line TWICE -- once under `<class><lines>` and once under the method that owns it --
    so a count of raw `<line>` elements is exactly double. Measured on run 33584086404: 16 254 raw
    elements, 8 127 distinct `(file, line)` pairs, and `lines-valid="8127"` on both tiers' roots,
    with `lines-covered` 4550 / 5133 matching the distinct covered counts exactly. `(class, file,
    line)` is 8 127 as well -- no two classes in either report claim the same line -- and no source
    file appears under more than one of the three `<package>` elements. That last one is the known
    way this could legitimately differ: a file compiled into two assemblies is counted twice by the
    root and once by a `(file, line)` key, and the union across those assemblies is the answer we
    want. If that day comes, this fails RED naming the file rather than drifting silently, which is
    the direction to fail in.
    """
    problems = []
    parsed = {
        "lines-valid": len(lines),
        "lines-covered": sum(1 for hits in lines.values() if hits > 0),
    }
    for attribute, count in parsed.items():
        declared = root.get(attribute)
        if declared is None:
            problems.append(f"{report}: its <coverage> root declares no {attribute}, so this parse is "
                            f"checked against nothing (parsed {count}).")
            continue
        if int(declared) != count:
            problems.append(f"{report}: parsed {attribute}={count}, but its own <coverage> root declares "
                            f"{attribute}={declared}.")
    return problems


def read_tier(directory: str):
    """Union one tier's own reports. Returns (lines, branches, files, report_count, problems).

    `lines` maps (file, line) -> hits; `branches` maps (file, line) -> (covered, total).
    A tier normally emits one report, but coverlet emits one per test assembly, so this
    unions within a tier by the same rules it unions across them.

    `problems` is per DOCUMENT rather than per tier, because the root that a parse has to
    reproduce is the root of the document it parsed; summing two roots would double-count
    every line two reports share, which is the same mistake as averaging two rates.
    """
    found = sorted(glob.glob(os.path.join(directory, "**", "coverage.cobertura.xml"), recursive=True))
    lines: dict[tuple[str, int], int] = {}
    branches: dict[tuple[str, int], tuple[int, int]] = {}
    seen: set[str] = set()
    reports = []

    # DEDUPLICATED BY CONTENT, so the count this returns is evidence rather than noise. The uploaded results
    # directory holds the collector's staging copy as well as the final report --
    # `_<machine>_<timestamp>/In/<machine>/coverage.cobertura.xml` beside `<guid>/coverage.cobertura.xml` --
    # and on the runner the two are BYTE-IDENTICAL (measured, run 33583653752, both 1 639 264 bytes, both
    # `lines-covered="4550"`). The union was already immune to the duplicate; the printed report count was
    # not, and read as two tiers' worth of independent evidence when it is one document twice.
    for path in found:
        with open(path, "rb") as handle:
            digest = hashlib.sha256(handle.read()).hexdigest()
        if digest in seen:
            continue
        seen.add(digest)
        reports.append(path)

    problems: list[str] = []
    for report in reports:
        root = ET.parse(report).getroot()
        # Per document, so the assertion below compares like with like. Merged straight into the
        # tier's dicts afterwards -- the second loop costs one pass over a dict, not over the XML.
        own_lines: dict[tuple[str, int], int] = {}
        for klass in root.iter("class"):
            filename = normalise(klass.get("filename", ""))
            for line in klass.iter("line"):
                number = int(line.get("number", 0))
                key = (filename, number)
                hits = int(line.get("hits", 0))
                own_lines[key] = max(own_lines.get(key, 0), hits)

                # `condition-coverage` reads e.g. `50% (1/2)`. Absent on a non-branch line.
                # COVERLET WRITES `branch="True"`, CAPITALISED -- .NET's bool formatting, not XML's
                # `xs:boolean`. Comparing against the lowercase form silently zeroes every branch figure,
                # which is what the first draft of this file did and what running it caught.
                condition = line.get("condition-coverage")
                if (line.get("branch") or "").lower() == "true" and condition and "(" in condition:
                    covered_text, _, total_text = condition.split("(", 1)[1].rstrip(")").partition("/")
                    covered, total = int(covered_text), int(total_text)
                    prior = branches.get(key, (0, 0))
                    branches[key] = (max(prior[0], covered), max(prior[1], total))

        problems.extend(check_against_root(report, root, own_lines))
        for key, hits in own_lines.items():
            lines[key] = max(lines.get(key, 0), hits)

    files = {filename for filename, _ in lines}
    return lines, branches, files, len(reports), problems


def rates(lines, branches) -> tuple[float, float, int, int, int, int]:
    lines_valid = len(lines)
    lines_covered = sum(1 for hits in lines.values() if hits > 0)
    branches_valid = sum(total for _, total in branches.values())
    branches_covered = sum(covered for covered, _ in branches.values())
    line_rate = 100.0 * lines_covered / lines_valid if lines_valid else 0.0
    branch_rate = 100.0 * branches_covered / branches_valid if branches_valid else 0.0
    return line_rate, branch_rate, lines_covered, lines_valid, branches_covered, branches_valid


def main(argv: list[str]) -> int:
    if len(argv) < 2 or any("=" not in arg for arg in argv[1:]):
        print("::error::usage: check-coverage-floor.py <tier>=<directory> [<tier>=<directory> ...]")
        return 2

    # REQUIRED, never defaulted. A floor that falls back to 0 when the variable is missing is a gate that
    # cannot fail, reached by deleting one line of YAML -- exactly the shape this script's own subject is.
    if "MINIMUM_LINE_COVERAGE" not in os.environ:
        print("::error::MINIMUM_LINE_COVERAGE is not set. It is the floor; defaulting it to 0 would make "
              "this gate unable to fail.")
        return 2
    floor = int(os.environ["MINIMUM_LINE_COVERAGE"])
    tiers = [arg.split("=", 1) for arg in argv[1:]]

    merged_lines: dict[tuple[str, int], int] = {}
    merged_branches: dict[tuple[str, int], tuple[int, int]] = {}
    per_tier = []
    file_sets = []
    failed = False

    for name, directory in tiers:
        lines, branches, files, report_count, problems = read_tier(directory)
        # ASSERTED BEFORE THE FIGURES ARE USED, and it does not short-circuit the floor: a run with
        # both faults reports both, because `failed` accumulates and the floor is still evaluated on
        # every run where the parse agrees. A guard that made the floor unreachable would be the same
        # defect this card exists to remove, wearing the fix's clothes.
        for problem in problems:
            print(f"::error::Parse mismatch in the '{name}' tier. {problem} An under-read shrinks the "
                  "numerator and the denominator together, so the reported rate RISES while coverage "
                  "falls; see check_against_root() in scripts/check-coverage-floor.py.")
            failed = True
        if not report_count:
            listing = "\n".join(sorted(glob.glob(os.path.join(directory, "**", "*"), recursive=True))[:40])
            print(f"::error::No cobertura report for the '{name}' tier under {directory}. That job must run "
                  'with --collect:"XPlat Code Coverage" and upload its results directory.')
            print(f"{name} artifact contained:\n{listing or '(nothing)'}")
            failed = True
            continue

        per_tier.append((name, rates(lines, branches), len(files), report_count))
        file_sets.append((name, files))
        for key, hits in lines.items():
            merged_lines[key] = max(merged_lines.get(key, 0), hits)
        for key, (covered, total) in branches.items():
            prior = merged_branches.get(key, (0, 0))
            merged_branches[key] = (max(prior[0], covered), max(prior[1], total))

    if failed:
        return 1

    # Two tiers that share no source file did not merge, they concatenated. See the header.
    if len(file_sets) > 1:
        shared = set.intersection(*(files for _, files in file_sets))
        if not shared:
            print("::error::The tiers' reports share no source file, so their union is a concatenation and "
                  "every merged figure below would be an average wearing a union's name. Most likely the two "
                  "reports spell paths differently; see normalise() in scripts/check-coverage-floor.py.")
            return 1

    merged = rates(merged_lines, merged_branches)
    names = " + ".join(name for name, _ in tiers)

    rows = []
    for name, figures, file_count, report_count in per_tier:
        line_rate, branch_rate, lines_covered, lines_valid, _, _ = figures
        rows.append(f"| `{name}` | {line_rate:.1f}% | {branch_rate:.1f}% | "
                    f"{lines_covered}/{lines_valid} | {file_count} | {report_count} |")
    merged_line, merged_branch, merged_covered, merged_valid, _, _ = merged
    merged_files = len({filename for filename, _ in merged_lines})
    rows.append(f"| **merged** | **{merged_line:.1f}%** | **{merged_branch:.1f}%** | "
                f"{merged_covered}/{merged_valid} | {merged_files} | "
                f"{sum(row[3] for row in per_tier)} |")

    summary = (f"Line coverage {merged_line:.1f}% (floor {floor}%) · branch {merged_branch:.1f}% "
               f"· merged over {names}")
    table = "\n".join([
        "| tier | line | branch | lines covered/valid | files | distinct reports |",
        "|---|---:|---:|---:|---:|---:|",
        *rows,
    ])
    footnote = ("Lines are a true union. Branch is a per-line maximum and therefore a LOWER bound — "
                "cobertura cannot say which arm a tier took. The floor is on line coverage only.")

    print(table)
    print()
    print(summary)
    print(footnote)

    step_summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if step_summary:
        with open(step_summary, "a", encoding="utf-8") as handle:
            handle.write(f"### {summary}\n\n{table}\n\n_{footnote}_\n")

    if merged_line < floor:
        print(f"::error::Merged line coverage {merged_line:.1f}% is below the {floor}% floor.")
        return 1
    return 0


if __name__ == "__main__":
    # The summary line carries `·` and `—`. On a Windows console that is cp1252, printing them raises
    # UnicodeEncodeError and the gate exits non-zero for a reason that has nothing to do with coverage --
    # a local check disagreeing with CI, which is worse than no local check.
    for stream in (sys.stdout, sys.stderr):
        stream.reconfigure(encoding="utf-8", errors="replace")
    sys.exit(main(sys.argv))
