#!/usr/bin/env python3
"""Parallel synthetic CLI integration checks; no servers, accounts or customer data."""

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
FIXTURE = HERE / "fixtures" / "capture"
BASE = json.loads((FIXTURE / "manifest.json").read_text())
ROWS = [json.loads(line) for chunk in BASE["tables"][0]["chunks"]
        for line in (FIXTURE / chunk["file"]).read_text().splitlines()]


def run_case(change=None, expected=None, check=None, raw=None, finalize=None):
    with tempfile.TemporaryDirectory() as temporary:
        root = Path(temporary)
        capture = root / "capture"
        shutil.copytree(FIXTURE, capture)
        manifest, rows = copy.deepcopy(BASE), copy.deepcopy(ROWS)
        if change:
            change(manifest, rows)
        if raw is not None or change:
            data = raw if raw is not None else b"".join(
                (json.dumps(row, ensure_ascii=True, separators=(",", ":")) + "\n").encode()
                for row in rows)
            (capture / "changed.jsonl").write_bytes(data)
            manifest["tables"][0]["chunks"] = [{"file": "changed.jsonl", "bytes": len(data),
                "sha256": hashlib.sha256(data).hexdigest(), "rows": len(rows)}]
        if finalize:
            finalize(manifest, capture)
        (capture / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
        before = {p.name: p.read_bytes() for p in capture.iterdir() if p.is_file()}
        output = root / "out"
        result = subprocess.run([sys.executable, "-B", str(HERE / "convert.py"),
                                 str(capture / "manifest.json"), str(output)],
                                capture_output=True, timeout=45)
        assert {p.name: p.read_bytes() for p in capture.iterdir() if p.is_file()} == before
        if expected:
            assert result.returncode == 1, (expected, result.stdout)
            assert expected.encode() in result.stderr, result.stderr
            assert not output.exists(), "failed conversion left artifacts"
            assert b"Traceback" not in result.stderr
        else:
            assert result.returncode == 0, result.stderr
            sql = (output / "import.sql").read_bytes()
            provenance = json.loads((output / "provenance.json").read_text())
            assert provenance["output"]["sha256"] == hashlib.sha256(sql).hexdigest()
            assert provenance["output"]["bytes"] == len(sql)
            assert sorted(p.name for p in output.iterdir()) == ["import.sql", "provenance.json"]
            if check:
                check(sql, provenance)
        return result


def set_value(index, value):
    return lambda manifest, rows: rows[0].__setitem__(index, value)


def set_column(index, key, value):
    return lambda manifest, rows: manifest["tables"][0]["columns"][index].__setitem__(key, value)


def decode_copy(sql):
    """Independent decoder for the generated COPY subset, never an SQL executor."""
    lines = sql.decode("utf-8").split("\n")
    start = next(i for i, line in enumerate(lines) if line.startswith("COPY ")) + 1
    rows = []
    for line in lines[start:]:
        if line == r"\.":
            break
        cells = []
        for cell in line.split("\t"):
            if cell == r"\N":
                cells.append(None)
                continue
            result, i = "", 0
            while i < len(cell):
                if cell[i] == "\\":
                    i += 1
                    result += {"n": "\n", "r": "\r", "t": "\t", "b": "\b", "f": "\f", "v": "\v", "\\": "\\"}[cell[i]]
                else:
                    result += cell[i]
                i += 1
            cells.append(result)
        rows.append(cells)
    return rows


def golden():
    def check(sql, provenance):
        assert sql == (HERE / "fixtures" / "expected" / "import.sql").read_bytes()
        decoded = decode_copy(sql)
        assert decoded[0][1] == "123456789012345678901234567890.12345678"
        assert decoded[0][2:4] == ["922337203685477.5807", "-214748.3648"]
        assert decoded[0][4:6] == ["2024-02-29T23:59:59.1234567", "2024-02-29T23:59:59.7654321-03:00"]
        assert decoded[0][6] == "00112233-4455-6677-8899-aabbccddeeff"
        assert decoded[0][7] == ROWS[0][7]
        assert decoded[0][8] == r"\x00ff800102"
        assert decoded[0][9] == "t"
        assert decoded[1][7:10] == ["", r"\x", "f"]
        assert decoded[2][7:10] == [None, None, None]
        assert decoded[0][11] == "A   "
        assert provenance["tables"][0]["collection"] == "dbo_orders"
        assert provenance["tables"][0]["columns"][0]["identity"] is True
        assert provenance["tables"][0]["columns"][10]["computed"] is True
    run_case(check=check)


def deterministic():
    with tempfile.TemporaryDirectory() as temporary:
        outputs = []
        for name in ("one", "two"):
            output = Path(temporary) / name
            subprocess.run([sys.executable, "-B", str(HERE / "convert.py"), str(FIXTURE / "manifest.json"), str(output)],
                           check=True, capture_output=True, timeout=30)
            outputs.append([(output / file).read_bytes() for file in ("import.sql", "provenance.json")])
        assert outputs[0] == outputs[1]
        assert outputs[0][1] == (HERE / "fixtures" / "expected" / "provenance.json").read_bytes()


def malformed_manifest(data, expected):
    with tempfile.TemporaryDirectory() as temporary:
        root = Path(temporary)
        capture = root / "capture"
        capture.mkdir()
        path = capture / "manifest.json"
        path.write_bytes(data)
        result = subprocess.run([sys.executable, "-B", str(HERE / "convert.py"), str(path), str(root / "out")],
                                capture_output=True, timeout=30)
        assert result.returncode == 1 and expected.encode() in result.stderr, result.stderr
        assert not (root / "out").exists()


def file_safety():
    with tempfile.TemporaryDirectory() as temporary:
        root = Path(temporary)
        capture = root / "capture"
        shutil.copytree(FIXTURE, capture)
        output = root / "out"
        output.mkdir()
        (output / "keep").write_text("preserve")
        command = [sys.executable, "-B", str(HERE / "convert.py"), str(capture / "manifest.json")]
        result = subprocess.run(command + [str(output)], capture_output=True, timeout=30)
        assert result.returncode == 1 and (output / "keep").read_text() == "preserve"
        result = subprocess.run(command + [str(capture / "new")], capture_output=True, timeout=30)
        assert result.returncode == 1 and b"output_inside_snapshot" in result.stderr
        chunk = capture / BASE["tables"][0]["chunks"][0]["file"]
        outside = root / "outside.jsonl"
        chunk.rename(outside)
        chunk.symlink_to(outside)
        result = subprocess.run(command + [str(root / "new")], capture_output=True, timeout=30)
        assert result.returncode == 1 and not (root / "new").exists()


def corrupt_chunk():
    with tempfile.TemporaryDirectory() as temporary:
        root = Path(temporary)
        capture = root / "capture"
        shutil.copytree(FIXTURE, capture)
        chunk = capture / BASE["tables"][0]["chunks"][0]["file"]
        data = chunk.read_bytes().replace(b"1234.50", b"1234.51")
        chunk.write_bytes(data)
        result = subprocess.run([sys.executable, "-B", str(HERE / "convert.py"), str(capture / "manifest.json"), str(root / "out")],
                                capture_output=True, timeout=30)
        assert result.returncode == 1 and b"chunk_checksum_mismatch" in result.stderr
        assert not (root / "out").exists()


def many_rows():
    def change(manifest, rows):
        first = rows[0]
        rows.clear()
        for i in range(2000):
            row = copy.deepcopy(first)
            row[0], row[7] = str(i), "chunked 😀 " + "x" * 2048
            rows.append(row)
    run_case(change, check=lambda sql, provenance: (
        assert_equal(len(decode_copy(sql)), 2000), assert_equal(provenance["output"]["rows"], 2000)))


def assert_equal(actual, expected):
    assert actual == expected, (actual, expected)


def identifier_quoting():
    def change(manifest, rows):
        table = manifest["tables"][0]
        table["schema"], table["name"] = 'dbo"x', 'order;--$tag$'
        table["columns"][7]["name"] = '名".x;--'
        rows[0][7] = "\\N\n\\.\n\\! do-not-run\nSELECT 1;"
    def check(sql, provenance):
        assert b'"dbo""x"."order;--$tag$"' in sql
        assert '"名"".x;--"'.encode() in sql
        assert decode_copy(sql)[0][7] == "\\N\n\\.\n\\! do-not-run\nSELECT 1;"
        assert provenance["tables"][0]["columns"][7]["name"] == '名".x;--'
    run_case(change, check=check)


def composite_key():
    def change(manifest, rows):
        table = manifest["tables"][0]
        table["columns"][7]["nullable"] = False
        table["primaryKey"] = ["id", "note"]
        for i, row in enumerate(rows):
            row[0], row[7] = "1", ("a:b%", "a%3Ab", "a%3A:b")[i]
    run_case(change)


def empty_table():
    run_case(lambda manifest, rows: rows.clear(), check=lambda sql, provenance:
             assert_equal(provenance["output"]["rows"], 0))


def duplicate_table():
    def change(manifest, rows):
        manifest["tables"].append(copy.deepcopy(manifest["tables"][0]))
    run_case(change, expected="duplicate_table")


def collection_collision():
    def change(manifest, rows):
        table = copy.deepcopy(manifest["tables"][0])
        table["name"] = "ORDERS"
        manifest["tables"].append(table)
    run_case(change, expected="collection_collision")


def multi_table():
    def finalize(manifest, capture):
        table = copy.deepcopy(manifest["tables"][0])
        table["name"] = "other"
        for chunk in table["chunks"]:
            original = chunk["file"]
            chunk["file"] = "other-" + original
            shutil.copyfile(capture / original, capture / chunk["file"])
        manifest["tables"].append(table)
    run_case(finalize=finalize, check=lambda sql, provenance:
             assert_equal([t["rows"] for t in provenance["tables"]], [3, 3]))


def cross_chunk_duplicate():
    def finalize(manifest, capture):
        chunk = manifest["tables"][0]["chunks"][1]
        path = capture / chunk["file"]
        data = path.read_bytes().replace(b'"-9223372036854775808"', b'"9223372036854775807"')
        path.write_bytes(data)
        chunk["bytes"], chunk["sha256"] = len(data), hashlib.sha256(data).hexdigest()
    run_case(finalize=finalize, expected="duplicate_document_id")


def integer_boundaries():
    for kind, low, high in (("tinyint", "0", "255"), ("smallint", "-32768", "32767"),
                            ("int", "-2147483648", "2147483647")):
        def change(manifest, rows):
            manifest["tables"][0]["columns"][0]["type"] = kind
            for row, value in zip(rows, (low, high, "1")):
                row[0] = value
        run_case(change)
        def overflow(manifest, rows):
            change(manifest, rows)
            rows[0][0] = str(int(high) + 1)
        run_case(overflow, expected="integer_range")


def scale_boundaries():
    for precision, scale, text in ((38, 0, "9" * 38), (38, 38, "0." + "9" * 38)):
        def change(manifest, rows):
            column = manifest["tables"][0]["columns"][1]
            column.update(precision=precision, scale=scale)
            for row in rows:
                row[1] = text
        run_case(change, check=lambda sql, provenance: assert_equal(decode_copy(sql)[0][1], text))


def zero_temporal_scale():
    def change(manifest, rows):
        for index in (4, 5, 12):
            manifest["tables"][0]["columns"][index]["scale"] = 0
        for row in rows:
            row[4] = "9999-12-31T23:59:59"
            row[5] = "2024-02-29T14:00:00+14:00"
            row[12] = "23:59:59"
    run_case(change, check=lambda sql, provenance:
             assert_equal(decode_copy(sql)[0][4:6], ["9999-12-31T23:59:59", "2024-02-29T14:00:00+14:00"]))


def fixed_binary():
    def change(manifest, rows):
        manifest["tables"][0]["columns"][8].update(type="binary", length=5)
        rows[1][8] = "0x00FF800102"
    run_case(change, check=lambda sql, provenance: assert_equal(decode_copy(sql)[1][8], r"\x00ff800102"))
    def missing_padding(manifest, rows):
        change(manifest, rows)
        rows[1][8] = "0x"
    run_case(missing_padding, expected="binary_padding_required")


def main():
    cases = [("golden semantics", golden), ("deterministic", deterministic),
             ("file safety", file_safety), ("checksum", corrupt_chunk),
             ("many rows", many_rows), ("quoted identifiers", identifier_quoting),
             ("composite key", composite_key), ("empty table", empty_table),
             ("duplicate table", duplicate_table), ("collection collision", collection_collision),
             ("multi-table", multi_table), ("cross-chunk duplicate", cross_chunk_duplicate),
             ("integer boundaries", integer_boundaries), ("scale boundaries", scale_boundaries),
             ("zero temporal scale", zero_temporal_scale), ("fixed binary", fixed_binary)]
    bad_values = [
        (0, "9223372036854775808", "integer_range"), (0, "-9223372036854775809", "integer_range"),
        (0, True, "expected_string"), (0, "01", "integer_format"),
        (1, "1234567890123456789012345678901.12345678", "decimal_precision_scale"),
        (1, "1.123456789", "decimal_precision_scale"), (1, "1e3", "decimal_format"),
        (1, 1.1, "json_number_not_supported"), (2, "922337203685477.5808", "money_range"),
        (3, "214748.3648", "money_range"), (4, "2023-02-29T12:00:00.1234567", "temporal_range"),
        (4, "2024-02-29T12:00:00.123456", "temporal_format"),
        (4, "2024-02-29T24:00:00.1234567", "temporal_range"),
        (5, "2024-02-29T12:00:00.1234567+14:01", "offset_range"),
        (5, "0001-01-01T00:00:00.0000000+01:00", "temporal_range"),
        (5, "2024-02-29T12:00:00.1234567Z", "temporal_format"),
        (5, "2024-02-29T12:00:00.1234567-00:00", "unknown_offset"),
        (6, "00112233445566778899aabbccddeeff", "uuid_format"),
        (7, "\ud800", "invalid_unicode"), (7, "x\0y", "nul_not_supported"),
        (8, "0x123", "binary_hex_required"), (8, "aGVsbG8=", "binary_hex_required"),
        (9, 1, "bit_boolean_required"), (11, "A", "nchar_padding_required"),
        (13, "0000-01-01", "date_range"), (0, None, "null_not_allowed"),
        (7, "x" * (1024 * 1024), "document_size_limit"),
    ]
    for i, (index, value, error) in enumerate(bad_values):
        cases.append((f"invalid value {i}: {error}", lambda index=index, value=value, error=error:
                      run_case(set_value(index, value), expected=error)))
    for kind in ("float", "real", "sql_variant", "rowversion", "xml", "geography", "datetime"):
        cases.append((f"unsupported {kind}", lambda kind=kind:
                      run_case(set_column(7, "type", kind), expected="unsupported_type")))
    cases.extend([
        ("missing cell", lambda: run_case(lambda manifest, rows: rows[0].pop(), expected="row_width")),
        ("extra cell", lambda: run_case(lambda manifest, rows: rows[0].append(None), expected="row_width")),
        ("nullable PK", lambda: run_case(set_column(0, "nullable", True), expected="unsupported_primary_key")),
        ("unknown column field", lambda: run_case(set_column(0, "sql", "DROP TABLE secret"), expected="manifest_fields")),
        ("duplicate ID", lambda: run_case(lambda manifest, rows: rows[1].__setitem__(0, rows[0][0]), expected="duplicate_document_id")),
        ("UTF-16 length", lambda: run_case(set_column(7, "length", 1), expected="text_length")),
        ("long identifier", lambda: run_case(set_column(7, "name", "x" * 64), expected="identifier_length")),
        ("control identifier", lambda: run_case(set_column(7, "name", "a\nb"), expected="identifier_control")),
        ("duplicate JSON keys", lambda: malformed_manifest(b'{"format":1,"format":2}', "duplicate_json_key")),
        ("manifest BOM", lambda: malformed_manifest(b'\xef\xbb\xbf{}', "malformed_json_utf8")),
        ("invalid UTF-8", lambda: run_case(raw=b'["\xff"]\n', expected="malformed_json_utf8")),
        ("row BOM", lambda: run_case(raw=b'\xef\xbb\xbf[]\n', expected="malformed_json_utf8")),
        ("nonfinite JSON", lambda: run_case(raw=b'[NaN]\n', expected="json_number_not_supported")),
        ("no LF", lambda: run_case(raw=b'[]', expected="row_line_limit_or_termination")),
        ("oversized line", lambda: run_case(raw=b' ' * (16 * 1024 * 1024 + 1), expected="row_line_limit_or_termination")),
        ("SQL rejected", lambda: run_case(raw=b'COPY x FROM stdin;\n', expected="malformed_json_utf8")),
        ("row count short", lambda: run_case(finalize=lambda m, c: m["tables"][0]["chunks"][0].__setitem__("rows", 2), expected="chunk_count_mismatch")),
        ("row count excess", lambda: run_case(finalize=lambda m, c: m["tables"][0]["chunks"][0].__setitem__("rows", 0), expected="chunk_bounds")),
        ("chunk size", lambda: run_case(finalize=lambda m, c: m["tables"][0]["chunks"][0].__setitem__("bytes", 1), expected="chunk_size_mismatch")),
        ("path traversal", lambda: run_case(finalize=lambda m, c: m["tables"][0]["chunks"][0].__setitem__("file", "../secret.jsonl"), expected="unsafe_chunk_path")),
        ("non-ASCII table", lambda: run_case(finalize=lambda m, c: m["tables"][0].__setitem__("name", "订单"), expected="non_ascii_table_identifier")),
        ("nested row", lambda: run_case(set_value(7, {"secret": "not logged"}), expected="expected_string")),
        ("manifest bound", lambda: malformed_manifest(b' ' * (1024 * 1024 + 1), "manifest_size_limit")),
        ("text controls", lambda: run_case(set_value(7, "x\b\f\v\u2028y"), check=lambda sql, provenance:
                                          assert_equal(decode_copy(sql)[0][7], "x\b\f\v\u2028y"))),
    ])
    failures = []
    with ThreadPoolExecutor(max_workers=4) as pool:
        futures = [(name, pool.submit(function)) for name, function in cases]
        for name, future in futures:
            try:
                future.result()
            except Exception as error:
                failures.append(name)
                print(f"FAIL {name}: {error}")
    print(f"{len(cases) - len(failures)}/{len(cases)} parallel synthetic CLI checks passed")
    return bool(failures)


if __name__ == "__main__":
    sys.exit(main())
