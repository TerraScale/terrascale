#!/usr/bin/env bash
# Check out the ts-dotnet and ts-libs revisions pinned in TS_DOTNET_REV and
# TS_LIBS_REV into a directory this job owns under RUNNER_TEMP, and point the
# build at it through TerraScaleRoot (GITHUB_ENV). It must sit outside this
# repository: inside it, the SDKs would inherit ts-auth's Directory.Build.props
# and central package versions. No shared sibling checkout is touched.
set -euo pipefail

test "$#" -eq 0 || { echo "usage: $0" >&2; exit 2; }
: "${ORG_READ_TOKEN:?org read token is required}"
: "${GITHUB_ENV:?GITHUB_ENV is required}"

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
for pair in "ts-dotnet:${TS_DOTNET_REV:-}" "ts-libs:${TS_LIBS_REV:-}"; do
  name=${pair%%:*}
  rev=${pair#*:}
  [[ $rev =~ ^[0-9a-f]{40}$ ]] || { echo "the $name revision must be a full commit SHA" >&2; exit 2; }
  dir="$deps/$name"
  # Job-owned and fresh: leftover tracked edits or untracked build inputs from an
  # earlier run must not change what gets built.
  rm -rf "$dir"
  mkdir -p "$dir"
  git init -q "$dir"
  git -C "$dir" -c credential.helper= fetch -q --depth 1 "https://git.terrascale.tech/TerraScale/${name}.git" "$rev"
  git -C "$dir" checkout -q --detach FETCH_HEAD
  test "$(git -C "$dir" rev-parse HEAD)" = "$rev"
done

echo "TerraScaleRoot=$deps" >> "$GITHUB_ENV"
