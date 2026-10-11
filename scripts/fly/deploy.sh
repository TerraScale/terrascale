#!/usr/bin/env bash
# Rolls one Fly app out region by region: gru, a live check through the app's public URL, then sjc. A failed check
# stops the rollout (fix forward). Each region runs one always-on machine; the first rollout creates them.
#
#   FLY_API_TOKEN=<deploy token> scripts/fly/deploy.sh <app> <fly.toml> <image>
set -euo pipefail

test "$#" -eq 3 || { echo "usage: $0 <app> <fly.toml> <image>" >&2; exit 2; }
app=$1 config=$2 image=$3

for region in gru sjc; do
  if [[ $region != gru ]]; then
    # Clones the machine just deployed (new image, new volume) when the region has none; a no-op otherwise.
    flyctl scale count 1 -a "$app" -c "$config" --region "$region" --yes
  fi
  # --ha=false: the first deploy creates one machine in primary_region (gru), not Fly's default pair.
  flyctl deploy -a "$app" -c "$config" --image "$image" --regions "$region" --ha=false --yes --wait-timeout 5m
  # A machine that hit its restart limit stays stopped through a deploy that fixes it (docs/fly/spike-1a.md).
  for id in $(flyctl machine list -a "$app" --json | jq -r --arg r "$region" '.[] | select(.region == $r and .state == "stopped") | .id'); do
    flyctl machine start "$id" -a "$app"
  done
  echo "Live check: https://$app.fly.dev/healthz in $region"
  # fly-prefer-region makes the Fly proxy answer from the region just deployed.
  curl -fsS --retry 20 --retry-delay 3 --retry-all-errors --max-time 5 -H "fly-prefer-region: $region" \
    -o /dev/null -w "HTTP %{http_code}, request %header{fly-request-id}\n" "https://$app.fly.dev/healthz"
done
