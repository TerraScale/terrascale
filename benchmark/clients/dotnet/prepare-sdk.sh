#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "$0")"
if [[ $# -gt 1 || ($# -eq 1 && "$1" != --verify) ]]; then
  echo 'Usage: prepare-sdk.sh [--verify]' >&2
  exit 1
fi
exec 9>.sdk.lock
if [[ ${1:-} == --verify ]]; then
  flock --shared 9
else
  flock --exclusive 9
fi
revision=$(cat sdk.rev)
if [[ ! "$revision" =~ ^[0-9a-f]{40}$ ]]; then
  echo 'sdk.rev must contain one full Git revision.' >&2
  exit 1
fi
if [[ ! -e .sdk ]]; then
  if [[ ${1:-} == --verify ]]; then
    echo 'Run bash clients/dotnet/prepare-sdk.sh before building.' >&2
    exit 1
  fi
  git clone https://github.com/TerraScale/ts-dotnet.git .sdk
fi
if [[ $(git -C .sdk rev-parse --show-toplevel 2>/dev/null || true) != "$(pwd -P)/.sdk" ]]; then
  echo '.sdk is not a usable SDK checkout; move it aside and re-run prepare-sdk.sh.' >&2
  exit 1
fi
if [[ $(git -C .sdk remote get-url origin 2>/dev/null || true) != https://github.com/TerraScale/ts-dotnet.git ]]; then
  echo 'SDK checkout must use the official ts-dotnet origin.' >&2
  exit 1
fi
if [[ -n $(git -C .sdk status --porcelain) ]]; then
  echo 'SDK checkout has local changes; refusing to replace them.' >&2
  exit 1
fi
if [[ ${1:-} == --verify ]]; then
  if [[ $(git -C .sdk rev-parse HEAD) != "$revision" ]]; then
    echo 'SDK checkout does not match sdk.rev; run prepare-sdk.sh.' >&2
    exit 1
  fi
else
  git -C .sdk fetch origin refs/heads/main:refs/remotes/origin/main
fi
if ! git -C .sdk merge-base --is-ancestor "$revision" origin/main; then
  echo 'sdk.rev is not an ancestor of official origin/main; update the pin or refresh with prepare-sdk.sh.' >&2
  exit 1
fi
if [[ ${1:-} != --verify ]]; then
  git -C .sdk checkout --detach "$revision"
fi
