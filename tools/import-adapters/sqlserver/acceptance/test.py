#!/usr/bin/env python3
"""Parallel offline CLI checks; all SQL Server/core receipts here are synthetic."""

from concurrent.futures import ThreadPoolExecutor
import copy
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

HERE = Path(__file__).resolve().parent
CAPTURE = HERE / "golden-capture"
OUTPUT = HERE / "golden-output"
MANIFEST = json.loads((CAPTURE / "manifest.json").read_text(encoding="utf-8"))
ROWS = [json.loads(line) for chunk in MANIFEST["tables"][0]["chunks"]
        for line in (CAPTURE / chunk["file"]).read_text(encoding="utf-8").splitlines()]
EXPECTED = json.loads((HERE / "expected-documents.json").read_text(encoding="utf-8"))
ARTIFACT = (OUTPUT / "import.sql").read_bytes()
PREVIEW_ID = "01ARZ3NDEKTSV4RRFFQ69G5FAV"


def cli(*args, error=None):
    result = subprocess.run([sys.executable, "-B", str(HERE / "check.py"), *map(str, args)],
                            capture_output=True, timeout=15)
    if error:
        assert result.returncode == 1, "invalid input was accepted"
        assert result.stdout == b"", "refusal printed input"
        assert result.stderr.startswith(b"acceptance_refused:")
        assert error.encode() in result.stderr, result.stderr
        assert b"Traceback" not in result.stderr
        assert b"private-synthetic-sentinel" not in result.stderr
    else:
        assert result.returncode == 0, result.stderr
        assert result.stdout == ("offline_fixture_check_passed:" + str(args[0]) + "\n").encode()
        assert result.stderr == b""


def raw_rows(rows):
    return b"".join((json.dumps(row, ensure_ascii=False, separators=(",", ":")) + "\n").encode()
                    for row in rows)


def seal_case(change=None, error=None, raw=None):
    with tempfile.TemporaryDirectory(dir=HERE / "runs") as temporary:
        root = Path(temporary)
        rows = copy.deepcopy(ROWS)
        if change:
            change(rows)
        data = raw_rows(rows) if raw is None else raw
        source, destination = root / "source.raw", root / "capture"
        source.write_bytes(data)
        cli("seal", source, destination, error=error)
        assert source.read_bytes() == data
        if error:
            assert not destination.exists(), "refusal left capture artifacts"
        else:
            assert {p.name: p.read_bytes() for p in destination.iterdir()} == {
                p.name: p.read_bytes() for p in CAPTURE.iterdir()}
            assert destination.stat().st_mode & 0o777 == 0o700
            assert all(p.stat().st_mode & 0o777 == 0o600 for p in destination.iterdir())
            output = root / "output"
            subprocess.run([sys.executable, "-B", str(HERE.parent / "convert.py"),
                            str(destination / "manifest.json"), str(output)],
                           check=True, capture_output=True, timeout=15)
            cli("artifact", output)
            assert {p.name: p.read_bytes() for p in output.iterdir()} == {
                p.name: p.read_bytes() for p in OUTPUT.iterdir()}


def preview():
    mappings = {
        "id": "integer", "amount": "decimal_string", "wallet": "decimal_string",
        "small": "decimal_string", "occurred": "string", "offset": "string", "guid": "string",
        "note.名": "string", "payload": "base64", "active": "boolean", "calculated": "decimal_string",
        "padded": "string", "clock": "string", "day": "date", "unit": "decimal_string",
        "qty": "integer", "edge": "integer",
    }
    return {"state": "ready", "source": {"format": "postgres_plain", "bytes": len(ARTIFACT),
            "sha256": hashlib.sha256(ARTIFACT).hexdigest()}, "preview": {"id": PREVIEW_ID,
            "blocking": 0, "tables": [{"source": "dbo.sqlserver_acceptance",
            "collection": "dbo_sqlserver_acceptance", "rows": 4, "idPolicy": "primary_key",
            "primaryKey": ["id"], "excluded": False,
            "columns": [{"name": c["name"], "mapping": mappings[c["name"]], "nullable": c["nullable"],
                         "excluded": False} for c in MANIFEST["tables"][0]["columns"]]}]}}


def completed():
    # A matching synthetic checksum tests receipt validation, not server checksum calculation.
    return {"state": "completed", "source": {"sha256": hashlib.sha256(ARTIFACT).hexdigest()},
            "validation": {"passed": True, "tables": [{"source": "dbo.sqlserver_acceptance",
            "collection": "dbo_sqlserver_acceptance", "sourceRows": 4, "documents": 4,
            "sourceChecksum": "ab" * 32, "documentChecksum": "ab" * 32, "match": True}]}}


def documents():
    return {"next_cursor": None, "items": [{"id": key, "version": "synthetic-version-" + key,
            "state": "value", "document": copy.deepcopy(body)} for key, body in EXPECTED.items()]}


def receipt_case(command, factory, change=None, error=None, raw=None):
    with tempfile.TemporaryDirectory(dir=HERE / "runs") as temporary:
        path = Path(temporary) / "receipt.json"
        value = factory()
        if change:
            change(value)
        data = json.dumps(value, ensure_ascii=True).encode() if raw is None else raw
        path.write_bytes(data)
        args = [command, path] + ([OUTPUT] if command != "documents" else [])
        cli(*args, error=error)
        assert path.read_bytes() == data


def set_path(*path, value):
    def change(obj):
        for key in path[:-1]:
            obj = obj[key]
        obj[path[-1]] = value
    return change


def artifact_case(file, change, error):
    with tempfile.TemporaryDirectory(dir=HERE / "runs") as temporary:
        output = Path(temporary) / "output"
        shutil.copytree(OUTPUT, output)
        path = output / file
        path.write_bytes(change(path.read_bytes()))
        before = {p.name: p.read_bytes() for p in output.iterdir()}
        cli("artifact", output, error=error)
        assert before == {p.name: p.read_bytes() for p in output.iterdir()}


def file_safety():
    with tempfile.TemporaryDirectory(dir=HERE / "runs") as temporary:
        root = Path(temporary)
        source, capture = root / "raw", root / "capture"
        source.write_bytes(raw_rows(ROWS))
        capture.mkdir()
        (capture / "keep").write_bytes(b"preserve")
        cli("seal", source, capture, error="invalid_receipt_or_local_io")
        assert list(capture.iterdir()) == [capture / "keep"]
        assert (capture / "keep").read_bytes() == b"preserve"
        (root / "link").symlink_to(source)
        cli("seal", root / "link", root / "new", error="invalid_receipt_or_local_io")
        cli("seal", root, root / "new", error="input_not_regular_file")
        assert not (root / "new").exists()


def main():
    (HERE / "runs").mkdir(mode=0o700, exist_ok=True)
    cases = [("seal/converter/golden pipeline", lambda: seal_case()), ("file safety", file_safety)]
    display_rows = copy.deepcopy(ROWS)
    display_rows[0][6] = display_rows[0][6].upper()
    display_rows[0][8] = "0x" + display_rows[0][8][2:].upper()
    display = b"\r\n" + raw_rows(display_rows).replace(b"\n", b"\r\n\r\n")
    cases.append(("sqlcmd blank separators/CRLF/hex case", lambda: seal_case(raw=display)))
    row_changes = [
        ("short count", lambda r: r.pop(), "acceptance_row_count"),
        ("extra count", lambda r: r.append(r[0]), "acceptance_row_count"),
        ("row order", lambda r: r.reverse(), "acceptance_row_order"),
        ("missing slot", lambda r: r[2].pop(7), "acceptance_row_width"),
        ("NULL versus empty", lambda r: r[2].__setitem__(7, ""), "acceptance_value_mismatch"),
        ("binary NULL versus empty", lambda r: r[2].__setitem__(8, "0x"), "acceptance_value_mismatch"),
        ("bit integer", lambda r: r[0].__setitem__(9, 1), "bit_boolean_required"),
        ("decimal loss", lambda r: r[0].__setitem__(1, "123456789012345678901234567890.12345670"), "acceptance_value_mismatch"),
        ("money scale", lambda r: r[0].__setitem__(2, "922337203685477.58"), "decimal_precision_scale"),
        ("timezone changed", lambda r: r[0].__setitem__(5, "2024-02-29T23:59:59.7654321+00:00"), "acceptance_value_mismatch"),
        ("timestamp precision", lambda r: r[0].__setitem__(4, "2024-02-29T23:59:59.123456"), "temporal_format"),
        ("large row truncated", lambda r: r[3].__setitem__(7, r[3][7][:4000]), "acceptance_value_mismatch"),
        ("padding trimmed", lambda r: r[0].__setitem__(11, "A"), "nchar_padding_required"),
        ("computed changed", lambda r: r[0].__setitem__(10, "1234.01"), "acceptance_value_mismatch"),
        ("identifier changed", lambda r: r[0].__setitem__(0, "10"), "acceptance_row_order"),
        ("binary changed", lambda r: r[0].__setitem__(8, "0x00ff800100"), "acceptance_value_mismatch"),
    ]
    for name, change, error in row_changes:
        cases.append((name, lambda change=change, error=error: seal_case(change, error)))
    for name, raw, error in [
        ("invalid UTF-8", b'["\xff"]\n', "malformed_json_utf8"),
        ("BOM", b"\xef\xbb\xbf" + raw_rows(ROWS), "malformed_json_utf8"),
        ("float", b"[1.0]\n", "json_number_not_supported"),
        ("lone surrogate", b'["1","1.00000000","1.0000","1.0000","2024-02-29T00:00:00.0000000","2024-02-29T00:00:00.0000000+00:00","00112233-4455-6677-8899-aabbccddeeff","\\ud800",null,null,"1.00","A   ","00:00:00.0000000","2024-02-29","1.00","1","0"]\n', "invalid_unicode"),
        ("malformed/truncated JSON", raw_rows(ROWS)[:-10], "malformed_json_utf8"),
        ("sqlcmd wrapping", raw_rows(ROWS).replace(b"x" * 100, b"x" * 50 + b"\n" + b"x" * 50, 1), "malformed_json_utf8"),
        ("SQL/meta input", b"!! private-synthetic-sentinel\n", "malformed_json_utf8"),
        ("row bound", b"[" + b"x" * (128 * 1024) + b"]\n", "acceptance_row_limit"),
        ("whole-file bound", b"x" * (256 * 1024 + 1), "acceptance_file_limit"),
    ]:
        cases.append((name, lambda raw=raw, error=error: seal_case(error=error, raw=raw)))
    cases.extend([
        ("artifact bytes", lambda: artifact_case("import.sql", lambda b: b + b"-- changed\n", "acceptance_golden_artifact_mismatch")),
        ("artifact provenance", lambda: artifact_case("provenance.json", lambda b: b.replace(b'"rows": 4', b'"rows": 3'), "acceptance_provenance_mismatch")),
    ])
    for command, factory, mutations in [
        ("preview", preview, [
            (("source", "sha256"), "0" * 64, "acceptance_source_binding"),
            (("source", "format"), "sqlite", "acceptance_source_binding"),
            (("source", "bytes"), len(ARTIFACT) - 1, "acceptance_source_binding"),
            (("preview", "blocking"), 1, "acceptance_preview_blocked"),
            (("preview", "blocking"), False, "acceptance_preview_blocked"),
            (("preview", "tables", 0, "primaryKey"), ["edge"], "acceptance_table_plan"),
            (("preview", "tables", 0, "columns", 1, "mapping"), "float", "acceptance_column_plan"),
            (("preview", "tables", 0, "columns", 7, "name"), "note", "acceptance_column_plan"),
            (("preview", "tables", 0, "columns", 7, "nullable"), False, "acceptance_column_plan"),
            (("preview", "tables", 0, "excluded"), True, "acceptance_table_plan"),
            (("preview", "id"), "invalid", "acceptance_preview_id"),
        ]),
        ("completed", completed, [
            (("state",), "failed", "acceptance_not_completed"),
            (("source", "sha256"), "0" * 64, "acceptance_not_completed"),
            (("validation", "passed"), 1, "acceptance_validation"),
            (("validation", "tables", 0, "sourceRows"), True, "acceptance_count_checksum"),
            (("validation", "tables", 0, "documents"), 3, "acceptance_count_checksum"),
            (("validation", "tables", 0, "documentChecksum"), "cd" * 32, "acceptance_count_checksum"),
            (("validation", "tables", 0, "sourceChecksum"), "not-hex", "acceptance_count_checksum"),
            (("validation", "tables", 0, "match"), False, "acceptance_count_checksum"),
        ]),
        ("documents", documents, [
            (("next_cursor",), "more", "acceptance_document_count"),
            (("items", 1, "id"), "1", "acceptance_document_id"),
            (("items", 0, "state"), "tombstone", "acceptance_document_envelope"),
            (("items", 0, "document", "id"), True, "acceptance_document_value"),
            (("items", 0, "document", "edge"), 9223372036854775808, "acceptance_document_value"),
            (("items", 1, "document", "edge"), "-9223372036854775808", "acceptance_document_value"),
            (("items", 0, "document", "amount"), "123456789012345678901234567890.1234567", "acceptance_document_value"),
            (("items", 2, "document", "payload"), "", "acceptance_document_value"),
        ]),
    ]:
        cases.append((command + " valid", lambda command=command, factory=factory: receipt_case(command, factory)))
        for path, value, error in mutations:
            cases.append((command + " rejects " + ".".join(map(str, path)),
                          lambda command=command, factory=factory, path=path, value=value, error=error:
                          receipt_case(command, factory, set_path(*path, value=value), error)))
    cases.extend([
        ("missing NULL field", lambda: receipt_case("documents", documents,
            lambda r: r["items"][2]["document"].pop("payload"), "acceptance_document_value")),
        ("missing receipt field", lambda: receipt_case("preview", dict, error="invalid_receipt_or_local_io")),
        ("duplicate JSON key", lambda: receipt_case("documents", documents, error="duplicate_json_key", raw=b'{"items":[],"items":[]}')),
        ("receipt float", lambda: receipt_case("documents", documents, error="json_number_not_supported", raw=b'{"items":[1.0]}')),
    ])
    failures = []
    with ThreadPoolExecutor(max_workers=4) as pool:
        futures = [(name, pool.submit(case)) for name, case in cases]
        for name, future in futures:
            try:
                future.result()
            except Exception:
                failures.append(name)
                print("FAIL: " + name, file=sys.stderr)
    print(f"{len(cases) - len(failures)}/{len(cases)} parallel offline acceptance-preparation CLI checks passed")
    return bool(failures)


if __name__ == "__main__":
    sys.exit(main())
