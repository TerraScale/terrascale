#!/usr/bin/env bash
set -euo pipefail

script_dir=$(CDPATH= cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
fixtures=$script_dir/test-fixtures/workflow-runs
revision=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa

"$script_dir/verify-workflow-runs.sh" --fixtures "$fixtures" \
  101 202 "$revision" TerraScale/TerraBase refs/heads/main qualification.yml
if "$script_dir/verify-workflow-runs.sh" --fixtures "$fixtures" \
  101 101 "$revision" TerraScale/TerraBase refs/heads/main qualification.yml >/dev/null 2>&1; then
  echo "same-run candidate/capability reuse unexpectedly succeeded" >&2
  exit 1
fi
if "$script_dir/verify-workflow-runs.sh" --fixtures "$fixtures" \
  101 311 "$revision" TerraScale/TerraBase refs/heads/main release.yml >/dev/null 2>&1; then
  echo "release workflow admitted as capability evidence" >&2
  exit 1
fi
for run_id in 303 304 305 306 307 308 309 310 312 313; do
  if "$script_dir/verify-workflow-runs.sh" --fixtures "$fixtures" \
    "$run_id" 202 "$revision" TerraScale/TerraBase refs/heads/main qualification.yml >/dev/null 2>&1; then
    echo "candidate workflow fixture $run_id unexpectedly succeeded" >&2
    exit 1
  fi
done
if "$script_dir/verify-workflow-runs.sh" --fixtures "$fixtures" \
  101 311 "$revision" TerraScale/TerraBase refs/heads/main qualification.yml >/dev/null 2>&1; then
  echo "wrong-capability-workflow fixture unexpectedly succeeded" >&2
  exit 1
fi
if "$script_dir/verify-workflow-runs.sh" 101 202 "$revision" TerraScale/TerraBase refs/heads/main qualification.yml >/dev/null 2>&1; then
  echo "missing Forgejo API configuration unexpectedly succeeded" >&2
  exit 1
fi
echo "workflow-run admission negative checks passed"
