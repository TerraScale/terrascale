#!/usr/bin/env bash
set -euo pipefail

source_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
fixture_root=$(mktemp -d)
trap 'rm -rf -- "$fixture_root"' EXIT

make_fixture() {
  local fixture=$1

  mkdir -p \
    "$fixture/.forgejo/workflows" \
    "$fixture/deploy/release" \
    "$fixture/scripts" \
    "$fixture/.claude/worktrees/ignored/terrabase-go"
  cp "$source_root/scripts/validate-release-contracts.sh" "$fixture/scripts/"
  cp "$source_root/.forgejo/workflows/ci.yml" "$fixture/.forgejo/workflows/"
  cp "$source_root/.forgejo/workflows/release.yml" "$fixture/.forgejo/workflows/"
  cp "$source_root/.forgejo/workflows/promote.yml" "$fixture/.forgejo/workflows/"
  cp "$source_root/deploy/release/package.sh" "$fixture/deploy/release/"
  cp "$source_root/deploy/release/reproduce.sh" "$fixture/deploy/release/"
  cp "$source_root/deploy/release/verify.sh" "$fixture/deploy/release/"
  printf '.claude/\n' >"$fixture/.gitignore"

  git -C "$fixture" init -q
  git -C "$fixture" add .gitignore
  printf 'package ignored\n' >"$fixture/.claude/worktrees/ignored/terrabase-go/client.go"
}

run_contracts() {
  local fixture=$1
  (
    cd "$fixture"
    bash scripts/validate-release-contracts.sh --fixture-only
  )
}

negative_fixture_count=0

pass_fixture="$fixture_root/pass"
mkdir -p "$pass_fixture"
make_fixture "$pass_fixture"
run_contracts "$pass_fixture" >/dev/null

mode_fixture="$fixture_root/mode"
mkdir -p "$mode_fixture"
make_fixture "$mode_fixture"
printf '%s\n' \
  '#!/usr/bin/env bash' \
  'set -euo pipefail' \
  "printf 'invoked\\n' >>'$mode_fixture/fixture-suite-invoked'" \
  >"$mode_fixture/scripts/test-validate-release-contracts.sh"

run_contracts "$mode_fixture" >/dev/null
if [[ -e "$mode_fixture/fixture-suite-invoked" ]]; then
  printf 'fixture-only validation recursively executed the fixture suite\n' >&2
  exit 1
fi

(
  cd "$mode_fixture"
  bash scripts/validate-release-contracts.sh >/dev/null
)
if [[ $(wc -l <"$mode_fixture/fixture-suite-invoked") -ne 1 ]]; then
  printf 'normal validation did not execute the fixture suite exactly once\n' >&2
  exit 1
fi

if (
  cd "$mode_fixture"
  bash scripts/validate-release-contracts.sh --unexpected >/dev/null 2>&1
); then
  printf 'expected an unknown validator argument to fail\n' >&2
  exit 1
fi
if (
  cd "$mode_fixture"
  bash scripts/validate-release-contracts.sh --fixture-only extra >/dev/null 2>&1
); then
  printf 'expected multiple validator arguments to fail\n' >&2
  exit 1
fi

untracked_fixture="$fixture_root/untracked"
mkdir -p "$untracked_fixture"
make_fixture "$untracked_fixture"
printf 'package rogue\n' >"$untracked_fixture/rogue.go"
if output=$(run_contracts "$untracked_fixture" 2>&1); then
  printf 'expected untracked Go source to fail validation\n' >&2
  exit 1
fi
grep -Fq './rogue.go' <<<"$output"
((negative_fixture_count += 1))

tracked_fixture="$fixture_root/tracked"
mkdir -p "$tracked_fixture"
make_fixture "$tracked_fixture"
printf 'package rogue\n' >"$tracked_fixture/.claude/worktrees/ignored/rogue.go"
git -C "$tracked_fixture" add -f .claude/worktrees/ignored/rogue.go
if output=$(run_contracts "$tracked_fixture" 2>&1); then
  printf 'expected tracked nested Go source to fail validation\n' >&2
  exit 1
fi
grep -Fq './.claude/worktrees/ignored/rogue.go' <<<"$output"
((negative_fixture_count += 1))

extra_workflow_fixture="$fixture_root/extra-workflow"
mkdir -p "$extra_workflow_fixture"
make_fixture "$extra_workflow_fixture"
printf 'name: unreviewed\n' >"$extra_workflow_fixture/.forgejo/workflows/extra.yml"
if run_contracts "$extra_workflow_fixture" >/dev/null 2>&1; then
  printf 'expected an extra workflow to fail validation\n' >&2
  exit 1
fi
((negative_fixture_count += 1))

symlinked_workflow_fixture="$fixture_root/symlinked-workflow"
mkdir -p "$symlinked_workflow_fixture"
make_fixture "$symlinked_workflow_fixture"
mv "$symlinked_workflow_fixture/.forgejo/workflows/ci.yml" \
  "$symlinked_workflow_fixture/ci.yml"
ln -s ../../ci.yml "$symlinked_workflow_fixture/.forgejo/workflows/ci.yml"
if run_contracts "$symlinked_workflow_fixture" >/dev/null 2>&1; then
  printf 'expected a symlinked workflow to fail validation\n' >&2
  exit 1
fi
((negative_fixture_count += 1))

symlinked_directory_fixture="$fixture_root/symlinked-directory"
mkdir -p "$symlinked_directory_fixture"
make_fixture "$symlinked_directory_fixture"
mv "$symlinked_directory_fixture/.forgejo/workflows" \
  "$symlinked_directory_fixture/.forgejo/workflows-reviewed"
ln -s workflows-reviewed "$symlinked_directory_fixture/.forgejo/workflows"
if run_contracts "$symlinked_directory_fixture" >/dev/null 2>&1; then
  printf 'expected a symlinked workflow directory to fail validation\n' >&2
  exit 1
fi
((negative_fixture_count += 1))

missing_workflow_fixture="$fixture_root/missing-workflow"
mkdir -p "$missing_workflow_fixture"
make_fixture "$missing_workflow_fixture"
rm "$missing_workflow_fixture/.forgejo/workflows/promote.yml"
if run_contracts "$missing_workflow_fixture" >/dev/null 2>&1; then
  printf 'expected a missing workflow to fail validation\n' >&2
  exit 1
fi
((negative_fixture_count += 1))

non_regular_workflow_fixture="$fixture_root/non-regular-workflow"
mkdir -p "$non_regular_workflow_fixture"
make_fixture "$non_regular_workflow_fixture"
rm "$non_regular_workflow_fixture/.forgejo/workflows/release.yml"
mkdir "$non_regular_workflow_fixture/.forgejo/workflows/release.yml"
if run_contracts "$non_regular_workflow_fixture" >/dev/null 2>&1; then
  printf 'expected a non-regular workflow to fail validation\n' >&2
  exit 1
fi
((negative_fixture_count += 1))

failing_find_fixture="$fixture_root/failing-find"
mkdir -p "$failing_find_fixture"
make_fixture "$failing_find_fixture"
failing_find_bin="$failing_find_fixture/find-bin"
mkdir -p "$failing_find_bin"
real_find=$(command -v find)
printf '%s\n' \
  '#!/usr/bin/env bash' \
  'set -euo pipefail' \
  "\"$real_find\" \"\$@\"" \
  'exit 1' >"$failing_find_bin/find"
chmod 0755 "$failing_find_bin/find"
if PATH="$failing_find_bin:$PATH" run_contracts "$failing_find_fixture" \
  >/dev/null 2>&1; then
  printf 'expected a failing workflow inventory scan to fail validation\n' >&2
  exit 1
fi
((negative_fixture_count += 1))

if [[ $negative_fixture_count -ne 8 ]]; then
  printf 'expected eight negative release-contract fixtures, ran %s\n' \
    "$negative_fixture_count" >&2
  exit 1
fi

printf 'validated %s negative release-contract fixtures and normal/fixture-only dispatch\n' \
  "$negative_fixture_count"
