#!/usr/bin/env python3
"""Offline canonical Extended JSON v2 JSONL -> contract-v1 PostgreSQL COPY."""

import argparse
import base64
import binascii
import decimal
import hashlib
import json
import math
import os
from pathlib import Path
import re
import sqlite3
import stat
import sys
import tempfile

MAX_SOURCE = 1024**3
MAX_LINE = 16 * 1024**2
MAX_DOCUMENT = 1024**2
ADAPTER = "mongodb-ejson-v1"
FORMAT = "mongoexport-canonical-ejson-v2-jsonl"
HEADER = (
    "-- MongoDB offline adapter; import contract v1 revision 3\n"
    "SET client_encoding = 'UTF8';\n"
    "SET standard_conforming_strings = on;\n"
    "CREATE TABLE public.mongodb_documents (\n"
    "    id text NOT NULL PRIMARY KEY,\n"
    "    document jsonb NOT NULL,\n"
    "    provenance jsonb NOT NULL\n"
    ");\n"
    "COPY public.mongodb_documents (id, document, provenance) FROM stdin;\n"
)
INTEGER = re.compile(r"-?(0|[1-9][0-9]*)\Z")
NUMBER = re.compile(r"[+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?\Z")
DECIMAL128 = decimal.Context(
    prec=34, Emin=-6143, Emax=6144, clamp=1,
    traps=[decimal.InvalidOperation, decimal.Inexact, decimal.Overflow],
)


class ConversionError(Exception):
    """Messages contain positions and fixed descriptions, never source values."""


class JsonNumber:
    def __init__(self, text):
        self.text = text


def reject_constant(_):
    raise ConversionError("non-JSON numeric constant")


def pairs(items):
    result = {}
    for key, value in items:
        if key in result:
            raise ConversionError("duplicate JSON member")
        result[key] = value
    return result


def text(value):
    if not isinstance(value, str) or "\0" in value:
        raise ConversionError("expected a string without NUL")
    try:
        value.encode("utf-8", errors="strict")
    except UnicodeError:
        raise ConversionError("unpaired Unicode surrogate") from None
    return value


def integer(value, bits):
    if not isinstance(value, str) or len(value) > 20 or not INTEGER.fullmatch(value):
        raise ConversionError("invalid canonical integer wrapper")
    number = int(value)
    if not -(2 ** (bits - 1)) <= number < 2 ** (bits - 1) or value == "-0":
        raise ConversionError("integer outside BSON range or noncanonical zero")
    return number


def validate(value, depth=1, root=False):
    if depth > 16:
        raise ConversionError("source nesting exceeds adapter limit")
    if value is None or isinstance(value, bool):
        return
    if isinstance(value, str):
        text(value)
        return
    if isinstance(value, list):
        for item in value:
            validate(item, depth + 1)
        return
    if not isinstance(value, dict):
        raise ConversionError("relaxed/raw numbers are unsupported")
    for key in value:
        text(key)
    special = [key for key in value if key.startswith("$")]
    if not special:
        for item in value.values():
            validate(item, depth + 1)
        return
    if root or len(value) != 1:
        raise ConversionError("ambiguous dollar-prefixed document members")
    kind, payload = next(iter(value.items()))
    if kind == "$oid":
        if not isinstance(payload, str) or not re.fullmatch(r"[0-9a-f]{24}", payload):
            raise ConversionError("invalid canonical ObjectId")
    elif kind in ("$numberInt", "$numberLong"):
        integer(payload, 32 if kind == "$numberInt" else 64)
    elif kind in ("$numberDecimal", "$numberDouble"):
        if not isinstance(payload, str) or len(payload) > 128:
            raise ConversionError("invalid numeric wrapper")
        if payload in ("NaN", "Infinity", "-Infinity"):
            return
        if not NUMBER.fullmatch(payload):
            raise ConversionError("invalid numeric wrapper")
        if kind == "$numberDecimal":
            try:
                DECIMAL128.copy().create_decimal(payload)
            except decimal.DecimalException:
                raise ConversionError("Decimal128 would round or overflow") from None
        else:
            parsed = float(payload)
            if not math.isfinite(parsed) or (parsed == 0 and decimal.Decimal(payload) != 0):
                raise ConversionError("double would overflow or underflow")
    elif kind == "$date":
        if not isinstance(payload, dict) or set(payload) != {"$numberLong"}:
            raise ConversionError("date must be canonical UTC epoch milliseconds")
        integer(payload["$numberLong"], 64)
    elif kind == "$binary":
        if not isinstance(payload, dict) or set(payload) != {"base64", "subType"}:
            raise ConversionError("invalid canonical binary wrapper")
        encoded, subtype = payload["base64"], payload["subType"]
        if not isinstance(encoded, str) or not isinstance(subtype, str) or not re.fullmatch(r"[0-9a-f]{2}", subtype):
            raise ConversionError("invalid binary encoding or subtype")
        try:
            decoded = base64.b64decode(encoded, validate=True)
        except (ValueError, binascii.Error):
            raise ConversionError("invalid base64") from None
        if base64.b64encode(decoded).decode("ascii") != encoded:
            raise ConversionError("noncanonical base64")
    elif kind == "$timestamp":
        if not isinstance(payload, dict) or set(payload) != {"t", "i"}:
            raise ConversionError("invalid BSON timestamp wrapper")
        for key in ("t", "i"):
            number = payload[key]
            if not isinstance(number, JsonNumber) or not re.fullmatch(r"0|[1-9][0-9]{0,9}", number.text) or int(number.text) >= 2**32:
                raise ConversionError("BSON timestamp components must be uint32")
            payload[key] = int(number.text)
    else:
        raise ConversionError("unsupported Extended JSON wrapper or dollar field")


def dumps(value):
    return json.dumps(value, ensure_ascii=True, separators=(",", ":"), allow_nan=False)


def key_for(document):
    if "_id" not in document:
        raise ConversionError("missing _id")
    value = document["_id"]
    if isinstance(value, str):
        return dumps(["string", value]).encode("ascii")
    if isinstance(value, dict) and len(value) == 1:
        kind, payload = next(iter(value.items()))
        if kind == "$oid":
            return dumps(["objectId", payload]).encode("ascii")
        if kind in ("$numberInt", "$numberLong"):
            return dumps(["integer", str(int(payload))]).encode("ascii")
    raise ConversionError("_id supports only string, ObjectId, int32 or int64")


def document_id(database, collection, key):
    # Fixed ULID time zero, 80 hash bits; the disk index rejects any collision.
    identity = dumps([ADAPTER, database, collection, json.loads(key)]).encode("ascii")
    number = int.from_bytes(hashlib.sha256(identity).digest()[:10], "big")
    alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"
    result = ""
    for _ in range(26):
        result = alphabet[number & 31] + result
        number >>= 5
    return result


def check_body(body):
    nodes = 0
    pending = [(body, 1)]
    while pending:
        value, depth = pending.pop()
        nodes += 1
        if nodes > 4096:
            raise ConversionError("mapped document exceeds 4096 values")
        if isinstance(value, (dict, list)):
            if depth > 16:
                raise ConversionError("mapped document exceeds 16 levels")
            children = value.values() if isinstance(value, dict) else value
            pending.extend((item, depth + 1) for item in children)
    if len(dumps(body).encode("ascii")) > MAX_DOCUMENT:
        raise ConversionError("mapped document exceeds 1 MiB")


def copy_field(value):
    return value.replace("\\", "\\\\").replace("\t", "\\t").replace("\n", "\\n").replace("\r", "\\r")


def signature(file):
    info = os.fstat(file.fileno())
    return (info.st_dev, info.st_ino, info.st_size, info.st_mtime_ns, info.st_ctime_ns)


def source_digest(file, expected_bytes):
    digest = hashlib.sha256()
    total = 0
    while chunk := file.read(1024**2):
        total += len(chunk)
        if total > expected_bytes:
            raise ConversionError("source grew during hashing")
        digest.update(chunk)
    if total != expected_bytes:
        raise ConversionError("source changed during hashing")
    return digest.hexdigest()


def convert(source, output, database, collection):
    for label in (database, collection):
        text(label)
        if not 1 <= len(label.encode("utf-8")) <= 255 or any(ord(char) < 32 or ord(char) == 127 for char in label):
            raise ConversionError("namespace labels must be 1 to 255 UTF-8 bytes without controls")
    if os.path.lexists(output):
        raise ConversionError("output already exists; choose a new path")
    descriptor = os.open(source, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
    with os.fdopen(descriptor, "rb") as input_file:
        if not stat.S_ISREG(os.fstat(input_file.fileno()).st_mode):
            raise ConversionError("source must be a regular nonsymlink file")
        initial = signature(input_file)
        if not 0 < initial[2] <= MAX_SOURCE:
            raise ConversionError("source must be nonempty and at most 1 GiB")
        source_hash = source_digest(input_file, initial[2])
        if signature(input_file) != initial:
            raise ConversionError("source changed during hashing")
        input_file.seek(0)
        with tempfile.TemporaryDirectory(prefix=".mongodb-", dir=output.parent) as scratch:
            artifact = Path(scratch) / "export.sql"
            seen = sqlite3.connect(Path(scratch) / "seen.sqlite")
            try:
                seen.execute("CREATE TABLE seen (source_key BLOB PRIMARY KEY, target_id TEXT UNIQUE)")
                seen.execute("PRAGMA cache_size = -2048")
                second_hash = hashlib.sha256()
                source_bytes = 0
                row = 0
                with os.fdopen(os.open(artifact, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600), "wb") as target:
                    target.write(HEADER.encode("ascii"))
                    while raw := input_file.readline(MAX_LINE + 1):
                        row += 1
                        try:
                            if len(raw) > MAX_LINE:
                                raise ConversionError("source record exceeds 16 MiB")
                            source_bytes += len(raw)
                            if source_bytes > initial[2]:
                                raise ConversionError("source grew during conversion")
                            second_hash.update(raw)
                            document = json.loads(raw.decode("utf-8", errors="strict"), object_pairs_hook=pairs,
                                                  parse_int=JsonNumber, parse_float=JsonNumber, parse_constant=reject_constant)
                            if not isinstance(document, dict):
                                raise ConversionError("each line must hold one document object")
                            validate(document, root=True)
                            key = key_for(document)
                            identity = document_id(database, collection, key)
                            try:
                                seen.execute("INSERT INTO seen VALUES (?, ?)", (key, identity))
                            except sqlite3.IntegrityError:
                                raise ConversionError("duplicate _id or target ID collision") from None
                            provenance = {"adapter": ADAPTER, "sourceFormat": FORMAT, "sourceSha256": source_hash,
                                          "database": database, "collection": collection, "sourceRecord": row}
                            check_body({"id": identity, "document": document, "provenance": provenance})
                            line = "\t".join(copy_field(field) for field in (identity, dumps(document), dumps(provenance))) + "\n"
                            target.write(line.encode("ascii"))
                            if target.tell() + 3 > MAX_SOURCE:
                                raise ConversionError("output exceeds core's 1 GiB artifact limit")
                        except (UnicodeError, json.JSONDecodeError, RecursionError):
                            raise ConversionError(f"record {row}: malformed UTF-8 JSON or excessive nesting") from None
                        except ConversionError as error:
                            raise ConversionError(f"record {row}: {error}") from None
                    if signature(input_file) != initial or second_hash.hexdigest() != source_hash:
                        raise ConversionError("source changed during conversion")
                    target.write(b"\\.\n")
                    target.flush()
                    os.fsync(target.fileno())
                # A hard link publishes the completed artifact atomically and refuses a racing existing output.
                with artifact.open("rb") as verified:
                    artifact_hash = hashlib.file_digest(verified, "sha256").hexdigest()
                os.link(artifact, output)
                return row, artifact.stat().st_size, artifact_hash
            finally:
                seen.close()


class Arguments(argparse.ArgumentParser):
    def error(self, message):
        self.exit(2, "error: invalid arguments; use --help\n")


def main():
    parser = Arguments(description=__doc__)
    parser.add_argument("source", type=Path, help="read-only canonical Extended JSON v2 JSONL file")
    parser.add_argument("output", type=Path, help="new PostgreSQL COPY text artifact; never overwritten")
    parser.add_argument("--database", required=True, help="source database label for provenance and ID derivation")
    parser.add_argument("--collection", required=True, help="source collection label for provenance and ID derivation")
    args = parser.parse_args()
    try:
        rows, size, digest = convert(args.source, args.output, args.database, args.collection)
    except ConversionError as error:
        print(f"error: {error}", file=sys.stderr)
        return 1
    except (OSError, sqlite3.Error):
        print("error: local file/index operation failed; output was not overwritten", file=sys.stderr)
        return 1
    print(f"converted {rows} documents; artifactBytes={size}; artifactSha256={digest}")
    print("warning: BSON types remain Extended JSON wrappers; indexes and MongoDB behavior are not imported")
    return 0


if __name__ == "__main__":
    sys.exit(main())
