#!/usr/bin/env bash
set -euo pipefail

projects=(
  src/TerraScale.Dashboard/TerraScale.Dashboard.csproj
  src/TerraScale.Dashboard.EntityFrameworkDesign/TerraScale.Dashboard.EntityFrameworkDesign.csproj
)

if [ -n "${BASE_SHA:-}" ] && git cat-file -e "$BASE_SHA^{commit}" &&
  git diff --quiet "$BASE_SHA" HEAD -- '*.csproj' '*.props' '*.targets' '*.slnx' '*.sln' \
    '*.editorconfig' '*.globalconfig' '*.ruleset' '**/packages.lock.json' global.json NuGet.config \
    .github/scripts/dashboard/format.sh; then
  mapfile -d '' files < <(git diff --name-only --diff-filter=ACMR -z "$BASE_SHA" HEAD -- 'src/TerraScale.Dashboard*/**/*.cs')
  if [ "${#files[@]}" -eq 0 ]; then
    echo 'No C# changes: formatting is already proved on main.'
    exit 0
  fi
  for project in "${projects[@]}"; do
    dir="${project%/*}/"
    matched=()
    for file in "${files[@]}"; do
      if [[ $file == "$dir"* ]]; then
        matched+=("$file")
      fi
    done
    if [ "${#matched[@]}" -gt 0 ]; then
      "${DOTNET:-dotnet}" format whitespace "$project" --verify-no-changes --no-restore --include "${matched[@]}"
    fi
  done
  exit 0
fi

for project in "${projects[@]}"; do
  "${DOTNET:-dotnet}" format whitespace "$project" --verify-no-changes --no-restore
done
