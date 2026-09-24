#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

python3 - "${repo_root}" <<'PY'
from pathlib import Path
import re
import sys
import tempfile


PRIVATE_TYPES = re.compile(
    r"\b(?:RsaPrivateKey|PrivateKeyParts|DecryptingKey|Decryptor|"
    r"DecodePrivateKey|DecodeRsaPrivateKey)\b"
)
RSA_PRIVATE_PATH = re.compile(
    r"\brsa\s*::(?:[A-Za-z_]\w*\s*::)*(?:"
    r"RsaPrivateKey|PrivateKeyParts|DecryptingKey|Decryptor|"
    r"DecodePrivateKey|DecodeRsaPrivateKey)\b"
)
RSA_SIGNING_PATH = re.compile(
    r"\brsa\s*::(?:[A-Za-z_]\w*\s*::)*(?:pkcs1v15|pss)\s*::\s*SigningKey\b"
)
RSA_SIGNING_IMPORT = re.compile(
    r"\brsa\s*::\s*(?:pkcs1v15|pss)\s*::\s*\{[^}]*\bSigningKey\b"
)
CFG_TEST_ATTRIBUTE = re.compile(r"(?m)^[ \t]*#\[cfg\s*\(\s*test\s*\)\]")


def _raw_string_end(source: str, index: int) -> int | None:
    prefix = re.match(r"(?:br|r)(#{0,})\"", source[index:])
    if not prefix or (index and (source[index - 1].isalnum() or source[index - 1] == "_")):
        return None
    closing = '"' + prefix.group(1)
    end = source.find(closing, index + prefix.end())
    return len(source) if end < 0 else end + len(closing)


def _skip_comment_or_literal(source: str, index: int) -> int | None:
    if source.startswith("//", index):
        end = source.find("\n", index + 2)
        return len(source) if end < 0 else end
    if source.startswith("/*", index):
        depth = 1
        cursor = index + 2
        while cursor < len(source) and depth:
            if source.startswith("/*", cursor):
                depth += 1
                cursor += 2
            elif source.startswith("*/", cursor):
                depth -= 1
                cursor += 2
            else:
                cursor += 1
        return cursor

    raw_end = _raw_string_end(source, index)
    if raw_end is not None:
        return raw_end
    if source[index] == '"':
        cursor = index + 1
        while cursor < len(source):
            if source[cursor] == "\\":
                cursor += 2
            elif source[cursor] == '"':
                return cursor + 1
            else:
                cursor += 1
        return len(source)
    if source[index] == "'":
        # Ignore character literals, but leave Rust lifetimes in code.
        cursor = index + 1
        if cursor < len(source) and source[cursor] == "\\":
            cursor += 2
        else:
            cursor += 1
        if cursor < len(source) and source[cursor] == "'":
            return cursor + 1
    return None


def _code_mask(source: str) -> list[bool]:
    mask = [False] * len(source)
    cursor = 0
    while cursor < len(source):
        skipped = _skip_comment_or_literal(source, cursor)
        if skipped is not None:
            cursor = skipped
        else:
            mask[cursor] = True
            cursor += 1
    return mask


def _skip_attributes_and_space(source: str, index: int) -> int:
    cursor = index
    while cursor < len(source):
        while cursor < len(source) and source[cursor].isspace():
            cursor += 1
        skipped = _skip_comment_or_literal(source, cursor)
        if skipped is not None:
            cursor = skipped
            continue
        if source.startswith("#[", cursor):
            depth = 1
            cursor += 2
            while cursor < len(source) and depth:
                skipped = _skip_comment_or_literal(source, cursor)
                if skipped is not None:
                    cursor = skipped
                elif source[cursor] == "[":
                    depth += 1
                    cursor += 1
                elif source[cursor] == "]":
                    depth -= 1
                    cursor += 1
                else:
                    cursor += 1
            continue
        return cursor
    return cursor


def _test_item_end(source: str, index: int) -> int:
    cursor = index
    delimiters: list[str] = []
    pairs = {"(": ")", "[": "]", "{": "}"}
    while cursor < len(source):
        skipped = _skip_comment_or_literal(source, cursor)
        if skipped is not None:
            cursor = skipped
            continue
        character = source[cursor]
        if character == "{" and not delimiters:
            depth = 1
            cursor += 1
            while cursor < len(source) and depth:
                skipped = _skip_comment_or_literal(source, cursor)
                if skipped is not None:
                    cursor = skipped
                elif source[cursor] == "{":
                    depth += 1
                    cursor += 1
                elif source[cursor] == "}":
                    depth -= 1
                    cursor += 1
                else:
                    cursor += 1
            return cursor
        if character in pairs:
            delimiters.append(pairs[character])
        elif delimiters and character == delimiters[-1]:
            delimiters.pop()
        elif character == ";" and not delimiters:
            return cursor + 1
        cursor += 1
    return cursor


def _without_test_cfg_items(source: str) -> str:
    mask = _code_mask(source)
    output = list(source)
    for attribute in CFG_TEST_ATTRIBUTE.finditer(source):
        if not all(mask[position] for position in range(*attribute.span())):
            continue
        item_start = _skip_attributes_and_space(source, attribute.end())
        item_end = _test_item_end(source, item_start)
        for position in range(attribute.start(), item_end):
            if output[position] not in "\r\n":
                output[position] = " "
    return "".join(output)


def _scan_workspace(repo_root: Path) -> list[tuple[Path, int, str]]:
    crates = repo_root / "crates"
    roots = sorted(path for path in crates.glob("*/src") if path.is_dir())
    if not roots:
        raise FileNotFoundError(f"missing production source roots: {crates}/*/src")

    violations: list[tuple[Path, int, str]] = []
    for root in roots:
        for source in sorted(root.rglob("*.rs")):
            text = _without_test_cfg_items(source.read_text(encoding="utf-8"))
            for match in PRIVATE_TYPES.finditer(text):
                line = text.count("\n", 0, match.start()) + 1
                violations.append((source, line, match.group(0)))
            for pattern in (RSA_PRIVATE_PATH, RSA_SIGNING_PATH, RSA_SIGNING_IMPORT):
                for match in pattern.finditer(text):
                    line = text.count("\n", 0, match.start()) + 1
                    violations.append((source, line, match.group(0)))
            # Catch grouped imports like `use rsa::{RsaPrivateKey, ...}`.
            for match in re.finditer(r"\buse\s+([^;]+);", text, re.DOTALL):
                statement = match.group(1)
                if re.search(r"\brsa\s*::", statement):
                    forbidden = PRIVATE_TYPES.search(statement)
                    if forbidden:
                        line = text.count("\n", 0, match.start()) + 1
                        violations.append((source, line, forbidden.group(0)))
    return sorted(set(violations))


def _self_test() -> None:
    with tempfile.TemporaryDirectory(prefix="ts-auth-rsa-guard-") as temporary:
        fixture_root = Path(temporary)
        fixture = fixture_root / "crates/auth-app/src/guard_fixture.rs"
        fixture.parent.mkdir(parents=True)
        fixture.write_text(
            "use p256::ecdsa::signature::Signer;\n"
            "#[cfg(test)]\n"
            "mod tests {\n"
            "    use rsa::RsaPrivateKey;\n"
            "    const JSON: &str = r#\"{ not Rust braces }\"#;\n"
            "}\n",
            encoding="utf-8",
        )
        if _scan_workspace(fixture_root):
            raise RuntimeError("self-test rejected test-only RSA or generic ECDSA signing")

        fixture.write_text(
            "use rsa::RsaPrivateKey;\n"
            "use p256::ecdsa::signature::Signer;\n"
            "#[cfg(test)]\n"
            "mod tests { use rsa::RsaPrivateKey; }\n",
            encoding="utf-8",
        )
        violations = _scan_workspace(fixture_root)
        if not any(path == fixture and api == "RsaPrivateKey" for path, _, api in violations):
            raise RuntimeError("self-test did not detect production RSA use in auth-app source")


repo_root = Path(sys.argv[1])
try:
    _self_test()
    violations = _scan_workspace(repo_root)
except (OSError, RuntimeError, FileNotFoundError) as error:
    print(f"RSA usage guard failed: {error}", file=sys.stderr)
    sys.exit(2)

if violations:
    print(
        "RSA usage guard failed: RSA private-key/signing/decryption APIs are "
        "not permitted in production workspace source.",
        file=sys.stderr,
    )
    for source, line, api in violations:
        print(f"{source.relative_to(repo_root)}:{line}: {api}", file=sys.stderr)
    sys.exit(1)

print(
    "RSA usage guard passed: production workspace source uses no private-key/signing/decryption RSA APIs."
)
PY
