#!/usr/bin/env python3
"""Audit reproducible V1 and charter evidence in parity contract scopes.

The audit has two phases. ``contract`` mode validates the pinned checkout,
source anchors, test anchors, and explicit test-gap accounting. It completes
with unresolved gaps so W2 can record the work still owed. ``strict`` mode is
the release/qualification gate and fails closed while any gap remains.

Citation tokens are deliberately small and unambiguous::

    [v1:src/path/file.cs::TypeOrMember]
    [v1:src/path/file.cs]
    [v1:test:test/path/file.cs#TestTypeOrMethod]
    [v1:test:test/path/file.cs]
    [v1:test-gap:concrete reason]

Source and test symbols are checked as declarations, not incidental
substrings. A path-only token is an explicit file anchor and still requires
the exact file to exist. Paths may contain spaces; they may not escape the
supplied root.

An open ``[v1:test-gap:...]`` marker may carry a parent adjudication recorded
in ``parity/adjudications.json`` (the ``adjudicated-no-v1-test`` disposition).
The adjudication is an input, not a generated artifact: it maps a contract
row id to a ``decision_id`` matching ``PD-YYYY-MM-DD-NN``, a ``search_record``
of the form ``<repository-relative path>#<section>`` that must resolve to a
committed file containing that section, a ``v2_proof_obligation`` naming the
owed V2-side proof, and an ``owner``. Strict mode passes with such a gap
reported as ``adjudicated``: an obligation still open, never a silent pass.
Missing, incomplete, or invalid adjudication fails exactly like an unresolved
gap, and an adjudication that matches no open gap is itself an error.
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from pathlib import Path
from typing import Any, Iterator


SOURCE_CITATION = "source"
TEST_CITATION = "test"
TEST_GAP = "test-gap"
CHARTER_CITATION = "charter"
GLOB_CHARS = set("*?[")

ADJUDICATION_PATH = ("parity", "adjudications.json")
ADJUDICATION_FIELDS = ("decision_id", "search_record", "v2_proof_obligation", "owner")
DECISION_ID_PATTERN = re.compile(r"PD-[0-9]{4}-[0-9]{2}-[0-9]{2}-[0-9]{2}")


class CitationError(ValueError):
    """A contract citation cannot be reproduced from the supplied roots."""


def read_json(path: Path) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise CitationError(f"unable to read JSON {path}: {error}") from error


def run_git(root: Path, *arguments: str) -> str:
    try:
        result = subprocess.run(
            ["git", "-C", str(root), *arguments],
            check=False,
            capture_output=True,
            text=True,
        )
    except OSError as error:
        raise CitationError(f"unable to inspect V1 Git worktree: {error}") from error
    if result.returncode != 0:
        detail = result.stderr.strip() or result.stdout.strip()
        raise CitationError(f"V1 root is not a Git worktree ({' '.join(arguments)}): {detail}")
    return result.stdout.strip()


def verify_pinned_commit(v1_root: Path, expected_commit: str) -> Path:
    """Require a clean worktree rooted at the exact commit.

    ``.git`` may be a file for a linked Git worktree, so Git is the authority
    for worktree identity.  The canonical path is returned so later file
    resolution cannot escape through a symlinked root.
    """

    try:
        canonical_root = v1_root.resolve(strict=True)
    except OSError as error:
        raise CitationError(f"unable to resolve V1 root {v1_root}: {error}") from error
    if not canonical_root.is_dir():
        raise CitationError(f"V1 root is not a directory: {v1_root}")
    git_top = Path(run_git(canonical_root, "rev-parse", "--show-toplevel")).resolve()
    if git_top != canonical_root:
        raise CitationError(f"V1 root must be the Git worktree root: {v1_root} (Git root: {git_top})")
    actual = run_git(canonical_root, "rev-parse", "HEAD")
    if actual != expected_commit:
        raise CitationError(f"V1 root is {actual}, expected pinned commit {expected_commit}")
    status = run_git(canonical_root, "status", "--porcelain=v1", "--untracked-files=all")
    if status:
        changed = "; ".join(status.splitlines()[:3])
        if len(status.splitlines()) > 3:
            changed += "; ..."
        raise CitationError(f"V1 root must be a clean Git worktree; changes found: {changed}")
    return canonical_root


def canonical_file(root: Path, candidate: Path, label: str) -> Path:
    """Resolve a citation and require its real path to remain under ``root``."""

    try:
        canonical_root = root.resolve(strict=True)
        # Keep a canonical path for containment even when the final file is
        # absent; resolve_one then emits its stable missing-file diagnostic.
        canonical_candidate = candidate.resolve(strict=False)
    except OSError as error:
        raise CitationError(f"{label} cannot be resolved: {error}") from error
    try:
        canonical_candidate.relative_to(canonical_root)
    except ValueError as error:
        raise CitationError(f"{label} resolves outside its supplied root: {candidate}") from error
    return canonical_candidate


def resolve_one(root: Path, relative: str, *, allow_glob: bool, label: str) -> Path:
    if not relative or Path(relative).is_absolute():
        raise CitationError(f"{label} must be a relative path")
    path = Path(relative)
    if ".." in path.parts:
        raise CitationError(f"{label} must stay inside its supplied root")
    if any(character in relative for character in GLOB_CHARS):
        if not allow_glob:
            raise CitationError(f"{label} may not use globs")
        matches: list[Path] = []
        for candidate in sorted(root.glob(relative)):
            canonical_candidate = canonical_file(root, candidate, label)
            if canonical_candidate.is_file():
                matches.append(canonical_candidate)
        if len(matches) != 1:
            raise CitationError(f"{label} glob matched {len(matches)} files: {relative}")
        candidate = matches[0]
    else:
        candidate = root / relative
        candidate = canonical_file(root, candidate, label)
    if not candidate.is_file():
        raise CitationError(f"{label} does not name an existing file: {relative}")
    return candidate


def citation_tokens(scope: str) -> Iterator[tuple[str, str, str | None]]:
    """Yield ``(kind, path-or-reason, symbol)`` from bracketed scope tokens."""

    for match in re.finditer(r"\[([^\]\r\n]+)\]", scope):
        body = match.group(1)
        if body.startswith("v1:src/"):
            value = body[len("v1:") :]
            path, separator, symbol = value.partition("::")
            yield SOURCE_CITATION, path, symbol if separator else None
        elif body.startswith("v1:test:"):
            value = body[len("v1:test:") :]
            path, separator, symbol = value.partition("#")
            yield TEST_CITATION, path, symbol if separator else None
        elif body.startswith("v1:test-gap:"):
            reason = body[len("v1:test-gap:") :].strip()
            if not reason:
                raise CitationError("V1 test-gap citation must include a concrete reason")
            yield TEST_GAP, reason, None
        elif body.startswith("charter:"):
            value = body[len("charter:") :]
            path, separator, heading = value.partition("::")
            if not separator or not path or not heading.strip():
                raise CitationError(f"malformed charter citation: [{body}]")
            yield CHARTER_CITATION, path, heading
        elif body.startswith("v1:") or body.startswith("charter"):
            raise CitationError(f"malformed citation token: [{body}]")


def mask_non_code(contents: str) -> str:
    """Mask comments and quoted literals while preserving line/column shape."""

    output: list[str] = []
    index = 0
    state = "code"
    while index < len(contents):
        current = contents[index]
        following = contents[index + 1] if index + 1 < len(contents) else ""
        if state == "code":
            if current == "/" and following == "/":
                output.extend((" ", " "))
                index += 2
                state = "line-comment"
                continue
            if current == "/" and following == "*":
                output.extend((" ", " "))
                index += 2
                state = "block-comment"
                continue
            if current == '"':
                output.append(" ")
                index += 1
                state = "string"
                continue
            if current == "'":
                output.append(" ")
                index += 1
                state = "char"
                continue
            output.append(current)
            index += 1
            continue
        if state == "line-comment":
            if current in "\r\n":
                output.append(current)
                state = "code"
            else:
                output.append(" ")
            index += 1
            continue
        if state == "block-comment":
            if current == "*" and following == "/":
                output.extend((" ", " "))
                index += 2
                state = "code"
            else:
                output.append(current if current in "\r\n" else " ")
                index += 1
            continue
        if state in {"string", "char"}:
            if current in "\r\n":
                output.append(current)
                # Regular C# literals cannot span a line. Resetting here
                # prevents a malformed fixture from hiding later declarations.
                state = "code"
                index += 1
            elif current == "\\":
                output.append(" ")
                if following:
                    output.append(" " if following not in "\r\n" else following)
                    index += 2
                else:
                    index += 1
            elif (state == "string" and current == '"') or (state == "char" and current == "'"):
                output.append(" ")
                index += 1
                state = "code"
            else:
                output.append(" ")
                index += 1
    return "".join(output)


def declaration_exists(contents: str, symbol: str) -> bool:
    """Match a declaration-shaped C# type/member anchor.

    This is intentionally a declaration-shape check, not a C# parser.  The
    prefix rules reject expression contexts that commonly produce incidental
    matches while accepting the type/member forms used by the V1 contracts.
    """

    pieces = [piece.strip().split("<", 1)[0].strip() for piece in symbol.split(".") if piece.strip()]
    if not pieces or any(not re.fullmatch(r"@?[A-Za-z_][A-Za-z0-9_]*", piece) for piece in pieces):
        return False

    code = mask_non_code(contents)
    type_declaration = re.compile(
        rf"\b(?:class|interface|struct|enum|record(?:\s+(?:class|struct))?|delegate)\s+@?{re.escape(pieces[0])}\b"
    )
    if len(pieces) > 1 and not type_declaration.search(code):
        return False

    # Splitting at declaration delimiters keeps matching linear even for the
    # very large V1 source files. It also makes one-line fixture declarations
    # behave like their formatted counterparts.
    statements = re.sub(r"[{};]", "\n", code).splitlines()
    expression_keywords = {
        "as",
        "await",
        "break",
        "case",
        "catch",
        "continue",
        "default",
        "do",
        "else",
        "finally",
        "fixed",
        "for",
        "foreach",
        "goto",
        "if",
        "is",
        "lock",
        "nameof",
        "new",
        "return",
        "sizeof",
        "stackalloc",
        "switch",
        "throw",
        "try",
        "typeof",
        "unchecked",
        "using",
        "var",
        "when",
        "while",
        "yield",
    }

    def declaration_prefix(before: str) -> str | None:
        # Attributes and preprocessor directives may precede a declaration;
        # they do not make an expression into a declaration.
        cleaned = re.sub(r"\[[^\]\r\n]*\]", " ", before)
        cleaned = re.sub(r"(?:^|\n)\s*#.*(?:\n|$)", " ", cleaned)
        cleaned = cleaned.strip()
        if not cleaned or "=" in cleaned or cleaned.endswith((".", "?.", "::", "->")):
            return None
        if re.search(r"[()]", cleaned):
            # Tuple return types are parenthesized in the declaration prefix,
            # e.g. ``(T Entity, string ChangeVector) ConditionalLoad<T>``.
            # Calls and control-flow expressions do not have a balanced
            # parenthesized suffix before the referenced name.
            depth = 0
            suffix_start: int | None = None
            for index in range(len(cleaned) - 1, -1, -1):
                character = cleaned[index]
                if character == ")":
                    depth += 1
                elif character == "(":
                    depth -= 1
                    if depth == 0:
                        suffix_start = index
                        break
                    if depth < 0:
                        return None
            if suffix_start is None or re.search(r"[()]", cleaned[:suffix_start]):
                return None
        words = re.findall(r"@?[A-Za-z_][A-Za-z0-9_]*", cleaned)
        if not words or any(word.lstrip("@").lower() in expression_keywords for word in words):
            return None
        return cleaned

    for piece in pieces if len(pieces) == 1 else pieces[1:]:
        name = re.escape(piece)
        found = False
        for statement in statements:
            statement = statement.strip()
            match = re.search(rf"(?<![A-Za-z0-9_])@?{name}(?![A-Za-z0-9_])", statement)
            if match is None:
                continue
            before = statement[: match.start()].strip()
            after = statement[match.end() :].lstrip()
            # A type declaration is valid on its own, including a single
            # symbol citation. The type check above already handles dotted
            # symbols, but this branch keeps constructors and nested types
            # useful too.
            if re.search(
                rf"\b(?:class|interface|struct|enum|record(?:\s+(?:class|struct))?|delegate)\s+@?{name}\b",
                statement,
            ):
                found = True
                break
            prefix = declaration_prefix(before)
            # Methods need a return type/modifier before the name and an
            # opening parenthesis after it. Calls have an expression keyword,
            # assignment, receiver, or no declaration prefix and are rejected.
            if after.startswith("(") or re.match(r"<[^>\r\n]+>\s*\(", after):
                if prefix is not None:
                    found = True
                    break
            # Properties and fields have a type/modifier prefix before the
            # name and a declaration delimiter, expression body, initializer,
            # or statement boundary after it. A bare prose occurrence has no
            # such prefix.
            if after.startswith(("=>", "=", ":", ",")) or not after:
                if prefix is not None:
                    found = True
                    break
        if not found:
            return False
    return True


def validate_declaration(root: Path, relative: str, symbol: str | None, row_id: str, kind: str) -> None:
    label = f"{row_id} V1 {kind} citation"
    prefix = "src/" if kind == SOURCE_CITATION else "test/"
    if not relative.startswith(prefix):
        raise CitationError(f"{label} must start with {prefix}: {relative}")
    candidate = resolve_one(root, relative, allow_glob=kind == TEST_CITATION, label=label)
    if symbol is None:
        return
    if not symbol.strip():
        raise CitationError(f"{label} symbol is empty: {relative}")
    try:
        contents = candidate.read_text(encoding="utf-8")
    except UnicodeDecodeError as error:
        raise CitationError(f"{label} is not UTF-8 text: {relative}") from error
    if not declaration_exists(contents, symbol):
        raise CitationError(f"{row_id} V1 {kind} declaration is absent from {relative}: {symbol}")


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


def expected_commit_from_manifest(root: Path) -> str:
    manifest = read_json(root / "parity" / "contracts" / "manifest.json")
    expected = manifest.get("source_commit")
    if not isinstance(expected, str) or not re.fullmatch(r"[0-9a-f]{40}", expected):
        raise CitationError("parity/contracts/manifest.json has no valid source_commit")
    return expected


def write_report(path: Path | None, report: dict[str, Any]) -> None:
    if path is None:
        return
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(report, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    except OSError as error:
        raise CitationError(f"unable to write V1 test-gap report {path}: {error}") from error


def load_adjudications(root: Path, errors: list[str]) -> dict[str, dict[str, str]]:
    """Read ``parity/adjudications.json`` and admit only complete entries.

    Every entry must carry a parent decision id matching ``PD-YYYY-MM-DD-NN``,
    a search record of the form ``<repo-relative path>#<section>`` that
    resolves inside the repository and contains that section, a V2-side proof
    obligation, and an owner. Anything else is reported as an error and the
    row's gaps stay unresolved; incomplete evidence never becomes a pass.
    """

    path = root.joinpath(*ADJUDICATION_PATH)
    if not path.is_file():
        return {}
    try:
        payload = read_json(path)
    except CitationError as error:
        errors.append(str(error))
        return {}
    if not isinstance(payload, dict):
        errors.append(f"{path} must be a JSON object")
        return {}
    unknown_top = sorted(set(payload) - {"schema_version", "adjudications"})
    if unknown_top:
        errors.append(f"{path} has unknown top-level field(s): {', '.join(unknown_top)}")
    entries = payload.get("adjudications")
    if not isinstance(entries, dict):
        errors.append(f"{path} must map row ids under 'adjudications'")
        return {}

    adjudications: dict[str, dict[str, str]] = {}
    for row_id, entry in entries.items():
        if not isinstance(entry, dict):
            errors.append(f"adjudication for {row_id} must be an object")
            continue
        missing = [
            field
            for field in ADJUDICATION_FIELDS
            if not isinstance(entry.get(field), str) or not entry[field].strip()
        ]
        unknown = sorted(set(entry) - set(ADJUDICATION_FIELDS))
        if missing:
            errors.append(f"adjudication for {row_id} is missing non-empty field(s): {', '.join(missing)}")
        if unknown:
            errors.append(f"adjudication for {row_id} has unknown field(s): {', '.join(unknown)}")

        decision_id = entry.get("decision_id")
        if isinstance(decision_id, str) and decision_id.strip() and not DECISION_ID_PATTERN.fullmatch(decision_id.strip()):
            errors.append(
                f"adjudication for {row_id} decision_id must match PD-YYYY-MM-DD-NN: {decision_id}"
            )

        search_record = entry.get("search_record")
        if isinstance(search_record, str) and search_record.strip():
            record_path, separator, section = search_record.strip().partition("#")
            if not separator or not record_path or not section.strip():
                errors.append(
                    f"adjudication for {row_id} search record must be '<repo-relative path>#<section>' "
                    f"(found: {search_record})"
                )
            else:
                label = f"{row_id} adjudication search record"
                try:
                    resolved = resolve_one(root, record_path, allow_glob=False, label=label)
                    if section.strip() not in resolved.read_text(encoding="utf-8"):
                        raise CitationError(
                            f"{row_id} adjudication search record section is absent from {record_path}: {section.strip()}"
                        )
                except (CitationError, OSError) as error:
                    errors.append(str(error))

        if missing or unknown:
            continue
        adjudications[str(row_id)] = {field: str(entry[field]).strip() for field in ADJUDICATION_FIELDS}
    return adjudications


def validate(
    root: Path,
    v1_root: Path | None,
    workspace_root: Path | None,
    expected_commit: str | None,
    mode: str,
    gap_report: Path | None,
) -> int:
    if v1_root is None:
        report = {"schema_version": "1.0", "status": "UNRUN", "reason": "no pinned V1 checkout supplied"}
        write_report(gap_report, report)
        print("V1 citation audit UNRUN: no pinned V1 checkout supplied", file=sys.stderr)
        return 1

    try:
        pinned = expected_commit or expected_commit_from_manifest(root)
        v1_root = verify_pinned_commit(v1_root, pinned)
    except CitationError as error:
        report = {"schema_version": "1.0", "status": "UNRUN", "reason": str(error)}
        write_report(gap_report, report)
        raise

    errors: list[str] = []
    adjudications = load_adjudications(root, errors)
    known_rows: set[str] = set()
    adjudicated_rows: set[str] = set()
    gaps: list[dict[str, Any]] = []
    total = 0
    source_citations = 0
    test_citations = 0
    charter_rows = 0
    v1_rows = 0
    for item in iter_contract_items(root):
        total += 1
        row_id = item.get("id", "<missing-id>")
        known_rows.add(str(row_id))
        scope = item.get("v1_evidence", {}).get("scope", "")
        source_matches: list[tuple[str, str, str | None]] = []
        test_matches: list[tuple[str, str, str | None]] = []
        charter_matches: list[tuple[str, str, str | None]] = []
        row_gaps: list[str] = []
        try:
            for kind, value, symbol in citation_tokens(scope):
                if kind == SOURCE_CITATION:
                    source_matches.append((kind, value, symbol))
                elif kind == TEST_CITATION:
                    test_matches.append((kind, value, symbol))
                elif kind == TEST_GAP:
                    row_gaps.append(value)
                elif kind == CHARTER_CITATION:
                    charter_matches.append((kind, value, symbol))
        except CitationError as error:
            errors.append(f"{row_id} {error}")
            continue

        if source_matches:
            v1_rows += 1
            for _, relative, symbol in source_matches:
                source_citations += 1
                try:
                    validate_declaration(v1_root, relative, symbol, row_id, SOURCE_CITATION)
                except CitationError as error:
                    errors.append(str(error))
        elif charter_matches:
            charter_rows += 1
            if workspace_root is None:
                errors.append(f"{row_id} charter citation requires --workspace-root")
            else:
                for _, relative, heading in charter_matches:
                    try:
                        charter_path = resolve_one(workspace_root, relative, allow_glob=False, label=f"{row_id} charter citation")
                        if heading not in charter_path.read_text(encoding="utf-8"):
                            raise CitationError(f"{row_id} charter heading is absent from {relative}: {heading}")
                    except (CitationError, OSError) as error:
                        errors.append(str(error))
            if "No claim that V1" not in scope:
                errors.append(f"{row_id} charter-backed row must state 'No claim that V1'")
        else:
            errors.append(f"{row_id} requires an explicit V1 or charter citation")

        for _, relative, symbol in test_matches:
            test_citations += 1
            try:
                validate_declaration(v1_root, relative, symbol, row_id, TEST_CITATION)
            except CitationError as error:
                errors.append(str(error))
        row_adjudication = adjudications.get(str(row_id))
        if row_gaps and row_adjudication is not None:
            adjudicated_rows.add(str(row_id))
        for reason in row_gaps:
            gap: dict[str, Any] = {"row_id": str(row_id), "reason": reason}
            if row_adjudication is not None:
                gap["adjudicated"] = dict(row_adjudication)
            gaps.append(gap)
        if source_matches and not test_matches and not row_gaps:
            errors.append(f"{row_id} V1-backed row requires an exact test citation or [v1:test-gap:reason]")

    for row_id in adjudications:
        if row_id not in known_rows:
            errors.append(f"adjudication names unknown contract row: {row_id}")
        elif row_id not in adjudicated_rows:
            errors.append(
                f"adjudication for {row_id} does not correspond to an open test gap; remove the stale entry"
            )

    adjudicated_gaps = [gap for gap in gaps if "adjudicated" in gap]
    unresolved_gaps = [gap for gap in gaps if "adjudicated" not in gap]
    if errors:
        status = "failed_invalid_citations"
    elif unresolved_gaps:
        status = "failed_unresolved_test_gaps" if mode == "strict" else "complete_with_gaps"
    elif adjudicated_gaps:
        status = "complete_with_adjudicated_gaps"
    else:
        status = "complete"
    report = {
        "schema_version": "1.0",
        "status": status,
        "mode": mode,
        "source_commit": pinned,
        "counts": {
            "rows": total,
            "v1_backed_rows": v1_rows,
            "charter_backed_rows": charter_rows,
            "source_citations": source_citations,
            "test_citations": test_citations,
            "test_gaps": len(gaps),
            "test_gaps_adjudicated": len(adjudicated_gaps),
            "test_gaps_unresolved": len(unresolved_gaps),
        },
        "gaps": gaps,
    }
    write_report(gap_report, report)

    print(
        f"V1 citation audit: {total} rows, {v1_rows} V1-backed, {charter_rows} charter-backed, "
        f"{source_citations} source citations, {test_citations} test citations, "
        f"{len(gaps)} test gaps ({len(adjudicated_gaps)} adjudicated, {len(unresolved_gaps)} unresolved)"
    )
    for gap in adjudicated_gaps:
        entry = gap["adjudicated"]
        print(
            f"adjudicated test gap {gap['row_id']}: decision {entry['decision_id']}; "
            f"owner {entry['owner']}; V2 proof obligation open; search record {entry['search_record']}"
        )
    if errors:
        for error in errors:
            print(f"citation invalid: {error}", file=sys.stderr)
        return 1
    if unresolved_gaps:
        if mode == "strict":
            print(f"STRICT V1 QUALIFICATION FAILED: {len(unresolved_gaps)} UNRESOLVED TEST GAPS", file=sys.stderr)
            return 1
        print(f"CONTRACT AUDIT COMPLETE WITH {len(unresolved_gaps)} UNRESOLVED TEST GAPS")
        return 0
    if mode == "strict":
        print("STRICT V1 QUALIFICATION PASSED")
        if adjudicated_gaps:
            decisions = ", ".join(
                sorted({gap["adjudicated"]["decision_id"] for gap in adjudicated_gaps})
            )
            print(
                f"ADJUDICATED NO-V1-TEST GAPS: {len(adjudicated_gaps)} "
                f"(V2 proof obligation open per {decisions})"
            )
    else:
        suffix = f" ({len(adjudicated_gaps)} ADJUDICATED)" if adjudicated_gaps else ""
        print(f"CONTRACT AUDIT COMPLETE WITH 0 UNRESOLVED TEST GAPS{suffix}")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--v1-root", type=Path)
    parser.add_argument("--workspace-root", type=Path)
    parser.add_argument("--expected-commit", help="override the manifest commit for an isolated fixture")
    parser.add_argument("--mode", choices=("contract", "strict"), default="contract")
    parser.add_argument("--strict", action="store_true", help="alias for --mode strict")
    parser.add_argument("--gap-report", type=Path, help="write a deterministic machine-readable audit/gap report")
    args = parser.parse_args()
    mode = "strict" if args.strict else args.mode
    try:
        root = args.root.resolve()
        v1_root = args.v1_root.resolve() if args.v1_root else None
        workspace_root = args.workspace_root.resolve() if args.workspace_root else None
        gap_report = args.gap_report.resolve() if args.gap_report else None
        return validate(root, v1_root, workspace_root, args.expected_commit, mode, gap_report)
    except CitationError as error:
        print(f"citation invalid: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
