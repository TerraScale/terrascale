#!/usr/bin/env bash
# Release the executable that stage-release.sh built and smoke-ran, in two
# steps, so a release becomes visible only after every gate passed and always
# carries the tested bytes (nothing is rebuilt or uploaded twice):
#
#   release.sh draft DIST_DIR   release job: attach DIST_DIR/terrabase-linux-amd64
#                               and its .sha256 to a draft release and write
#                               release_id to GITHUB_OUTPUT.
#   release.sh publish ID       publish job, after fast and release passed:
#                               publish the draft.
#
# A push to main drafts the prerelease ci-<sha>-run-<run id>. A pushed tag
# v<Version> drafts the release <tag>, with notes generated from the main
# history since the previous v* tag. Drafts are visible only to repository
# writers, and Forgejo creates a missing tag only when the draft is published.
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
# call CURL_ARGS...: print the response body; on an HTTP error print it to stderr and fail.
call() {
  : > "$work/response"
  if curl -sS --fail-with-body -K "$work/auth" -o "$work/response" "$@"; then cat "$work/response"
  else cat "$work/response" >&2; echo >&2; return 1; fi
}

draft() {
  local dist=$1 name=terrabase-linux-amd64 version tag prerelease notes previous code id file marker
  : "${GITHUB_SHA:?}" "${GITHUB_REF:?}" "${GITHUB_RUN_ID:?}" "${GITHUB_RUN_NUMBER:?}" "${GITHUB_OUTPUT:?}"
  [[ -s $dist/$name ]] || fail "missing staged release executable: $dist/$name"
  [[ $(git rev-parse HEAD) == "$GITHUB_SHA" ]] || fail "checkout is not the event revision $GITHUB_SHA"
  version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' src/TerraScale.TerraBase/TerraScale.TerraBase.csproj)

  case $GITHUB_REF in
    refs/heads/main)
      tag="ci-$GITHUB_SHA-run-$GITHUB_RUN_ID" prerelease=true
      notes="Untagged build of \`main\` (terrabase $version). Use a \`v*\` release for anything that matters."
      ;;
    refs/tags/v*)
      tag=${GITHUB_REF#refs/tags/}
      [[ $tag == "v$version" ]] || fail "tag $tag does not match <Version>$version</Version>; bump the version on main first"
      # Only main's own commits: a merged side-branch commit lacks main's integration changes.
      # grep reads all input, so git never dies of SIGPIPE under pipefail.
      git rev-list --first-parent origin/main | grep -x "$GITHUB_SHA" >/dev/null || fail "tag $tag does not point at a commit on main"
      # Semantic-version prerelease tags (v1.2.0-rc.1) stay prereleases.
      if [[ $tag == *-* ]]; then prerelease=true; else prerelease=false; fi
      previous=$(git describe --first-parent --tags --abbrev=0 --match 'v*' HEAD^ 2>/dev/null || true)
      notes="## Changes${previous:+ since $previous}"$'\n\n'$(git log --first-parent --format='- %s (%h)' "${previous:+$previous..}HEAD")
      ;;
    *) fail "no release for $GITHUB_REF" ;;
  esac

  (cd "$dist" && sha256sum "$name" > "$name.sha256")
  notes+=$'\n\n'"Commit: $GITHUB_SHA"$'\n'"Run: $GITHUB_SERVER_URL/$GITHUB_REPOSITORY/actions/runs/$GITHUB_RUN_NUMBER"
  notes+=$'\n'"SHA-256: \`$(cut -d' ' -f1 "$dist/$name.sha256")\` (\`sha256sum -c $name.sha256\`)"
  # Identifies drafts of this run; reruns keep the run ID. Hidden when the notes render.
  marker="<!-- terrabase-ci run=$GITHUB_RUN_ID sha=$GITHUB_SHA -->"
  notes+=$'\n'"$marker"

  # A draft left by an earlier attempt of this run never passed every gate:
  # replace it. Any other release, published or a draft prepared by hand, is
  # never touched.
  code=$(curl -sS -K "$work/auth" -o "$work/existing.json" -w '%{http_code}' "$api/releases/tags/$tag")
  case $code in
    404) ;;
    200)
      jq -e --arg marker "$marker" '.draft and (.body // "" | contains($marker))' "$work/existing.json" >/dev/null ||
        fail "release $tag already exists and is not a draft of this run; refusing to replace it"
      echo "replacing the draft $tag left by an earlier attempt of this run"
      call -X DELETE "$api/releases/$(jq -r .id "$work/existing.json")" >/dev/null
      ;;
    *) cat "$work/existing.json" >&2; fail "looking up release $tag answered HTTP $code" ;;
  esac

  jq -n --arg tag "$tag" --arg sha "$GITHUB_SHA" --arg body "$notes" --argjson prerelease "$prerelease" \
    '{tag_name: $tag, target_commitish: $sha, name: $tag, body: $body, draft: true, prerelease: $prerelease}' > "$work/release.json"
  id=$(call -H 'Content-Type: application/json' --data @"$work/release.json" "$api/releases" | jq -er .id)
  for file in "$dist/$name" "$dist/$name.sha256"; do
    call -F "attachment=@$file" "$api/releases/$id/assets?name=${file##*/}" > "$work/asset.json"
    jq -e --argjson size "$(stat -c %s "$file")" '.size == $size' "$work/asset.json" >/dev/null ||
      fail "uploaded ${file##*/} does not have the staged size"
  done
  echo "release_id=$id" >> "$GITHUB_OUTPUT"
  echo "drafted $tag (release $id) with the tested $name"
}

publish() {
  [[ $1 =~ ^[0-9]+$ ]] || fail "invalid release id '$1'"
  call -X PATCH -H 'Content-Type: application/json' --data '{"draft": false}' "$api/releases/$1" > "$work/published.json"
  jq -r '"published \(.tag_name): \(.html_url)"' "$work/published.json"
}

case ${1:-}:$# in
  draft:2) draft "$2" ;;
  publish:2) publish "$2" ;;
  *) echo "usage: $0 draft DIST_DIR | publish RELEASE_ID" >&2; exit 2 ;;
esac
