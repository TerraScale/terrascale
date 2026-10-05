#!/usr/bin/env bash
# Reuse downloaded packages across runs without trusting anything a job could
# have modified.
#
# Only NuGet's HTTP cache (the raw .nupkg downloads) persists on the runner's
# /cache volume. Every job extracts packages into its own fresh global packages
# folder under RUNNER_TEMP, and locked-mode restore checks each .nupkg against
# the SHA-512 content hash in packages.lock.json before extracting it (NU1403).
# A tampered download therefore fails the restore instead of reaching a build;
# the extracted files, which locked mode does not rehash, are never shared.
set -euo pipefail

: "${GITHUB_ENV:?GITHUB_ENV is required}"
packages="${RUNNER_TEMP:?RUNNER_TEMP is required}/nuget-packages-${GITHUB_RUN_ID:-local}-${GITHUB_JOB:-job}"
rm -rf "$packages"
mkdir -p "$packages"
echo "NUGET_PACKAGES=$packages" >> "$GITHUB_ENV"

http_cache=/cache/nuget/terrabase/http-cache
scratch=/cache/nuget/terrabase/scratch
# NuGet coordinates concurrent HTTP-cache access through locks in its scratch directory.
if mkdir -p "$http_cache" "$scratch" 2>/dev/null && [[ -w $http_cache && -w $scratch ]]; then
  echo "NUGET_HTTP_CACHE_PATH=$http_cache" >> "$GITHUB_ENV"
  echo "NUGET_SCRATCH=$scratch" >> "$GITHUB_ENV"
  echo "NuGet downloads cache: $http_cache; packages extracted to $packages"
else
  echo "no writable /cache volume; NuGet downloads are not cached"
fi
