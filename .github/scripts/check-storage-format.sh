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
import os
import re
import subprocess
import sys

format_file = "src/TerraBase/Engine/StorageFormat.cs"
storage = re.compile(r"^(src/TerraBase\.Storage/|src/TerraBase/Engine/|native/native-pin\.env$|native/build-native\.sh$)")
base = sys.argv[1]


def fail(message):
    sys.exit("storage-format: " + message)


def format_id(source):
    match = re.search(r'public const string Id = "([a-z0-9.-]+)";', source or "")
    return match[1] if match else None


current = format_id(open(format_file).read())
if current is None:
    fail(f"{format_file} must declare public const string Id")
shown = subprocess.run(["git", "show", f"{base}:{format_file}"], capture_output=True, text=True)
previous = format_id(shown.stdout) if shown.returncode == 0 else None
changed = [name for name in subprocess.run(["git", "diff", "--name-only", base, "HEAD"], capture_output=True, text=True, check=True).stdout.split()
           if storage.search(name)]
labels = set(os.environ["LABELS"].split())
pending = "storage-format" in labels and "Needs format bump" in os.environ["BODY"]
bumped = current != previous
if {"storage-format", "storage-format-unchanged"} <= labels:
    fail("the labels storage-format and storage-format-unchanged contradict each other")
if bumped and "storage-format" not in labels:
    fail(f"StorageFormat.Id moves from {previous} to {current}: label the pull request storage-format after the release rehearsal")
if "storage-format" in labels and not bumped and not pending:
    fail("labelled storage-format: change StorageFormat.Id, or state Needs format bump")
if changed and not bumped and "storage-format-unchanged" not in labels and not pending:
    fail("on-disk format files changed (" + " ".join(changed) + "): change StorageFormat.Id, or label storage-format-unchanged once a reviewer confirms the bytes on disk stay the same")
print(f"storage-format: ok (Id {current}; base {previous})")
PY
