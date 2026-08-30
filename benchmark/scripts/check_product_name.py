#!/usr/bin/env python3
"""Reject retired product-name variants outside fingerprinted exceptions."""

from __future__ import annotations

import argparse
import hashlib
import os
import re
import stat
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path


REPOSITORY_ROOT = Path(__file__).resolve().parents[1]
ALLOWLIST_PATH = Path("docs/rename/legacy-name-allowlist.tsv")
MAX_FILE_BYTES = 8 * 1024 * 1024

SEPARATOR = rb"[\s._-]*"
PRODUCT_PATTERN = rb"terra" + SEPARATOR + rb"base" + SEPARATOR + rb"v2"
SHORT_PATTERN = b"tb" + SEPARATOR + b"v2"
LANE_PATTERN = b"v2" + SEPARATOR + b"lane"

RULES = (
    ("retired-product-name", re.compile(PRODUCT_PATTERN, re.IGNORECASE)),
    ("retired-short-prefix", re.compile(SHORT_PATTERN, re.IGNORECASE)),
    ("retired-lane-symbol", re.compile(LANE_PATTERN, re.IGNORECASE)),
)


class CheckError(RuntimeError):
    """A fail-closed repository or allowlist error."""


@dataclass(frozen=True)
class Match:
    """One exact path or UTF-8 text token found by one rule."""

    rule: str
    kind: str
    line: int
    column: int
    value: bytes


@dataclass(frozen=True)
class Allowance:
    """The exact expected state of one exceptional path."""

    expected_matches: int
    fingerprint: str
    reason: str


@dataclass(frozen=True)
class FileScan:
    """The matches and fail-closed issue for one repository path."""

    matches: tuple[Match, ...]
    issue: str | None = None
    nested_repository: str | None = None


def parse_arguments(arguments: list[str]) -> argparse.Namespace:
    """Parses the two explicit scan modes and read-only inventory mode."""

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--tracked-only",
        action="store_true",
        help="scan tracked files only, except that allowlisted paths are still verified",
    )
    parser.add_argument(
        "--inventory",
        action="store_true",
        help="print exact match counts and fingerprints without applying allowances",
    )
    return parser.parse_args(arguments)


def git_paths(arguments: list[str]) -> set[str]:
    """Returns NUL-delimited Git paths without shell or locale parsing."""

    result = subprocess.run(
        ["git", "ls-files", "-z", *arguments],
        cwd=REPOSITORY_ROOT,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        check=False,
    )
    if result.returncode != 0:
        detail = result.stderr.decode("utf-8", errors="replace").strip()
        raise CheckError(f"git ls-files failed: {detail}")

    paths: set[str] = set()
    for encoded_path in result.stdout.split(b"\0"):
        if not encoded_path:
            continue
        try:
            paths.add(encoded_path.decode("utf-8"))
        except UnicodeDecodeError as error:
            raise CheckError("repository path is not valid UTF-8") from error
    return paths


def read_bounded_file(path: Path) -> bytes:
    """Reads at most the configured limit plus one sentinel byte."""

    with path.open("rb") as handle:
        data = handle.read(MAX_FILE_BYTES + 1)
    if len(data) > MAX_FILE_BYTES:
        raise CheckError(f"file exceeds the {MAX_FILE_BYTES}-byte scan limit")
    return data


def read_allowlist() -> dict[str, Allowance]:
    """Parses strict four-column allowance records."""

    absolute_path = REPOSITORY_ROOT / ALLOWLIST_PATH
    try:
        data = read_bounded_file(absolute_path)
    except FileNotFoundError as error:
        raise CheckError(f"{ALLOWLIST_PATH} is missing") from error
    try:
        text = data.decode("utf-8")
    except UnicodeDecodeError as error:
        raise CheckError(f"{ALLOWLIST_PATH} is not valid UTF-8") from error

    allowances: dict[str, Allowance] = {}
    for line_number, line in enumerate(text.splitlines(), start=1):
        if not line or line.startswith("#"):
            continue
        fields = line.split("\t")
        if len(fields) != 4 or any(not field.strip() for field in fields):
            raise CheckError(
                f"{ALLOWLIST_PATH}:{line_number} must contain path, expected match count, "
                "match fingerprint, and reason"
            )
        relative_path, count_text, fingerprint, reason = fields
        try:
            expected_matches = int(count_text, 10)
        except ValueError as error:
            raise CheckError(
                f"{ALLOWLIST_PATH}:{line_number} has an invalid expected match count"
            ) from error
        if expected_matches < 1:
            raise CheckError(
                f"{ALLOWLIST_PATH}:{line_number} has an invalid expected match count"
            )
        if not re.fullmatch(r"[0-9a-f]{64}", fingerprint):
            raise CheckError(
                f"{ALLOWLIST_PATH}:{line_number} has an invalid SHA-256 match fingerprint"
            )
        if relative_path in allowances:
            raise CheckError(f"{ALLOWLIST_PATH}:{line_number} repeats {relative_path}")
        allowances[relative_path] = Allowance(expected_matches, fingerprint, reason)
    return allowances


def collect_matches(value: bytes, kind: str, line: int) -> tuple[Match, ...]:
    """Finds all rules, ordered by byte location and rule order."""

    candidates: list[tuple[int, int, Match]] = []
    for rule_index, (rule, pattern) in enumerate(RULES):
        for found in pattern.finditer(value):
            candidates.append(
                (
                    found.start(),
                    rule_index,
                    Match(rule, kind, line, found.start() + 1, found.group(0)),
                )
            )
    candidates.sort(key=lambda item: (item[0], item[1], item[2].value))
    return tuple(item[2] for item in candidates)


def nested_repository(relative_path: str) -> str | None:
    """Names a nested repository containing a path, if one exists."""

    parent = Path(relative_path).parent
    while parent != Path("."):
        if (REPOSITORY_ROOT / parent / ".git").exists():
            return parent.as_posix()
        parent = parent.parent
    return None


def scan_file(relative_path: str, tracked: bool) -> FileScan:
    """Scans one bounded regular UTF-8 file and fails closed on odd types."""

    nested = nested_repository(relative_path)
    if nested is not None:
        return FileScan((), nested_repository=nested)

    absolute_path = REPOSITORY_ROOT / relative_path
    try:
        metadata = absolute_path.lstat()
    except FileNotFoundError:
        issue = "tracked path is missing" if tracked else "allowlisted path is missing"
        return FileScan((), issue=issue)

    mode = metadata.st_mode
    if stat.S_ISDIR(mode):
        return FileScan(())
    if stat.S_ISLNK(mode):
        return FileScan((), issue="symbolic link requires explicit review")
    if not stat.S_ISREG(mode):
        return FileScan((), issue="special file requires explicit review")

    try:
        data = read_bounded_file(absolute_path)
    except CheckError as error:
        return FileScan((), issue=str(error))
    if b"\0" in data:
        return FileScan(())
    try:
        data.decode("utf-8")
    except UnicodeDecodeError:
        return FileScan(())

    matches: list[Match] = []
    for line_number, line in enumerate(data.splitlines(), start=1):
        matches.extend(collect_matches(line, "text", line_number))
    return FileScan(tuple(matches))


def fingerprint_matches(relative_path: str, matches: tuple[Match, ...]) -> str:
    """Binds a path, rule, location, and exact token bytes."""

    digest = hashlib.sha256()
    digest.update(b"terrabase.product-name-allowance/v1\0")
    digest.update(relative_path.encode("utf-8"))
    digest.update(b"\0")
    for match in matches:
        digest.update(match.rule.encode("ascii"))
        digest.update(b"\0")
        digest.update(match.kind.encode("ascii"))
        digest.update(b"\0")
        digest.update(str(match.line).encode("ascii"))
        digest.update(b"\0")
        digest.update(str(match.column).encode("ascii"))
        digest.update(b"\0")
        digest.update(match.value)
        digest.update(b"\0")
    return digest.hexdigest()


def format_match(relative_path: str, path_kind: str, match: Match) -> str:
    """Renders a stable diagnostic without trusting matched text as markup."""

    token = match.value.decode("utf-8", errors="backslashreplace")
    if match.kind == "path":
        location = f"path byte {match.column}"
    else:
        location = f"{match.line}:{match.column}"
    return f"{relative_path}:{location} [{path_kind} {match.rule}: {token!r}]"


def run(arguments: list[str]) -> int:
    """Runs the repository check or read-only inventory."""

    options = parse_arguments(arguments)
    allowances = read_allowlist()
    tracked_paths = git_paths(["--cached"])
    untracked_paths = git_paths(["--others", "--exclude-standard"])

    repository_paths = set(tracked_paths)
    if not options.tracked_only:
        repository_paths.update(untracked_paths)
    repository_paths.update(allowances)

    results: dict[str, FileScan] = {}
    nested_repositories: set[str] = set()
    for relative_path in sorted(repository_paths):
        path_matches = collect_matches(relative_path.encode("utf-8"), "path", 0)
        file_scan = scan_file(relative_path, relative_path in tracked_paths)
        if file_scan.nested_repository is not None:
            nested_repositories.add(file_scan.nested_repository)
        results[relative_path] = FileScan(
            path_matches + file_scan.matches,
            issue=file_scan.issue,
            nested_repository=file_scan.nested_repository,
        )

    if options.inventory:
        issues = []
        for relative_path, result in results.items():
            if result.matches:
                print(
                    f"{relative_path}\t{len(result.matches)}\t"
                    f"{fingerprint_matches(relative_path, result.matches)}"
                )
            if result.issue is not None:
                issues.append(f"{relative_path}: {result.issue}")
        for issue in sorted(issues):
            print(f"product-name inventory: {issue}", file=sys.stderr)
        return 1 if issues else 0

    violations: list[str] = []
    for relative_path, result in results.items():
        path_kind = "tracked" if relative_path in tracked_paths else "untracked"
        allowance = allowances.get(relative_path)
        if allowance is None:
            violations.extend(
                format_match(relative_path, path_kind, match) for match in result.matches
            )
            if result.issue is not None:
                violations.append(f"{relative_path} [{path_kind} {result.issue}]")
            continue

        actual_fingerprint = fingerprint_matches(relative_path, result.matches)
        if result.issue is not None:
            violations.append(f"{relative_path} [allowance cannot hide {result.issue}]")
        elif (
            len(result.matches) != allowance.expected_matches
            or actual_fingerprint != allowance.fingerprint
        ):
            violations.append(
                f"{ALLOWLIST_PATH} [allowance mismatch for {relative_path}: expected "
                f"{allowance.expected_matches} matches and {allowance.fingerprint}, found "
                f"{len(result.matches)} and {actual_fingerprint}]"
            )

    for nested in sorted(nested_repositories):
        print(f"Skipped nested repository: {nested}")

    if violations:
        print("Retired product-name variants detected:", file=sys.stderr)
        for violation in sorted(set(violations)):
            print(f"  - {violation}", file=sys.stderr)
        return 1

    scope = "tracked files" if options.tracked_only else "tracked and non-ignored files"
    print(f"Product-name migration check passed ({scope}).")
    return 0


def main() -> int:
    """Converts expected operational errors into one-line diagnostics."""

    try:
        return run(sys.argv[1:])
    except (CheckError, OSError) as error:
        print(f"product-name migration check: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
