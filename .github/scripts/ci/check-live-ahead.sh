#!/usr/bin/env bash
# Writes skip=true when ts-orch already runs this commit or a newer main commit, so a late or queued release never rolls
# production back. Clean-slate services, unreadable live images and older live commits deploy; diverged history fails.
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

services=$(curl -fsS --retry 3 --retry-delay 2 --max-time 30 \
  -H "Authorization: Bearer ${jwt}" \
  "${orch_url}/api/services")
if ! jq -e '.services | type == "array"' <<< "$services" > /dev/null; then
  echo "::error::${orch_url}/api/services returned no services array; refusing to guess the live ${service} image" >&2
  exit 1
fi
live_image=$(jq -r --arg service "$service" '[.services[]? | select(.name == $service) | .image // empty] | first // empty' <<< "$services")
if [[ -z $live_image ]]; then
  decide false "ts-orch runs no ${service} image yet; deploying ${revision}"
  exit 0
fi

if ! config=$(docker buildx imagetools inspect "$live_image" --format '{{json .Image}}'); then
  echo "::warning::Cannot inspect the live image ${live_image}; deploying ${revision}"
  decide false "live image ${live_image} is unreadable; deploying ${revision}"
  exit 0
fi
live_revision=$(jq -r '(.config.Labels // .["linux/arm64"].config.Labels // .["linux/arm64/v8"].config.Labels // {})["org.opencontainers.image.revision"] // empty' <<< "$config")
if ! [[ $live_revision =~ ^[0-9a-f]{40}$ ]]; then
  echo "::warning::Live image ${live_image} carries no source revision label; deploying ${revision}"
  decide false "live image ${live_image} has no source revision; deploying ${revision}"
  exit 0
fi

if [[ $live_revision == "$revision" ]]; then
  decide true "live already runs ${revision}; skipping the release"
  exit 0
fi

status=''
for attempt in 1 2 3; do
  if status=$(gh api "repos/${repository}/compare/${live_revision}...${revision}?per_page=1" --jq '.status'); then
    break
  fi
  if (( attempt == 3 )); then
    echo "::error::Cannot compare live ${live_revision} with ${revision} after ${attempt} attempts" >&2
    exit 1
  fi
  echo "Comparing live ${live_revision} with ${revision} failed on attempt ${attempt}; retrying in 2s" >&2
  sleep 2
done
case $status in
  ahead) decide false "live ${live_revision} is older than ${revision}; deploying" ;;
  behind|identical) decide true "live ${live_revision} is already ahead of ${revision}; skipping to avoid a rollback" ;;
  diverged)
    echo "::error::Live ${service} ${live_revision} and ${revision} have diverged; refusing to release" >&2
    exit 1
    ;;
  *)
    echo "::error::Unexpected comparison status '${status}' between live ${live_revision} and ${revision}" >&2
    exit 1
    ;;
esac
