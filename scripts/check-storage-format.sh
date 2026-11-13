#!/usr/bin/env bash
set -euo pipefail
labels="" body=""
if [[ $# -eq 3 ]]; then
  labels=$(gh pr view "$3" -R "$2" --json labels --jq '.labels[].name')
  body=$(gh pr view "$3" -R "$2" --json body --jq '.body')
elif [[ $# -ne 1 ]]; then
  echo "usage: $0 <base revision> [<owner/repo> <pull request number>]" >&2
  exit 2
fi
LABELS=$labels BODY=$body python3 - "$1" <<'PY'
import datetime
import hashlib
import os
import pathlib
import re
import subprocess
import sys

root = str(next(pathlib.Path("src/TerraBase/Engine").glob("*/storage-format.lock")).parent) + "/"
client = str(next(pathlib.Path("src/vendor").glob("*.Data.Sqlite"))) + "/"
pin = str(next(pathlib.Path("benchmark").glob("*-sync-host/*-pin.env")))
base = sys.argv[1]
storage = re.compile("|".join([
    "^" + re.escape(root),
    r"^src/TerraBase/Engine/StoredDatabaseFile\.cs$",
    r"^src/TerraBase/Engine/Revisions/RevisionManifest(File)?\.cs$",
    r"^src/TerraBase/Features/Backup/SnapshotBundle\.cs$",
    "^" + re.escape(client),
    "^" + re.escape(pin) + "$",
]))
step_pattern = re.compile(r'new\((\d+),\s*(\d+),\s*"([0-9-]+)",\s*("""\n?(.*?)"""|"((?:[^"\\]|\\.)*)")\)', re.S)


def fail(message):
    sys.exit("storage-format: " + message)


def show(revision, name):
    result = subprocess.run(["git", "show", revision + ":" + name], capture_output=True, text=True)
    return result.stdout if result.returncode == 0 else None


def schema(source):
    values = {}
    pattern = r'private const string (\w+SchemaSql) = (\w+SchemaSql \+ )?"""\n(.*?)\n( *)""";'
    for name, prefix, text, indent in re.findall(pattern, source, re.S):
        values[name] = (values[prefix.split()[0]] if prefix else "") + "\n".join(
            line[len(indent):] if line else "" for line in text.split("\n"))
    if not {"SystemSchemaSql", "ClientSchemaSql"} <= values.keys():
        fail("schema constants could not be read")
    return hashlib.sha256((values["SystemSchemaSql"] + values["ClientSchemaSql"]).encode()).hexdigest()


def formats(source):
    def constant(name):
        match = re.search(r"public const int " + name + r" = (\d+);", source)
        return int(match[1]) if match else None
    if constant("Version") is not None:
        version = constant("Version")
        return version, version, version
    found = constant("SupportedFrom"), constant("SupportedTo"), constant("Target")
    if None in found:
        fail("StorageFormat must declare SupportedFrom, SupportedTo and Target")
    return found


def lock(source):
    rows = {}
    for row in (source or "").splitlines():
        number, digest = row.split()
        if not re.fullmatch(r"[0-9a-f]{64}", digest) or int(number) in rows:
            fail("invalid schema lock")
        rows[int(number)] = digest
    return rows


def steps(source):
    return [(int(start), int(end), released, (raw if raw else plain).strip())
            for start, end, released, _, raw, plain in step_pattern.findall(source or "")]


current = open(root + "StorageFormat.cs").read()
low, high, target = formats(current)
if not low <= target <= high:
    fail(f"Target {target} must lie inside Supported {low} to {high}")
digest = schema(next(pathlib.Path(root).glob("*DatabaseStore.cs")).read_text())
locked = lock(open(root + "storage-format.lock").read())
for number in range(low, high + 1):
    if number not in locked:
        fail(f"supported format {number} has no schema lock entry")
if locked[target] != digest:
    fail(f"the schema text no longer matches the lock entry of Target {target}: add a format step instead")

declared = steps(open(root + "StorageMigrations.cs").read())
for start, end, released, sql in declared:
    datetime.date.fromisoformat(released)
    if end != start + 1 or start not in locked or end not in locked or not sql:
        fail(f"step {start} to {end} must advance one locked format and carry SQL")
for number in range(low, high):
    if not any(start == number for start, _, _, _ in declared):
        fail(f"supported formats {number} and {number + 1} need a migration step")

base_format = show(base, root + "StorageFormat.cs")
if base_format is None:
    fail("comparison base is missing")
base_low, base_high, base_target = formats(base_format)
for number, expected in lock(show(base, root + "storage-format.lock")).items():
    if locked.get(number) != expected:
        fail("existing schema lock entries must remain unchanged")
if target < base_target:
    fail(f"Target only moves forward ({base_target} to {target})")
if target != base_target and not base_low <= target <= base_high:
    fail(f"Target {target} is not supported by the base build ({base_low} to {base_high}): first ship a build that supports it, then raise Target")
if not low <= base_target <= high:
    fail(f"this build must open the base build's Target {base_target}")
if high < base_high:
    fail(f"a supported format was dropped from the top ({base_high} to {high})")
today = datetime.date.today()
for step in steps(show(base, root + "StorageMigrations.cs")):
    if (today - datetime.date.fromisoformat(step[2])).days < 60 and step not in declared:
        fail("migration steps must be retained unchanged for at least 60 days")

labels = set(os.environ["LABELS"].split())
pending = "storage-format" in labels and "Needs format bump" in os.environ["BODY"]
changed = [name for name in subprocess.run(["git", "diff", "--name-only", base, "HEAD"], capture_output=True, text=True, check=True).stdout.split()
           if storage.search(name)]
introduced = high > base_high
if {"storage-format", "storage-format-unchanged"} <= labels:
    fail("the labels storage-format and storage-format-unchanged contradict each other")
if target != base_target and "storage-format" not in labels:
    fail(f"Target moves from {base_target} to {target}: label the pull request storage-format after the release rehearsal")
if "storage-format" in labels and target == base_target and not introduced and not pending:
    fail("labelled storage-format: add a format step, raise Target, or state Needs format bump")
if changed and not introduced and "storage-format-unchanged" not in labels and not pending:
    fail("on-disk format files changed (" + " ".join(changed) + "): add a format step, or label storage-format-unchanged once a reviewer confirms the bytes on disk stay the same")
print(f"storage-format: ok (Supported {low} to {high}, Target {target}; base Supported {base_low} to {base_high}, Target {base_target})")
PY
