#!/usr/bin/env bash
# PR previews in the Fly org terrascale-previews-515: app ts-pr-<n>, one multi-container machine (TerraBase + ts-auth) with
# a 1 GB volume, stopped when idle and started by the next request. FLY_API_TOKEN is an org token for terrascale-previews-515.
# The containers share one network namespace, so each service keeps its own ports: ts-auth at https://ts-pr-<n>.fly.dev
# (8090), TerraBase's public API at https://ts-pr-<n>.fly.dev:8443 (8080). ts-auth reaches TerraBase's internal listener
# at top1.nearest.of.ts-pr-<n>.internal:8081, its own machine (App.TerraBaseOrigin).
#
#   scripts/fly/preview.sh app <pr>                         create the app, its IPs and volume (before images are pushed)
#   scripts/fly/preview.sh machine <pr> <terrabase-image> <auth-image>   create or update the machine
#   scripts/fly/preview.sh destroy <pr>                     delete the app and everything in it; succeeds if already gone
#   scripts/fly/preview.sh expired                          print the JSON array of PRs whose preview must go: closed PRs,
#                                                           and open PRs whose oldest machine (or, with no machine, oldest
#                                                           volume) was created more than 24 hours ago
# `expired` reads PR state with gh (GH_TOKEN, GITHUB_REPOSITORY). Every API failure is an error, never "absent".
set -euo pipefail
set +x

org=terrascale-previews-515
api() { curl -sS --fail-with-body -H "Authorization: $FLY_API_TOKEN" -H 'Content-Type: application/json' "$@"; }
machines=https://api.machines.dev/v1
usage() {
  echo "usage: $0 app <pr> | machine <pr> <terrabase-image> <auth-image> | destroy <pr> | expired" >&2
  exit 2
}
# A PR number is the only input that names an app; anything else could reach an unrelated app.
[[ ${1:-} == expired || ${2:-} =~ ^[1-9][0-9]{0,8}$ ]] || usage

# Prints the PR number of every preview bucket (terrascale-ts-pr-<pr>) in the org. flyctl storage list has no JSON:
# fail unless every line has its table shape (" NAME │ ORG", then " <name> │ <org>"), so a changed format is never "none".
buckets() {
  local table
  table=$(flyctl storage list -o "$org") || return 1
  awk -v org="$org" '
    NR == 1 { if (NF != 3 || $1 != "NAME" || $2 != "│" || $3 != "ORG") exit 1; next }
    NF == 0 { next }
    NF != 3 || $2 != "│" || $3 != org { exit 1 }
    $1 ~ /^terrascale-ts-pr-[1-9][0-9]*$/ && length($1) <= 26 { print substr($1, 18) }
  ' <<<"$table"
}

# Reads a Fly machine or volume JSON list; prints its oldest created_at as epoch seconds, or nothing for []. Any other
# shape, or a date date(1) cannot parse, fails. Epochs, not strings: offsets make string order wrong.
oldest() {
  local stamps stamp epoch min=""
  stamps=$(jq -er 'if type == "array" and all(.[]; .created_at | type == "string" and length > 0 and (contains("\n") | not))
    then map(.created_at) | join("\n") else error("unexpected Fly listing") end') || return 1
  [[ -n $stamps ]] || return 0
  while IFS= read -r stamp; do
    epoch=$(date -u -d "$stamp" +%s) || return 1
    if [[ -z $min ]] || (( epoch < min )); then min=$epoch; fi
  done <<<"$stamps"
  printf '%s\n' "$min"
}

# Prints the PR number when its preview must go; returns non-zero when an API call or a date fails. $2: an app exists.
expired() {
  local state created cutoff
  state=$(gh api "repos/$GITHUB_REPOSITORY/pulls/$1" --jq .state) || return 1
  case $state in
    closed) echo "$1" ;;
    open)
      # An open PR's leftover bucket without an app has no age; its next preview build fails visibly on create.
      [[ $2 == true ]] || return 0
      created=$(api "$machines/apps/ts-pr-$1/machines" | oldest) || return 1
      [[ -n $created ]] || created=$(flyctl volumes list -a "ts-pr-$1" --json | oldest) || return 1
      # Neither machines nor volumes: nothing paid is left to age, so the app stays until the PR closes.
      [[ -n $created ]] || return 0
      cutoff=$(date -u -d '24 hours ago' +%s) || return 1
      if (( created < cutoff )); then echo "$1"; fi
      ;;
    *) echo "unexpected state '$state' for PR $1" >&2; return 1 ;;
  esac
}

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
    body=$(mktemp)
    trap 'rm -f -- "$body"' EXIT
    code=$(curl -sS -o "$body" -w '%{http_code}' -H "Authorization: $FLY_API_TOKEN" "$machines/apps/$app")
    case $code in
      404) echo "$app is already deleted; checking its bucket" ;;
      200) jq -e --arg org "$org" '.organization.slug == $org' "$body" >/dev/null \
        || { echo "refusing to delete $app: not in $org" >&2; exit 1; } ;;
      *) echo "Fly API returned $code for $app" >&2; exit 1 ;;
    esac
    # The Tigris bucket (terrascale-<app>; bucket names are global) outlives the app, so it is looked up on its own.
    bucket_prs=$(buckets)
    if grep -qx "$2" <<<"$bucket_prs"; then
      flyctl storage destroy "terrascale-$app" --yes
    fi
    [[ $code == 404 ]] || flyctl apps destroy "$app" --yes
    ;;
  expired)
    # One joined string, so [] is a valid empty list while empty output, null names or other shapes fail jq -e.
    apps=$(flyctl apps list -o "$org" --json \
      | jq -er 'if type == "array" and all(.[]; .Name | type == "string") then map(.Name) | join("\n") else error end')
    bucket_prs=$(buckets)
    app_prs=
    for app in $apps; do
      if [[ $app =~ ^ts-pr-([1-9][0-9]{0,8})$ ]]; then app_prs+="${BASH_REMATCH[1]}"$'\n'; fi
    done
    prs=() failed=0
    while read -r pr; do
      [[ -n $pr ]] || continue
      has_app=false
      if grep -qx "$pr" <<<"$app_prs"; then has_app=true; fi
      out=$(expired "$pr" "$has_app") || { echo "cannot check PR $pr" >&2; failed=1; continue; }
      [[ -z $out ]] || prs+=("$out")
    done < <(sort -nu <<<"$app_prs$bucket_prs")
    # Print what could be decided so those previews still go, but fail the run for the rest.
    jq -cn '$ARGS.positional | map(tonumber)' --args "${prs[@]}"
    exit "$failed"
    ;;
  *) usage ;;
esac
