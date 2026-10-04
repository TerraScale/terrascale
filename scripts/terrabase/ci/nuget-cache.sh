#!/usr/bin/env bash
# Keep NuGet's global packages folder on the runner's persistent /cache volume,
# scoped to this repository, so restores reuse downloaded packages across runs.
# Locked-mode restore still verifies every package against packages.lock.json.
# Without a writable /cache the job keeps NuGet's default per-container folder.
set -euo pipefail

: "${GITHUB_ENV:?GITHUB_ENV is required}"
cache=/cache/nuget/terrabase
if mkdir -p "$cache" 2>/dev/null && [[ -w $cache ]]; then
  echo "NUGET_PACKAGES=$cache" >> "$GITHUB_ENV"
  echo "NuGet packages cache: $cache"
else
  echo "no writable /cache volume; using the default NuGet packages folder"
fi
