#!/usr/bin/env bash
# Publish the release executable that the release job built, smoke-ran and
# retained on ci-<sha>-run-<run id>, once Fast has passed for the same run.
# Nothing is rebuilt: the retained bytes are read back and must match the
# release job's manifest byte for byte before qualification.json, the
# completion marker, is uploaded.
#
# QUALIFICATION (base64) and QUALIFICATION_SHA256 carry the release job's outputs.
set -euo pipefail

for tool in base64 fj jq sha256sum; do
  command -v "$tool" >/dev/null || { echo "$tool is required" >&2; exit 1; }
done
[[ ${GITHUB_RUN_ID:-} =~ ^[0-9]+$ ]] || { echo 'Forgejo run ID is required' >&2; exit 1; }
[[ ${GITHUB_RUN_NUMBER:-} =~ ^[0-9]+$ ]] || { echo 'Forgejo run number is required' >&2; exit 1; }
[[ ${GITHUB_SHA:-} =~ ^[0-9a-f]{40}$ ]] || { echo 'Forgejo source SHA is required' >&2; exit 1; }
[[ ${QUALIFICATION_SHA256:-} =~ ^[0-9a-f]{64}$ && -n ${QUALIFICATION:-} ]] || {
  echo 'the release job did not retain a qualification manifest' >&2
  exit 1
}
: "${GITHUB_REPOSITORY:?GITHUB_REPOSITORY is required}"
: "${FORGEJO_TOKEN:?Forgejo Actions token is required}"
: "${FORGEJO_READ_TOKEN:?Forgejo read token is required}"
: "${FORGEJO_SERVER_URL:?Forgejo server URL is required}"

repo=$(cd "$(dirname "$0")/../.." && pwd)
work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT
chmod 700 "$work"
manifest="$work/qualification.json"
base64 -d <<<"$QUALIFICATION" > "$manifest"
[[ $(sha256sum "$manifest" | awk '{print $1}') == "$QUALIFICATION_SHA256" ]] || {
  echo 'qualification manifest differs from the release job digest' >&2
  exit 1
}
jq -e --arg source "$GITHUB_SHA" --argjson run_id "$GITHUB_RUN_ID" --argjson run_number "$GITHUB_RUN_NUMBER" '
  .schema == "terrascale.terrabase.dotnet.qualification.v1" and .source_revision == $source and
  .verification.run_id == $run_id and .verification.run_number == $run_number and
  (.artifacts | length) == 1' "$manifest" >/dev/null || {
  echo 'qualification manifest does not describe this run' >&2
  exit 1
}

host=${FORGEJO_SERVER_URL#https://}
export XDG_DATA_HOME="$work/write" XDG_CONFIG_HOME="$work/config" FJ_READ_PROFILE="$work/read"
mkdir -m 0700 "$XDG_DATA_HOME" "$XDG_CONFIG_HOME" "$FJ_READ_PROFILE"
printf '%s' "$FORGEJO_TOKEN" | fj -H "$host" auth add-token >/dev/null
printf '%s' "$FORGEJO_READ_TOKEN" | XDG_DATA_HOME="$FJ_READ_PROFILE" fj -H "$host" auth add-token >/dev/null
unset FORGEJO_TOKEN FORGEJO_READ_TOKEN GITHUB_TOKEN

tag="ci-${GITHUB_SHA}-run-${GITHUB_RUN_ID}"
mkdir -m 0700 "$work/readback"
while IFS=$'\t' read -r name bytes sha256; do
  bash "$repo/scripts/ci/read-back-asset.sh" "$tag" "$name" "$sha256:$bytes" "$work/readback/$name"
done < <(jq -r '.artifacts[] | [.name, .bytes, .sha256] | @tsv' "$manifest")
fj -H "$host" release asset create -r "$GITHUB_REPOSITORY" "$tag" "$manifest"
printf 'TS_TERRABASE_DOTNET_QUALIFICATION_V1 source_sha=%s run_id=%s run_number=%s manifest_sha256=%s\n' \
  "$GITHUB_SHA" "$GITHUB_RUN_ID" "$GITHUB_RUN_NUMBER" "$QUALIFICATION_SHA256"
