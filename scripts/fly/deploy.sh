#!/usr/bin/env bash
# Rolls one Fly app out region by region: gru, sjc, fra, each followed by a live check of its new machine. A failed
# check stops the rollout (fix forward). TerraBase's storage-format preflight checks every serving region before any
# mutation; a mismatch requires a coordinated, approved reset by one executor. Deployment never wipes volumes.
# Each region runs one always-on machine, created by a human (never by CI). Prints
# how long each region took (and appends it to the GitHub job summary in CI).
#
#   FLY_API_TOKEN=<deploy token> scripts/fly/deploy.sh <app> <fly.toml> <image>
set -euo pipefail

test "$#" -eq 3 || { echo "usage: $0 <app> <fly.toml> <image>" >&2; exit 2; }
app=$1 config=$2 image=$3

machines() { flyctl machine list -a "$app" --json | jq -r --arg r "$1" --arg s "$2" '.[] | select(.region == $r and (.state | test($s))) | .id'; }

# The storage format the region's machine serves (a build refuses a directory of another format, so a serving machine's
# format is the one on its disk; "unversioned" before builds reported it), or nothing when it does not answer from its
# own region: then the rollout runs and a refusal is classified from the logs.
running_format() {
  local id body
  id=$(machines "$1" '^started$' | head -n1) || return 1
  [[ -n $id ]] || return 0
  body=$(curl -fsS --retry 3 --retry-all-errors --max-time 5 -H "fly-force-instance-id: $id" "https://$app.fly.dev/healthz") || return 0
  jq -r --arg r "$1" 'select(.region == $r) | .storageFormat // "unversioned"' <<<"$body"
}

# Checks the region's machine and sets id. Runs as an if condition, where set -e is off: every step returns on failure.
live() {
  local state
  # A machine that hit its restart limit stays stopped through a deploy that fixes it (docs/fly/spike-1a.md).
  for id in $(machines "$region" '^stopped$'); do
    if ! flyctl machine start "$id" -a "$app"; then
      # Fly Proxy can start this exact machine after the list above. Accept only that concurrent start; the pinned
      # live checks below still have to pass. Missing machines, failed lookups and other states stop the rollout.
      state=$(flyctl machine list -a "$app" --json | jq -er --arg id "$id" '.[] | select(.id == $id) | .state') \
        || return 1
      [[ $state == starting || $state == started ]] || return 1
      echo "$app $region ($id) is already $state; checking health"
    fi
  done
  # fly-force-instance-id pins the request to the region's machine; fly-prefer-region would fall back to another region.
  id=$(machines "$region" . | head -n1)
  curl -fsS --retry 20 --retry-delay 2 --retry-all-errors --max-time 5 -H "fly-force-instance-id: $id" \
    -o /dev/null -w "Live check $app $region ($id): HTTP %{http_code}, request %header{fly-request-id}\n" "https://$app.fly.dev/healthz" \
    || return 1
  if [[ $app == ts-terrabase ]]; then
    # The new machine must ship its backup to Tigris: /backupz is 503 until every file it ships reached the bucket.
    curl -fsS --retry 45 --retry-delay 2 --retry-all-errors --max-time 5 -H "fly-force-instance-id: $id" \
      -o /dev/null -w "Backup check $app $region ($id): HTTP %{http_code}\n" "https://$app.fly.dev/backupz" || return 1
  fi
  # Records (and asserts) the commit the region's machine runs: the image tag is the commit SHA.
  local deployed
  deployed=$(flyctl machine list -a "$app" --json | jq -r --arg id "$id" '.[] | select(.id == $id) | .config.image')
  # A clone (volume reset) pins the image by digest: <image>@sha256:<digest>.
  [[ ${deployed%@sha256:*} == "$image" ]] || { echo "$app $region ($id) runs $deployed, not $image" >&2; return 1; }
}

if [[ $app == ts-terrabase ]]; then
  # `terrabase --storage-format` prints the format the image writes without starting a server (the image has no shell).
  format=$(docker run --rm "$image" --storage-format) && [[ -n $format ]] \
    || { echo "could not read the storage format of $image" >&2; exit 1; }
  echo "$image writes storage format $format"
  # Check the whole cluster before updating even the first region: a regional wipe could refill from old peers.
  for region in gru sjc fra; do
    running=$(running_format "$region") \
      || { echo "could not inspect $app $region machines; rollout refused before mutation." >&2; exit 1; }
    if [[ -n $running && $running != "$format" ]]; then
      echo "$app $region stores format $running, $image writes $format: rollout refused before mutation." >&2
      echo 'A single reset executor must isolate old peers, stage every old machine on the target image, and complete the approved cluster reset before retrying deployment.' >&2
      exit 1
    fi
  done
fi

for region in gru sjc fra; do
  start=$SECONDS
  started_at=$(date -u +%Y-%m-%dT%H:%M:%SZ)
  echo "$started_at: starting $app $region rollout ($image)"
  # Machines and volumes cost money: CI never adds them.
  if [[ -z $(machines "$region" .) ]]; then
    echo "$app has no machine in $region; CI creates no machines or volumes. With approval, a human creates it" \
      "(gru: flyctl volumes create <mount source> -a $app -r gru -s 1 if fly.toml mounts one, then" \
      "flyctl deploy -a $app -c $config --image $image --regions gru --ha=false; other regions:" \
      "flyctl scale count 1 -a $app -c $config --region $region) and reruns this deploy." >&2
    exit 1
  fi
  # --ha=false: a deploy never adds Fly's default second machine. An unavailable region can be repaired by its new
  # image, but any deployment or live-check failure stops here. Storage-format recovery never wipes data automatically.
  if ! { flyctl deploy -a "$app" -c "$config" --image "$image" --regions "$region" --ha=false --yes --wait-timeout 3m && live; }; then
    echo "$app $region rollout failed; no automatic volume reset was attempted." >&2
    exit 1
  fi
  completed_at=$(date -u +%Y-%m-%dT%H:%M:%SZ)
  line="$started_at -> $completed_at: $app $region ($id) runs ${image##*:}${format:+, storage format $format}: live $((SECONDS - start)) s after its deploy started"
  echo "$line"
  if [[ -n ${GITHUB_STEP_SUMMARY:-} ]]; then echo "- $line" >>"$GITHUB_STEP_SUMMARY"; fi
done
