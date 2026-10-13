#!/usr/bin/env python3
"""Offline acceptance evidence checks; never contacts or starts a service."""

import argparse
import decimal
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import struct
import sys

HERE = Path(__file__).resolve().parent
MAX_FILE = 4 * 1024**2
DATABASE = "mongodb_adapter_acceptance"
COLLECTION = "records"


def require(condition, code):
    if not condition:
        raise ValueError(code)


def pairs(items):
    result = {}
    for key, value in items:
        require(key not in result, "duplicate_json_member")
        result[key] = value
    return result


def read(path):
    require(path.is_file() and not path.is_symlink() and path.stat().st_size <= MAX_FILE, "missing_or_oversized_evidence")
    with path.open("rb") as input_file:
        raw = input_file.read(MAX_FILE + 1)
    require(len(raw) <= MAX_FILE, "oversized_evidence")
    return raw


def parse(raw):
    return json.loads(raw.decode("utf-8"), object_pairs_hook=pairs)


def write(path, value):
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(descriptor, "w", encoding="utf-8") as output:
        json.dump(value, output, ensure_ascii=True, sort_keys=True, indent=2, allow_nan=False)
        output.write("\n")


def canonical(value):
    return json.dumps(value, sort_keys=True, ensure_ascii=True, separators=(",", ":"), allow_nan=False).encode("ascii")


def bodies_hash(bodies):
    return hashlib.sha256(canonical(sorted(bodies, key=lambda body: body["id"]))).hexdigest()


def typed_key(document):
    value = document["_id"]
    if isinstance(value, str):
        return ["string", value]
    if set(value) == {"$oid"}:
        return ["objectId", value["$oid"]]
    require(set(value) in ({"$numberInt"}, {"$numberLong"}), "unexpected_id_type")
    return ["integer", str(int(next(iter(value.values()))))]


def identity(document):
    key = ["mongodb-ejson-v1", DATABASE, COLLECTION, typed_key(document)]
    encoded = json.dumps(key, ensure_ascii=True, separators=(",", ":")).encode("ascii")
    value = int.from_bytes(hashlib.sha256(encoded).digest()[:10], "big")
    alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"
    result = ""
    for _ in range(26):
        result = alphabet[value & 31] + result
        value >>= 5
    return result


def same_bson(actual, expected):
    if isinstance(expected, dict):
        require(isinstance(actual, dict) and set(actual) == set(expected), "source_members_or_wrappers_changed")
        if set(expected) == {"$numberDouble"}:
            a, b = actual["$numberDouble"], expected["$numberDouble"]
            if b in ("NaN", "Infinity", "-Infinity"):
                require(a == b, "double_special_changed")
            else:
                require(struct.pack("!d", float(a)) == struct.pack("!d", float(b)), "double_bits_changed")
        elif set(expected) == {"$numberDecimal"}:
            a, b = decimal.Decimal(actual["$numberDecimal"]), decimal.Decimal(expected["$numberDecimal"])
            require(a.as_tuple() == b.as_tuple(), "decimal_value_scale_or_sign_changed")
        else:
            for key in expected:
                same_bson(actual[key], expected[key])
    elif isinstance(expected, list):
        require(isinstance(actual, list) and len(actual) == len(expected), "source_array_changed")
        for a, b in zip(actual, expected):
            same_bson(a, b)
    else:
        require(type(actual) is type(expected) and actual == expected, "source_scalar_changed")


def unescape(field):
    result = []
    i = 0
    while i < len(field):
        char = field[i]
        if char == "\\":
            i += 1
            require(i < len(field) and field[i] in "\\tnr", "unsupported_copy_escape")
            char = {"\\": "\\", "t": "\t", "n": "\n", "r": "\r"}[field[i]]
        result.append(char)
        i += 1
    return "".join(result)


def seed_documents():
    documents = [parse(line) for line in read(HERE / "seed.jsonl").splitlines()]
    documents[0]["chunked"] = "x" * 700000
    return documents


def seed(run):
    documents = seed_documents()
    descriptor = os.open(run / "seed-full.jsonl", os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(descriptor, "wb") as output:
        for document in documents:
            output.write(canonical(document) + b"\n")
    return {"seedRows": len(documents), "chunkedCharacters": 700000}


def auth_config(run):
    path = run / "core-auth.conf"
    raw = read(path)
    require(stat.S_IMODE(path.stat().st_mode) == 0o600, "auth_config_must_be_private")
    require(bool(re.fullmatch(rb'header = "Authorization: Bearer [A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+"\n', raw)),
            "auth_config_must_contain_only_one_local_jwt_header")
    return {"privateAuthConfigChecked": True}


def poll_state(run, phase):
    job = parse(read(run / ("preview.json" if phase == "preview-state" else "completed.json")))
    state = job["state"]
    if phase == "preview-state":
        require(state in ("analyzing", "ready"), "unexpected_preview_state")
        return 0 if state == "ready" else 2
    require(state in ("queued", "running", "validating", "completed"), "unexpected_completion_state")
    require(type(job["source"]["staged"]) is bool, "invalid_staging_state")
    return 0 if state == "completed" and job["source"]["staged"] is False else 2


def prepare(run):
    source, artifact = read(run / "export.jsonl"), read(run / "converted.sql")
    documents = [parse(line) for line in source.splitlines()]
    seed = seed_documents()
    seeded = [parse(line) for line in read(run / "seed-full.jsonl").splitlines()]
    require(canonical(seeded) == canonical(seed), "generated_seed_changed")
    require(len(documents) == len(seed) == 3, "source_row_count_changed")
    expected_seed = {canonical(typed_key(doc)): doc for doc in seed}
    require(len({canonical(typed_key(doc)) for doc in documents}) == 3, "duplicate_source_id")
    for document in documents:
        same_bson(document, expected_seed[canonical(typed_key(document))])
    header, marker, data = artifact.decode("ascii").partition(
        "COPY public.mongodb_documents (id, document, provenance) FROM stdin;\n")
    require(bool(marker) and "id text NOT NULL PRIMARY KEY" in header and data.endswith("\\.\n"), "unexpected_copy_shape")
    rows = data[:-3].splitlines()
    require(len(rows) == 3, "artifact_row_count_changed")
    source_sha = hashlib.sha256(source).hexdigest()
    bodies = []
    for ordinal, (document, line) in enumerate(zip(documents, rows), 1):
        fields = [unescape(field) for field in line.split("\t")]
        require(len(fields) == 3 and fields[0] == identity(document), "mapped_id_changed")
        provenance = {"adapter": "mongodb-ejson-v1", "sourceFormat": "mongoexport-canonical-ejson-v2-jsonl",
                      "sourceSha256": source_sha, "database": DATABASE, "collection": COLLECTION, "sourceRecord": ordinal}
        require(canonical(parse(fields[1].encode())) == canonical(document)
                and canonical(parse(fields[2].encode())) == canonical(provenance), "mapped_body_changed")
        bodies.append({"id": fields[0], "document": document, "provenance": provenance})
    manifest = {"sourceRows": 3, "sourceSha256": source_sha, "artifactBytes": len(artifact),
                "artifactSha256": hashlib.sha256(artifact).hexdigest(), "bodiesSha256": bodies_hash(bodies),
                "bodies": bodies, "pins": parse(read(HERE / "pins.json"))}
    write(run / "expected.json", manifest)
    write(run / "create-import.json", {"format": "postgres_plain", "sourceBytes": len(artifact),
          "sourceSha256": manifest["artifactSha256"], "options": {"missingPrimaryKey": "reject", "exclude": []}})
    return {key: value for key, value in manifest.items() if key not in ("bodies", "pins")}


def preview(run, expected):
    job = parse(read(run / "preview.json"))
    plan = job["preview"]
    require(job["state"] == "ready" and plan["blocking"] == 0 and plan["warnings"] == 0, "preview_not_clear")
    require(len(plan["tables"]) == 1, "unexpected_preview_tables")
    table = plan["tables"][0]
    require(table["source"] == "public.mongodb_documents" and table["collection"] == "mongodb_documents"
            and table["rows"] == 3 and not table["excluded"] and table["idPolicy"] == "primary_key"
            and table["primaryKey"] == ["id"], "preview_mapping_changed")
    columns = {column["name"]: (column["mapping"], column["nullable"], column["excluded"]) for column in table["columns"]}
    require(columns == {"id": ("string", False, False), "document": ("json", False, False),
                        "provenance": ("json", False, False)}, "preview_columns_changed")
    require(bool(re.fullmatch(r"[0-7][0-9A-HJKMNP-TV-Z]{25}", plan["id"])), "invalid_preview_id")
    require(job["source"]["sha256"] == expected["artifactSha256"]
            and job["source"]["bytes"] == expected["artifactBytes"], "preview_artifact_changed")
    return plan["id"]


def start_body(run):
    expected = parse(read(run / "expected.json"))
    preview_id = preview(run, expected)
    status = read(run / "before-start.status").decode("ascii").strip()
    body = parse(read(run / "before-start.json"))
    require((status == "200" and body["items"] == [] and body["next_cursor"] is None)
            or (status == "404" and body["error"]["code"] == "unknown_collection"),
            "preview_wrote_documents_or_read_failed")
    write(run / "start.json", {"previewId": preview_id})
    return {"previewChecked": True, "sourceRows": 3}


def verify(run):
    expected = parse(read(run / "expected.json"))
    preview_id = preview(run, expected)
    done, page = parse(read(run / "completed.json")), parse(read(run / "documents.json"))
    require(done["state"] == "completed" and done["error"] is None
            and done["confirmedPreviewId"] == preview_id and done["validation"]["passed"], "import_not_validated")
    require(done["source"]["sha256"] == expected["artifactSha256"] and not done["source"]["staged"], "completed_source_changed")
    require(done["progress"]["rowsWritten"] == done["progress"]["rowsTotal"] == 3, "import_progress_count_changed")
    require(len(done["validation"]["tables"]) == 1, "validation_table_count_changed")
    table = done["validation"]["tables"][0]
    require(table["source"] == "public.mongodb_documents" and table["collection"] == "mongodb_documents"
            and table["sourceRows"] == table["documents"] == 3 and table["match"], "validation_counts_changed")
    checksum = table["sourceChecksum"]
    require(bool(re.fullmatch(r"[0-9a-f]{64}", checksum)) and checksum == table["documentChecksum"], "core_checksum_mismatch")
    require(page["next_cursor"] is None and len(page["items"]) == 3, "readback_count_changed")
    bodies = [item["document"] for item in page["items"]]
    require(all(item["id"] == item["document"]["id"] and item["state"] == "value" for item in page["items"]), "readback_envelope_changed")
    require(sorted(bodies, key=lambda body: body["id"]) == sorted(expected["bodies"], key=lambda body: body["id"]), "readback_body_or_wrappers_changed")
    require(bodies_hash(bodies) == expected["bodiesSha256"], "readback_acceptance_checksum_mismatch")
    result = {"capturedEvidencePassed": True, "rows": 3, "artifactSha256": expected["artifactSha256"],
              "sourceSha256": expected["sourceSha256"], "bodiesSha256": expected["bodiesSha256"],
              "coreChecksum": checksum, "pins": expected["pins"]}
    write(run / "verified.json", result)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("seed", "auth-config", "prepare", "preview-state", "completion-state", "start-body", "verify"))
    parser.add_argument("run", type=Path)
    args = parser.parse_args()
    try:
        run = args.run.resolve(strict=True)
        require(run.is_relative_to(HERE / "runs") and run.is_dir(), "run_must_be_inside_acceptance_runs")
        if args.action in ("preview-state", "completion-state"):
            return poll_state(run, args.action)
        result = {"seed": seed, "auth-config": auth_config, "prepare": prepare,
                  "start-body": start_body, "verify": verify}[args.action](run)
        print(json.dumps(result, sort_keys=True))
    except (OSError, ValueError, KeyError, TypeError, struct.error, decimal.DecimalException):
        print("acceptance evidence rejected; inspect the local procedure and synthetic evidence files", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
