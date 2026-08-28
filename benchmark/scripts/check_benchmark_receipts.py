#!/usr/bin/env python3
"""Fail closed on a performance receipt with no objective-status line.

The standing discipline of PD-2026-08-26-03 D3: every performance-relevant
run records its objective status -- PostgreSQL-parity-or-better: met / not
met / inconclusive -- and a run that regressed against the objective names
the owner of its follow-up. This checker enforces the recording, not the
outcome: ``inconclusive`` is a valid, honest status, and a receipt saying
so passes. A receipt with no status line at all is a performance statement
nobody can audit, so the check fails closed on it.

Rules, one shape each:

* every ``*.md`` under the receipts root carries exactly one canonical line
  ``PostgreSQL-parity-or-better: <status>`` where ``<status>`` is one of
  ``met``, ``not met``, ``inconclusive``;
* a receipt may quote the vocabulary inside fenced code blocks while it
  explains the rule, so fenced blocks are read but never counted: a
  canonical line inside them documents the shape and does not satisfy or
  duplicate the receipt's own status;
* a line beginning ``verdict:`` carries one of ``v2-faster``,
  ``no-improvement``, ``v2-regression``, ``inconclusive``;
* a receipt whose status is ``not met`` also names a regression follow-up
  owner on a line beginning ``regression follow-up owner:``;
* a receipt that is not valid UTF-8 is a problem line, not a traceback.
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

OBJECTIVE_STATUSES = ("met", "not met", "inconclusive")
VERDICTS = ("v2-faster", "no-improvement", "v2-regression", "inconclusive")

OBJECTIVE_LINE = re.compile(
    r"^PostgreSQL-parity-or-better:\s*(?P<status>.+?)\s*$", re.MULTILINE
)
VERDICT_LINE = re.compile(r"^verdict:\s*(?P<verdict>.+?)\s*$", re.MULTILINE)
REGRESSION_OWNER_LINE = re.compile(r"^regression follow-up owner:\s*(?P<owner>.+?)\s*$", re.MULTILINE)


def receipt_files(root: Path) -> list[Path]:
    """Every receipt under ``root``, sorted for stable output."""
    if not root.is_dir():
        return []
    return sorted(path for path in root.glob("*.md") if path.is_file())


def strip_fenced_blocks(text: str) -> tuple[str, str]:
    """Splits ``text`` into (scannable, fenced) with code blocks removed.

    A receipt that documents the discipline quotes the canonical line
    inside a fenced block. Those quotations describe the rule; they are
    neither the receipt's own status nor a duplicate of it, so they are read
    out of the scan rather than counted.
    """
    scannable: list[str] = []
    fenced: list[str] = []
    inside = False
    for line in text.splitlines(keepends=True):
        if line.lstrip().startswith("```"):
            inside = not inside
            scannable.append(line)
            continue
        (fenced if inside else scannable).append(line)
    return "".join(scannable), "".join(fenced)


def check_receipt(path: Path, relative: str) -> list[str]:
    """Returns the problems with one receipt, empty when it is compliant."""
    # Read bytes and decode defensively: a receipt some editor wrote in a
    # non-UTF-8 encoding is an auditability problem like any other, and it
    # must surface as this checker's problem-line output rather than as a
    # UnicodeDecodeError traceback with the wrong diagnostic shape.
    try:
        text = path.read_bytes().decode("utf-8")
    except UnicodeDecodeError:
        return [f"{relative}: receipt is not valid UTF-8"]
    scannable, _fenced = strip_fenced_blocks(text)
    problems: list[str] = []

    statuses = [match.group("status") for match in OBJECTIVE_LINE.finditer(scannable)]
    if not statuses:
        problems.append(f"{relative}: no canonical objective-status line")
    else:
        if len(statuses) > 1:
            problems.append(
                f"{relative}: {len(statuses)} objective-status lines, exactly one is required"
            )
        for status in statuses:
            if status not in OBJECTIVE_STATUSES:
                expected = ", ".join(OBJECTIVE_STATUSES)
                problems.append(
                    f"{relative}: objective status {status!r} is outside the vocabulary ({expected})"
                )
        if statuses == ["not met"] and not REGRESSION_OWNER_LINE.search(scannable):
            problems.append(
                f"{relative}: status 'not met' without a 'regression follow-up owner:' line"
            )

    for match in VERDICT_LINE.finditer(scannable):
        verdict = match.group("verdict")
        if verdict not in VERDICTS:
            expected = ", ".join(VERDICTS)
            problems.append(
                f"{relative}: verdict {verdict!r} is outside the vocabulary ({expected})"
            )
    return problems


def main(argv: list[str] | None = None) -> int:
    """Entry point: 0 when every receipt is auditable, 1 otherwise."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--receipts-root",
        default=str(Path(__file__).resolve().parents[1] / "docs" / "evidence" / "benchmarks"),
        help="directory holding the benchmark receipts (default: docs/evidence/benchmarks)",
    )
    arguments = parser.parse_args(argv)

    root = Path(arguments.receipts_root)
    receipts = receipt_files(root)
    if not receipts:
        print(f"benchmark receipt discipline: no receipts under {root}", file=sys.stderr)
        return 1

    problems: list[str] = []
    for path in receipts:
        problems.extend(check_receipt(path, path.name))

    if problems:
        for problem in problems:
            print(f"benchmark receipt discipline: {problem}", file=sys.stderr)
        return 1

    print(
        f"benchmark receipt discipline passed: {len(receipts)} receipts, "
        "every one carries an objective-status line"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
