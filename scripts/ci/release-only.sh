#!/usr/bin/env bash
set -euo pipefail
mapfile -t debug_dirs < <(find src sdk benchmark -type d -path '*/bin/Debug' -prune)
if ((${#debug_dirs[@]})); then
  printf 'Release builds must not leave Debug outputs; a project fell back to Debug because it is missing from the solution filter:\n' >&2
  printf '  %s\n' "${debug_dirs[@]}" >&2
  exit 1
fi
echo 'release-only: ok'
