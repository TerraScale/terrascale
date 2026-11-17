#!/usr/bin/env bash
set -euo pipefail
service=${1:?service} image=${2:?image}
case "$service" in
  terrabase|ts-auth) package=$service ;;
  ts-dashboard) package=terrascale-dashboard ;;
  *) echo "Unknown application service" >&2; exit 2 ;;
esac
[[ $image =~ ^ghcr\.io/terrascale/${package}@sha256:[0-9a-f]{64}$ ]] || { echo "Expected the existing ${package} package pinned by sha256" >&2; exit 2; }
work=${RUNNER_TEMP:?}/ts-orch
summary=${GITHUB_STEP_SUMMARY:-/dev/null}
docker buildx imagetools create --prefer-index=false --tag "ghcr.io/terrascale/${package}:release" "$image"
printf 'Selected %s %s for verified host pulls\n' "$service" "$image" | tee -a "$summary"
bash "$(dirname "$0")/fetch-ts-orch.sh" "$work"
"$work/terrascale-orchestrator-linux-amd64" release wait --service "$service" --image "$image" \
  --regions "$work/regions.json" | tee -a "$summary"
