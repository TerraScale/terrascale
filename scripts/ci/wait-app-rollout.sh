#!/usr/bin/env bash
set -euo pipefail
service=${1:?service} image=${2:?image}
case "$service" in
  terrabase|ts-auth) package=$service ;;
  ts-dashboard) package=terrascale-dashboard ;;
  *) exit 2 ;;
esac
[[ $image =~ ^ghcr\.io/terrascale/${package}@sha256:[0-9a-f]{64}$ ]]
work=${RUNNER_TEMP:?}/ts-orch
regions=$work/regions.json
summary=${GITHUB_STEP_SUMMARY:-/dev/null}
log=$RUNNER_TEMP/release-wait.log
result=0
"$work/terrascale-orchestrator-linux-amd64" release wait --service "$service" --image "$package@${image##*@}" \
  --regions "$regions" 2>&1 | tee "$log" || result=$?
{
  printf '\n### %s rollout\n\nWait exit status: %s\n\n```text\nRegion\tSelected\tFleet-ready digest\tHealth\tReady\tObserved at\tReason\n' \
    "$service" "$result"
  jq -Rrs --arg service "$service" --arg selected "$package@${image##*@}" --slurpfile regions "$regions" '
  split("\n") | map(select(contains("Region release observation"))
    | capture("region=(?<region>[^ ]+) observed_at=(?<observed_at>[^ ]+) state=(?<state>.*)$")
    | .state |= (fromjson | fromjson)) as $observations
  | $regions[0][] | .region as $region
  | ($observations | map(select(.region == $region)) | last) as $observed
  | [$region, $selected, ($observed.state.running[$service] // "unavailable"),
      ($observed.state.health // "unavailable"), (($observed.state.ready // false) | tostring),
      ($observed.observed_at // "unavailable"), ($observed.state.fetch_error // $observed.state.error // "")]
  | @tsv
  ' "$log"
  printf '```\n'
} >> "$summary"
exit "$result"
