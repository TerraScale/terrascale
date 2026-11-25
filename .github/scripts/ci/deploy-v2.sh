#!/usr/bin/env bash
set -euo pipefail

service=${1:?service required}
image=${2:?image required}
orch_url=${ORCH_URL:-https://orch.terrascale.tech}
orch_url=${orch_url%/}
timeout_seconds=${ROLLOUT_TIMEOUT_SECONDS:-900}
summary=${GITHUB_STEP_SUMMARY:-/dev/null}

case "$service" in
  terrabase|ts-platform|ts-auth) package=$service ;;
  ts-dashboard) package='(terrascale-dashboard|ts-dashboard)' ;;
  *) echo "Unknown service: $service" >&2; exit 2 ;;
esac

if ! [[ $image =~ ^[a-z0-9-]+\.ocir\.io/[a-z0-9]+/terrascale/${package}@sha256:[0-9a-f]{64}$ ]]; then
  echo "Expected a digest-pinned OCIR image for $service, got: $image" >&2
  exit 2
fi

: "${ACTIONS_ID_TOKEN_REQUEST_URL:?GitHub Actions OIDC request URL is required (id-token: write)}"
: "${ACTIONS_ID_TOKEN_REQUEST_TOKEN:?GitHub Actions OIDC request token is required (id-token: write)}"

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

fetch_oidc_jwt() {
  local sep='?'
  if [[ "$ACTIONS_ID_TOKEN_REQUEST_URL" == *\?* ]]; then
    sep='&'
  fi
  local token_response
  token_response=$(curl -fsS --retry 3 --retry-delay 2 \
    -H "Authorization: bearer ${ACTIONS_ID_TOKEN_REQUEST_TOKEN}" \
    "${ACTIONS_ID_TOKEN_REQUEST_URL}${sep}audience=ts-orch")
  jq -er '.value' <<< "$token_response"
}

jwt=$(fetch_oidc_jwt)
jwt_fetched_at=$SECONDS

payload=$(jq -cn --arg service "$service" --arg image "$image" '{service: $service, image: $image}')
deploy_body="$work/deploy.json"
deploy_status=$(curl -sS --max-time 30 -o "$deploy_body" -w '%{http_code}' \
  -X POST \
  -H "Authorization: Bearer ${jwt}" \
  -H 'Content-Type: application/json' \
  -d "$payload" \
  "${orch_url}/api/deploy")

if [[ "$deploy_status" != '200' && "$deploy_status" != '202' ]]; then
  echo "POST ${orch_url}/api/deploy failed with HTTP ${deploy_status}:" >&2
  cat "$deploy_body" >&2
  echo >&2
  exit 1
fi

rollout_id=$(jq -er '.id' "$deploy_body")
last_phase=$(jq -r '.phase // "unknown"' "$deploy_body")
echo "Started rollout ${rollout_id} for ${service} (${image}): phase=${last_phase}"

deadline=$((SECONDS + timeout_seconds))
poll_body="$work/rollouts.json"
current_json=$(cat "$deploy_body")

while true; do
  case "$last_phase" in
    succeeded)
      {
        printf '\n### %s v2 rollout\n\n' "$service"
        printf -- '- Rollout ID: %s\n' "$rollout_id"
        printf -- '- Image: %s\n' "$image"
        printf -- '- Phase: %s\n' "$last_phase"
      } >> "$summary"
      echo "Rollout ${rollout_id} for ${service} succeeded."
      exit 0
      ;;
    failed|rolled_back)
      reason=$(jq -r '.error // .reason // "no error detail"' <<< "$current_json")
      {
        printf '\n### %s v2 rollout\n\n' "$service"
        printf -- '- Rollout ID: %s\n' "$rollout_id"
        printf -- '- Image: %s\n' "$image"
        printf -- '- Phase: %s\n' "$last_phase"
        printf -- '- Reason: %s\n' "$reason"
      } >> "$summary"
      echo "::error::Rollout ${rollout_id} for ${service} ended with phase=${last_phase}: ${reason}" >&2
      exit 1
      ;;
  esac

  if (( SECONDS >= deadline )); then
    echo "::error::Timed out after ${timeout_seconds}s waiting for rollout ${rollout_id} (${service}), last phase=${last_phase}" >&2
    echo "$current_json" >&2
    exit 1
  fi

  sleep 2

  if (( SECONDS - jwt_fetched_at >= 180 )); then
    jwt=$(fetch_oidc_jwt)
    jwt_fetched_at=$SECONDS
  fi

  poll_status=$(curl -sS --max-time 30 -o "$poll_body" -w '%{http_code}' \
    -H "Authorization: Bearer ${jwt}" \
    "${orch_url}/api/rollouts")

  if [[ "$poll_status" != '200' ]]; then
    echo "GET ${orch_url}/api/rollouts returned HTTP ${poll_status}; retrying..." >&2
    continue
  fi

  found=$(jq -c --arg id "$rollout_id" '.rollouts[]? | select(.id == $id)' "$poll_body")
  if [[ -z "$found" ]]; then
    echo "Rollout ${rollout_id} not yet listed in /api/rollouts; retrying..." >&2
    continue
  fi

  current_json=$found
  phase=$(jq -r '.phase // "unknown"' <<< "$current_json")
  if [[ "$phase" != "$last_phase" ]]; then
    echo "Rollout ${rollout_id} (${service}): phase=${phase}"
    last_phase=$phase
  fi
done
