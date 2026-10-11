#!/usr/bin/env bash
# Release the executables that stage-release.sh built and checked, in two
# steps, so a release becomes visible only after every gate passed and always
# carries the tested bytes (nothing is rebuilt or uploaded twice):
#
#   release.sh draft DIST_DIR   terrabase job, after every gate passed: attach
#                               DIST_DIR/terrabase-linux-amd64,
#                               DIST_DIR/terrabase-linux-arm64 and their .sha256
#                               files to a draft release and write release_id to
#                               GITHUB_OUTPUT.
#   release.sh publish ID       terrabase-release job, after terrabase passed:
#                               publish the draft.
#
# A push to main drafts the prerelease ci-<sha>-run-<run id>. Drafts are visible
# only to repository writers, and Forgejo creates a missing tag only when the
# draft is published. Only main is released; the v* tag path is gone.
#
# Environment: FORGEJO_TOKEN (the job token) and the GITHUB_* variables of the job.
set -euo pipefail

fail() { echo "$*" >&2; exit 1; }
: "${FORGEJO_TOKEN:?FORGEJO_TOKEN is required}" "${GITHUB_SERVER_URL:?}" "${GITHUB_REPOSITORY:?}"
api="$GITHUB_SERVER_URL/api/v1/repos/$GITHUB_REPOSITORY"
work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT
# curl reads the token from a private config file, never from its arguments.
printf 'header = "Authorization: token %s"\n' "$FORGEJO_TOKEN" > "$work/auth"
unset FORGEJO_TOKEN
# call DEADLINE_SECONDS CURL_ARGS...: print the response body; failures retain it on stderr.
call() {
  local max_time=$1
  shift
  : > "$work/response"
  if curl -sS --connect-timeout 10 --max-time "$max_time" --fail-with-body \
    -w '%{stderr}release HTTP %{http_code}: %{size_upload} upload bytes in %{time_total}s\n' \
    -K "$work/auth" -o "$work/response" "$@"; then cat "$work/response"
  else cat "$work/response" >&2; echo >&2; return 1; fi
}

draft() {
  local dist=$1 names=(terrabase-linux-amd64 terrabase-linux-arm64) name version tag notes code id file marker max_time size
  : "${GITHUB_SHA:?}" "${GITHUB_REF:?}" "${GITHUB_RUN_ID:?}" "${GITHUB_RUN_NUMBER:?}" "${GITHUB_OUTPUT:?}"
  for name in "${names[@]}"; do
    [[ -s $dist/$name ]] || fail "missing staged release executable: $dist/$name"
  done
  [[ $(git rev-parse HEAD) == "$GITHUB_SHA" ]] || fail "checkout is not the event revision $GITHUB_SHA"
  version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' src/TerraScale.TerraBase/TerraScale.TerraBase.csproj)

  [[ $GITHUB_REF == refs/heads/main ]] || fail "no release for $GITHUB_REF"
  tag="ci-$GITHUB_SHA-run-$GITHUB_RUN_ID"
  notes="Untagged build of \`main\` (terrabase $version)."

  notes+=$'\n\n'"Commit: $GITHUB_SHA"$'\n'"Run: $GITHUB_SERVER_URL/$GITHUB_REPOSITORY/actions/runs/$GITHUB_RUN_NUMBER"
  for name in "${names[@]}"; do
    (cd "$dist" && sha256sum "$name" > "$name.sha256")
    notes+=$'\n'"SHA-256 $name: \`$(cut -d' ' -f1 "$dist/$name.sha256")\` (\`sha256sum -c $name.sha256\`)"
  done
  # Identifies drafts of this run; reruns keep the run ID. Hidden when the notes render.
  marker="<!-- terrabase-ci run=$GITHUB_RUN_ID sha=$GITHUB_SHA -->"
  notes+=$'\n'"$marker"

  # A draft left by an earlier attempt of this run never passed every gate:
  # replace it. Any other release, published or a draft prepared by hand, is
  # never touched.
  code=$(curl -sS --connect-timeout 10 --max-time 30 -K "$work/auth" \
    -o "$work/existing.json" -w '%{http_code}' "$api/releases/tags/$tag")
  case $code in
    404) ;;
    200)
      jq -e --arg marker "$marker" '.draft and (.body // "" | contains($marker))' "$work/existing.json" >/dev/null ||
        fail "release $tag already exists and is not a draft of this run; refusing to replace it"
      echo "replacing the draft $tag left by an earlier attempt of this run"
      call 30 -X DELETE "$api/releases/$(jq -r .id "$work/existing.json")" >/dev/null
      ;;
    *) cat "$work/existing.json" >&2; fail "looking up release $tag answered HTTP $code" ;;
  esac

  jq -n --arg tag "$tag" --arg sha "$GITHUB_SHA" --arg body "$notes" \
    '{tag_name: $tag, target_commitish: $sha, name: $tag, body: $body, draft: true, prerelease: true}' > "$work/release.json"
  id=$(call 30 -H 'Content-Type: application/json' --data @"$work/release.json" "$api/releases" | jq -er .id)
  for name in "${names[@]}"; do
    for file in "$dist/$name" "$dist/$name.sha256"; do
      # Two five-minute binary transfers leave room for the build in the 20-minute job.
      max_time=30
      [[ $file != "$dist/$name" ]] || max_time=300
      size=$(stat -c %s "$file")
      echo "uploading ${file##*/} ($size bytes; ${max_time}s deadline)" >&2
      call "$max_time" -F "attachment=@$file" "$api/releases/$id/assets?name=${file##*/}" > "$work/asset.json"
      jq -e --argjson size "$size" '.size == $size' "$work/asset.json" >/dev/null ||
        fail "uploaded ${file##*/} does not have the staged size"
    done
  done
  echo "release_id=$id" >> "$GITHUB_OUTPUT"
  echo "drafted $tag (release $id) with the tested ${names[*]}"
}

publish() {
  [[ $1 =~ ^[0-9]+$ ]] || fail "invalid release id '$1'"
  call 30 -X PATCH -H 'Content-Type: application/json' --data '{"draft": false}' "$api/releases/$1" > "$work/published.json"
  jq -r '"published \(.tag_name): \(.html_url)"' "$work/published.json"
}

case ${1:-}:$# in
  draft:2) draft "$2" ;;
  publish:2) publish "$2" ;;
  *) echo "usage: $0 draft DIST_DIR | publish RELEASE_ID" >&2; exit 2 ;;
esac
