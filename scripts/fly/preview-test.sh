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
  "volumes list") if [[ -e $STATE/volumes-$4 ]]; then cat "$STATE/volumes-$4"; else echo '[{"id":"vol-test"}]'; fi ;;
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
  cat "$STATE/machines-${BASH_REMATCH[1]}"
elif [[ $* == *'/healthz'* || $* == *'/readyz'* ]]; then
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
cat > "$state/bin/gh" <<'FAKE'
#!/usr/bin/env bash
# gh api repos/<repo>/pulls/<n> --jq .state; a missing fixture is an API failure.
cat "$STATE/pr-${2##*/}"
FAKE
chmod +x "$state/bin/"*
export PATH="$state/bin:$PATH" FLY_API_TOKEN=fixture GITHUB_REPOSITORY=fixture/repo
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
test "$(cat "$state/checks")" = $'https://ts-pr-123.fly.dev:8443/readyz\nhttps://ts-pr-123.fly.dev/readyz\nhttps://ts-pr-123.fly.dev:8443/readyz\nhttps://ts-pr-123.fly.dev/readyz'
touch "$state/fail-ready"
if "$here/preview.sh" machine 123 registry.fly.io/test:terrabase registry.fly.io/test:auth > "$state/failed"; then
  echo 'preview: published a URL for an unready preview' >&2
  exit 1
fi
if grep -qx 'https://ts-pr-123.fly.dev' "$state/failed"; then
  echo 'preview: printed a URL after a failed readiness check' >&2
  exit 1
fi

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
rm "$state/existing"
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
