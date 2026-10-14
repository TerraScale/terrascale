#!/usr/bin/env bash
# PR previews in the Fly org terrascale-previews-515: app ts-pr-<n>, one multi-container machine (TerraBase + ts-auth) with
# a 1 GB volume, stopped when idle and started by the next request. FLY_API_TOKEN is an org token for terrascale-previews-515.
# The containers share one network namespace, so each service keeps its own ports: ts-auth at https://ts-pr-<n>.fly.dev
# (8090), TerraBase's public API at https://ts-pr-<n>.fly.dev:8443 (8080). ts-auth reaches TerraBase's internal listener
# at top1.nearest.of.ts-pr-<n>.internal:8081, its own machine (App.TerraBaseOrigin).
#
#   scripts/fly/preview.sh app <pr>                         create the app, its IPs and volume (before images are pushed)
#   scripts/fly/preview.sh machine <pr> <terrabase-image> <auth-image>   create or update the machine
#   scripts/fly/preview.sh destroy <pr>                     delete the app and everything in it
#   scripts/fly/preview.sh reap                             destroy previews created more than 24 hours ago
set -euo pipefail
set +x

org=terrascale-previews-515
api() { curl -sS --fail-with-body -H "Authorization: $FLY_API_TOKEN" -H 'Content-Type: application/json' "$@"; }
machines=https://api.machines.dev/v1
# The preview's Tigris bucket (terrascale-<app>; bucket names are global) outlives the app unless destroyed first.
# A failed bucket deletion stops here so the app stays listed and the next reap can retry.
destroy_app() { flyctl storage destroy "terrascale-$1" --yes; flyctl apps destroy "$1" --yes; }

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
    # Throwaway keys for this preview only, set once so updates keep reading what earlier pushes encrypted.
    secrets=$(flyctl secrets list -a "$app" --json | jq -r '.[].name')
    for key in TS_AUTH_MASTER_KEY TS_AUTH_AUDIT_SIGNING_KEY; do
      if ! grep -qx "$key" <<<"$secrets"; then
        flyctl secrets set -a "$app" --stage "$key=$(openssl rand -base64 32)"
      fi
    done
    # TerraBase refuses to start without backup: each preview gets its own Tigris bucket and master key.
    secrets=$(flyctl secrets list -a "$app" --json | jq -r '.[].name')
    if ! grep -qx BUCKET_NAME <<<"$secrets"; then
      flyctl storage create -a "$app" -n "terrascale-$app" --yes > /dev/null
    fi
    if ! grep -qx TERRABASE_MASTER_KEY <<<"$secrets"; then
      flyctl secrets set -a "$app" --stage TERRABASE_MASTER_KEY="$(openssl rand -base64 32)"
    fi
    if ! grep -qx TERRABASE_PEER_SECRET <<<"$secrets"; then
      (
        umask 077
        peer_secret_file=$(mktemp /tmp/terrabase-peer-secret.XXXXXX)
        trap 'rm -f -- "$peer_secret_file"' EXIT
        openssl rand -hex 32 > "$peer_secret_file"
        flyctl secrets set -a "$app" --stage TERRABASE_PEER_SECRET=- < "$peer_secret_file"
      )
    fi
    ;;
  machine)
    app=ts-pr-$2 terrabase=$3 auth=$4
    volume=$(flyctl volumes list -a "$app" --json | jq -r '.[0].id')
    # App secrets reach a container only when its config maps them (staged secrets alone never arrive).
    config=$(jq -n --arg terrabase "$terrabase" --arg auth "$auth" --arg volume "$volume" --arg app "$app" '{
      guest: {cpu_kind: "shared", cpus: 1, memory_mb: 1024},
      mounts: [{volume: $volume, path: "/data"}],
      containers: [
        {name: "terrabase", image: $terrabase, secrets: ([
          "AWS_ENDPOINT_URL_S3", "AWS_REGION", "BUCKET_NAME", "AWS_ACCESS_KEY_ID", "AWS_SECRET_ACCESS_KEY", "TERRABASE_MASTER_KEY", "TERRABASE_PEER_SECRET"
        ] | map({env_var: ., name: .}))},
        {name: "ts-auth", image: $auth, env: {TS_AUTH_PUBLIC_ORIGIN: "https://\($app).fly.dev"},
          secrets: (["TS_AUTH_MASTER_KEY", "TS_AUTH_AUDIT_SIGNING_KEY", "TERRABASE_PEER_SECRET"] | map({env_var: ., name: .})),
          depends_on: [{name: "terrabase", condition: "started"}]}
      ],
      services: [
        {
          protocol: "tcp", internal_port: 8090, autostop: "stop", autostart: true, min_machines_running: 0,
          ports: [{port: 443, handlers: ["tls", "http"]}, {port: 80, handlers: ["http"], force_https: true}]
        },
        {
          protocol: "tcp", internal_port: 8080, autostop: "stop", autostart: true, min_machines_running: 0,
          ports: [{port: 8443, handlers: ["tls", "http"]}]
        }
      ]
    }')
    id=$(api "$machines/apps/$app/machines" | jq -r '.[0].id // empty')
    if [[ -z $id ]]; then
      created=$(api -X POST "$machines/apps/$app/machines" -d "$(jq -n --argjson config "$config" '{region: "gru", config: $config}')")
      jq '{id, state}' <<<"$created"
      id=$(jq -er .id <<<"$created")
      curl -fsS --retry 20 --retry-delay 2 --retry-all-errors --max-time 5 \
        -o /dev/null "https://$app.fly.dev:8443/healthz"
      # A new disposable preview needs its first issuer key; updates preserve state and fail if keys were lost.
      flyctl ssh console -a "$app" --machine "$id" --container ts-auth -C '/app/ts-auth provision-signing-key'
    else
      api -X POST "$machines/apps/$app/machines/$id" -d "$(jq -n --argjson config "$config" '{config: $config}')" | jq '{id, state}'
    fi
    for origin in "https://$app.fly.dev:8443" "https://$app.fly.dev"; do
      curl -fsS --retry 20 --retry-delay 2 --retry-all-errors --max-time 5 -o /dev/null "$origin/readyz"
    done
    echo "https://$app.fly.dev"
    ;;
  destroy)
    app=ts-pr-$2
    if flyctl status -a "$app" >/dev/null 2>&1; then
      destroy_app "$app"
    fi
    ;;
  reap)
    cutoff=$(date -u -d '24 hours ago' +%s)
    for app in $(flyctl apps list -o "$org" --json | jq -r '.[].Name | select(startswith("ts-pr-"))'); do
      created=$(api "$machines/apps/$app/machines" | jq -r '[.[].created_at] | min // empty')
      if [[ -n $created ]] && (( $(date -u -d "$created" +%s) < cutoff )); then
        echo "Reaping $app (machine created $created)"
        destroy_app "$app"
      fi
    done
    ;;
  *)
    echo "usage: $0 app <pr> | machine <pr> <terrabase-image> <auth-image> | destroy <pr> | reap" >&2
    exit 2
    ;;
esac
