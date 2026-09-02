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
here reproduce coverlet's own `<coverage>` root exactly -- 4554/8127 unit, 5133/8127 integration, the
same 56.0% the gh#431 issue quotes. The per-tier BRANCH figures do not, and cannot: coverlet's root
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
"""

from __future__ import annotations

import glob
import os
import sys
import xml.etree.ElementTree as ET


def normalise(path: str) -> str:
    """Spell a cobertura `filename` the same way whichever runner produced it."""
    return path.replace("\\", "/").lstrip("/")


def read_tier(directory: str):
    """Union one tier's own reports. Returns (lines, branches, files, report_count).

    `lines` maps (file, line) -> hits; `branches` maps (file, line) -> (covered, total).
    A tier normally emits one report, but coverlet emits one per test assembly, so this
    unions within a tier by the same rules it unions across them.
    """
    reports = sorted(glob.glob(os.path.join(directory, "**", "coverage.cobertura.xml"), recursive=True))
    lines: dict[tuple[str, int], int] = {}
    branches: dict[tuple[str, int], tuple[int, int]] = {}

    for report in reports:
        root = ET.parse(report).getroot()
        for klass in root.iter("class"):
            filename = normalise(klass.get("filename", ""))
            for line in klass.iter("line"):
                number = int(line.get("number", 0))
                key = (filename, number)
                hits = int(line.get("hits", 0))
                lines[key] = max(lines.get(key, 0), hits)

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

    files = {filename for filename, _ in lines}
    return lines, branches, files, len(reports)


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
        lines, branches, files, report_count = read_tier(directory)
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
        "| tier | line | branch | lines covered/valid | files | reports |",
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
