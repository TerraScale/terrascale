#!/usr/bin/env python3
"""Read-only oracle for this authored fixture, not a general import/checksum library."""

import base64
import copy
import hashlib
import json
from pathlib import Path
import runpy
import subprocess
import sys
import tempfile


HERE = Path(__file__).resolve().parent


def read(path):
    return json.loads(Path(path).read_text(encoding="utf-8"))


def canonical(value):
    # This fixture uses ASCII keys, ordinary BMP letters, C0 escapes and integers.
    # Do not extend this to floats or arbitrary Unicode without checking .NET's encoder.
    if isinstance(value, dict):
        return "{" + ",".join(canonical(k) + ":" + canonical(v) for k, v in sorted(value.items())) + "}"
    if type(value) is int:
        exponent = 0
        while value and value % 10 == 0:
            value //= 10
            exponent += 1
        return str(value) + "e" + str(exponent)
    assert value is None or type(value) is str, "unexpected fixture JSON type"
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def checksum(documents):
    total = 0
    for row in documents:
        body = dict(row["document"])
        # Match EntityPayload.WithoutId: remove only a string id equal to the key.
        if type(body.get("id")) is str and body["id"] == row["id"]:
            del body["id"]
        term = row["id"].encode("utf-8") + b"\0" + canonical(body).encode("utf-8")
        total += int.from_bytes(hashlib.sha256(term).digest(), "big")
    return f"{total % (1 << 256):064x}"


def expected():
    tables = read(HERE / "expected.json")["tables"]
    for table in tables:
        for row in table["documents"]:
            for name, value in row["document"].items():
                if isinstance(value, dict):
                    assert value == {"$repeat": ["漢", 25000]}
                    row["document"][name] = "漢" * 25000
        assert checksum(table["documents"]) == table["coreChecksum"], "fixture checksum changed"
    return tables


def mapped(value, mapping):
    if value is None:
        return None
    if mapping == "integer":
        return int(value)
    if mapping == "base64":
        assert value.startswith("\\x")
        return base64.b64encode(bytes.fromhex(value[2:])).decode("ascii")
    if mapping == "timestamp":
        return value.replace(" ", "T")
    if mapping == "timestamptz":
        assert value.endswith("+00:00")
        return value[:-6].replace(" ", "T") + "Z"
    return value


def same_documents(actual, wanted):
    assert len(actual) == len(wanted), "document count differs"
    by_id = {row["id"]: row["document"] for row in actual}
    assert len(by_id) == len(actual), "duplicate document id"
    assert set(by_id) == {row["id"] for row in wanted}, "document ids differ"
    for row in wanted:
        assert canonical(by_id[row["id"]]) == canonical(row["document"]), "typed document differs"


def bundle(directory, source, preview_report):
    root = Path(directory)
    report = read(root / "report.json")
    artifact = (root / "import.sql").read_bytes()
    original = Path(source).read_bytes()
    assert report["sourceBytes"] == len(original)
    assert report["sourceSha256"] == hashlib.sha256(original).hexdigest()
    assert report["outputBytes"] == len(artifact)
    assert report["outputSha256"] == hashlib.sha256(artifact).hexdigest()
    assert (root / "report.json").read_bytes() == Path(preview_report).read_bytes()
    assert (report["tableCount"], report["rowCount"], report["skippedIndexes"]) == (4, 4, 2)
    assert report["outputFormat"] == "postgres_plain"
    # Reuse the baseline tests' independent COPY decoder; no converter internals.
    rows = runpy.run_path(str(HERE.parent / "tests.py"))["copy_tables"](artifact)
    plans = {p["sourceTable"]: p for p in report["tables"]}
    tables = expected()
    assert set(rows) == set(plans) == {t["name"] for t in tables}
    for table in tables:
        plan = plans[table["name"]]
        assert plan["primaryKey"] == table["primaryKey"]
        assert plan["collection"] == table["collection"]
        assert [(c["name"], c["pg_type"], c["nullable"]) for c in plan["columns"]] == [
            (c[0], c[1], c[3]) for c in table["columns"]]
        assert plan["copyRows"] == plan["sourceRows"] == len(table["documents"])
        actual = []
        for fields in rows[table["name"]]:
            assert len(fields) == len(table["columns"])
            document = {c[0]: mapped(v, c[2]) for c, v in zip(table["columns"], fields)}
            keys = [str(document[k]).replace("%", "%25").replace(":", "%3A") for k in table["primaryKey"]]
            actual.append({"id": ":".join(keys), "document": document})
        same_documents(actual, table["documents"])
        assert checksum(actual) == table["coreChecksum"]


def preview(job, state="ready"):
    assert job["state"] == state and job["error"] is None
    assert job["options"] == {"missingPrimaryKey": "reject", "exclude": []}
    plan = job["preview"]
    assert plan["blocking"] == 0 and plan["skipped"] == [] and plan["skippedTruncated"] is False
    actual = {t["source"]: t for t in plan["tables"]}
    tables = expected()
    assert len(actual) == len(plan["tables"]) == len(tables)
    assert set(actual) == {t["source"] for t in tables}
    for table in tables:
        found = actual[table["source"]]
        assert found["collection"] == table["collection"] and found["excluded"] is False
        assert found["rows"] == len(table["documents"])
        assert found["idPolicy"] == "primary_key" and found["primaryKey"] == table["primaryKey"]
        assert [(c["name"], c["sourceType"], c["mapping"], c["nullable"]) for c in found["columns"]] == [tuple(c) for c in table["columns"]]
        assert all(c["excluded"] is False for c in found["columns"])
        assert sorted(found["constraints"], key=lambda c: c["kind"]) == sorted(table["constraints"], key=lambda c: c["kind"])
        assert all(i["severity"] != "blocking" for i in found["issues"])


def completed(job, readback):
    preview(job, "completed")
    assert job["confirmedPreviewId"] == job["preview"]["id"]
    assert job["source"]["staged"] is False
    assert job["progress"]["tablesDone"] == job["progress"]["tablesTotal"] == 4
    assert job["progress"]["rowsWritten"] == job["progress"]["rowsTotal"] == 4
    assert job["validation"]["passed"] is True
    validation = {t["source"]: t for t in job["validation"]["tables"]}
    tables = expected()
    assert len(validation) == len(job["validation"]["tables"]) == len(tables)
    assert set(validation) == {t["source"] for t in tables}
    for table in tables:
        entry = validation[table["source"]]
        assert entry["collection"] == table["collection"]
        assert entry["sourceRows"] == entry["documents"] == len(table["documents"])
        assert entry["sourceChecksum"] == entry["documentChecksum"] == table["coreChecksum"]
        assert entry["match"] is True
        page = read(Path(readback) / (table["collection"] + ".json"))
        assert page["next_cursor"] is None, "readback must contain the whole fixture"
        same_documents(page["items"], table["documents"])


def self_check():
    assert canonical({"b": None, "a": 100}) == '{"a":1e2,"b":null}'
    assert checksum([{"id": "1", "document": {"n": 10}}]) == hashlib.sha256(b'1\0{"n":1e1}').hexdigest()
    assert checksum([{"id": "1", "document": {"id": "1", "n": 10}}]) == checksum([{"id": "1", "document": {"n": 10}}])
    tables = expected()
    rows = tables[0]["documents"]
    assert checksum(rows) == checksum(list(reversed(rows)))
    assert checksum(rows + rows[:1]) != checksum(rows)
    for bad in ({}, {"id": "100", "payload": "漢" * 24999}, {"id": "100", "payload": "漢" * 25000}, {"id": True, "payload": "漢" * 25000}):
        try:
            same_documents([{"id": "100", "document": bad}], tables[2]["documents"])
        except AssertionError:
            pass
        else:
            raise AssertionError("bad typed/missing fixture value accepted")
    # Synthetic export-shaped text only: this is not evidence from a real exporter.
    seed = (HERE / "seed.sql").read_text(encoding="utf-8")
    assert seed.count("REPEAT('漢',25000)") == 1
    with tempfile.TemporaryDirectory(prefix=".oracle-", dir=HERE) as directory:
        root = Path(directory)
        source = root / "synthetic-profile.sql"
        source.write_text(seed.replace("REPEAT('漢',25000)", "'" + "漢" * 25000 + "'"), encoding="utf-8")
        source.chmod(0o400)
        for action, destination in (("dry-run", root / "preview.json"), ("convert", root / "bundle")):
            result = subprocess.run([sys.executable, "-I", "-B", str(HERE.parent / "mysql_import.py"), action,
                                     str(source), str(destination), "--source-version", "mysql-8.4"],
                                    capture_output=True, timeout=30, check=False)
            assert result.returncode == 0, "synthetic oracle conversion failed"
        bundle(root / "bundle", source, root / "preview.json")
        # Constructed API envelopes exercise the verifier, never a server.
        job = {"state": "ready", "error": None,
               "options": {"missingPrimaryKey": "reject", "exclude": []},
               "preview": {"id": "synthetic-preview", "blocking": 0, "skipped": [],
                           "skippedTruncated": False, "tables": []}}
        for table in tables:
            job["preview"]["tables"].append({
                "source": table["source"], "collection": table["collection"], "excluded": False,
                "rows": len(table["documents"]), "idPolicy": "primary_key", "primaryKey": table["primaryKey"],
                "columns": [{"name": c[0], "sourceType": c[1], "mapping": c[2], "nullable": c[3], "excluded": False}
                            for c in table["columns"]],
                "constraints": copy.deepcopy(table["constraints"]), "issues": []})
            (root / (table["collection"] + ".json")).write_text(
                json.dumps({"items": table["documents"], "next_cursor": None}), encoding="utf-8")
        preview(job)
        job.update(state="completed", confirmedPreviewId="synthetic-preview", source={"staged": False},
                   progress={"tablesDone": 4, "tablesTotal": 4, "rowsWritten": 4, "rowsTotal": 4},
                   validation={"passed": True, "tables": [
                       {"source": t["source"], "collection": t["collection"], "sourceRows": len(t["documents"]),
                        "documents": len(t["documents"]), "sourceChecksum": t["coreChecksum"],
                        "documentChecksum": t["coreChecksum"], "match": True} for t in tables]})
        completed(job, root)
        bad = copy.deepcopy(job)
        bad["validation"]["tables"][0]["documentChecksum"] = "0" * 64
        try:
            completed(bad, root)
        except AssertionError:
            pass
        else:
            raise AssertionError("bad core checksum accepted")


def main():
    args = sys.argv[1:]
    if args == ["self-check"]:
        self_check()
        print("acceptance oracle checks passed (synthetic only)")
    elif len(args) == 4 and args[0] == "bundle":
        bundle(*args[1:])
        print("fixture COPY values/counts/checksums and dry-run agree")
    elif len(args) == 2 and args[0] == "preview":
        preview(read(args[1]))
        print("fixture core preview agrees")
    elif len(args) == 3 and args[0] == "completed":
        completed(read(args[1]), args[2])
        print("fixture core counts/checksums/typed readback agree")
    else:
        raise ValueError("usage: verify.py self-check | bundle DIR SOURCE DRY_REPORT | preview JOB | completed JOB READBACK_DIR")


if __name__ == "__main__":
    try:
        main()
    except (AssertionError, OSError, ValueError, KeyError, TypeError, subprocess.TimeoutExpired):
        print("acceptance oracle failed; inspect local fixture evidence privately", file=sys.stderr)
        sys.exit(1)
