#!/usr/bin/env bash
set -euo pipefail

args=(format TerraScale.Dashboard.slnx --verify-no-changes --no-restore)
# Main's formatting is already proved. Global build/analyzer changes still check every file.
if [ -n "${BASE_SHA:-}" ] && git cat-file -e "$BASE_SHA^{commit}" &&
  git diff --quiet "$BASE_SHA" HEAD -- '*.csproj' '*.props' '*.targets' '*.slnx' '*.sln' \
    '*.editorconfig' '*.globalconfig' '*.ruleset' '**/packages.lock.json' global.json NuGet.Config; then
  mapfile -d '' files < <(git diff --name-only --diff-filter=ACMR -z "$BASE_SHA" HEAD -- '*.cs')
  if [ "${#files[@]}" -eq 0 ]; then
    echo 'No C# changes: formatting is already proved on main.'
    exit 0
  fi
  args+=(--include "${files[@]}")
fi
"${DOTNET:-dotnet}" "${args[@]}"
