#!/usr/bin/env python3
"""Parallel, synthetic end-to-end tests of the local CLI; no server/network."""

from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys
import tempfile

HERE = Path(__file__).resolve().parent
FIXTURE = HERE / "fixtures/canonical.jsonl"


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def invoke(directory, raw, output="output.sql", database="synthetic", collection="records"):
    source = directory / "input.jsonl"
    if source.exists():
        source.chmod(0o600)
    source.write_bytes(raw)
    source.chmod(0o444)
    before = source.stat()
    result = subprocess.run(
        [sys.executable, "-I", "-B", str(HERE / "convert.py"), str(source), str(directory / output),
         "--database", database, "--collection", collection], capture_output=True, timeout=30,
    )
    after = source.stat()
    require(source.read_bytes() == raw and before.st_mtime_ns == after.st_mtime_ns
            and before.st_mode == after.st_mode, "source changed")
    require(not list(directory.glob(".mongodb-*")), "temporary data not cleaned up")
    return result, directory / output


def unescape(field):
    output = []
    i = 0
    while i < len(field):
        if field[i] == "\\":
            i += 1
            require(i < len(field) and field[i] in "\\tnr", "unexpected COPY escape")
            output.append({"\\": "\\", "t": "\t", "n": "\n", "r": "\r"}[field[i]])
        else:
            output.append(field[i])
        i += 1
    return "".join(output)


def read_artifact(path):
    raw = path.read_bytes()
    prefix, marker, data = raw.decode("ascii").partition(
        "COPY public.mongodb_documents (id, document, provenance) FROM stdin;\n")
    require(bool(marker), "wrong COPY header")
    require("id text NOT NULL PRIMARY KEY" in prefix and "document jsonb NOT NULL" in prefix
            and "provenance jsonb NOT NULL" in prefix, "wrong table mapping")
    require(data.endswith("\\.\n"), "missing COPY terminator")
    rows = []
    for line in data[:-3].splitlines():
        fields = line.split("\t")
        require(len(fields) == 3, "COPY field splitting changed")
        identity, document, provenance = map(unescape, fields)
        require(bool(re.fullmatch(r"0{10}[0-9A-HJKMNP-TV-Z]{16}", identity)), "not a zero-time ULID")
        body = {"id": identity, "document": json.loads(document), "provenance": json.loads(provenance)}
        require(len(json.dumps(body, separators=(",", ":")).encode()) <= 1024**2, "body too large")
        rows.append(body)
    return rows


def encode(document):
    return (json.dumps(document, ensure_ascii=True, separators=(",", ":")) + "\n").encode()


def accepted_fixture(directory):
    raw = FIXTURE.read_bytes()
    result, output = invoke(directory, raw)
    require(result.returncode == 0, "fixture conversion failed")
    require(output.read_bytes() == (HERE / "fixtures/expected.sql").read_bytes(), "golden artifact differs")
    require(output.stat().st_mode & 0o777 == 0o600, "artifact permissions are not private")
    rows = read_artifact(output)
    expected = [json.loads(line) for line in raw.splitlines()]
    require([row["document"] for row in rows] == expected, "BSON document changed")
    require(rows[0]["document"]["nullable"] is None and "nullable" not in rows[1]["document"], "null/missing collapsed")
    require(list(rows[0]["document"]) == list(expected[0]), "source member order changed in artifact")
    for index, row in enumerate(rows, 1):
        require(row["provenance"] == {"adapter": "mongodb-ejson-v1",
                "sourceFormat": "mongoexport-canonical-ejson-v2-jsonl",
                "sourceSha256": hashlib.sha256(raw).hexdigest(), "database": "synthetic",
                "collection": "records", "sourceRecord": index}, "provenance differs")
    result, repeat = invoke(directory, raw, "repeat.sql")
    require(result.returncode == 0 and repeat.read_bytes() == output.read_bytes(), "nondeterministic output")


def identifier_domains(directory):
    raw = b"".join(encode({"_id": identity}) for identity in
                   ["1", {"$numberInt": "1"}, {"$oid": "0123456789abcdef01234567"}, "0123456789abcdef01234567"])
    result, output = invoke(directory, raw, database='quote"; DROP TABLE x; --', collection="MiXeD.表")
    require(result.returncode == 0, "valid namespace label rejected")
    rows = read_artifact(output)
    require(len({row["id"] for row in rows}) == 4, "typed ID domains collided")
    require(output.read_text().count("CREATE TABLE") == 1, "namespace became SQL")
    result, changed = invoke(directory, raw, "other.sql", collection="different")
    require(result.returncode == 0 and read_artifact(changed)[0]["id"] != rows[0]["id"], "namespace missing from ID")


def large_chunked_records(directory):
    document = {"_id": "large", "data": "x" * 700_000, "unicode": "🙂" * 500}
    raw = encode(document) + encode({"_id": "tail", "present": None})
    result, output = invoke(directory, raw)
    require(result.returncode == 0, "large/chunked record failed")
    require(read_artifact(output)[0]["document"] == document, "chunk boundaries changed record")


def many_records(directory):
    raw = b"".join(encode({"_id": {"$numberLong": str(index)}}) for index in range(1000))
    result, output = invoke(directory, raw)
    require(result.returncode == 0 and len(read_artifact(output)) == 1000, "stream lost records")


def depth_boundary(directory):
    value = "leaf"
    for _ in range(14):
        value = {"x": value}
    result, output = invoke(directory, encode({"_id": "depth", "x": value}))
    require(result.returncode == 0 and len(read_artifact(output)) == 1, "depth 16 container rejected")
    result, output = invoke(directory, encode({"_id": "depth", "x": {"x": value}}), "too-deep.sql")
    require(result.returncode == 1 and not output.exists(), "depth 17 accepted")


def value_boundary(directory):
    # Body(1), id(1), document(1), _id(1), array(1), provenance(1)+6 members = 12.
    result, output = invoke(directory, encode({"_id": "values", "a": [None] * 4084}))
    require(result.returncode == 0, "4096 values rejected")
    result, output = invoke(directory, encode({"_id": "values", "a": [None] * 4085}), "too-many.sql")
    require(result.returncode == 1 and not output.exists(), "4097 values accepted")


def no_clobber(directory):
    raw = encode({"_id": "one"})
    output = directory / "output.sql"
    output.write_bytes(b"keep me")
    result, _ = invoke(directory, raw)
    require(result.returncode == 1 and output.read_bytes() == b"keep me", "existing output clobbered")
    result, source = invoke(directory, raw, "input.jsonl")
    require(result.returncode == 1 and source.read_bytes() == raw, "source overwritten")
    (directory / "link.sql").symlink_to(output)
    result, _ = invoke(directory, raw, "link.sql")
    require(result.returncode == 1 and output.read_bytes() == b"keep me", "output symlink followed")


def symlink_source(directory):
    source = directory / "source.jsonl"
    source.write_bytes(encode({"_id": "one"}))
    link = directory / "link.jsonl"
    link.symlink_to(source)
    result = subprocess.run([sys.executable, "-I", "-B", str(HERE / "convert.py"), str(link),
                             str(directory / "output.sql"), "--database", "synthetic", "--collection", "records"],
                            capture_output=True, timeout=30)
    require(result.returncode == 1 and not (directory / "output.sql").exists(), "source symlink accepted")


def reject_case(raw):
    def test(directory):
        result, output = invoke(directory, raw)
        require(result.returncode == 1, "unsupported input accepted")
        require(not output.exists(), "failed conversion published output")
        require(b"Traceback" not in result.stderr and b"sensitive-marker" not in result.stderr
                and b"sensitive-marker" not in result.stdout, "source data leaked in diagnostics")
    return test


def decimal_exact(directory):
    cases = ["-0", "123.4500", "1E-6176", "1E+6112", "0E-6177", "NaN", "-Infinity",
             "10000000000000000000000000000000000"]
    raw = b"".join(encode({"_id": str(index), "decimal": {"$numberDecimal": value}})
                   for index, value in enumerate(cases))
    result, output = invoke(directory, raw)
    require(result.returncode == 0, "losslessly representable Decimal128 rejected")
    require([row["document"]["decimal"]["$numberDecimal"] for row in read_artifact(output)] == cases,
            "decimal spelling or scale changed")


def final_line_and_crlf(directory):
    raw = b'{"_id":"one"}\r\n{"_id":"two"}'
    result, output = invoke(directory, raw)
    require(result.returncode == 0 and len(read_artifact(output)) == 2, "CRLF or final unterminated record lost")


def main():
    tests = [(function.__name__, function) for function in
             [accepted_fixture, identifier_domains, large_chunked_records, many_records, depth_boundary,
              value_boundary, no_clobber, symlink_source, decimal_exact, final_line_and_crlf]]
    malformed = {
        "empty": b"", "blank": b'{"_id":"one"}\n\n', "array": b'[{"_id":"one"}]',
        "bad_utf8": b'{"_id":"sensitive-marker","x":"\xff"}',
        "bom": b'\xef\xbb\xbf{"_id":"one"}', "truncated": b'{"_id":"sensitive-marker",',
        "multi_object": b'{"_id":"one"}{"_id":"two"}',
        "duplicate_member": b'{"_id":"one","x":null,"x":false}',
        "duplicate_nested": b'{"_id":"one","x":{"a":null,"a":true}}',
        "duplicate_wrapper": b'{"_id":"one","x":{"$numberInt":"1","$numberInt":"2"}}',
        "raw_int": b'{"_id":"one","x":1}', "raw_float": b'{"_id":"one","x":1.0}',
        "raw_nan": b'{"_id":"one","x":NaN}', "raw_infinity": b'{"_id":"one","x":Infinity}',
        "unpaired_surrogate": b'{"_id":"one","x":"\\ud800"}',
        "nul": b'{"_id":"one","x":"\\u0000"}', "nul_key": b'{"_id":"one","\\u0000":null}',
        "missing_id": b'{"x":null}', "null_id": b'{"_id":null}', "bool_id": b'{"_id":true}',
        "array_id": b'{"_id":[]}', "object_id": b'{"_id":{"x":null}}',
        "duplicate_id": b'{"_id":"one"}\n{"_id":"one"}',
        "duplicate_numeric_id": b'{"_id":{"$numberInt":"1"}}\n{"_id":{"$numberLong":"1"}}',
        "oversized_body": encode({"_id": "one", "x": "x" * 1024**2}),
        "oversized_line": b'{"_id":"one","x":"' + b"x" * (16 * 1024**2) + b'"}',
        "excessive_parser_nesting": b'{"_id":"one","x":' + b"[" * 1500 + b"null" + b"]" * 1500 + b"}",
        "sql_input": b"CREATE FUNCTION sensitive-marker();", "meta_input": b"\\! sensitive-marker",
    }
    invalid_wrappers = [
        {"$oid": "abcdef"}, {"$oid": "ABCDEF0123456789ABCDEF01"},
        {"$numberInt": "2147483648"}, {"$numberLong": "9223372036854775808"},
        {"$numberLong": "-9223372036854775809"}, {"$numberLong": "01"}, {"$numberInt": "-0"},
        {"$numberInt": 1}, {"$numberLong": "1" * 5000},
        {"$numberDecimal": "12345678901234567890123456789012345"},
        {"$numberDecimal": "1E-6177"}, {"$numberDecimal": "1E+6145"},
        {"$numberDecimal": "sNaN"}, {"$numberDecimal": "1_0"}, {"$numberDecimal": " 1"},
        {"$numberDouble": "1E999"}, {"$numberDouble": "1E-999"}, {"$numberDouble": "inf"},
        {"$date": "2023-11-05T01:30:00-04:00"}, {"$date": {"$numberInt": "0"}},
        {"$date": {"$numberLong": "9223372036854775808"}},
        {"$binary": {"base64": "AB==", "subType": "00"}},
        {"$binary": {"base64": "AA", "subType": "00"}},
        {"$binary": {"base64": "AA==\n", "subType": "00"}},
        {"$binary": {"base64": "AA==", "subType": "0"}},
        {"$binary": {"base64": "AA==", "subType": "GG"}},
        {"$binary": "AA==", "$type": "00"},
        {"$timestamp": {"t": -1, "i": 0}}, {"$timestamp": {"t": 0, "i": 4294967296}},
        {"$timestamp": {"t": 0.0, "i": 0}}, {"$timestamp": {"t": "0", "i": 0}},
        {"$code": "sensitive-marker"}, {"$regularExpression": {"pattern": "x", "options": ""}},
        {"$undefined": True}, {"$uuid": "00000000-0000-0000-0000-000000000000"},
        {"$oid": "0123456789abcdef01234567", "x": None}, {"$customerField": None},
    ]
    for index, wrapper in enumerate(invalid_wrappers):
        malformed[f"invalid_wrapper_{index}"] = encode({"_id": "sensitive-marker", "x": wrapper})
    for name, raw in malformed.items():
        tests.append((name, reject_case(raw)))

    def run(item):
        name, function = item
        try:
            with tempfile.TemporaryDirectory(prefix=".test-", dir=HERE) as scratch:
                function(Path(scratch))
            return name, None
        except Exception as error:
            return name, str(error)

    with ThreadPoolExecutor() as executor:
        results = list(executor.map(run, tests))
    failures = [(name, error) for name, error in results if error is not None]
    for name, error in failures:
        print(f"FAIL {name}: {error}", file=sys.stderr)
    print(f"{len(results) - len(failures)}/{len(results)} parallel synthetic CLI tests passed")
    return bool(failures)


if __name__ == "__main__":
    sys.exit(main())
