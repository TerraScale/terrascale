#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
while (($#)); do
  case "$1" in
    --root)
      if (($# < 2)); then
        echo "check_secrets.sh: --root requires a directory" >&2
        exit 2
      fi
      repo_root="$(cd "$2" 2>/dev/null && pwd)" || {
        echo "check_secrets.sh: root is not a directory: $2" >&2
        exit 2
      }
      shift 2
      ;;
    *)
      echo "check_secrets.sh: unknown argument: $1" >&2
      exit 2
      ;;
  esac
done

declared_roots=(src tests scripts)
scan_paths=()
skipped_roots=()
for declared_root in "${declared_roots[@]}"; do
  candidate="$repo_root/$declared_root"
  if [[ -d "$candidate" ]]; then
    scan_paths+=("$candidate")
  else
    skipped_roots+=("$declared_root")
  fi
done

if ((${#skipped_roots[@]})); then
  echo "secret scan skipped missing roots: ${skipped_roots[*]}"
fi

if ((${#scan_paths[@]} == 0)); then
  echo "secret scan passed: no declared scan roots exist"
  exit 0
fi

set +e
secret_pattern="(AKIA[0-9A-Z]{16}|-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----|(^|[[:space:]])(password|passwd|token|secret)[[:space:]]*=[[:space:]]*([\"'])([^\"']+)([\"']))"
rg -n --hidden --glob '!*.pyc' --glob '!__pycache__/**' \
  "$secret_pattern" \
  "${scan_paths[@]}"
scan_status=$?
set -e

case "$scan_status" in
  0)
  echo "possible secret material found in source, tests, or scripts" >&2
  exit 1
  ;;
  1)
    echo "secret scan passed for: ${scan_paths[*]}"
    ;;
  *)
    echo "secret scan failed: scanner error (rg exit $scan_status)" >&2
    exit 2
    ;;
esac
