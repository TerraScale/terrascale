#!/usr/bin/env bash
set -euo pipefail

[[ $# -ge 3 && $1 == --target ]] || {
  echo "usage: deploy/release/verify-source-binding.sh --target <target> <release-output-directory>..." >&2
  exit 2
}
expected_target=$2
shift 2
case "$expected_target" in
  x86_64-unknown-linux-gnu|aarch64-unknown-linux-gnu) ;;
  *) exit 2 ;;
esac

script_dir=$(CDPATH= cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repository_root=$(CDPATH= cd -- "$script_dir/../.." && pwd)
cd "$repository_root"

git diff --quiet --ignore-submodules --
git diff --cached --quiet --ignore-submodules --
[[ -z "$(git ls-files --others --exclude-standard)" ]]
expected_revision=$(git rev-parse HEAD)
expected_tree=$(git rev-parse 'HEAD^{tree}')
expected_native_tools=

for output in "$@"; do
  "$script_dir/verify.sh" --unsigned "$output"
  manifest=$(find "$output" -maxdepth 1 -type f -name '*.release.json' -print)
  revision=$(jq -er '.source.revision' "$manifest")
  tree=$(jq -er '.source.tree' "$manifest")
  dirty=$(jq -er '.source.dirty' "$manifest")
  [[ "$(jq -er '.target' "$manifest")" == "$expected_target" ]]
  [[ "$revision" == "$expected_revision" && "$tree" == "$expected_tree" && "$dirty" == false ]] || {
    echo "release package source does not equal checked-out main: $output" >&2
    exit 1
  }
  provenance="$output/$(jq -er '.evidence.provenance' "$manifest")"
  native_tools=$(jq -cS '.predicate.buildDefinition.internalParameters.nativeTools' "$provenance")
  if [[ -z "$expected_native_tools" ]]; then
    expected_native_tools=$native_tools
  else
    [[ "$native_tools" == "$expected_native_tools" ]] || {
      echo "release components were built with different native tool identities" >&2
      exit 1
    }
  fi
  case "$(jq -er '.component' "$manifest")" in
    server) lock_file=Cargo.lock ;;
    studio) lock_file=crates/terrabase-studio/Cargo.lock ;;
    *) exit 1 ;;
  esac
  [[ "$(jq -er '.predicate.buildDefinition.internalParameters.cargoLockSha256' "$provenance")" == "$(sha256sum "$lock_file" | awk '{print $1}')" ]]
done

[[ "$(git rev-parse HEAD)" == "$expected_revision" ]]
[[ "$(git rev-parse 'HEAD^{tree}')" == "$expected_tree" ]]
git diff --quiet --ignore-submodules --
git diff --cached --quiet --ignore-submodules --
[[ -z "$(git ls-files --others --exclude-standard)" ]]

echo "verified release packages against main revision $expected_revision and tree $expected_tree"
