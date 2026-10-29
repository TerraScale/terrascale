#!/usr/bin/env bash
# Preserve the release exit status and put both output streams in its run summary.
set -euo pipefail
echo '### Release to every region' >> "$GITHUB_STEP_SUMMARY"
echo '```text' >> "$GITHUB_STEP_SUMMARY"
set +e
"$@" 2>&1 | tee -a "$GITHUB_STEP_SUMMARY"
statuses=("${PIPESTATUS[@]}")
set -e
echo '```' >> "$GITHUB_STEP_SUMMARY"
status=${statuses[0]}
if (( status != 0 )); then
  echo "Release failed (exit ${status})." >> "$GITHUB_STEP_SUMMARY"
  echo "::error::Release to every region failed (exit ${status}); see the release summary."
  exit "$status"
fi
if (( statuses[1] != 0 )); then
  echo '::error::Could not record the release outcome.'
  exit "${statuses[1]}"
fi
echo 'Release succeeded in every region.' >> "$GITHUB_STEP_SUMMARY"
