#!/usr/bin/env bash
# Storage train (docs/terrabase/ci.md): a pull request that changes a storage file bumps StorageFormat.Version or carries
# the label storage-format-unchanged; a bump carries the label storage-format, and that label needs a bump. Needs GH_TOKEN
# with pull-requests:read. CI runs it in the `test` job; scripts/terrabase/storage-format-check-test.sh checks it.
#
#   scripts/terrabase/storage-format-check.sh <owner/repo> <pull request number>
set -euo pipefail

test "$#" -eq 2 || { echo "usage: $0 <owner/repo> <pull request number>" >&2; exit 2; }
repo=$1 pr=$2

# Files whose change can change what is on disk. The storage owners (T1 Turso engine, R5 counters and sets, R2b/R2c
# client tables) keep this list current.
storage='^src/TerraBase/Engine/(Sqlite|Lattices)/|^src/TerraBase/Engine/StoredDatabaseFile\.cs$'
version_file=src/TerraBase/Engine/Sqlite/StorageFormat.cs

files=$(gh api --paginate "repos/$repo/pulls/$pr/files" --jq '.[].filename')
patch=$(gh api --paginate "repos/$repo/pulls/$pr/files" --jq ".[] | select(.filename == \"$version_file\") | .patch // \"\"")
labels=$(gh pr view "$pr" -R "$repo" --json labels --jq '.labels[].name')

changed=$(grep -E "$storage" <<<"$files" | grep -vxF "$version_file" || true)
bumped=$(grep -E '^\+ *public const int Version = [0-9]+;' <<<"$patch" || true)
has() { grep -qxF "$1" <<<"$labels"; }

fail() { echo "storage-format: $*" >&2; exit 1; }
if has storage-format && has storage-format-unchanged; then fail "the labels storage-format and storage-format-unchanged contradict each other"; fi
if [[ -n $bumped ]] && ! has storage-format; then fail "StorageFormat.Version changed: label the pull request storage-format (storage train)"; fi
if has storage-format && [[ -z $bumped ]]; then fail "labelled storage-format: bump StorageFormat.Version in $version_file"; fi
if [[ -n $changed && -z $bumped ]] && ! has storage-format-unchanged; then
  fail "storage files changed ($(paste -sd' ' <<<"$changed")): bump StorageFormat.Version and label storage-format, or label storage-format-unchanged if what is on disk stays the same"
fi
echo "storage-format: ok${bumped:+ (format bump, label storage-format)}"
