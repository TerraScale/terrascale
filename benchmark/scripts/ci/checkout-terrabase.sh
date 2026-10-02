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

# The token reaches Git only through this askpass helper's environment, never
# through a command-line argument or a stored remote URL.
askpass=$(mktemp)
trap 'rm -f -- "$askpass"' EXIT
cat > "$askpass" <<'ASKPASS'
#!/bin/sh
case $1 in
  Username*) printf '%s\n' oauth2 ;;
  *) printf '%s\n' "$ORG_READ_TOKEN" ;;
esac
ASKPASS
chmod 700 "$askpass"

# Only a fresh checkout guarantees that prepare-turso.sh and the patch series
# are the pinned revision's bytes; never reuse an existing sibling tree.
[[ ! -e $engine ]] || { echo "$engine already exists; this script needs a fresh checkout" >&2; exit 2; }
git init -q "$engine"
GIT_ASKPASS=$askpass GIT_TERMINAL_PROMPT=0 git -C "$engine" -c credential.helper= fetch -q --depth 1 \
  https://git.terrascale.tech/TerraScale/terrabase.git "$rev"
git -C "$engine" checkout -q --detach FETCH_HEAD
test "$(git -C "$engine" rev-parse HEAD)" = "$rev"
git -C "$engine" submodule update -q --init --depth 1 crates/turso
bash "$engine/scripts/ci/prepare-turso.sh"
printf 'TerraBase %s, Turso %s\n' "$rev" "$(git -C "$engine/crates/turso" rev-parse HEAD)"
