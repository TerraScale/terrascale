#!/usr/bin/env bash
set -euo pipefail
service=${1:?service} image=${2:?image}
case "$service" in
  ts-auth) package=ts-auth ;;
  ts-dashboard) package=terrascale-dashboard ;;
  *) exit 2 ;;
esac
[[ $image =~ ^ghcr\.io/terrascale/${package}@sha256:[0-9a-f]{64}$ ]]
work=${RUNNER_TEMP:?}/ts-orch
bash "$(dirname "$0")/fetch-ts-orch.sh" "$work"
"$work/terrascale-orchestrator-linux-amd64" release wait --service "$service" --image "$package@${image##*@}" \
  --regions "$work/regions.json" | tee -a "${GITHUB_STEP_SUMMARY:-/dev/null}"
