#!/usr/bin/env bash
# Check out the TerraBase revision pinned in terrabase.rev beside this
# repository, where the drivers' `../../terrabase` path dependencies expect it,
# then bring its Turso submodule to the pinned v0.8.1 gitlink plus TerraBase's
# own patches/turso series through TerraBase's prepare-turso.sh.
set -euo pipefail

test "$#" -eq 0 || { echo "usage: $0" >&2; exit 2; }
: "${ORG_READ_TOKEN:?org read token is required}"

repo=$(cd "$(dirname "$0")/../.." && pwd)
rev=$(tr -d '[:space:]' < "$repo/terrabase.rev")
[[ $rev =~ ^[0-9a-f]{40}$ ]] || { echo "terrabase.rev must hold a full commit SHA" >&2; exit 2; }
engine="$repo/../terrabase"

git init -q "$engine"
git -C "$engine" fetch -q --depth 1 \
  "https://oauth2:${ORG_READ_TOKEN}@git.terrascale.tech/TerraScale/terrabase.git" "$rev"
git -C "$engine" checkout -q --detach FETCH_HEAD
test "$(git -C "$engine" rev-parse HEAD)" = "$rev"
git -C "$engine" submodule update -q --init --depth 1 crates/turso
bash "$engine/scripts/ci/prepare-turso.sh"
printf 'TerraBase %s, Turso %s\n' "$rev" "$(git -C "$engine/crates/turso" rev-parse HEAD)"
