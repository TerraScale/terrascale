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
while IFS= read -r repository; do
  bash "$(dirname "$0")/attach-image-bundles.sh" "$repository@$digest" \
    "$RUNNER_TEMP/release-selection.sigstore.json"
done <<< "$repositories"
while IFS= read -r repository; do
  oras tag "$repository@$digest" release
  [[ $(oras resolve "$repository:release") == "$digest" ]]
  echo "SELECTED $repository@$digest: release tag verified" | tee -a "$summary"
done <<< "$repositories"
docker buildx imagetools create --prefer-index=false --tag "ghcr.io/terrascale/${package}:release" "$image"
image=$package@$digest
printf 'Selected %s %s for verified host pulls\n' "$service" "$image" | tee -a "$summary"
bash "$(dirname "$0")/fetch-ts-orch.sh" "$work"
"$work/terrascale-orchestrator-linux-amd64" release wait --service "$service" --image "$image" \
  --regions "$work/regions.json" | tee -a "$summary"
