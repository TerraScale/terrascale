#!/usr/bin/env python3
"""Convert sqlserver-jsonl-v1 captures to TerraBase's postgres_plain artifact."""

import argparse
import base64
from contextlib import closing
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import re
import sqlite3
import stat
import sys
import unicodedata

MIB = 1024 * 1024
MAX_SOURCE = 1024 * MIB
MAX_LINE = 16 * MIB
INTEGER = {
    "tinyint": (0, 255, "smallint"),
    "smallint": (-32768, 32767, "smallint"),
    "int": (-2147483648, 2147483647, "integer"),
    "bigint": (-9223372036854775808, 9223372036854775807, "bigint"),
}
SCALAR = {"bit": "boolean", "uniqueidentifier": "uuid", "date": "date"}
INTS = set(INTEGER)
SCALARS = set(SCALAR)
DECIMAL = {"decimal", "numeric", "money", "smallmoney"}
TEXT = {"nvarchar", "nchar"}
TEMPORAL = {"datetime2", "datetimeoffset", "time"}
BINARY = {"varbinary", "binary"}
WARNINGS = [
    "Decimals and money become exact JSON strings; currency semantics are lost.",
    "datetime2, datetimeoffset and time become strings; seven digits and offsets are retained, not normalized to UTC.",
    "Identifiers are preserved; collections follow the core schema_table naming rules (dbo is retained).",
    "Identity and computed values are captured only; generation rules are not recreated.",
    "Only listed base-table rows and primary keys are represented; other constraints, indexes, defaults, collation, permissions and database objects are not recreated.",
]


class Invalid(Exception):
    pass


def require(condition, code):
    if not condition:
        raise Invalid(code)


def exact_keys(value, expected):
    require(type(value) is dict and set(value) == set(expected), "manifest_fields")


def integer(value, low, high):
    require(type(value) is int and low <= value <= high, "manifest_integer")


def unicode_text(value):
    require(type(value) is str, "expected_string")
    try:
        value.encode("utf-8", errors="strict")
    except UnicodeError:
        raise Invalid("invalid_unicode") from None
    require("\0" not in value, "nul_not_supported")
    return value


def identifier(value):
    unicode_text(value)
    require(0 < len(value.encode("utf-8")) <= 63, "identifier_length")
    require(not any(unicodedata.category(c) == "Cc" for c in value), "identifier_control")
    return value


def quote(value):
    return '"' + value.replace('"', '""') + '"'


def no_float(_):
    raise Invalid("json_number_not_supported")


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, "duplicate_json_key")
        result[key] = value
    return result


def parse_json(raw):
    try:
        return json.loads(raw.decode("utf-8", errors="strict"),
                          object_pairs_hook=unique_object,
                          parse_float=no_float, parse_constant=no_float)
    except (ValueError, UnicodeError, RecursionError):
        raise Invalid("malformed_json_utf8") from None


def read_only(path):
    # Never open a supplied database or execute supplied SQL; inputs are regular JSON files.
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
    if not stat.S_ISREG(os.fstat(fd).st_mode):
        os.close(fd)
        raise Invalid("input_not_regular_file")
    return os.fdopen(fd, "rb")


def collection_name(schema, table):
    require(schema.isascii() and table.isascii(), "non_ascii_table_identifier")
    name = table if schema in {"public", "main"} else schema + "_" + table
    name = re.sub(r"[^a-z0-9_-]", "_", name.lower())
    if not re.match(r"[a-z]", name):
        name = "t_" + name
    name = name[:63]
    require(name not in {"collections", "commit_offset", "terrascale-import"}
            and not name.startswith(("sqlite_", "turso_")), "reserved_collection")
    return name


def validate_column(column):
    require(type(column) is dict, "manifest_column")
    kind = column.get("type")
    require(type(kind) is str and kind in INTS | SCALARS | DECIMAL | TEXT | TEMPORAL | BINARY,
            "unsupported_type")
    fields = {"name", "type", "nullable", "identity", "computed"}
    if kind in {"decimal", "numeric"}:
        fields |= {"precision", "scale"}
    if kind in TEMPORAL:
        fields |= {"scale"}
    if kind in TEXT | BINARY:
        fields |= {"length"}
    exact_keys(column, fields)
    identifier(column["name"])
    for flag in ("nullable", "identity", "computed"):
        require(type(column[flag]) is bool, "manifest_boolean")
    require(not (column["identity"] and column["computed"]), "identity_computed")
    require(not column["identity"] or kind in INTS | {"decimal", "numeric"}, "identity_type")
    if kind in {"decimal", "numeric"}:
        integer(column["precision"], 1, 38)
        integer(column["scale"], 0, column["precision"])
        require(not column["identity"] or column["scale"] == 0, "identity_scale")
    if kind in TEMPORAL:
        integer(column["scale"], 0, 7)
    if kind in TEXT | BINARY:
        length = column["length"]
        if length == "max":
            require(kind in {"nvarchar", "varbinary"}, "unsupported_max_length")
        else:
            integer(length, 1, 4000 if kind in TEXT else 8000)


def pg_type(column):
    kind = column["type"]
    if kind in INTEGER:
        return INTEGER[kind][2], "integer"
    if kind in SCALAR:
        return SCALAR[kind], {"bit": "boolean", "uniqueidentifier": "string", "date": "date"}[kind]
    if kind in DECIMAL:
        precision, scale = decimal_shape(column)
        return f"numeric({precision},{scale})", "decimal_string"
    if kind in BINARY:
        return "bytea", "base64"
    return "text", "string"


def decimal_shape(column):
    if column["type"] == "money":
        return 19, 4
    if column["type"] == "smallmoney":
        return 10, 4
    return column["precision"], column["scale"]


def validate_manifest(manifest):
    exact_keys(manifest, {"format", "source", "tables"})
    require(manifest["format"] == "sqlserver-jsonl-v1", "unsupported_format")
    source = manifest["source"]
    exact_keys(source, {"product", "majorVersion", "encoding", "consistency"})
    require(source["product"] == "Microsoft SQL Server" and source["encoding"] == "UTF-8", "unsupported_source")
    integer(source["majorVersion"], 13, 16)
    require(source["consistency"] in {"snapshot_transaction", "database_snapshot", "quiesced_copy"}, "unsupported_consistency")
    tables = manifest["tables"]
    require(type(tables) is list and 1 <= len(tables) <= 1000, "table_limit")
    names, collections, files = set(), set(), set()
    total_bytes = total_chunks = 0
    for table in tables:
        exact_keys(table, {"schema", "name", "columns", "primaryKey", "chunks"})
        pair = identifier(table["schema"]), identifier(table["name"])
        require(pair not in names, "duplicate_table")
        names.add(pair)
        collection = collection_name(*pair)
        require(collection not in collections, "collection_collision")
        collections.add(collection)
        columns = table["columns"]
        require(type(columns) is list and 1 <= len(columns) <= 1600, "column_limit")
        for column in columns:
            validate_column(column)
        by_name = {column["name"]: column for column in columns}
        require(len(by_name) == len(columns), "duplicate_column")
        pk = table["primaryKey"]
        require(type(pk) is list and 1 <= len(pk) <= len(columns), "primary_key_required")
        require(all(type(name) is str and name in by_name for name in pk), "primary_key_column")
        require(len(set(pk)) == len(pk), "duplicate_primary_key_column")
        for name in pk:
            column = by_name[name]
            require(not column["nullable"] and column["type"] in INTS | TEXT | {"uniqueidentifier"},
                    "unsupported_primary_key")
        chunks = table["chunks"]
        require(type(chunks) is list and len(chunks) >= 1, "chunks_required")
        total_chunks += len(chunks)
        require(total_chunks <= 1000, "chunk_limit")
        for chunk in chunks:
            exact_keys(chunk, {"file", "bytes", "sha256", "rows"})
            filename = chunk["file"]
            require(type(filename) is str and re.fullmatch(r"[A-Za-z0-9_-]+\.jsonl", filename), "unsafe_chunk_path")
            require(filename not in files, "duplicate_chunk")
            files.add(filename)
            integer(chunk["bytes"], 0, MAX_SOURCE)
            integer(chunk["rows"], 0, MAX_SOURCE)
            require(type(chunk["sha256"]) is str and re.fullmatch(r"[0-9a-f]{64}", chunk["sha256"]), "invalid_sha256")
            total_bytes += chunk["bytes"]
            require(total_bytes <= MAX_SOURCE, "source_size_limit")


def temporal(value, column):
    kind, scale = column["type"], column["scale"]
    fraction = r"\.[0-9]{" + str(scale) + "}" if scale else ""
    prefix = "" if kind == "time" else r"[0-9]{4}-[0-9]{2}-[0-9]{2}T"
    suffix = r"[+-][0-9]{2}:[0-9]{2}" if kind == "datetimeoffset" else ""
    require(re.fullmatch(prefix + r"[0-9]{2}:[0-9]{2}:[0-9]{2}" + fraction + suffix, value), "temporal_format")
    base = value[:8] if kind == "time" else value[:19]
    clock = base if kind == "time" else base[11:]
    require(int(clock[:2]) <= 23 and int(clock[3:5]) <= 59 and int(clock[6:8]) <= 59, "temporal_range")
    try:
        parsed = dt.time.fromisoformat(base) if kind == "time" else dt.datetime.fromisoformat(base)
        if kind == "datetimeoffset":
            offset = value[-6:]
            hours, minutes = int(offset[1:3]), int(offset[4:6])
            require(hours <= 14 and minutes <= 59 and (hours != 14 or minutes == 0), "offset_range")
            require(offset != "-00:00", "unknown_offset")
            delta = dt.timedelta(minutes=(hours * 60 + minutes) * (1 if offset[0] == "+" else -1))
            parsed - delta  # Validate both SQL Server local and UTC calendar ranges without dropping the 7th digit.
    except (ValueError, OverflowError):
        raise Invalid("temporal_range") from None
    return value


def convert_value(value, column):
    if value is None:
        require(column["nullable"], "null_not_allowed")
        return None, None
    kind = column["type"]
    if kind == "bit":
        require(type(value) is bool, "bit_boolean_required")
        return ("t" if value else "f"), value
    unicode_text(value)
    if kind in INTEGER:
        require(re.fullmatch(r"0|-?[1-9][0-9]*", value) and len(value) <= 20, "integer_format")
        low, high, _ = INTEGER[kind]
        number = int(value)
        require(low <= number <= high, "integer_range")
        return value, number
    if kind in DECIMAL:
        precision, scale = decimal_shape(column)
        require(len(value) <= 41 and re.fullmatch(r"-?(0|[1-9][0-9]*)(\.[0-9]+)?", value), "decimal_format")
        whole, _, fraction = value.lstrip("-").partition(".")
        require(len(fraction) == scale and len(whole.lstrip("0")) <= precision - scale, "decimal_precision_scale")
        if kind in {"money", "smallmoney"}:
            scaled = int(whole + fraction) * (-1 if value.startswith("-") else 1)
            low, high = INTEGER["bigint" if kind == "money" else "int"][:2]
            require(low <= scaled <= high, "money_range")
        return value, value
    if kind == "uniqueidentifier":
        require(re.fullmatch(r"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", value), "uuid_format")
        return value.lower(), value.lower()
    if kind in TEXT:
        units = len(value.encode("utf-16-le")) // 2
        if column["length"] != "max":
            require(units <= column["length"], "text_length")
            if kind == "nchar":
                require(units == column["length"], "nchar_padding_required")
        return value, value
    if kind in BINARY:
        require(re.fullmatch(r"0x(?:[0-9a-fA-F]{2})*", value), "binary_hex_required")
        data = bytes.fromhex(value[2:])
        if column["length"] != "max":
            require(len(data) <= column["length"], "binary_length")
            if kind == "binary":
                require(len(data) == column["length"], "binary_padding_required")
        return "\\x" + value[2:].lower(), base64.b64encode(data).decode("ascii")
    if kind == "date":
        require(re.fullmatch(r"[0-9]{4}-[0-9]{2}-[0-9]{2}", value), "date_format")
        try:
            dt.date.fromisoformat(value)
        except ValueError:
            raise Invalid("date_range") from None
        return value, value
    return temporal(value, column), value


def copy_cell(value):
    if value is None:
        return "\\N"
    return (value.replace("\\", "\\\\").replace("\t", "\\t").replace("\n", "\\n")
            .replace("\r", "\\r").replace("\b", "\\b").replace("\f", "\\f").replace("\v", "\\v"))


def document_id(table, values):
    by_name = dict(zip((c["name"] for c in table["columns"]), values))
    parts = [str(by_name[name]) for name in table["primaryKey"]]
    result = parts[0] if len(parts) == 1 else ":".join(p.replace("%", "%25").replace(":", "%3A") for p in parts)
    # .NET's character limit counts UTF-16 code units.
    require(1 <= len(result.encode("utf-16-le")) // 2 <= 256
            and not any(unicodedata.category(c) == "Cc" for c in result), "invalid_document_id")
    return result


def convert(manifest_path, output):
    manifest_path, output = Path(manifest_path), Path(output)
    root = manifest_path.resolve().parent
    require(not output.resolve().is_relative_to(root), "output_inside_snapshot")
    with read_only(manifest_path) as stream:
        raw = stream.read(MIB + 1)
    require(len(raw) <= MIB, "manifest_size_limit")
    manifest = parse_json(raw)
    validate_manifest(manifest)
    require(sum(chunk["bytes"] for t in manifest["tables"] for chunk in t["chunks"]) + len(raw) <= MAX_SOURCE,
            "source_size_limit")
    output.mkdir(mode=0o700)  # Exclusive: an existing path is never overwritten.
    sql_path, provenance_path, keys_path = output / ".import.tmp", output / ".provenance.tmp", output / ".keys.tmp"
    completed = False
    try:
        source_digest = hashlib.sha256(raw).hexdigest()
        digest, size, row_total = hashlib.sha256(), 0, 0
        mapped_tables = []
        with closing(sqlite3.connect(keys_path)) as keys, sql_path.open("xb") as sql:
            os.chmod(keys_path, 0o600)
            os.chmod(sql_path, 0o600)
            keys.execute("PRAGMA journal_mode=OFF")
            keys.execute("PRAGMA cache_size=-2048")
            keys.execute("PRAGMA page_size=4096")
            keys.execute("PRAGMA max_page_count=262144")
            keys.execute("CREATE TABLE seen (id TEXT PRIMARY KEY) WITHOUT ROWID")

            def write(text, statement=False):
                nonlocal size
                data = text.encode("utf-8")
                require(len(data) <= (MIB if statement else MAX_LINE), "output_record_limit")
                require(size + len(data) <= MAX_SOURCE, "output_size_limit")
                size += len(data)
                digest.update(data)
                sql.write(data)

            write("-- sqlserver-jsonl-v1; offline COPY artifact; see provenance.json\n")
            for ti, table in enumerate(manifest["tables"], 1):
                target = quote(table["schema"]) + "." + quote(table["name"])
                columns = table["columns"]
                definitions = [quote(c["name"]) + " " + pg_type(c)[0] + ("" if c["nullable"] else " NOT NULL") for c in columns]
                definitions.append("PRIMARY KEY (" + ", ".join(quote(n) for n in table["primaryKey"]) + ")")
                write("CREATE TABLE " + target + " (\n    " + ",\n    ".join(definitions) + "\n);\n", statement=True)
                write("COPY " + target + " (" + ", ".join(quote(c["name"]) for c in columns) + ") FROM stdin;\n", statement=True)
                keys.execute("DELETE FROM seen")
                table_rows = 0
                for chunk in table["chunks"]:
                    chunk_hash, chunk_bytes, chunk_rows = hashlib.sha256(), 0, 0
                    with read_only(root / chunk["file"]) as stream:
                        before = os.fstat(stream.fileno())
                        require(before.st_size == chunk["bytes"], "chunk_size_mismatch")
                        while True:
                            line = stream.readline(MAX_LINE + 1)
                            if not line:
                                break
                            table_rows += 1
                            chunk_rows += 1
                            chunk_bytes += len(line)
                            require(chunk_bytes <= chunk["bytes"] and chunk_rows <= chunk["rows"], "chunk_bounds")
                            require(len(line) <= MAX_LINE and line.endswith(b"\n"), "row_line_limit_or_termination")
                            chunk_hash.update(line)
                            row = parse_json(line)
                            require(type(row) is list and len(row) == len(columns), "row_width")
                            cells, values = [], []
                            for ci, (value, column) in enumerate(zip(row, columns), 1):
                                try:
                                    cell, mapped = convert_value(value, column)
                                except Invalid as error:
                                    raise Invalid(f"{error}:table={ti}:row={table_rows}:column={ci}") from None
                                cells.append(copy_cell(cell))
                                values.append(mapped)
                            body = dict(zip((c["name"] for c in columns), values))
                            # ASCII escaping is conservative relative to UTF-8 JSON, including control and supplementary characters.
                            require(len(json.dumps(body, ensure_ascii=True, separators=(",", ":")).encode("ascii")) <= MIB,
                                    "document_size_limit")
                            key = document_id(table, values)
                            try:
                                keys.execute("INSERT INTO seen(id) VALUES (?)", (key,))
                            except sqlite3.IntegrityError:
                                raise Invalid(f"duplicate_document_id:table={ti}:row={table_rows}") from None
                            except sqlite3.Error as error:
                                if error.sqlite_errorcode == sqlite3.SQLITE_FULL:
                                    raise Invalid("key_index_capacity") from None
                                raise
                            write("\t".join(cells) + "\n")
                        after = os.fstat(stream.fileno())
                    require((before.st_size, before.st_mtime_ns) == (after.st_size, after.st_mtime_ns), "source_changed")
                    require(chunk_rows == chunk["rows"] and chunk_bytes == chunk["bytes"], "chunk_count_mismatch")
                    require(chunk_hash.hexdigest() == chunk["sha256"], "chunk_checksum_mismatch")
                write("\\.\n")
                row_total += table_rows
                mapped_tables.append({
                    "schema": table["schema"], "table": table["name"], "target": target,
                    "collection": collection_name(table["schema"], table["name"]),
                    "rows": table_rows, "primaryKey": table["primaryKey"], "chunks": table["chunks"],
                    "columns": [{**c, "targetType": pg_type(c)[0], "mapping": pg_type(c)[1]} for c in columns],
                })
        provenance = {"adapter": "sqlserver-jsonl-v1", "contract": "docs/terrabase/import.md@54c0c4d9:v1-r3",
                      "source": manifest["source"], "manifestSha256": source_digest,
                      "output": {"format": "postgres_plain", "file": "import.sql", "bytes": size,
                                 "sha256": digest.hexdigest(), "rows": row_total},
                      "warnings": WARNINGS, "tables": mapped_tables}
        with provenance_path.open("x", encoding="utf-8") as stream:
            os.chmod(provenance_path, 0o600)
            json.dump(provenance, stream, ensure_ascii=True, indent=2, sort_keys=True)
            stream.write("\n")
        keys_path.unlink()
        provenance_path.rename(output / "provenance.json")
        sql_path.rename(output / "import.sql")  # The uploadable artifact appears only after all validation succeeds.
        completed = True
    finally:
        if not completed:
            for name in (".import.tmp", ".provenance.tmp", ".keys.tmp", ".keys.tmp-journal", "provenance.json", "import.sql"):
                (output / name).unlink(missing_ok=True)
            output.rmdir()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("manifest", type=Path)
    parser.add_argument("output_directory", type=Path, help="new directory outside the capture directory")
    args = parser.parse_args()
    try:
        convert(args.manifest, args.output_directory)
    except Invalid as error:
        print(f"conversion_refused:{error}", file=sys.stderr)
        return 1
    except (OSError, sqlite3.Error, TypeError, KeyError, ValueError, RecursionError):
        # Never echo source values, file paths or exception strings.
        print("conversion_refused:invalid_input_or_local_io", file=sys.stderr)
        return 1
    print("conversion_complete:import.sql:provenance.json")
    return 0


if __name__ == "__main__":
    sys.exit(main())
