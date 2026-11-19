#!/usr/bin/env bash
set -euo pipefail
service=${1:?service} image=${2:?image}
case "$service" in
  terrabase|ts-auth) package=$service ;;
  ts-dashboard) package=terrascale-dashboard ;;
  *) echo "Unknown application service" >&2; exit 2 ;;
esac
[[ $image =~ ^ghcr\.io/terrascale/${package}@sha256:[0-9a-f]{64}$ ]] || { echo "Expected the existing ${package} package pinned by sha256" >&2; exit 2; }
if [[ $service == terrabase && ${GITHUB_EVENT_NAME:?} != workflow_dispatch ]]; then
  echo 'TerraBase release selection requires workflow_dispatch' >&2
  exit 2
fi
work=${RUNNER_TEMP:?}/ts-orch
summary=${GITHUB_STEP_SUMMARY:-/dev/null}
jq -cn --arg service "$service" --arg revision "${GITHUB_SHA:?}" \
  '{service:$service, revision:$revision}' > "$RUNNER_TEMP/release-selection.json"
digest=${image##*@}
oras manifest fetch "$image" --output "$RUNNER_TEMP/release-manifest.json"
[[ sha256:$(sha256sum "$RUNNER_TEMP/release-manifest.json" | cut -d ' ' -f1) == "$digest" ]]
cosign attest-blob --yes --new-bundle-format --bundle "$RUNNER_TEMP/release-selection.sigstore.json" \
  --type https://terrascale.tech/attestations/release-selection/v1 \
  --predicate "$RUNNER_TEMP/release-selection.json" "$RUNNER_TEMP/release-manifest.json"
bash "$(dirname "$0")/attach-image-bundles.sh" "$image" "$RUNNER_TEMP/release-selection.sigstore.json"
repositories=$(bash "$(dirname "$0")/ocir-repositories.sh" "$package")
if [[ $service == terrabase ]]; then
  while IFS= read -r repository; do
    bash "$(dirname "$0")/attach-image-bundles.sh" "$repository@$digest" \
      "$RUNNER_TEMP/release-selection.sigstore.json"
  done <<< "$repositories"
else
  bash "$(dirname "$0")/app-image-regions.sh" attest "$image" "$RUNNER_TEMP/release-selection.sigstore.json"
fi
fleet=$package@$digest
select_release() {
  local repository=$1 observed
  oras tag "$repository@$digest" release
  observed=$(oras resolve "$repository:release")
  if [[ $observed != "$digest" ]]; then
    printf '::error::Release tag mismatch for %s: expected %s, observed %s\n' \
      "$repository:release" "$digest" "$observed" | tee -a "$summary" >&2
    return 1
  fi
  echo "SELECTED $repository@$digest: release tag verified" | tee -a "$summary"
}
select_bounded() {
  local repository=$1 status
  if timeout --kill-after=5 120 bash -euo pipefail -c "select_release \"\$1\"" _ "$repository"; then
    return 0
  else
    status=$?
    printf '::error::Release selection failed or exceeded its two-minute deadline for %s (exit %s)\n' "$repository" "$status" | tee -a "$summary" >&2
    return "$status"
  fi
}
bash "$(dirname "$0")/fetch-ts-orch.sh" "$work"
export -f select_release
export digest summary
pids=()
while IFS= read -r repository; do
  select_bounded "$repository" &
  pids+=("$!")
done <<< "$repositories"
result=0
for pid in "${pids[@]}"; do
  wait "$pid" || result=1
done
[[ $result == 0 ]] || exit "$result"
docker buildx imagetools create --prefer-index=false --tag "ghcr.io/terrascale/${package}:release" "$image"
printf 'Selected %s %s for verified host pulls\n' "$service" "$fleet" | tee -a "$summary"
