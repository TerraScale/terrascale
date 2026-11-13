#!/usr/bin/env bash
set -euo pipefail
python3 - "${1:-HEAD^1}" <<'PY'
import datetime
import hashlib
import pathlib
import re
import subprocess
import sys

root = "src/TerraBase/Engine/Turso/"
base = sys.argv[1]


def fail(message):
    sys.exit("storage-format: " + message)


def previous(name):
    result = subprocess.run(["git", "show", base + ":" + name], capture_output=True, text=True)
    return result.stdout if result.returncode == 0 else None


def schema(source):
    values = {}
    pattern = r'private const string (\w+SchemaSql) = (\w+SchemaSql \+ )?"""\n(.*?)\n( *)""";'
    for name, prefix, body, indent in re.findall(pattern, source, re.S):
        values[name] = (values[prefix.split()[0]] if prefix else "") + "\n".join(
            line[len(indent):] if line else "" for line in body.split("\n"))
    if not {"SystemSchemaSql", "ClientSchemaSql"} <= values.keys():
        fail("schema constants could not be read")
    return hashlib.sha256((values["SystemSchemaSql"] + values["ClientSchemaSql"]).encode()).hexdigest()


def version(source):
    match = re.search(r"public const int Version = (\d+);", source)
    if not match:
        fail("storage version is missing")
    return int(match[1])


def lock(source):
    rows = {}
    for row in source.splitlines():
        number, digest = row.split()
        if not re.fullmatch(r"[0-9a-f]{64}", digest) or int(number) in rows:
            fail("invalid schema lock")
        rows[int(number)] = digest
    return rows


current = version(pathlib.Path(root + "StorageFormat.cs").read_text())
digest = schema(pathlib.Path(root + "TursoDatabaseStore.cs").read_text())
locked = lock(pathlib.Path(root + "storage-format.lock").read_text())
if locked.get(current) != digest:
    fail("schema text changed without a new locked version and migration step")
old_version = previous(root + "StorageFormat.cs")
if old_version is None:
    fail("comparison base is missing")
old = version(old_version)
old_lock = previous(root + "storage-format.lock")
baseline = lock(old_lock) if old_lock is not None else {
    old: schema(previous(root + "TursoDatabaseStore.cs"))}
if current < old:
    fail("storage versions only move forward")
for number, expected in baseline.items():
    if locked.get(number) != expected:
        fail("existing schema lock entries must remain unchanged")
if old == current and baseline[old] != digest:
    fail("schema text changed without a version increment")

migration = pathlib.Path(root + "StorageMigrations.cs").read_text()
pattern = r'new\((\d+),\s*(\d+),\s*"([0-9-]+)",\s*(.*?)\)\s*[,\]]'
steps = re.findall(pattern, migration, re.S)
numbers = []
for start, end, released, sql in steps:
    start, end = int(start), int(end)
    datetime.date.fromisoformat(released)
    if end != start + 1 or start not in locked or end not in locked or not sql.strip():
        fail("each step must advance one locked version")
    numbers.append(start)
if not numbers or numbers != list(range(numbers[0], current)):
    fail("migration steps must be ordered and reach the current version")
for number in range(old, current):
    if number not in numbers:
        fail("a version increment needs its migration step")
old_migration = previous(root + "StorageMigrations.cs")
if old_migration:
    today = datetime.date.today()
    for start, end, released, sql in re.findall(pattern, old_migration, re.S):
        if (today - datetime.date.fromisoformat(released)).days < 60 and (start, end, released, sql) not in steps:
            fail("migration steps must be retained unchanged for at least 60 days")
print("storage-format: ok (format " + str(current) + ", schema locked, forward path present)")
PY
