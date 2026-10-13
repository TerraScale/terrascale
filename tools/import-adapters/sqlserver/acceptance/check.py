#!/usr/bin/env python3
"""Offline fixture sealing and acceptance receipt checks. Never opens a network connection."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import sys

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
import convert

LIMIT = 256 * 1024


def read(path):
    with convert.read_only(path) as stream:
        data = stream.read(LIMIT + 1)
    convert.require(len(data) <= LIMIT, "acceptance_file_limit")
    return data


def load(path):
    return convert.parse_json(read(path))


def expected():
    return load(HERE / "expected-documents.json")


def same(actual, wanted, code):
    # JSON serialization distinguishes bool from int and NULL from missing fields.
    convert.require(json.dumps(actual, sort_keys=True, ensure_ascii=True) ==
                    json.dumps(wanted, sort_keys=True, ensure_ascii=True), code)


def seal(raw_path, destination):
    template = load(HERE / "manifest-template.json")
    table = template["tables"][0]
    documents = expected()
    rows = []
    for line in read(raw_path).split(b"\n"):
        if not line.strip(b" \t\r"):
            continue  # Only sqlcmd's blank display separators are removed.
        convert.require(len(line) <= 128 * 1024, "acceptance_row_limit")
        row = convert.parse_json(line)
        convert.require(type(row) is list and len(row) == len(table["columns"]), "acceptance_row_width")
        convert.require(len(rows) < 4, "acceptance_row_count")
        body = {}
        for column, value in zip(table["columns"], row):
            _, mapped = convert.convert_value(value, column)
            body[column["name"]] = mapped
        identifier = str(body["id"])
        convert.require(identifier == str(len(rows) + 1), "acceptance_row_order")
        same(body, documents[identifier], "acceptance_value_mismatch")
        # Normalize only hexadecimal case; exact decimal and temporal strings stay untouched.
        for i, column in enumerate(table["columns"]):
            if column["type"] in {"uniqueidentifier", "varbinary"} and row[i] is not None:
                row[i] = row[i].lower()
        rows.append(row)
    convert.require(len(rows) == 4, "acceptance_row_count")
    chunks = []
    for filename, group in (("rows-0001.jsonl", rows[:3]), ("rows-0002.jsonl", rows[3:])):
        data = b"".join((json.dumps(row, ensure_ascii=False, separators=(",", ":")) + "\n").encode("utf-8") for row in group)
        convert.require(data == read(HERE / "golden-capture" / filename), "acceptance_golden_capture_mismatch")
        chunks.append((filename, data))
    table["chunks"] = [{"file": filename, "bytes": len(data),
                        "sha256": hashlib.sha256(data).hexdigest(), "rows": 3 if index == 0 else 1}
                       for index, (filename, data) in enumerate(chunks)]
    convert.validate_manifest(template)
    destination = Path(destination)
    destination.mkdir(mode=0o700)
    try:
        for filename, data in chunks:
            with (destination / filename).open("xb") as stream:
                os.chmod(destination / filename, 0o600)
                stream.write(data)
        with (destination / "manifest.json").open("x", encoding="utf-8") as stream:
            os.chmod(destination / "manifest.json", 0o600)
            json.dump(template, stream, indent=2, ensure_ascii=True)
            stream.write("\n")
    except BaseException:
        for filename in ("rows-0001.jsonl", "rows-0002.jsonl", "manifest.json"):
            (destination / filename).unlink(missing_ok=True)
        destination.rmdir()
        raise


def artifact(directory):
    data = read(Path(directory) / "import.sql")
    convert.require(data == read(HERE / "golden-output" / "import.sql"), "acceptance_golden_artifact_mismatch")
    provenance = load(Path(directory) / "provenance.json")
    same(provenance, load(HERE / "golden-output" / "provenance.json"), "acceptance_provenance_mismatch")
    convert.require(provenance["output"]["rows"] == 4 and provenance["output"]["bytes"] == len(data)
                    and provenance["output"]["sha256"] == hashlib.sha256(data).hexdigest(), "acceptance_provenance_mismatch")
    return data


def preview(path, directory):
    job = load(path)
    data = artifact(directory)
    convert.require(job["state"] == "ready" and job["source"]["format"] == "postgres_plain"
                    and job["source"]["sha256"] == hashlib.sha256(data).hexdigest()
                    and job["source"]["bytes"] == len(data), "acceptance_source_binding")
    plan = job["preview"]
    convert.require(type(plan["blocking"]) is int and plan["blocking"] == 0
                    and len(plan["tables"]) == 1, "acceptance_preview_blocked")
    table = plan["tables"][0]
    convert.require(table["source"] == "dbo.sqlserver_acceptance" and table["collection"] == "dbo_sqlserver_acceptance"
                    and table["rows"] == 4 and table["idPolicy"] == "primary_key" and table["excluded"] is False
                    and table["primaryKey"] == ["id"], "acceptance_table_plan")
    columns = load(HERE / "manifest-template.json")["tables"][0]["columns"]
    same([(c["name"], c["mapping"], c["nullable"], c["excluded"]) for c in table["columns"]],
         [(c["name"], convert.pg_type(c)[1], c["nullable"], False) for c in columns], "acceptance_column_plan")
    convert.require(type(plan["id"]) is str and re.fullmatch(r"[0-7][0-9A-HJKMNP-TV-Z]{25}", plan["id"]), "acceptance_preview_id")


def completed(path, directory):
    job = load(path)
    data = artifact(directory)
    convert.require(job["state"] == "completed" and job["source"]["sha256"] == hashlib.sha256(data).hexdigest(),
                    "acceptance_not_completed")
    validation = job["validation"]
    convert.require(validation["passed"] is True and len(validation["tables"]) == 1, "acceptance_validation")
    table = validation["tables"][0]
    convert.require(table["source"] == "dbo.sqlserver_acceptance" and table["collection"] == "dbo_sqlserver_acceptance"
                    and type(table["sourceRows"]) is int and type(table["documents"]) is int
                    and table["sourceRows"] == table["documents"] == 4 and table["match"] is True
                    and re.fullmatch(r"[0-9a-f]{64}", table["sourceChecksum"])
                    and table["sourceChecksum"] == table["documentChecksum"], "acceptance_count_checksum")


def documents(path):
    page = load(path)
    convert.require(page["next_cursor"] is None and len(page["items"]) == 4, "acceptance_document_count")
    wanted = expected()
    seen = set()
    for item in page["items"]:
        identifier = item["id"]
        convert.require(type(identifier) is str and identifier in wanted and identifier not in seen,
                        "acceptance_document_id")
        convert.require(item["state"] == "value" and type(item["version"]) is str and item["version"], "acceptance_document_envelope")
        same(item["document"], wanted[identifier], "acceptance_document_value")
        seen.add(identifier)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    command = sub.add_parser("seal")
    command.add_argument("raw")
    command.add_argument("capture")
    command = sub.add_parser("artifact")
    command.add_argument("directory")
    for name in ("preview", "completed"):
        command = sub.add_parser(name)
        command.add_argument("receipt")
        command.add_argument("directory")
    command = sub.add_parser("documents")
    command.add_argument("receipt")
    args = parser.parse_args()
    try:
        if args.command == "seal":
            seal(args.raw, args.capture)
        elif args.command == "artifact":
            artifact(args.directory)
        elif args.command == "preview":
            preview(args.receipt, args.directory)
        elif args.command == "completed":
            completed(args.receipt, args.directory)
        else:
            documents(args.receipt)
    except convert.Invalid as error:
        print(f"acceptance_refused:{error}", file=sys.stderr)
        return 1
    except (OSError, ValueError, KeyError, TypeError, RecursionError):
        print("acceptance_refused:invalid_receipt_or_local_io", file=sys.stderr)
        return 1
    print("offline_fixture_check_passed:" + args.command)
    return 0


if __name__ == "__main__":
    sys.exit(main())
