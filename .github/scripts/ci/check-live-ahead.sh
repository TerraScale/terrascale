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
  [
    (.services[] | select(.name == $service) | .image),
    (.placements[]
      | select(.serviceName == $service and ((.state | IN("stopped", "failed", "unschedulable")) | not))
      | .desiredImage, .activeImage, .candidateImage),
    (.rollouts[]
      | select(.serviceName == $service and ((.phase | IN("succeeded", "rolled_back", "failed")) | not))
      | .targetImage)
  ]
  | map(select(type == "string" and length > 0))
  | unique[]' <<< "$status"); then
  echo "::error::Cannot read the live ${service} images from ${orch_url}/api/status; refusing to release" >&2
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
  live=$(jq -r '(
      .config.Labels
      // .["linux/arm64"].config.Labels
      // .["linux/arm64/v8"].config.Labels
      // .["linux/amd64"].config.Labels
      // first(to_entries[]? | .value.config.Labels? // empty)
      // {}
    )["org.opencontainers.image.revision"] // empty' <<< "$config" 2> /dev/null) || live=''
  if ! [[ $live =~ ^[0-9a-f]{40}$ ]]; then
    echo "::error::Live ${service} image ${image} carries no valid org.opencontainers.image.revision label; refusing to release" >&2
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
