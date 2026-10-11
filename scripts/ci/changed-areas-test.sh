#!/usr/bin/env bash
# Self-check for changed-areas.sh: the area rules for representative paths, and
# the git-level diff with an empty base. CI runs it before choosing jobs.
set -euo pipefail

source "$(dirname "${BASH_SOURCE[0]}")/changed-areas.sh"

work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT
fail() { echo "changed-areas check failed: $*" >&2; exit 1; }

# expect PATH AREAS: the areas one path touches are exactly AREAS, in order.
expect() {
  local path=$1 want=$2 got
  got=$(areas_for "$path" | paste -sd' ' -)
  [[ $got == "$want" ]] || fail "$path touches '$got', expected '$want'"
}

expect .forgejo/workflows/ci.yml 'libs auth terrabase benchmark'
expect scripts/ci/changed-areas.sh 'libs auth terrabase'
expect scripts/ci/report-slow-failure.sh 'libs auth terrabase'
expect Directory.Packages.props 'libs auth terrabase'
expect Makefile 'libs auth terrabase'
expect TerraScale.slnx 'libs auth terrabase'
expect src/TerraScale.Logging/Logger.cs 'libs auth terrabase'
expect src/TerraScale.Endpoints.Generators/Generator.cs 'libs auth terrabase'
expect tests/TerraScale.Endpoints.Tests/BindingTests.cs 'libs auth terrabase'
expect benchmark/dotnet/TerraScale.Logging.Benchmarks/Program.cs 'libs auth terrabase'
expect scripts/libs/package-version.sh 'libs auth terrabase'
expect src/TerraScale.Auth.Store/AuthStore.cs auth
expect tests/TerraScale.Auth.Slow/ReadinessConcurrencyTests.cs auth
expect docs/auth/mfa.md auth
expect scripts/auth/dev.sh auth
expect .containerignore auth
expect src/TerraScale.TerraBase/Program.cs terrabase
expect tests/TerraScale.TerraBase.Slow/Platform/LifecycleTests.cs terrabase
expect scripts/terrabase/ci/release.sh terrabase
expect benchmark/internal/benchmark/engine.go benchmark
expect benchmark/clients/dotnet/Makefile benchmark
expect docs/terrabase/api.md ''
expect AGENTS.md ''
expect README.md ''

# Commits in a scratch repository: a base, an auth change, then a docs-only change.
repo=$work/repo
git init -q -b main "$repo"
mkdir -p "$repo/src/TerraScale.Auth" "$repo/docs/terrabase"
echo base > "$repo/src/TerraScale.Auth/Program.cs"
echo base > "$repo/AGENTS.md"
git -C "$repo" add -A
git -C "$repo" -c user.name=ci -c user.email=ci@localhost commit -qm base
base=$(git -C "$repo" rev-parse HEAD)
echo auth > "$repo/src/TerraScale.Auth/Program.cs"
git -C "$repo" -c user.name=ci -c user.email=ci@localhost commit -qam 'auth change'
auth_head=$(git -C "$repo" rev-parse HEAD)
echo docs > "$repo/AGENTS.md"
git -C "$repo" -c user.name=ci -c user.email=ci@localhost commit -qam 'docs change'
docs_head=$(git -C "$repo" rev-parse HEAD)

# diff BASE HEAD: the output of changed_areas inside the scratch repository.
diff_areas() {
  (cd "$repo" && changed_areas "$1" "$2") | paste -sd' ' -
}
[[ $(diff_areas "$base" "$auth_head") == 'libs=false auth=true terrabase=false benchmark=false' ]] ||
  fail "an auth-only change gave '$(diff_areas "$base" "$auth_head")'"
[[ $(diff_areas "$base" "$docs_head") == 'libs=false auth=true terrabase=false benchmark=false' ]] ||
  fail "a change to AGENTS.md on top of an auth change gave '$(diff_areas "$base" "$docs_head")'"
[[ $(diff_areas "$auth_head" "$docs_head") == 'libs=false auth=false terrabase=false benchmark=false' ]] ||
  fail "a docs-only change gave '$(diff_areas "$auth_head" "$docs_head")'"
[[ $(diff_areas '' "$docs_head") == 'libs=true auth=true terrabase=true benchmark=true' ]] ||
  fail 'an empty base did not change every area'
[[ $(diff_areas "$(printf '0%.0s' {1..40})" "$docs_head") == 'libs=true auth=true terrabase=true benchmark=true' ]] ||
  fail 'an all-zero base did not change every area'
(cd "$repo" && changed_areas 0123456789012345678901234567890123456789 "$docs_head") >/dev/null 2>&1 &&
  fail 'an unknown base was accepted'

echo 'changed-areas checks passed'
