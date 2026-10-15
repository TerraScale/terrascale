#!/usr/bin/env bash
# Check secret isolation, idempotent preview updates and safe cleanup without creating Fly resources.
set -euo pipefail
here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
state=$(mktemp -d)
trap 'rm -rf "$state"' EXIT
export STATE=$state
mkdir -p "$state/bin"
cat > "$state/bin/flyctl" <<'FAKE'
#!/usr/bin/env bash
set -euo pipefail
echo "$*" >> "$STATE/flyctl"
case "$1 $2" in
  "status -a") ;;
  "apps list") test ! -e "$STATE/fail-apps"; cat "$STATE/apps" ;;
  "storage list") test ! -e "$STATE/fail-list"; cat "$STATE/buckets" ;;
  "ips list") echo '[{}]' ;;
  "volumes list") if [[ -e $STATE/volumes-$4 ]]; then cat "$STATE/volumes-$4"; else cat "$STATE/volumes"; fi ;;
  "volumes create") echo "$*" >> "$STATE/volume-creates" ;;
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
  "ssh console") echo 'preview: startup owns signing-key provisioning; no duplicate SSH command' >&2; exit 1 ;;
  "logs -a") test ! -e "$STATE/fail-logs"; cat "$STATE/logs" ;;
  "storage destroy"|"apps destroy")
    test ! -e "$STATE/fail-$1"
    echo "$1 $2 $3" >> "$STATE/destroy"
    sed -i "/^ $3 /d" "$STATE/buckets" ;;
  *) echo "unexpected flyctl command: $1 $2" >&2; exit 1 ;;
esac
FAKE
cat > "$state/bin/curl" <<'FAKE'
#!/usr/bin/env bash
set -euo pipefail
if [[ $* == *'%{http_code}'* ]]; then
  while [[ $1 != -o ]]; do shift; done
  printf '{"organization":{"slug":"%s"}}' "$(cat "$STATE/org")" > "$2"
  cat "$STATE/code"
elif [[ $* =~ /apps/(ts-pr-[0-9]+)/machines$ && -e $STATE/machines-${BASH_REMATCH[1]} ]]; then
  test ! -e "$STATE/fail-machines" || exit 22
  cat "$STATE/machines-${BASH_REMATCH[1]}"
elif [[ $* == *'/healthz'* || $* == *'/readyz'* ]]; then
  # Records "<pinned machine> <url>"; fail-ready fails every /readyz, or only those pinned to the machine it names.
  [[ $* =~ fly-force-instance-id:\ ([^ ]+) ]] || exit 1
  printf '%s %s\n' "${BASH_REMATCH[1]}" "${*: -1}" >> "$STATE/checks"
  [[ ! -e $STATE/fail-ready || $* != *'/readyz'* ]] || [[ -s $STATE/fail-ready && $(cat "$STATE/fail-ready") != "${BASH_REMATCH[1]}" ]] || exit 22
elif [[ $* == *' -X POST '* ]]; then
  while (($#)); do
    case $1 in -X) url=$3 ;; -d) body=$2 ;; esac
    shift
  done
  # A create names its region; an update names its machine.
  if [[ $url == */machines ]]; then id=machine-$(jq -r .region <<<"$body"); else id=${url##*/}; fi
  printf '%s\n' "$body" > "$STATE/request-$id"
  echo "${url##*/apps/ts-pr-123/} $id" >> "$STATE/posts"
  echo "{\"id\":\"$id\",\"state\":\"created\"}"
else
  echo '[]'
fi
FAKE
cat > "$state/bin/gh" <<'FAKE'
#!/usr/bin/env bash
# gh api repos/<repo>/pulls/<n> --jq .state; a missing fixture is an API failure.
cat "$STATE/pr-${2##*/}"
FAKE
chmod +x "$state/bin/"*
export PATH="$state/bin:$PATH" FLY_API_TOKEN=fixture GITHUB_REPOSITORY=fixture/repo
printf '%s\n' TS_AUTH_MASTER_KEY TS_AUTH_AUDIT_SIGNING_KEY BUCKET_NAME TERRABASE_MASTER_KEY TERRABASE_PEER_SECRET > "$state/secrets"
both_volumes='[{"id":"vol-gru","name":"data","region":"gru"},{"id":"vol-sjc","name":"data","region":"sjc"}]'
echo "$both_volumes" > "$state/volumes"
"$here/preview.sh" app 123
test ! -e "$state/set"
test ! -e "$state/volume-creates"
# A preview from before the sjc replica keeps its gru volume and gains exactly one sjc volume.
echo '[{"id":"vol-gru","name":"data","region":"gru"}]' > "$state/volumes"
"$here/preview.sh" app 123
test "$(cat "$state/volume-creates")" = 'volumes create data -a ts-pr-123 -r sjc -s 1 --yes'
rm "$state/volume-creates"
echo '[]' > "$state/volumes"
"$here/preview.sh" app 123
test "$(cat "$state/volume-creates")" = $'volumes create data -a ts-pr-123 -r gru -s 1 --yes\nvolumes create data -a ts-pr-123 -r sjc -s 1 --yes'
rm "$state/volume-creates"
# Unknown capacity (another region, a duplicate, another name, a malformed listing) stops before anything is created,
# in app and in machine.
bad_inventories=('{}' '[{}]' '[{"id":"vol-fra","name":"data","region":"fra"}]'
  '[{"id":"vol-a","name":"data","region":"gru"},{"id":"vol-b","name":"data","region":"gru"}]'
  '[{"id":"vol-x","name":"other","region":"sjc"}]')
for bad in "${bad_inventories[@]}"; do
  printf '%s' "$bad" > "$state/volumes"
  if "$here/preview.sh" app 123 2>/dev/null; then echo "preview: accepted volumes $bad" >&2; exit 1; fi
  if "$here/preview.sh" machine 123 registry.fly.io/test:terrabase registry.fly.io/test:auth >/dev/null 2>&1; then
    echo "preview: machine accepted volumes $bad" >&2; exit 1
  fi
done
test ! -e "$state/volume-creates"
test ! -e "$state/posts"
# machine never creates a volume: a missing one is an error.
for region in gru sjc; do
  printf '[{"id":"vol-%s","name":"data","region":"%s"}]' "$region" "$region" > "$state/volumes"
  if "$here/preview.sh" machine 123 registry.fly.io/test:terrabase registry.fly.io/test:auth >/dev/null 2>&1; then
    echo 'preview: machine ran without both regional volumes' >&2; exit 1
  fi
  test ! -e "$state/posts"
done
echo "$both_volumes" > "$state/volumes"
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

gru_config='
  .config.guest == {cpu_kind: "shared", cpus: 2, memory_mb: 1024}
  and .config.mounts == [{volume: "vol-gru", path: "/data"}]
  and (.config.containers | map(.name)) == ["terrabase", "ts-auth"]
  and (.config.containers[] | select(.name == "ts-auth") |
    .secrets == (["TS_AUTH_MASTER_KEY", "TS_AUTH_AUDIT_SIGNING_KEY", "TERRABASE_PEER_SECRET"] | map({env_var: ., name: .}))
    and .env.TS_AUTH_PUBLIC_ORIGIN == "https://ts-pr-123.fly.dev")
  and (.config.services | map(.internal_port)) == [8090, 8080]
  and all(.config.services[]; .autostop == "stop" and .autostart and .min_machines_running == 0)'
sjc_config='
  .config.guest == {cpu_kind: "shared", cpus: 1, memory_mb: 512}
  and .config.mounts == [{volume: "vol-sjc", path: "/data"}]
  and (.config.containers | map(.name)) == ["terrabase"]
  and .config.services == [{protocol: "tcp", internal_port: 8080, autostop: "off", autostart: true,
    ports: [{port: 8443, handlers: ["tls", "http"]}]}]'
terrabase_secrets='
  .config.containers[] | select(.name == "terrabase") |
    (.secrets | map(.name) | sort) == (["AWS_ENDPOINT_URL_S3", "AWS_REGION", "BUCKET_NAME",
      "AWS_ACCESS_KEY_ID", "AWS_SECRET_ACCESS_KEY", "TERRABASE_MASTER_KEY", "TERRABASE_PEER_SECRET"] | sort)
    and .image == "registry.fly.io/test:terrabase"'
gru='{"id":"machine-gru","region":"gru"}' sjc='{"id":"machine-sjc","region":"sjc"}'
# create: none exist; upgrade: a preview from before the sjc replica; update: both exist. Each region ends with one machine.
for mode in create upgrade update; do
  case $mode in
    create) echo '[]' ;;
    upgrade) echo "[$gru]" ;;
    update) echo "[$sjc,$gru]" ;;
  esac > "$state/machines-ts-pr-123"
  rm -f "$state/posts" "$state/checks" "$state"/request-*
  test "$("$here/preview.sh" machine 123 registry.fly.io/test:terrabase registry.fly.io/test:auth | tail -n 1)" = https://ts-pr-123.fly.dev
  case $mode in
    create) test "$(cat "$state/posts")" = $'machines machine-sjc\nmachines machine-gru' ;;
    upgrade) test "$(cat "$state/posts")" = $'machines machine-sjc\nmachines/machine-gru machine-gru' ;;
    update) test "$(cat "$state/posts")" = $'machines/machine-sjc machine-sjc\nmachines/machine-gru machine-gru' ;;
  esac
  jq -e "($gru_config) and ($terrabase_secrets)" "$state/request-machine-gru" >/dev/null
  jq -e "($sjc_config) and ($terrabase_secrets)" "$state/request-machine-sjc" >/dev/null
  [[ $mode != create ]] || jq -e '.region == "gru"' "$state/request-machine-gru" >/dev/null
  [[ $mode != create ]] || jq -e '.region == "sjc"' "$state/request-machine-sjc" >/dev/null
  # Both TerraBase replicas, each pinned to its own machine, then ts-auth on gru: the original checks, in order.
  test "$(cat "$state/checks")" = $'machine-gru https://ts-pr-123.fly.dev:8443/readyz\nmachine-sjc https://ts-pr-123.fly.dev:8443/readyz\nmachine-gru https://ts-pr-123.fly.dev/readyz'
done
# Unknown machines (another region, a duplicate, a malformed or failed listing) stop before any machine changes.
for bad in '{}' '[{}]' '[{"id":"machine-fra","region":"fra"}]' "[$gru,{\"id\":\"machine-gru2\",\"region\":\"gru\"}]" "[{\"region\":\"sjc\"}]"; do
  printf '%s' "$bad" > "$state/machines-ts-pr-123"
  rm -f "$state/posts"
  if "$here/preview.sh" machine 123 registry.fly.io/test:terrabase registry.fly.io/test:auth >/dev/null 2>&1; then
    echo "preview: machine accepted machines $bad" >&2; exit 1
  fi
  test ! -e "$state/posts"
done
echo "[$sjc,$gru]" > "$state/machines-ts-pr-123"
touch "$state/fail-machines"
if "$here/preview.sh" machine 123 registry.fly.io/test:terrabase registry.fly.io/test:auth >/dev/null 2>&1; then
  echo 'preview: machine continued after its listing failed' >&2; exit 1
fi
test ! -e "$state/posts"
rm "$state/fail-machines"
touch "$state/fail-ready"
python3 - "$state/logs" <<'PY'
import json
import sys
component = "TerraScale.Authentication.Features.SigningKeys.SigningKeyRotation"
records = [
    {"msg": "Storage and signing key bootstrap failed; retrying", "component": component,
     "error": "System.Net.Http.HttpRequestException: HTTP 401 Bearer secret-canary", "stack": "status=503 AWS_SECRET_ACCESS_KEY=secret-canary"},
    {"msg": "Readiness check failed: {Cause}", "component": "TerraScale.Authentication.Features.Observability.ReadinessProbe", "Cause": "timed out"},
    {"msg": "Readiness check failed: {Cause}", "component": "TerraScale.Authentication.Features.Observability.ReadinessProbe", "Cause": "no signing keys: startup bootstrap pending (docs/auth/signing-keys.md)"},
    {"msg": "Readiness check failed: {Cause}", "component": "TerraScale.Authentication.Features.Observability.ReadinessProbe", "Cause": "secret-canary"},
    {"msg": "secret-canary", "component": component},
    {"msg": "Readiness check failed: {Cause}", "component": "secret-canary", "Cause": "secret-canary"},
    {"msg": {}, "component": component},
    {"msg": "Readiness check failed: {Cause}", "component": [], "Cause": "timed out"},
]
with open(sys.argv[1], "w") as output:
    for record in records:
        output.write(json.dumps({"message": json.dumps(record)}, indent=4) + "\n")
    output.write("secret-canary\nnull\n[]\n")
PY
readiness_status=0
"$here/preview.sh" machine 123 registry.fly.io/test:terrabase registry.fly.io/test:auth > "$state/failed" || readiness_status=$?
test "$readiness_status" = 22
test "$(grep -c '^{"event":' "$state/failed")" = 4
grep -qx '{"log_counts": {"entries": 10, "unparsed": 1, "app_records": 7, "matched": 4}}' "$state/failed"
grep -q 'System.Net.Http.HttpRequestException' "$state/failed"
grep -q '"401"' "$state/failed"
grep -q '"503"' "$state/failed"
grep -q '"cause": "timed out"' "$state/failed"
grep -q 'no signing keys: startup bootstrap pending (docs/auth/signing-keys.md)' "$state/failed"
if grep -q 'secret-canary' "$state/failed"; then echo 'preview: startup diagnostics exposed a secret' >&2; exit 1; fi
python3 - "$here/preview-log-summary.py" <<'PY'
import json
import subprocess
import sys

def summarize(data):
    output = subprocess.check_output([sys.executable, sys.argv[1]], input=data.encode()).decode()
    assert "secret-canary" not in output
    return [json.loads(line) for line in output.splitlines()]

empty = {"entries": 0, "unparsed": 0, "app_records": 0, "matched": 0}
assert summarize("") == [{"log_counts": empty}]
assert summarize("secret-canary") == [{"log_counts": dict(empty, unparsed=1)}]
assert summarize(json.dumps({"message": "secret-canary"}, indent=4)) == [
    {"log_counts": dict(empty, entries=1)}]
record = {"msg": "secret-canary", "component": "secret-canary"}
assert summarize(json.dumps(record)) == [{"log_counts": dict(empty, entries=1, app_records=1)}]
record = {"msg": "Readiness check failed: {Cause}",
          "component": "TerraScale.Authentication.Features.Observability.ReadinessProbe", "Cause": "timed out"}
compact = json.dumps({"message": json.dumps(record)})
pretty = json.dumps({"message": json.dumps(record)}, indent=4)
assert summarize(compact) == summarize(pretty) == summarize(json.dumps(record, indent=4))
assert summarize("secret-canary\n" + pretty)[-1] == {
    "log_counts": dict(empty, entries=1, app_records=1, matched=1, unparsed=1)}
assert summarize(pretty + "\n" + pretty[:-2])[-1] == {
    "log_counts": dict(empty, entries=1, app_records=1, matched=1, unparsed=1)}
assert summarize(pretty[:-2] + "\n" + pretty)[-1] == {
    "log_counts": dict(empty, entries=1, app_records=1, matched=1, unparsed=1)}
malformed = '{\n    "a": "x" "y"\n}\n'
assert summarize(malformed)[-1] == {"log_counts": dict(empty, unparsed=1)}
assert summarize(malformed + pretty)[-1] == {
    "log_counts": dict(empty, entries=1, app_records=1, matched=1, unparsed=1)}
assert summarize("[" * 10000) == [{"log_counts": dict(empty, unparsed=1)}]
assert summarize(json.dumps({"message": "[" * 10000})) == [{"log_counts": dict(empty, entries=1)}]
PY
grep -qx 'logs -a ts-pr-123 --machine machine-gru --no-tail --json' "$state/flyctl"
touch "$state/fail-logs"
readiness_status=0
"$here/preview.sh" machine 123 registry.fly.io/test:terrabase registry.fly.io/test:auth > "$state/no-logs" 2> "$state/log-error" || readiness_status=$?
test "$readiness_status" = 22
grep -q 'preview startup diagnostics unavailable' "$state/log-error"
rm "$state/fail-logs"
if grep -qx 'https://ts-pr-123.fly.dev' "$state/failed"; then
  echo 'preview: printed a URL after a failed readiness check' >&2
  exit 1
fi
# A failing sjc replica fails the preview with its own machine's diagnostics, before ts-auth is checked, and no URL.
echo machine-sjc > "$state/fail-ready"
: > "$state/flyctl"
rm "$state/checks"
readiness_status=0
"$here/preview.sh" machine 123 registry.fly.io/test:terrabase registry.fly.io/test:auth > "$state/failed-sjc" 2>/dev/null || readiness_status=$?
test "$readiness_status" = 22
test "$(grep -c '^logs ' "$state/flyctl")" = 1
grep -qx 'logs -a ts-pr-123 --machine machine-sjc --no-tail --json' "$state/flyctl"
test "$(cat "$state/checks")" = $'machine-gru https://ts-pr-123.fly.dev:8443/readyz\nmachine-sjc https://ts-pr-123.fly.dev:8443/readyz'
if grep -qx 'https://ts-pr-123.fly.dev' "$state/failed-sjc"; then
  echo 'preview: printed a URL after the sjc replica failed' >&2
  exit 1
fi
rm "$state/fail-ready"

fails() { if "$@"; then echo "preview: '$*' succeeded but must fail" >&2; exit 1; fi; }
destroyed() { test "$(cat "$state/destroy" 2>/dev/null)" = "$1"; rm -f "$state/destroy"; }
echo terrascale-previews-515 > "$state/org"
echo 200 > "$state/code"
# flyctl storage list's table (tablewriter, as flyctl v0.4.115 renders it); only exact preview names count.
bucket() { printf ' %s │ terrascale-previews-515\n' "$@" >> "$state/buckets"; }
printf ' NAME │ ORG\n' > "$state/buckets"
bucket terrascale-ts-pr-123 terrascale-ts-pr-0123 xterrascale-ts-pr-9 terrascale-ts-pr-9x ts-backups
# A bucket failure keeps the app, so the next run finds and retries both.
touch "$state/fail-storage"
fails "$here/preview.sh" destroy 123
destroyed ''
rm "$state/fail-storage"
"$here/preview.sh" destroy 123
destroyed $'storage destroy terrascale-ts-pr-123\napps destroy ts-pr-123'
# Idempotent: a gone bucket is skipped, and a gone app with a leftover bucket still loses the bucket.
"$here/preview.sh" destroy 123
destroyed 'apps destroy ts-pr-123'
echo 404 > "$state/code"
"$here/preview.sh" destroy 123
destroyed ''
bucket terrascale-ts-pr-123
"$here/preview.sh" destroy 123
destroyed 'storage destroy terrascale-ts-pr-123'
# A failed or reshaped bucket listing is an error, never "no bucket".
cp "$state/buckets" "$state/buckets.ok"
for table in '' 'NAME ORG' $' NAME │ ORG\n terrascale-ts-pr-7 │ terrascale' $' NAME │ ORG\n terrascale-ts-pr-7'; do
  printf '%s\n' "$table" > "$state/buckets"
  fails "$here/preview.sh" destroy 7
done
cp "$state/buckets.ok" "$state/buckets"
touch "$state/fail-list"
fails "$here/preview.sh" destroy 7
rm "$state/fail-list"
destroyed ''
# An API error or an app outside the preview org never deletes anything.
echo 500 > "$state/code"
fails "$here/preview.sh" destroy 123
echo 200 > "$state/code"
echo terrascale > "$state/org"
fails "$here/preview.sh" destroy 123
destroyed ''
echo terrascale-previews-515 > "$state/org"
# Only a plain PR number names an app: production names, empty and malformed arguments never reach Fly.
: > "$state/flyctl"
for pr in '' 0 007 12a -1 '1 2' '../ts-terrabase' terrabase 1234567890; do
  fails "$here/preview.sh" destroy "$pr" 2>/dev/null
  fails "$here/preview.sh" app "$pr" 2>/dev/null
done
fails "$here/preview.sh" destroy 2>/dev/null
test ! -s "$state/flyctl"

# Sweep: closed PRs go regardless of age, machines or app (6 has only a leftover bucket); open PRs go after 24 hours,
# dated by their oldest machine, else their oldest volume (4 old, 7 young); 9 has neither and stays until close.
rm "$state/machines-ts-pr-123"
ago() { echo "[{\"created_at\":\"$(date -u -d "$1 hours ago" +%FT%TZ)\"}]"; }
printf '%s\n' '[{"Name":"ts-pr-1"},{"Name":"ts-pr-2"},{"Name":"ts-pr-3"},{"Name":"ts-pr-4"},{"Name":"ts-pr-05"},{"Name":"ts-pr-7"},{"Name":"ts-pr-9"},{"Name":"ts-terrabase"}]' > "$state/apps"
bucket terrascale-ts-pr-1 terrascale-ts-pr-6 terrascale-ts-pr-8
echo closed > "$state/pr-1"; cp "$state/pr-1" "$state/pr-6"
for pr in 2 3 4 7 8 9; do echo open > "$state/pr-$pr"; done
ago 1 > "$state/machines-ts-pr-2"
ago 25 > "$state/machines-ts-pr-3"
ago 25 > "$state/volumes-ts-pr-4"
ago 1 > "$state/volumes-ts-pr-7"
echo '[]' > "$state/volumes-ts-pr-9"
test "$("$here/preview.sh" expired)" = '[1,3,4,6]'
# Offsets: PR 2's oldest machine is 25 hours old, but its string sorts after the young one; epochs decide.
echo "[{\"created_at\":\"$(date -u -d '15 hours ago' +%FT%T)-14:00\"},{\"created_at\":\"$(date -u -d '11 hours ago' +%FT%T)+14:00\"}]" > "$state/machines-ts-pr-2"
test "$("$here/preview.sh" expired)" = '[1,2,3,4,6]'
ago 1 > "$state/machines-ts-pr-2"
# Empty output, another shape, or a missing, null or unreadable date in a machine or volume list is an error, not
# "no machines"; decidable previews are still listed. A valid date never hides an invalid one, nor a newline-split blank.
for bad in '' '{}' '[{}]' '[{"created_at":null}]' '[{"created_at":"not-a-date"}]' '[{"created_at":"2026-10-13T18:36:33Z"},{"created_at":"not-a-date"}]' '[{"created_at":"2026-10-13T18:36:33Z\n"}]'; do
  for file in machines-ts-pr-2 volumes-ts-pr-7; do
    cp "$state/$file" "$state/$file.ok"
    printf '%s' "$bad" > "$state/$file"
    out=$("$here/preview.sh" expired 2>/dev/null) && exit 1
    test "$out" = '[1,3,4,6]'
    mv "$state/$file.ok" "$state/$file"
  done
done
# The same holds for the app list, but [] is a valid empty list (only buckets remain).
cp "$state/apps" "$state/apps.ok"
for bad in '' '{}' '{"x":{"Name":"ts-pr-1"}}' '[{}]' '[{"Name":null}]'; do
  printf '%s' "$bad" > "$state/apps"
  out=$("$here/preview.sh" expired 2>/dev/null) && exit 1
  test -z "$out"
done
echo '[]' > "$state/apps"
test "$("$here/preview.sh" expired)" = '[1,6]'
mv "$state/apps.ok" "$state/apps"
# A failed PR lookup is an error, not "keep".
rm "$state/pr-4"
out=$("$here/preview.sh" expired 2>/dev/null) && exit 1
test "$out" = '[1,3,6]'
# A failed Fly app or bucket listing lists nothing.
for failure in fail-apps fail-list; do
  touch "$state/$failure"
  out=$("$here/preview.sh" expired 2>/dev/null) && exit 1
  test -z "$out"
  rm "$state/$failure"
done
echo 'preview: all checks passed'
