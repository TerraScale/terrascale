#!/usr/bin/env bash
set -euo pipefail
service=${1:?service} image=${2:?image}
[[ $image =~ @sha256:[0-9a-f]{64}$ ]] || { echo 'Expected <service> <image@sha256:...>' >&2; exit 2; }
work=${RUNNER_TEMP:?}/ts-orch
log=$RUNNER_TEMP/release.log
summary=${GITHUB_STEP_SUMMARY:-/dev/null}
mkdir -p "$work"
tag=$(gh release list --repo TerraScale/ts-orchestrator --limit 30 --json tagName,createdAt \
  --jq '[.[] | select(.tagName | startswith("ci-"))] | sort_by(.createdAt) | last | .tagName // ""')
test -n "$tag"
gh release download "$tag" --repo TerraScale/ts-orchestrator --dir "$work" --pattern terrascale-orchestrator-linux-amd64 --clobber
curl -fsSL --max-time 30 https://raw.githubusercontent.com/TerraScale/ts-orchestrator/main/deploy/m1/regions.json -o "$work/regions.json"
chmod +x "$work/terrascale-orchestrator-linux-amd64"
regions=$(jq -r '[.[].region] | join(",")' "$work/regions.json")
echo "Releasing ${service} ${image} to ${regions} with ts-orch ${tag}"

started=$SECONDS
set +e
"$work/terrascale-orchestrator-linux-amd64" release app --service "$service" --image "$image" \
  --regions "$work/regions.json" 2>&1 | tee "$log"
status=$?
set -e
elapsed=$((SECONDS - started))
{
  echo "### Release ${service} (${elapsed}s, exit ${status})"
  echo '```text'
  cat "$log"
  echo '```'
} >> "$summary"
(( status == 0 )) && exit 0

{
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
