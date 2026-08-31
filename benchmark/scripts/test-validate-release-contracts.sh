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
    bash scripts/validate-release-contracts.sh
  )
}

pass_fixture="$fixture_root/pass"
mkdir -p "$pass_fixture"
make_fixture "$pass_fixture"
run_contracts "$pass_fixture"

untracked_fixture="$fixture_root/untracked"
mkdir -p "$untracked_fixture"
make_fixture "$untracked_fixture"
printf 'package rogue\n' >"$untracked_fixture/rogue.go"
if output=$(run_contracts "$untracked_fixture" 2>&1); then
  printf 'expected untracked Go source to fail validation\n' >&2
  exit 1
fi
grep -Fq './rogue.go' <<<"$output"

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

printf 'validated release-contract Go scan fixtures\n'
