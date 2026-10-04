#!/usr/bin/env bash
# Retain the release executable staged and smoke-run by stage-release.sh on the
# Forgejo prerelease ci-<sha>-run-<run id> and describe it in
# DIST_DIR/qualification.json.
#
# The release job runs this for main pushes. The bytes are uploaded and read
# back here, but qualification.json is not: the publish job uploads it as the
# completion marker once Fast has passed, so a partial or unqualified release is
# never admitted. The manifest reaches the publish job through the job outputs
# `qualification` (base64) and `qualification_sha256`.
set -euo pipefail

dist=${1:-}
[[ -n $dist && $# -eq 1 ]] || { echo "usage: $0 DIST_DIR" >&2; exit 2; }

repo=$(cd "$(dirname "$0")/../.." && pwd)
cd "$repo"

for tool in base64 curl dotnet fj jq sha256sum stat; do
  command -v "$tool" >/dev/null || { echo "$tool is required" >&2; exit 1; }
done
[[ ${GITHUB_RUN_ID:-} =~ ^[0-9]+$ ]] || { echo 'Forgejo run ID is required' >&2; exit 1; }
[[ ${GITHUB_RUN_NUMBER:-} =~ ^[0-9]+$ ]] || { echo 'Forgejo run number is required' >&2; exit 1; }
[[ ${GITHUB_SHA:-} =~ ^[0-9a-f]{40}$ ]] || { echo 'Forgejo source SHA is required' >&2; exit 1; }
[[ ${TS_LIBS_REV:-} =~ ^[0-9a-f]{40}$ ]] || { echo 'TS_LIBS_REV is required' >&2; exit 1; }
: "${GITHUB_OUTPUT:?GITHUB_OUTPUT is required}" "${GITHUB_REPOSITORY:?GITHUB_REPOSITORY is required}"
: "${FORGEJO_TOKEN:?Forgejo Actions token is required}"
: "${FORGEJO_READ_TOKEN:?Forgejo read token is required}"
: "${FORGEJO_SERVER_URL:?Forgejo server URL is required}"
[[ $(git rev-parse HEAD) == "$GITHUB_SHA" ]] || { echo 'checked-out source differs from the run' >&2; exit 1; }

name=terrabase-linux-amd64
artifact="$dist/$name"
[[ -s $artifact ]] || { echo "missing staged release executable: $artifact" >&2; exit 1; }

jq -cnS \
  --arg schema 'terrascale.terrabase.dotnet.qualification.v1' \
  --arg repository "$GITHUB_REPOSITORY" \
  --arg source_revision "$GITHUB_SHA" --arg source_tree "$(git rev-parse 'HEAD^{tree}')" \
  --arg dotnet_sdk "$(dotnet --version)" \
  --arg packages_lock_sha256 "$(sha256sum src/TerraScale.TerraBase/packages.lock.json | awk '{print $1}')" \
  --arg ts_libs_revision "$TS_LIBS_REV" \
  --argjson run_id "$GITHUB_RUN_ID" --argjson run_number "$GITHUB_RUN_NUMBER" \
  --arg name "$name" --argjson bytes "$(stat -c '%s' -- "$artifact")" \
  --arg sha256 "$(sha256sum -- "$artifact" | awk '{print $1}')" \
  '{schema:$schema,repository:$repository,source_revision:$source_revision,
    source_tree:$source_tree,dotnet_sdk:$dotnet_sdk,build_configuration:"Release",
    packaging:"self-contained single-file JIT",packages_lock_sha256:$packages_lock_sha256,
    ts_libs_revision:$ts_libs_revision,
    verification:{workflow:"fast.yml",jobs:["fast","release"],run_id:$run_id,run_number:$run_number},
    artifacts:[{name:$name,runtime:"linux-x64",bytes:$bytes,sha256:$sha256}]}' > "$dist/qualification.json"

auth_home=$(mktemp -d)
trap 'rm -rf -- "$auth_home"' EXIT
chmod 700 "$auth_home"
host=${FORGEJO_SERVER_URL#https://}
# The Actions token creates the release; reads use the separate read token,
# which can also download private-repository attachments.
export XDG_DATA_HOME="$auth_home/write" XDG_CONFIG_HOME="$auth_home/config" FJ_READ_PROFILE="$auth_home/read"
mkdir -m 0700 "$XDG_DATA_HOME" "$XDG_CONFIG_HOME" "$FJ_READ_PROFILE"
printf '%s' "$FORGEJO_TOKEN" | fj -H "$host" auth add-token >/dev/null
printf '%s' "$FORGEJO_READ_TOKEN" | XDG_DATA_HOME="$FJ_READ_PROFILE" fj -H "$host" auth add-token >/dev/null
# curl reads the token from a private header file, never from its arguments.
printf 'Authorization: token %s\n' "$FORGEJO_READ_TOKEN" > "$auth_home/read-header"
chmod 600 "$auth_home/read-header"
unset FORGEJO_TOKEN FORGEJO_READ_TOKEN GITHUB_TOKEN

# release_state TAG: prints "absent", "unqualified" or "qualified" from an
# authoritative API lookup, retrying transient failures; exits non-zero when the
# state cannot be established, so callers fail closed.
release_state() {
  local tag=$1 code delay=1
  for attempt in 1 2 3 4 5; do
    code=$(curl -sS -o "$auth_home/release.json" -w '%{http_code}' -H @"$auth_home/read-header" \
      "$FORGEJO_SERVER_URL/api/v1/repos/$GITHUB_REPOSITORY/releases/tags/$tag") || code=000
    case $code in
      404) echo absent; return 0 ;;
      200)
        if jq -e '.assets | type == "array"' "$auth_home/release.json" >/dev/null; then
          jq -r 'if any(.assets[]; .name == "qualification.json") then "qualified" else "unqualified" end' "$auth_home/release.json"
          return 0
        fi
        ;;
    esac
    echo "release lookup for $tag answered HTTP $code (attempt $attempt)" >&2
    sleep "$delay"
    delay=$((delay * 2))
  done
  echo "cannot establish the state of release $tag" >&2
  return 1
}

tag="ci-${GITHUB_SHA}-run-${GITHUB_RUN_ID}"
# A rerun keeps the run ID. A release left by an earlier attempt that never got
# its qualification.json was never admitted, so it is replaced; a qualified one
# is never touched.
state=$(release_state "$tag")
case $state in
  qualified) echo "$tag is already qualified; refusing to replace published bytes" >&2; exit 1 ;;
  unqualified)
    echo "replacing the unqualified release $tag left by an earlier attempt"
    fj -H "$host" release delete -r "$GITHUB_REPOSITORY" --by-tag "$tag"
    fj -H "$host" tag delete -r "$GITHUB_REPOSITORY" "$tag" || true
    ;;
  absent) ;;
  *) echo "unexpected release state '$state'" >&2; exit 1 ;;
esac
fj -H "$host" release create -r "$GITHUB_REPOSITORY" "$tag" --create-tag="$tag" --branch "$GITHUB_SHA" \
  --prerelease --attach="$artifact"
mkdir -m 0700 "$auth_home/readback"
bash scripts/ci/read-back-asset.sh "$tag" "$name" "$artifact" "$auth_home/readback/$name"
{
  printf 'qualification=%s\n' "$(base64 -w 0 "$dist/qualification.json")"
  printf 'qualification_sha256=%s\n' "$(sha256sum "$dist/qualification.json" | awk '{print $1}')"
} >> "$GITHUB_OUTPUT"
printf 'retained tested release bytes on %s qualification_sha256=%s\n' \
  "$tag" "$(sha256sum "$dist/qualification.json" | awk '{print $1}')"
