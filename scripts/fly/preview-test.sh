#!/usr/bin/env bash
# Check secret isolation and idempotent preview updates without creating Fly resources.
set -euo pipefail
here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
state=$(mktemp -d)
trap 'rm -rf "$state"' EXIT
export STATE=$state
mkdir -p "$state/bin"
cat > "$state/bin/flyctl" <<'FAKE'
#!/usr/bin/env bash
set -euo pipefail
case "$1 $2" in
  "status -a") ;;
  "ips list") echo '[{}]' ;;
  "volumes list") echo '[{"id":"vol-test"}]' ;;
  "secrets list") jq -Rn '[inputs | {name: .}]' < "$STATE/secrets" ;;
  "secrets set")
    # Record names only; never write generated secret values to a log.
    for arg in "${@:3}"; do
      if [[ $arg == *=* ]]; then
        name=${arg%%=*}
        printf '%s\n' "$name" >> "$STATE/set"
        if [[ $name == TERRABASE_PEER_SECRET ]]; then
          test "$arg" = TERRABASE_PEER_SECRET=-
          peer=$(cat)
          [[ $peer =~ ^[0-9a-f]{64}$ ]]
          test "$(stat -L -c %a /dev/stdin)" = 600
          readlink -f /dev/stdin > "$STATE/peer-file"
          [[ $(cat "$STATE/peer-file") == /tmp/terrabase-peer-secret.* ]]
        fi
        grep -qx "$name" "$STATE/secrets" || printf '%s\n' "$name" >> "$STATE/secrets"
      fi
    done
    test ! -e "$STATE/fail-secret" ;;
  "ssh console") printf '%s\n' "$*" >> "$STATE/provision" ;;
  "storage destroy"|"apps destroy") echo "$1 $2 $3" >> "$STATE/destroy" ;;
  *) echo "unexpected flyctl command: $1 $2" >&2; exit 1 ;;
esac
FAKE
cat > "$state/bin/curl" <<'FAKE'
#!/usr/bin/env bash
set -euo pipefail
if [[ $* == *'/healthz'* || $* == *'/readyz'* ]]; then
  printf '%s\n' "${*: -1}" >> "$STATE/checks"
  [[ ! -e $STATE/fail-ready || $* != *'/readyz'* ]] || exit 22
elif [[ $* == *' -X POST '* ]]; then
  while (($#)); do
    if [[ $1 == -d ]]; then printf '%s\n' "$2" > "$STATE/request"; break; fi
    shift
  done
  echo '{"id":"machine-test","state":"created"}'
else
  if [[ -e $STATE/existing ]]; then echo '[{"id":"machine-test"}]'; else echo '[]'; fi
fi
FAKE
chmod +x "$state/bin/"*
export PATH="$state/bin:$PATH" FLY_API_TOKEN=fixture
printf '%s\n' TS_AUTH_MASTER_KEY TS_AUTH_AUDIT_SIGNING_KEY BUCKET_NAME TERRABASE_MASTER_KEY TERRABASE_PEER_SECRET > "$state/secrets"
"$here/preview.sh" app 123
test ! -e "$state/set"
sed -i '/TS_AUTH_AUDIT_SIGNING_KEY/d' "$state/secrets"
"$here/preview.sh" app 123
test "$(cat "$state/set")" = TS_AUTH_AUDIT_SIGNING_KEY

# A missing peer credential is generated once through a private stdin file, which success and failure both delete.
sed -i '/TERRABASE_PEER_SECRET/d' "$state/secrets"
rm "$state/set"
bash -x "$here/preview.sh" app 123 > "$state/trace" 2>&1
test "$(cat "$state/set")" = TERRABASE_PEER_SECRET
test ! -e "$(cat "$state/peer-file")"
if grep -qE '[0-9a-f]{64}' "$state/trace"; then
  echo 'preview: peer credential exposed in command trace' >&2
  exit 1
fi
"$here/preview.sh" app 123
test "$(wc -l < "$state/set")" = 1
sed -i '/TERRABASE_PEER_SECRET/d' "$state/secrets"
touch "$state/fail-secret"
if "$here/preview.sh" app 123; then
  echo 'preview: continued after credential installation failed' >&2
  exit 1
fi
test ! -e "$(cat "$state/peer-file")"
rm "$state/fail-secret"

for mode in create update; do
  [[ $mode != update ]] || touch "$state/existing"
  "$here/preview.sh" machine 123 registry.fly.io/test:terrabase registry.fly.io/test:auth >/dev/null
  jq -e '
    .config.guest == {cpu_kind: "shared", cpus: 1, memory_mb: 1024}
    and .config.mounts == [{volume: "vol-test", path: "/data"}]
    and (.config.containers[] | select(.name == "ts-auth") |
      .secrets == (["TS_AUTH_MASTER_KEY", "TS_AUTH_AUDIT_SIGNING_KEY", "TERRABASE_PEER_SECRET"] | map({env_var: ., name: .}))
      and .env.TS_AUTH_PUBLIC_ORIGIN == "https://ts-pr-123.fly.dev")
    and (.config.containers[] | select(.name == "terrabase") |
      (.secrets | map(.name) | sort) == (["AWS_ENDPOINT_URL_S3", "AWS_REGION", "BUCKET_NAME",
        "AWS_ACCESS_KEY_ID", "AWS_SECRET_ACCESS_KEY", "TERRABASE_MASTER_KEY", "TERRABASE_PEER_SECRET"] | sort))
    and all(.config.services[]; .autostop == "stop" and .autostart and .min_machines_running == 0)
  ' "$state/request" >/dev/null
done
test "$(wc -l < "$state/provision")" = 1
grep -q -- '--machine machine-test --container ts-auth -C /app/ts-auth provision-signing-key' "$state/provision"
test "$(cat "$state/checks")" = $'https://ts-pr-123.fly.dev:8443/healthz\nhttps://ts-pr-123.fly.dev:8443/readyz\nhttps://ts-pr-123.fly.dev/readyz\nhttps://ts-pr-123.fly.dev:8443/readyz\nhttps://ts-pr-123.fly.dev/readyz'
touch "$state/fail-ready"
if "$here/preview.sh" machine 123 registry.fly.io/test:terrabase registry.fly.io/test:auth > "$state/failed"; then
  echo 'preview: published a URL for an unready preview' >&2
  exit 1
fi
if grep -qx 'https://ts-pr-123.fly.dev' "$state/failed"; then
  echo 'preview: printed a URL after a failed readiness check' >&2
  exit 1
fi
"$here/preview.sh" destroy 123
test "$(cat "$state/destroy")" = $'storage destroy terrascale-ts-pr-123\napps destroy ts-pr-123'
echo 'preview: all checks passed'
