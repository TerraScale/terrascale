#!/usr/bin/env bash
# Decide which CI areas a change touches. Prints libs, auth, terrabase and
# benchmark as name=true or name=false, one per line, ready for GITHUB_OUTPUT.
#
#   scripts/ci/changed-areas.sh BASE HEAD
#
# The diff runs from the merge base of BASE and HEAD to HEAD. An empty or
# all-zero BASE (a new branch, or a push without a previous tip) changes every
# area. Shared build files change libs, auth and terrabase. ts-libs changes also
# rebuild auth and terrabase, which reference it. Anything else, such as
# AGENTS.md or docs/terrabase, changes no area. changed-areas-test.sh asserts
# these rules.
set -euo pipefail

# areas_for PATH: print the areas one changed path belongs to, one per line.
areas_for() {
  case $1 in
    .forgejo/*) printf '%s\n' libs auth terrabase benchmark ;;
    Directory.Build.props | Directory.Build.targets | Directory.Packages.props | global.json | NuGet.config | \
      .editorconfig | BannedSymbols.txt | terrascale.slnx | Makefile | scripts/ci/*)
      printf '%s\n' libs auth terrabase ;;
    src/TerraScale.Endpoints*/* | src/TerraScale.Logging/* | tests/TerraScale.Endpoints.Tests/* | \
      tests/TerraScale.Logging.Tests/* | benchmark/dotnet/* | scripts/libs/*)
      printf '%s\n' libs auth terrabase ;;
    src/TerraScale.Auth*/* | tests/TerraScale.Auth.*/* | docs/auth/* | scripts/auth/* | .containerignore)
      printf '%s\n' auth ;;
    src/TerraScale.TerraBase*/* | tests/TerraScale.TerraBase.*/* | scripts/terrabase/*)
      printf '%s\n' terrabase ;;
    benchmark/*) printf '%s\n' benchmark ;;
  esac
}

# changed_areas BASE HEAD: print name=true|false for every area, from the current repository.
changed_areas() {
  local base=$1 head=$2 files path area
  declare -A changed=()
  if [[ -z $base || $base =~ ^0+$ ]]; then
    for area in libs auth terrabase benchmark; do changed[$area]=1; done
  else
    # An explicit exit: set -e is off inside && and || lists, and a silent diff failure must not read as "no change".
    files=$(git diff --name-only "$base...$head") || { echo "cannot diff $base...$head" >&2; exit 1; }
    while IFS= read -r path; do
      while IFS= read -r area; do
        [[ -z $area ]] || changed[$area]=1
      done < <(areas_for "$path")
    done <<< "$files"
  fi
  for area in libs auth terrabase benchmark; do
    if [[ -n ${changed[$area]:-} ]]; then echo "$area=true"; else echo "$area=false"; fi
  done
}

if [[ ${BASH_SOURCE[0]} == "$0" ]]; then
  test "$#" -eq 2 || { echo "usage: $0 BASE HEAD" >&2; exit 2; }
  changed_areas "$1" "$2"
fi
