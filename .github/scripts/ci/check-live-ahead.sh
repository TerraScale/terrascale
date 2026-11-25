#!/usr/bin/env bash
set -euo pipefail

service=${1:?service required}
revision=${GITHUB_SHA:?GITHUB_SHA is required}
repository=${GITHUB_REPOSITORY:?GITHUB_REPOSITORY is required}
orch_url=${ORCH_URL:-https://orch.terrascale.tech}
orch_url=${orch_url%/}
output=${GITHUB_OUTPUT:-/dev/stdout}
summary=${GITHUB_STEP_SUMMARY:-/dev/null}

: "${ACTIONS_ID_TOKEN_REQUEST_URL:?GitHub Actions OIDC request URL is required (id-token: write)}"
: "${ACTIONS_ID_TOKEN_REQUEST_TOKEN:?GitHub Actions OIDC request token is required (id-token: write)}"

if ! [[ $revision =~ ^[0-9a-f]{40}$ ]]; then
  echo "::error::Invalid run commit: $revision" >&2
  exit 2
fi

decide() {
  echo "skip=$1" >> "$output"
  printf -- '- %s: %s\n' "$service" "$2" | tee -a "$summary"
}

sep='?'
if [[ $ACTIONS_ID_TOKEN_REQUEST_URL == *\?* ]]; then
  sep='&'
fi
jwt=$(curl -fsS --retry 3 --retry-delay 2 --max-time 30 \
  -H "Authorization: bearer ${ACTIONS_ID_TOKEN_REQUEST_TOKEN}" \
  "${ACTIONS_ID_TOKEN_REQUEST_URL}${sep}audience=ts-orch" | jq -er '.value')
echo "::add-mask::$jwt"

if ! status=$(curl -fsS --retry 3 --retry-delay 2 --max-time 30 \
  -H "Authorization: Bearer ${jwt}" \
  "${orch_url}/api/status"); then
  echo "::error::Cannot read ${orch_url}/api/status; refusing to guess the live ${service} images" >&2
  exit 1
fi
if ! jq -e '(.services | type == "array") and (.placements | type == "array") and (.rollouts | type == "array")' <<< "$status" > /dev/null 2>&1; then
  echo "::error::${orch_url}/api/status returned no services, placements or rollouts array; refusing to guess the live ${service} images" >&2
  exit 1
fi

if ! images=$(jq -r --arg service "$service" '
  def image_field($name; $required):
    .[$name] as $value
    | if $value == null or $value == "" then
        if $required then error("\($name) is missing or empty") else empty end
      elif ($value | type) != "string" then error("\($name) is not a string")
      elif ($value | test("\\s")) then error("\($name) contains whitespace")
      else $value
      end;
  def checked($field):
    if (.[$field] | type) == "string" then . else error("\($field) is not a string") end;
  if ([.services[], .placements[], .rollouts[]] | all(type == "object")) then . else error("non-object entry") end
  | [
      (.services[] | select(.name == $service) | image_field("image"; true)),
      (.placements[]
        | select(.serviceName == $service)
        | checked("state")
        | select(.state | IN("stopped", "failed", "unschedulable") | not)
        | image_field("desiredImage"; true), image_field("activeImage"; false), image_field("candidateImage"; false)),
      (.rollouts[]
        | select(.serviceName == $service)
        | checked("phase")
        | select(.phase | IN("succeeded", "rolled_back", "failed") | not)
        | image_field("targetImage"; true))
    ]
  | unique[]' <<< "$status"); then
  echo "::error::${orch_url}/api/status lists an invalid live ${service} image; refusing to release" >&2
  exit 1
fi
live_images=()
if [[ -n $images ]]; then
  mapfile -t live_images <<< "$images"
fi

if (( ${#live_images[@]} == 0 )); then
  decide false "ts-orch runs no ${service} image yet; deploying ${revision}"
  exit 0
fi

inspect_revision() {
  local image=$1 config='' attempt live
  for attempt in 1 2 3; do
    if config=$(docker buildx imagetools inspect "$image" --format '{{json .Image}}'); then
      break
    fi
    if (( attempt == 3 )); then
      echo "::error::Cannot inspect the live ${service} image ${image} after ${attempt} attempts; refusing to release" >&2
      return 1
    fi
    echo "Inspecting the live ${service} image ${image} failed on attempt ${attempt}; retrying in 2s" >&2
    sleep 2
  done
  live=$(jq -r '
    def revision:
      if type == "object" and (.config | type) == "object" and (.config.Labels | type) == "object" then
        .config.Labels["org.opencontainers.image.revision"]
      else
        null
      end
      | if type == "string" and test("^[0-9a-f]{40}\\z") then . else error("missing or invalid revision") end;
    def attestation:
      .key == "unknown/unknown" or (if (.value | type) == "object" then (.value.os // "") == "unknown" else false end);
    if type != "object" then
      error("image config is not an object")
    elif (.config | type) == "object" then
      revision
    else
      [to_entries[] | select(attestation | not) | .value]
      | if length == 0 then error("no platform configs") else map(revision) end
      | if (unique | length) != 1 then error("conflicting platform revisions") else .[0] end
    end' <<< "$config") || live=''
  if ! [[ $live =~ ^[0-9a-f]{40}$ ]]; then
    echo "::error::Live ${service} image ${image} has no single valid org.opencontainers.image.revision label across its platforms; refusing to release" >&2
    return 1
  fi
  printf '%s\n' "$live"
}

compare_status() {
  local live=$1 result='' attempt
  if [[ $live == "$revision" ]]; then
    printf 'identical\n'
    return 0
  fi
  for attempt in 1 2 3; do
    if result=$(gh api "repos/${repository}/compare/${live}...${revision}?per_page=1" --jq '.status'); then
      printf '%s\n' "$result"
      return 0
    fi
    if (( attempt == 3 )); then
      echo "::error::Cannot compare live ${live} with ${revision} after ${attempt} attempts" >&2
      return 1
    fi
    echo "Comparing live ${live} with ${revision} failed on attempt ${attempt}; retrying in 2s" >&2
    sleep 2
  done
}

declare -A seen=()
older=0
newer=()
for image in "${live_images[@]}"; do
  live_revision=$(inspect_revision "$image") || exit 1
  if [[ -n ${seen[$live_revision]:-} ]]; then
    continue
  fi
  seen[$live_revision]=1
  relation=$(compare_status "$live_revision") || exit 1
  case $relation in
    identical) ;;
    ahead) older=$((older + 1)) ;;
    behind) newer+=("${image} (${live_revision})") ;;
    diverged)
      echo "::error::Live ${service} image ${image} at ${live_revision} and ${revision} have diverged; refusing to release" >&2
      exit 1
      ;;
    *)
      echo "::error::Unexpected comparison status '${relation}' between live ${live_revision} (${image}) and ${revision}" >&2
      exit 1
      ;;
  esac
done

if (( ${#newer[@]} > 0 )); then
  decide true "live already runs newer ${newer[*]} than ${revision}; skipping to avoid a rollback"
elif (( older == 0 )); then
  decide true "every live ${service} image already runs ${revision}; skipping the release"
else
  decide false "${older} live ${service} revision(s) are older than ${revision} and none are newer; deploying"
fi
