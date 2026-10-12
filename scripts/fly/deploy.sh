#!/usr/bin/env bash
# Rolls one Fly app out region by region: gru, sjc, fra, each followed by a live check of its new machine. A failed
# check stops the rollout (fix forward). Each region runs one always-on machine; the first rollout creates them. Prints how long each
# region took (and appends it to the GitHub job summary in CI).
#
#   FLY_API_TOKEN=<deploy token> scripts/fly/deploy.sh <app> <fly.toml> <image>
set -euo pipefail

test "$#" -eq 3 || { echo "usage: $0 <app> <fly.toml> <image>" >&2; exit 2; }
app=$1 config=$2 image=$3

machines() { flyctl machine list -a "$app" --json | jq -r --arg r "$1" --arg s "$2" '.[] | select(.region == $r and (.state | test($s))) | .id'; }

for region in gru sjc fra; do
  start=$SECONDS
  if [[ $region != gru && -z $(machines "$region" .) ]]; then
    # Clones the machine just deployed (new image, new volume).
    flyctl scale count 1 -a "$app" -c "$config" --region "$region" --yes
  fi
  # --ha=false: the first deploy creates one machine in primary_region (gru), not Fly's default pair.
  flyctl deploy -a "$app" -c "$config" --image "$image" --regions "$region" --ha=false --yes --wait-timeout 3m
  # A machine that hit its restart limit stays stopped through a deploy that fixes it (docs/fly/spike-1a.md).
  for id in $(machines "$region" '^stopped$'); do
    flyctl machine start "$id" -a "$app"
  done
  # fly-force-instance-id pins the request to the region's machine; fly-prefer-region would fall back to another region.
  id=$(machines "$region" . | head -n1)
  curl -fsS --retry 20 --retry-delay 2 --retry-all-errors --max-time 5 -H "fly-force-instance-id: $id" \
    -o /dev/null -w "Live check $app $region ($id): HTTP %{http_code}, request %header{fly-request-id}\n" "https://$app.fly.dev/healthz"
  if [[ $app == ts-terrabase ]]; then
    # The new machine must ship its backup to Tigris: /backupz is 503 until every file it ships reached the bucket.
    curl -fsS --retry 45 --retry-delay 2 --retry-all-errors --max-time 5 -H "fly-force-instance-id: $id" \
      -o /dev/null -w "Backup check $app $region ($id): HTTP %{http_code}\n" "https://$app.fly.dev/backupz"
  fi
  line="$app $region: live $((SECONDS - start)) s after its deploy started"
  echo "$line"
  if [[ -n ${GITHUB_STEP_SUMMARY:-} ]]; then echo "- $line" >>"$GITHUB_STEP_SUMMARY"; fi
done
