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
  local repository=$1
  oras tag "$repository@$digest" release
  [[ $(oras resolve "$repository:release") == "$digest" ]]
  echo "SELECTED $repository@$digest: release tag verified" | tee -a "$summary"
}
bash "$(dirname "$0")/fetch-ts-orch.sh" "$work"
while IFS= read -r repository; do
  [[ $repository == */terrascale-stg/* ]] || continue
  tags=$(oras repo tags "$repository" --format json)
  staging_release=$(jq -r '.tags | index("release") != null' <<< "$tags")
  if [[ $staging_release == true ]]; then
    select_release "$repository"
    jq -cn '[{region:"stg"}]' > "$RUNNER_TEMP/release-staging-region.json"
    bash "$(dirname "$0")/wait-app-rollout.sh" "$service" "$image" "$RUNNER_TEMP/release-staging-region.json"
  else
    echo "STAGING $repository: no existing :release tag; staging rollout is not configured" | tee -a "$summary"
  fi
done <<< "$repositories"
pids=()
while IFS= read -r repository; do
  [[ $repository != */terrascale-stg/* ]] || continue
  select_release "$repository" &
  pids+=("$!")
done <<< "$repositories"
result=0
for pid in "${pids[@]}"; do
  wait "$pid" || result=1
done
[[ $result == 0 ]] || exit "$result"
docker buildx imagetools create --prefer-index=false --tag "ghcr.io/terrascale/${package}:release" "$image"
printf 'Selected %s %s for verified host pulls\n' "$service" "$fleet" | tee -a "$summary"
