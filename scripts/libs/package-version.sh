#!/usr/bin/env bash
# Prints the UTC commit time as YYYY.MMDD.HHmm (plus .ss when the first parent shares that minute).
# Every part stays <= 65534 so it fits AssemblyVersion/FileVersion and sorts by build time.
# shortcut: two first-parent commits in the same UTC second still collide; upgrade if main ever gets sub-second merges.
set -euo pipefail

calver() {
  local stamp version
  # GNU date (Linux) is required; BSD/macOS date cannot parse "@epoch".
  stamp=$(date -u -d "@$1" +%Y%m%d%H%M%S)
  version="$((10#${stamp:0:4})).$((10#${stamp:4:4})).$((10#${stamp:8:4}))"
  if [[ -n ${2:-} && $(date -u -d "@$2" +%Y%m%d%H%M) == "${stamp:0:12}" ]]; then
    # Second 0 would be a ".0" part NuGet normalizes away, i.e. both commits share one UTC second.
    if ((10#${stamp:12:2} == 0)); then
      echo "calver: commit and first parent share one UTC second; the version would be ambiguous" >&2
      return 1
    fi
    version+=".$((10#${stamp:12:2}))"
  fi
  echo "$version"
}

if [[ ${BASH_SOURCE[0]} == "$0" ]]; then
  rev=${1:-HEAD}
  calver "$(git log -1 --format=%ct "$rev")" "$(git log -1 --format=%ct "$rev^" 2>/dev/null || true)"
fi
