#!/usr/bin/env bash
# Release one pinned image to every region through ts-orch: bounded, with a heartbeat while ts-orch works and
# diagnostics when it fails. ts-orch prints its result only when the release ends.
# usage: release-app.sh <service> <image@sha256:...> <limit-seconds>; output also in $RUNNER_TEMP/release.log.
set -euo pipefail
service=${1:?service} image=${2:?image} limit=${3:?limit seconds}
[[ $image =~ @sha256:[0-9a-f]{64}$ && $limit =~ ^[1-9][0-9]*$ ]] || { echo 'Expected <service> <image@sha256:...> <seconds>' >&2; exit 2; }
work=${RUNNER_TEMP:?}/ts-orch
log=$RUNNER_TEMP/release.log
summary=${GITHUB_STEP_SUMMARY:-/dev/null}
mkdir -p "$work"
: > "$log"
tag=$(gh release list --repo TerraScale/ts-orchestrator --limit 30 --json tagName,createdAt \
  --jq '[.[] | select(.tagName | startswith("ci-"))] | sort_by(.createdAt) | last | .tagName // ""')
test -n "$tag"
gh release download "$tag" --repo TerraScale/ts-orchestrator --dir "$work" --pattern terrascale-orchestrator-linux-amd64 --clobber
curl -fsSL --max-time 30 https://raw.githubusercontent.com/TerraScale/ts-orchestrator/main/deploy/m1/regions.json -o "$work/regions.json"
chmod +x "$work/terrascale-orchestrator-linux-amd64"
regions=$(jq -r '[.[].region] | join(",")' "$work/regions.json")
echo "Releasing ${service} ${image} to ${regions} with ts-orch ${tag} (limit ${limit}s)"

started=$SECONDS
timeout --kill-after=15 "$limit" "$work/terrascale-orchestrator-linux-amd64" release app --service "$service" \
  --image "$image" --regions "$work/regions.json" > "$log" 2>&1 &
pid=$!
shown=0 heard=$SECONDS
while kill -0 "$pid" 2> /dev/null; do
  sleep 2
  tail -c +"$((shown + 1))" "$log" > "$work/new"
  if [[ -s $work/new ]]; then
    cat "$work/new"
    shown=$((shown + $(stat -c %s "$work/new"))) heard=$SECONDS
  elif (( SECONDS - heard >= 20 )); then
    heard=$SECONDS
    echo "release ${service}: $((SECONDS - started))s of ${limit}s, waiting for ts-orch in ${regions}"
  fi
done
set +e
wait "$pid"
status=$?
set -e
tail -c +"$((shown + 1))" "$log"
elapsed=$((SECONDS - started))
{
  echo "### Release ${service} (${elapsed}s, exit ${status})"
  echo '```text'
  cat "$log"
  echo '```'
} >> "$summary"
(( status == 0 )) && exit 0

{
  (( status == 124 || status == 137 )) && echo "ts-orch did not finish within ${limit}s and was stopped."
  echo "ts-orch ${tag}; image ${image}"
  for row in $(jq -r '.[] | "\(.region)=\(.nats)"' "$work/regions.json"); do
    endpoint=${row#*=tls://}
    if timeout 5 bash -c ": > /dev/tcp/${endpoint%:*}/${endpoint##*:}" 2> /dev/null; then
      echo "${row%%=*}: NATS ${endpoint} reachable"
    else
      echo "${row%%=*}: NATS ${endpoint} unreachable"
    fi
  done
  for url in https://api.terrascale.tech/healthz https://auth.terrascale.tech/healthz https://dash.terrascale.tech/readyz; do
    echo "${url}: $(curl -s -o /dev/null --max-time 10 -w '%{http_code} revision=%header{x-terrascale-revision}' "$url" || echo unreachable)"
  done
} | tee -a "$summary"
echo "::error::Release of ${service} failed after ${elapsed}s (exit ${status}); see the job summary."
exit "$status"
