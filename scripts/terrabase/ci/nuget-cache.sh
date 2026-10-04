#!/usr/bin/env bash
# Keep NuGet's global packages folder on the runner's persistent /cache volume so
# restores reuse downloaded packages across runs.
#
# The cache is split by trust. Locked mode verifies the dependency graph and
# content hashes recorded at extraction, but it does not rehash already
# extracted files, so code from a pull request could otherwise plant modified
# package files that a main release build reuses. Only builds of protected main
# (push, schedule, manual dispatch on main) use the trusted folder; everything
# else, including every pull request, uses a separate untrusted folder.
# Without a writable /cache the job keeps NuGet's default per-container folder.
set -euo pipefail

: "${GITHUB_ENV:?GITHUB_ENV is required}"
case "${GITHUB_EVENT_NAME:-}:${GITHUB_REF:-}" in
  push:refs/heads/main | schedule:refs/heads/main | workflow_dispatch:refs/heads/main) lane=main ;;
  *) lane=untrusted ;;
esac
cache=/cache/nuget/terrabase/$lane
if mkdir -p "$cache" 2>/dev/null && [[ -w $cache ]]; then
  echo "NUGET_PACKAGES=$cache" >> "$GITHUB_ENV"
  echo "NuGet packages cache: $cache"
else
  echo "no writable /cache volume; using the default NuGet packages folder"
fi
