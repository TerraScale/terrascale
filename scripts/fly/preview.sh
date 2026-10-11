#!/usr/bin/env bash
# PR previews in the Fly org terrascale-previews-515: app ts-pr-<n>, one multi-container machine (TerraBase + ts-auth) with
# a 1 GB volume, stopped when idle and started by the next request. FLY_API_TOKEN is an org token for terrascale-previews-515.
#
#   scripts/fly/preview.sh app <pr>                         create the app, its IPs and volume (before images are pushed)
#   scripts/fly/preview.sh machine <pr> <terrabase-image> <auth-image>   create or update the machine
#   scripts/fly/preview.sh destroy <pr>                     delete the app and everything in it
#   scripts/fly/preview.sh reap                             destroy previews created more than 24 hours ago
set -euo pipefail

org=terrascale-previews-515
api() { curl -sS --fail-with-body -H "Authorization: $FLY_API_TOKEN" -H 'Content-Type: application/json' "$@"; }
machines=https://api.machines.dev/v1

case ${1:-} in
  app)
    app=ts-pr-$2
    if ! flyctl status -a "$app" >/dev/null 2>&1; then
      flyctl apps create "$app" -o "$org"
    fi
    if [[ $(flyctl ips list -a "$app" --json | jq length) == 0 ]]; then
      flyctl ips allocate-v4 --shared -a "$app"
      flyctl ips allocate-v6 -a "$app"
    fi
    if [[ $(flyctl volumes list -a "$app" --json | jq length) == 0 ]]; then
      flyctl volumes create data -a "$app" -r gru -s 1 --yes
    fi
    ;;
  machine)
    app=ts-pr-$2 terrabase=$3 auth=$4
    volume=$(flyctl volumes list -a "$app" --json | jq -r '.[0].id')
    config=$(jq -n --arg terrabase "$terrabase" --arg auth "$auth" --arg volume "$volume" '{
      guest: {cpu_kind: "shared", cpus: 1, memory_mb: 1024},
      mounts: [{volume: $volume, path: "/data"}],
      containers: [
        {name: "terrabase", image: $terrabase},
        {name: "ts-auth", image: $auth, depends_on: [{name: "terrabase", condition: "started"}]}
      ],
      services: [{
        protocol: "tcp", internal_port: 8080, autostop: "stop", autostart: true, min_machines_running: 0,
        ports: [{port: 443, handlers: ["tls", "http"]}, {port: 80, handlers: ["http"], force_https: true}]
      }]
    }')
    id=$(api "$machines/apps/$app/machines" | jq -r '.[0].id // empty')
    if [[ -z $id ]]; then
      api -X POST "$machines/apps/$app/machines" -d "$(jq -n --argjson config "$config" '{region: "gru", config: $config}')" | jq '{id, state}'
    else
      api -X POST "$machines/apps/$app/machines/$id" -d "$(jq -n --argjson config "$config" '{config: $config}')" | jq '{id, state}'
    fi
    echo "https://$app.fly.dev"
    ;;
  destroy)
    app=ts-pr-$2
    if flyctl status -a "$app" >/dev/null 2>&1; then
      flyctl apps destroy "$app" --yes
    fi
    ;;
  reap)
    cutoff=$(date -u -d '24 hours ago' +%s)
    for app in $(flyctl apps list -o "$org" --json | jq -r '.[].Name | select(startswith("ts-pr-"))'); do
      created=$(api "$machines/apps/$app/machines" | jq -r '[.[].created_at] | min // empty')
      if [[ -n $created ]] && (( $(date -u -d "$created" +%s) < cutoff )); then
        echo "Reaping $app (machine created $created)"
        flyctl apps destroy "$app" --yes
      fi
    done
    ;;
  *)
    echo "usage: $0 app <pr> | machine <pr> <terrabase-image> <auth-image> | destroy <pr> | reap" >&2
    exit 2
    ;;
esac
