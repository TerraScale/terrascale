#!/usr/bin/env bash
set -euo pipefail

# Fast local negative checks for the release admission boundary. This test
# intentionally does not build, sign, publish, or manufacture capability
# receipts; protected keys and real qualification evidence remain external.
script_dir=$(CDPATH= cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
work=$(mktemp -d "${TMPDIR:-/tmp}/terrabase-release-test.XXXXXXXX")
cleanup() {
  if [[ -n ${work-} && -d "$work" && "$work" == */terrabase-release-test.* ]]; then
    rm -rf -- "$work"
  fi
}
trap cleanup EXIT

assert_rejected() {
  local label=$1
  shift
  if "$@" >"$work/$label.log" 2>&1; then
    echo "$label unexpectedly succeeded" >&2
    return 1
  fi
}

test -x "$script_dir/verify.sh"
assert_rejected verify-empty "$script_dir/verify.sh" --unsigned "$work/empty"
mkdir "$work/release"
assert_rejected verify-no-manifest "$script_dir/verify.sh" --unsigned "$work/release"
assert_rejected sign-no-checksums "$script_dir/sign.sh" "$work/release"
assert_rejected capability-no-manifest "$script_dir/require-production-capability.sh" "$work/release"

echo "release boundary negative checks passed"
