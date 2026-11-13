#!/usr/bin/env bash
set -euo pipefail
thing=${1:?thing}
case "$thing" in
  auth) package=ts-auth ;;
  dashboard) package=terrascale-dashboard ;;
  *) exit 2 ;;
esac
image=ghcr.io/terrascale/$package
export TS_AUTH_REVISION="$REVISION" TsDotnetRoot="$PWD/sdk/dotnet" TS_DOTNET_DIR=sdk/dotnet
if docker buildx imagetools inspect "$image:$REVISION" --raw > "$RUNNER_TEMP/manifest.json" 2> "$RUNNER_TEMP/manifest-error"; then
  echo "Reusing $image:$REVISION"
else
  if ! grep -Eq 'manifest unknown|not found' "$RUNNER_TEMP/manifest-error"; then
    cat "$RUNNER_TEMP/manifest-error" >&2
    exit 1
  fi
  if [ "$thing" = auth ]; then
    bash scripts/ci/image-payloads.sh publish auth
    bash scripts/ci/image-payloads.sh runtime-dockerfile auth > "$RUNNER_TEMP/image-payloads/Dockerfile"
    context=$RUNNER_TEMP/image-payloads
    dockerfile=$context/Dockerfile
  else
    dotnet restore scripts/dashboard/TerraScale.Dashboard.slnx --locked-mode
    npm ci --prefix src/TerraScale.Dashboard/web
    dotnet build scripts/dashboard/TerraScale.Dashboard.slnx --configuration Release --no-restore
    Configuration=Release make dashboard-lint
    context=$RUNNER_TEMP/dashboard-publish
    dotnet publish src/TerraScale.Dashboard --configuration Release --no-restore \
      --output "$context" -p:ContinuousIntegrationBuild=true -p:SourceRevisionId="$REVISION"
    dockerfile=scripts/dashboard/ci/Dockerfile.runtime
  fi
  bash scripts/ci/release-only.sh
  docker buildx build "$context" --file "$dockerfile" --platform linux/arm64 \
    --label "org.opencontainers.image.source=https://github.com/$GITHUB_REPOSITORY" \
    --label "org.opencontainers.image.revision=$REVISION" --tag "$image:$REVISION" --push
  docker buildx imagetools inspect "$image:$REVISION" --raw > "$RUNNER_TEMP/manifest.json"
fi
arm64=$(jq -er '.manifests[] | select(.platform.os == "linux" and .platform.architecture == "arm64") | .digest' "$RUNNER_TEMP/manifest.json")
[[ "$arm64" =~ ^sha256:[0-9a-f]{64}$ ]]
docker buildx imagetools inspect "$image@$arm64" --format '{{json .Image}}' | \
  jq -e --arg revision "$REVISION" '.architecture == "arm64" and .os == "linux" and .config.Labels["org.opencontainers.image.revision"] == $revision'
docker buildx imagetools create --tag "$image:main" "$image@$arm64"
echo "IMAGE=$image@$arm64" >> "$GITHUB_ENV"
