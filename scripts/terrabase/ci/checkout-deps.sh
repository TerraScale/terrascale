#!/usr/bin/env bash
# Check out the ts-libs revision pinned in TS_LIBS_REV into a directory this job
# owns under RUNNER_TEMP and point the build at it through TerraScaleRoot
# (GITHUB_ENV). It must sit outside this repository: inside it, ts-libs would
# inherit TerraBase's Directory.Build.props and central package versions.
set -euo pipefail

test "$#" -eq 0 || { echo "usage: $0" >&2; exit 2; }
: "${ORG_READ_TOKEN:?the ORG_READ_TOKEN secret is required to read TerraScale/ts-libs}"
: "${GITHUB_ENV:?GITHUB_ENV is required}"
[[ ${TS_LIBS_REV:-} =~ ^[0-9a-f]{40}$ ]] || { echo "TS_LIBS_REV must be a full commit SHA" >&2; exit 2; }

# The token reaches git through an askpass helper that reads it from the
# environment, so it never appears in a URL or in any process's arguments.
askpass=$(mktemp)
trap 'rm -f "$askpass"' EXIT
cat >"$askpass" <<'HELPER'
#!/bin/sh
case "$1" in
  Username*) echo oauth2 ;;
  *) printf '%s\n' "$ORG_READ_TOKEN" ;;
esac
HELPER
chmod 700 "$askpass"
export GIT_ASKPASS="$askpass" GIT_TERMINAL_PROMPT=0

deps="${RUNNER_TEMP:-$(mktemp -d)}/terrascale-deps-${GITHUB_RUN_ID:-local}-${GITHUB_JOB:-job}"
dir="$deps/ts-libs"
# Job-owned and fresh: leftovers from an earlier run must not change the build.
rm -rf "$dir"
mkdir -p "$dir"
git init -q "$dir"
git -C "$dir" -c credential.helper= fetch -q --depth 1 "https://git.terrascale.tech/TerraScale/ts-libs.git" "$TS_LIBS_REV"
git -C "$dir" checkout -q --detach FETCH_HEAD
test "$(git -C "$dir" rev-parse HEAD)" = "$TS_LIBS_REV"

echo "TerraScaleRoot=$deps" >> "$GITHUB_ENV"
echo "checked out ts-libs $TS_LIBS_REV into $dir"
