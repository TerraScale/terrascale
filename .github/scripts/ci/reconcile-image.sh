#!/usr/bin/env bash
set -euo pipefail
thing=${1:?thing}
case "$thing" in
  auth) package=ts-auth ;;
  platform) package=ts-platform ;;
  dashboard) package=terrascale-dashboard ;;
  *) exit 2 ;;
esac
image=ghcr.io/terrascale/$package
export TS_AUTH_REVISION="$REVISION"
if [[ ${IMAGE_CACHED:?} == false ]]; then
  if [[ "$thing" == auth || "$thing" == platform ]]; then
    bash .github/scripts/ci/image-payloads.sh publish "$thing"
    bash .github/scripts/ci/image-payloads.sh runtime-dockerfile "$thing" > "$RUNNER_TEMP/image-payloads/Dockerfile"
    context=$RUNNER_TEMP/image-payloads
    dockerfile=$context/Dockerfile
  else
    dotnet restore src/TerraScale.Dashboard/TerraScale.Dashboard.csproj --locked-mode
    npm ci --prefix src/TerraScale.Dashboard/web
    context=$RUNNER_TEMP/dashboard-publish
    dotnet publish src/TerraScale.Dashboard --configuration Release --no-restore --runtime linux-arm64 --self-contained false \
      --output "$context" -p:ContinuousIntegrationBuild=true -p:SourceRevisionId="$REVISION"
    make dashboard-lint CONFIGURATION=Release
    dockerfile=$RUNNER_TEMP/Dockerfile.dashboard
    bash .github/scripts/ci/image-payloads.sh runtime-dockerfile dashboard > "$dockerfile"
  fi
  bash .github/scripts/ci/release-only.sh
  docker buildx build "$context" --file "$dockerfile" --platform linux/arm64 \
    --label "org.opencontainers.image.source=https://github.com/$GITHUB_REPOSITORY" \
    --label "org.opencontainers.image.revision=$REVISION" --tag "$image:$REVISION" --push
  docker buildx imagetools inspect "$image:$REVISION" --raw > "$RUNNER_TEMP/manifest.json"
fi
arm64=$(jq -r 'if .manifests then (.manifests[] | select(.platform.os == "linux" and .platform.architecture == "arm64") | .digest) else empty end' "$RUNNER_TEMP/manifest.json")
if [[ -z "$arm64" ]]; then
  arm64=$(docker buildx imagetools inspect "$image:$REVISION" --format '{{json .Manifest.Digest}}' | jq -er '.')
fi
[[ "$arm64" =~ ^sha256:[0-9a-f]{64}$ ]]
docker buildx imagetools inspect "$image@$arm64" --format '{{json .Image}}' | \
  jq -e --arg revision "$REVISION" '.architecture == "arm64" and .os == "linux" and .config.Labels["org.opencontainers.image.revision"] == $revision'
docker buildx imagetools create --tag "$image:main" "$image@$arm64"
echo "IMAGE=$image@$arm64" >> "$GITHUB_ENV"
