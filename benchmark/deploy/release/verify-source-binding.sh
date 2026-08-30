#!/usr/bin/env bash
set -euo pipefail

[[ $# -ge 1 ]] || {
  echo "usage: deploy/release/verify-source-binding.sh <release-output-directory>..." >&2
  exit 2
}

script_dir=$(CDPATH= cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repository_root=$(CDPATH= cd -- "$script_dir/../.." && pwd)
cd "$repository_root"

git diff --quiet --ignore-submodules --
git diff --cached --quiet --ignore-submodules --
expected_revision=$(git rev-parse HEAD)
expected_tree=$(git rev-parse 'HEAD^{tree}')

for output in "$@"; do
  "$script_dir/verify.sh" --unsigned "$output"
  manifest=$(find "$output" -maxdepth 1 -type f -name '*.release.json' -print)
  revision=$(jq -er '.source.revision' "$manifest")
  tree=$(jq -er '.source.tree' "$manifest")
  dirty=$(jq -er '.source.dirty' "$manifest")
  [[ "$revision" == "$expected_revision" && "$tree" == "$expected_tree" && "$dirty" == false ]] || {
    echo "release package source does not equal checked-out main: $output" >&2
    exit 1
  }
done

echo "verified release packages against main revision $expected_revision and tree $expected_tree"
