#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
scan_paths=("$repo_root/src" "$repo_root/tests" "$repo_root/scripts")
if rg -n --hidden --glob '!*.pyc' --glob '!__pycache__/**' \
  '(AKIA[0-9A-Z]{16}|-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----|(^|[[:space:]])(password|passwd|token|secret)[[:space:]]*=[[:space:]]*["'"'][^"'"']+["'"'])' \
  "${scan_paths[@]}"; then
  echo "possible secret material found in source, tests, or scripts" >&2
  exit 1
fi
echo "secret scan passed for source, tests, and scripts"
