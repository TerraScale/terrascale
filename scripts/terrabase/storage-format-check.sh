#!/usr/bin/env bash
# Storage train: format feature PRs carry storage-format and "Needs format bump" for the coordinated reset;
# the reset PR bumps StorageFormat.Version. Unchanged layouts carry storage-format-unchanged. Needs GH_TOKEN
# with pull-requests:read. CI runs it in the `changes` job; scripts/terrabase/storage-format-check-test.sh checks it.
#
#   scripts/terrabase/storage-format-check.sh <owner/repo> <pull request number>
set -euo pipefail

test "$#" -eq 2 || { echo "usage: $0 <owner/repo> <pull request number>" >&2; exit 2; }
repo=$1 pr=$2

# Files whose change can change what is on disk. The storage owners (T1 Turso engine, R5 counters and sets, R2b/R2c
# client tables) keep this list current.
storage='^src/TerraBase/Engine/(Turso|Lattices)/|^src/TerraBase/Engine/StoredDatabaseFile\.cs$'
version_file=src/TerraBase/Engine/Turso/StorageFormat.cs

files=$(gh api --paginate "repos/$repo/pulls/$pr/files" --jq '.[].filename')
patch=$(gh api --paginate "repos/$repo/pulls/$pr/files" --jq ".[] | select(.filename == \"$version_file\") | .patch // \"\"")
labels=$(gh pr view "$pr" -R "$repo" --json labels --jq '.labels[].name')
body=$(gh pr view "$pr" -R "$repo" --json body --jq '.body')

changed=$(grep -E "$storage" <<<"$files" | grep -vxF "$version_file" || true)
bumped=$(grep -E '^\+ *public const int Version = [0-9]+;' <<<"$patch" || true)
has() { grep -qxF "$1" <<<"$labels"; }
pending() { has storage-format && grep -qF 'Needs format bump' <<<"$body"; }

fail() { echo "storage-format: $*" >&2; exit 1; }
if has storage-format && has storage-format-unchanged; then fail "the labels storage-format and storage-format-unchanged contradict each other"; fi
if [[ -n $bumped ]] && ! has storage-format; then fail "StorageFormat.Version changed: label the pull request storage-format (storage train)"; fi
if has storage-format && [[ -z $bumped ]] && ! pending; then fail "labelled storage-format: state Needs format bump for the coordinated reset, or bump StorageFormat.Version in the reset PR"; fi
if [[ -n $changed && -z $bumped ]] && ! has storage-format-unchanged && ! pending; then
  fail "storage files changed ($(paste -sd' ' <<<"$changed")): bump StorageFormat.Version and label storage-format, or label storage-format-unchanged if what is on disk stays the same"
fi
echo "storage-format: ok${bumped:+ (format bump, label storage-format)}"
