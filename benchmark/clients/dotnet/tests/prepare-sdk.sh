#!/usr/bin/env bash
set -euo pipefail
client_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
fixture=$(mktemp -d)
trap 'rm -rf -- "$fixture"' EXIT
cp "$client_root/prepare-sdk.sh" "$client_root/sdk.rev" "$client_root/Makefile" "$fixture/"
cd -- "$fixture"
git init -q

expect_failure() {
  if bash prepare-sdk.sh --verify >diagnostic 2>&1; then
    echo "Expected SDK verification failure: $1" >&2
    exit 1
  fi
  if [[ $(<diagnostic) != *"$1"* ]]; then
    echo "Expected SDK diagnostic: $1" >&2
    exit 1
  fi
}

expect_failure 'before building'
make --no-print-directory -n build test check >commands
[[ $(head -n 1 commands) == 'bash prepare-sdk.sh' ]]
mkdir .sdk
# An empty directory must not resolve to the parent benchmark repository.
expect_failure 'not a usable SDK checkout'
rmdir .sdk
git init -q .sdk
git -C .sdk -c user.name=fixture -c user.email=fixture@example.test -c commit.gpgsign=false commit -qm initial --allow-empty
git -C .sdk rev-parse HEAD >sdk.rev
expect_failure 'official ts-dotnet origin'
git -C .sdk remote add origin ssh://git@git.terrascale.tech/TerraScale/ts-dotnet.git
git -C .sdk update-ref refs/remotes/origin/main HEAD
bash prepare-sdk.sh --verify
touch .sdk/untracked
expect_failure 'local changes'
rm .sdk/untracked
git -C .sdk -c user.name=fixture -c user.email=fixture@example.test -c commit.gpgsign=false commit -qm feature --allow-empty
expect_failure 'does not match sdk.rev'
git -C .sdk rev-parse HEAD >sdk.rev
expect_failure 'not an ancestor of official origin/main'
echo 'SDK preparation: 8 checks passed (no network requests).'
