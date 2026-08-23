#!/usr/bin/env python3
"""Validate explicit V1 citations in TerraBaseV2 parity contract scopes.

The contract scope remains readable prose, but every source-backed row must
also carry machine-checkable citations in this form::

    [v1:src/path/file.cs::Symbol]
    [v1:test/path/file.cs::TestName]

Paths are resolved against a caller-supplied exact V1 checkout. A test path
may contain a glob, but it must resolve to exactly one file so the citation
cannot silently become ambiguous. V2-only rows use a charter citation::

    [charter:specs/2026-08-23-terrabase-v2-goal.md::Section heading]

This validator does not treat a discovery evidence ID as a source citation.
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from pathlib import Path
from typing import Any


PINNED_V1_COMMIT = "fa99b9257776dbe625e6a2388c74c9ce916637ee"
V1_CITATION = re.compile(r"\[v1:([^\]]+?)::([^\]]+)\]")
CHARTER_CITATION = re.compile(r"\[charter:([^\]]+?)::([^\]]+)\]")
GLOB_CHARS = set("*?[")


class CitationError(ValueError):
    """A contract citation cannot be reproduced from the supplied roots."""


def read_json(path: Path) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise CitationError(f"unable to read JSON {path}: {error}") from error


def resolve_one(root: Path, relative: str, *, allow_glob: bool, label: str) -> Path:
    if not relative or Path(relative).is_absolute():
        raise CitationError(f"{label} must be a relative path")
    if ".." in Path(relative).parts:
        raise CitationError(f"{label} must stay inside its supplied root")
    if any(character in relative for character in GLOB_CHARS):
        if not allow_glob:
            raise CitationError(f"{label} source citations may not use globs")
        matches = sorted(root.glob(relative))
        if len(matches) != 1:
            raise CitationError(f"{label} glob matched {len(matches)} files: {relative}")
        candidate = matches[0]
    else:
        candidate = root / relative
    if not candidate.is_file():
        raise CitationError(f"{label} does not name an existing file: {relative}")
    return candidate


def validate_v1_citation(root: Path, relative: str, symbol: str, row_id: str) -> None:
    if not relative.startswith(("src/", "test/")):
        raise CitationError(f"{row_id} V1 citation must start with src/ or test/: {relative}")
    candidate = resolve_one(root, relative, allow_glob=relative.startswith("test/"), label=f"{row_id} V1 citation")
    try:
        contents = candidate.read_text(encoding="utf-8")
    except UnicodeDecodeError as error:
        raise CitationError(f"{row_id} V1 citation is not UTF-8 text: {relative}") from error
    if not symbol.strip():
        raise CitationError(f"{row_id} V1 citation symbol is empty: {relative}")
    if symbol not in contents:
        raise CitationError(f"{row_id} V1 symbol is absent from {relative}: {symbol}")


def verify_pinned_commit(v1_root: Path) -> None:
    git_dir = v1_root / ".git"
    if not git_dir.exists():
        return
    try:
        result = subprocess.run(
            ["git", "-C", str(v1_root), "rev-parse", "HEAD"],
            check=False,
            capture_output=True,
            text=True,
        )
    except OSError as error:
        raise CitationError(f"unable to inspect V1 commit: {error}") from error
    if result.returncode != 0:
        raise CitationError(f"unable to inspect V1 commit: {result.stderr.strip()}")
    actual = result.stdout.strip()
    if actual != PINNED_V1_COMMIT:
        raise CitationError(f"V1 root is {actual}, expected pinned commit {PINNED_V1_COMMIT}")


def iter_contract_items(root: Path) -> list[dict[str, Any]]:
    contracts = root / "parity" / "contracts"
    items: list[dict[str, Any]] = []
    for path in sorted(contracts.glob("*.json")):
        if path.name == "manifest.json":
            continue
        fragment = read_json(path)
        for item in fragment.get("items", []):
            items.append(item)
    return items


def validate(root: Path, v1_root: Path, workspace_root: Path | None) -> int:
    verify_pinned_commit(v1_root)
    errors: list[str] = []
    total = 0
    v1_rows = 0
    charter_rows = 0
    for item in iter_contract_items(root):
        total += 1
        row_id = item.get("id", "<missing-id>")
        scope = item.get("v1_evidence", {}).get("scope", "")
        v1_matches = list(V1_CITATION.finditer(scope))
        charter_matches = list(CHARTER_CITATION.finditer(scope))
        if v1_matches:
            v1_rows += 1
            for match in v1_matches:
                try:
                    validate_v1_citation(v1_root, match.group(1), match.group(2), row_id)
                except CitationError as error:
                    errors.append(str(error))
        elif charter_matches:
            charter_rows += 1
            if workspace_root is None:
                errors.append(f"{row_id} charter citation requires --workspace-root")
            else:
                for match in charter_matches:
                    try:
                        resolve_one(workspace_root, match.group(1), allow_glob=False, label=f"{row_id} charter citation")
                        charter_path = workspace_root / match.group(1)
                        if match.group(2) not in charter_path.read_text(encoding="utf-8"):
                            raise CitationError(
                                f"{row_id} charter symbol is absent from {match.group(1)}: {match.group(2)}"
                            )
                    except (CitationError, OSError) as error:
                        errors.append(str(error))
            if "No claim that V1" not in scope:
                errors.append(f"{row_id} charter-backed row must state 'No claim that V1'")
        else:
            errors.append(f"{row_id} requires an explicit V1 or charter citation")

    if errors:
        for error in errors:
            print(f"citation invalid: {error}", file=sys.stderr)
        return 1
    print(f"V1 citation audit passed: {total} rows, {v1_rows} V1-backed, {charter_rows} charter-backed")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--v1-root", type=Path, required=True)
    parser.add_argument("--workspace-root", type=Path)
    args = parser.parse_args()
    try:
        return validate(args.root.resolve(), args.v1_root.resolve(), args.workspace_root.resolve() if args.workspace_root else None)
    except CitationError as error:
        print(f"citation invalid: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
