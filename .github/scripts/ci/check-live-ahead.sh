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

reject_nul() {
  if [[ -n $(LC_ALL=C tr -cd '\0' < "$1" | tr '\0' x) ]]; then
    echo "::error::$2 contained a NUL byte" >&2
    exit 1
  fi
}

sep='?'
if [[ $ACTIONS_ID_TOKEN_REQUEST_URL == *\?* ]]; then
  sep='&'
fi
jwt=$(curl -fsS --retry 3 --retry-delay 2 --max-time 30 \
  -H "Authorization: bearer ${ACTIONS_ID_TOKEN_REQUEST_TOKEN}" \
  "${ACTIONS_ID_TOKEN_REQUEST_URL}${sep}audience=ts-orch" | jq -er '.value')
echo "::add-mask::$jwt"

tmpdir=$(mktemp -d)
trap 'rm -rf "$tmpdir"' EXIT
status_file="${tmpdir}/status.json"
if ! curl -fsS --retry 3 --retry-delay 2 --max-time 30 \
  -H "Authorization: Bearer ${jwt}" \
  -o "$status_file" \
  "${orch_url}/api/status"; then
  echo "::error::Cannot read ${orch_url}/api/status; refusing to guess the live ${service} images" >&2
  exit 1
fi
reject_nul "$status_file" "${orch_url}/api/status"
if ! jq -se 'length == 1 and (.[0] | type == "object") and (.[0].services | type == "array") and (.[0].placements | type == "array") and (.[0].rollouts | type == "array")' "$status_file" > /dev/null 2>&1; then
  echo "::error::${orch_url}/api/status did not return a single object with services, placements and rollouts arrays; refusing to guess the live ${service} images" >&2
  exit 1
fi

if ! images=$(jq -r --arg service "$service" '
  def image_field($name; $required):
    .[$name] as $value
    | if $value == null or $value == "" then
        if $required then error("\($name) is missing or empty") else empty end
      elif ($value | type) != "string" then error("\($name) is not a string")
      elif ($value | explode | any(. <= 32 or . == 127)) or ($value | test("\\s")) then error("\($name) contains whitespace or control characters")
      else $value
      end;
  def checked($field):
    if (.[$field] | type) == "string" then . else error("\($field) is not a string") end;
  def named($field):
    (.[$field] | type == "string" and length > 0);
  if ([.services[], .placements[], .rollouts[]] | all(type == "object")) then . else error("non-object entry") end
  | if (.services | all(named("name"))) then . else error("service without a name") end
  | if ([.placements[], .rollouts[]] | all(named("serviceName"))) then . else error("placement or rollout without a serviceName") end
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
  | unique[]' "$status_file"); then
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

inspect_json() {
  local ref=$1 field=$2 file attempt
  file=$(mktemp "${tmpdir}/inspect.XXXXXX")
  for attempt in 1 2 3; do
    if docker buildx imagetools inspect "$ref" --format "{{json .${field}}}" > "$file"; then
      break
    fi
    if (( attempt == 3 )); then
      echo "::error::Cannot inspect the ${field} of live ${service} image ${ref} after ${attempt} attempts; refusing to release" >&2
      return 1
    fi
    echo "Inspecting the ${field} of live ${service} image ${ref} failed on attempt ${attempt}; retrying in 2s" >&2
    sleep 2
  done
  reject_nul "$file" "docker inspect ${field} for ${ref}"
  if ! jq -se 'length == 1 and (.[0] | type == "object")' "$file" > /dev/null 2>&1; then
    echo "::error::Live ${service} image ${ref} returned no single ${field} object; refusing to release" >&2
    return 1
  fi
  printf '%s\n' "$file"
}

config_revision() {
  local ref=$1 config live
  config=$(inspect_json "$ref" Image) || return 1
  live=$(jq -r '
    if (.os // "") == "unknown" then error("attestation config") else . end
    | if (.config | type) == "object" and (.config.Labels | type) == "object" then .config.Labels["org.opencontainers.image.revision"] else null end
    | if type == "string" and test("^[0-9a-f]{40}\\z") then . else error("missing or invalid revision") end' "$config") || live=''
  if ! [[ $live =~ ^[0-9a-f]{40}$ ]]; then
    echo "::error::Live ${service} image ${ref} has no valid org.opencontainers.image.revision label or is an attestation; refusing to release" >&2
    return 1
  fi
  printf '%s\n' "$live"
}

repository_of() {
  local ref=${1%%@*}
  if [[ ${ref##*/} == *:* ]]; then
    ref=${ref%:*}
  fi
  printf '%s\n' "$ref"
}

inspect_revision() {
  local image=$1 manifest child digests repo digest live revisions=()
  manifest=$(inspect_json "$image" Manifest) || return 1
  if ! digests=$(jq -r '
    def index_type:
      IN("application/vnd.oci.image.index.v1+json", "application/vnd.docker.distribution.manifest.list.v2+json");
    def attestation:
      (.annotations | type) == "object" and .annotations["vnd.docker.reference.type"] == "attestation-manifest"
      and (.platform | type) == "object" and .platform.os == "unknown" and .platform.architecture == "unknown";
    if (.manifests != null) or (.mediaType | index_type) then
      if (.manifests | type) != "array" then error("index without a manifests array") else . end
      | if (.manifests | all(type == "object" and (.digest | type == "string" and test("^[a-z0-9]+(?:[+._-][a-z0-9]+)*:[a-zA-Z0-9=_-]+\\z")) and (.mediaType | index_type | not))) then . else error("invalid or nested descriptor") end
      | [.manifests[] | select(attestation | not) | .digest]
      | if length == 0 then error("no platform descriptors") else .[] end
    else
      empty
    end' "$manifest"); then
    echo "::error::Live ${service} image ${image} has an invalid image index; refusing to release" >&2
    return 1
  fi
  if [[ -z $digests ]]; then
    config_revision "$image"
    return
  fi
  repo=$(repository_of "$image")
  while IFS= read -r digest; do
    child=$(inspect_json "${repo}@${digest}" Manifest) || return 1
    if ! jq -e '.manifests == null and (.mediaType | IN("application/vnd.oci.image.index.v1+json", "application/vnd.docker.distribution.manifest.list.v2+json") | not)' "$child" > /dev/null 2>&1; then
      echo "::error::Live ${service} image ${image} platform ${digest} resolves to a nested image index; refusing to release" >&2
      return 1
    fi
    live=$(config_revision "${repo}@${digest}") || return 1
    revisions+=("$live")
  done <<< "$digests"
  if (( $(printf '%s\n' "${revisions[@]}" | sort -u | wc -l) != 1 )); then
    echo "::error::Live ${service} image ${image} has conflicting org.opencontainers.image.revision labels across its platforms; refusing to release" >&2
    return 1
  fi
  printf '%s\n' "${revisions[0]}"
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
