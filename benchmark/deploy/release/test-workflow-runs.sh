#!/usr/bin/env bash
set -euo pipefail

script_dir=$(CDPATH= cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
fixtures=$script_dir/test-fixtures/workflow-runs
revision=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa

"$script_dir/verify-workflow-runs.sh" --fixtures "$fixtures" \
  101 202 "$revision" TerraScale/TerraBase refs/heads/main qualification.yml
if "$script_dir/verify-workflow-runs.sh" --fixtures "$fixtures" \
  303 202 "$revision" TerraScale/TerraBase refs/heads/main qualification.yml >/dev/null 2>&1; then
  echo "wrong-revision workflow fixture unexpectedly succeeded" >&2
  exit 1
fi
if "$script_dir/verify-workflow-runs.sh" 101 202 "$revision" TerraScale/TerraBase refs/heads/main qualification.yml >/dev/null 2>&1; then
  echo "missing Forgejo API configuration unexpectedly succeeded" >&2
  exit 1
fi
echo "workflow-run admission negative checks passed"
