#!/usr/bin/env bash
# Rolls one Fly app out region by region: gru, sjc, fra, each followed by a live check of its new machine. A failed
# check stops the rollout (fix forward). Each region runs one always-on machine, created by a human (never by CI). Prints
# how long each region took (and appends it to the GitHub job summary in CI).
#
#   FLY_API_TOKEN=<deploy token> scripts/fly/deploy.sh <app> <fly.toml> <image>
set -euo pipefail

test "$#" -eq 3 || { echo "usage: $0 <app> <fly.toml> <image>" >&2; exit 2; }
app=$1 config=$2 image=$3

machines() { flyctl machine list -a "$app" --json | jq -r --arg r "$1" --arg s "$2" '.[] | select(.region == $r and (.state | test($s))) | .id'; }

# Checks the region's machine and sets id. Runs as an if condition, where set -e is off: every step returns on failure.
live() {
  # A machine that hit its restart limit stays stopped through a deploy that fixes it (docs/fly/spike-1a.md).
  for id in $(machines "$region" '^stopped$'); do
    flyctl machine start "$id" -a "$app" || return 1
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
  [[ $deployed == "$image" ]] || { echo "$app $region ($id) runs $deployed, not $image" >&2; return 1; }
}

for region in gru sjc fra; do
  start=$SECONDS
  # Machines and volumes cost money: CI never adds them (volume-reset.sh only replaces a region's refused volume).
  if [[ -z $(machines "$region" .) ]]; then
    echo "$app has no machine in $region; CI creates no machines or volumes. With approval, a human creates it" \
      "(gru: flyctl volumes create <mount source> -a $app -r gru -s 1 if fly.toml mounts one, then" \
      "flyctl deploy -a $app -c $config --image $image --regions gru --ha=false; other regions:" \
      "flyctl scale count 1 -a $app -c $config --region $region) and reruns this deploy." >&2
    exit 1
  fi
  # --ha=false: a deploy never adds Fly's default second machine. A storage-format refusal can fail the deploy or only
  # the live checks after it (the machine starts, then exits): either way the reset moves the region onto a fresh volume
  # (only while TerraBase holds no customer data) and the checks run again; any other failure, or a refused reset, stops
  # the rollout.
  if ! { flyctl deploy -a "$app" -c "$config" --image "$image" --regions "$region" --ha=false --yes --wait-timeout 3m && live; }; then
    { [[ $app == ts-terrabase ]] && "$(dirname "$0")/volume-reset.sh" "$app" "$region" && live; } || exit 1
  fi
  line="$app $region ($id) runs ${image##*:}: live $((SECONDS - start)) s after its deploy started"
  echo "$line"
  if [[ -n ${GITHUB_STEP_SUMMARY:-} ]]; then echo "- $line" >>"$GITHUB_STEP_SUMMARY"; fi
done
