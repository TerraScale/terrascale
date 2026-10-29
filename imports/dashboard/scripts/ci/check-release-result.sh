#!/usr/bin/env bash
# Exercise the actual release wrapper, including a failure whose only diagnostic is stderr.
set -euo pipefail
directory=$(mktemp -d)
trap 'rm -rf "$directory"' EXIT
export GITHUB_STEP_SUMMARY="$directory/summary"
set +e
bash scripts/ci/release-result.sh bash -c 'echo receipt; echo "deployment rejected" >&2; exit 23' > "$directory/log" 2>&1
status=$?
set -e
test "$status" -eq 23
grep -q 'receipt' "$GITHUB_STEP_SUMMARY"
grep -q 'deployment rejected' "$GITHUB_STEP_SUMMARY"
grep -q 'Release failed (exit 23)' "$GITHUB_STEP_SUMMARY"
grep -q '::error::' "$directory/log"
if grep -q 'Release succeeded' "$GITHUB_STEP_SUMMARY"; then exit 1; fi
: > "$GITHUB_STEP_SUMMARY"
bash scripts/ci/release-result.sh bash -c 'echo serving' > "$directory/log" 2>&1
grep -q 'serving' "$GITHUB_STEP_SUMMARY"
grep -q 'Release succeeded in every region' "$GITHUB_STEP_SUMMARY"
echo 'PASS: release exit status and both output streams remain visible'
