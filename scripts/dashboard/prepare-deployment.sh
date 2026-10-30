#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."
[ -z "$(git status --porcelain)" ] || { echo 'Commit the dashboard changes before qualifying a deployment.' >&2; exit 1; }
revision=$(git rev-parse HEAD)
export GITHUB_SHA="$revision"
export ContinuousIntegrationBuild=true SourceRevisionId="$revision"
workspace=$(mktemp -d /tmp/dashboard-qualified.XXXXXX)
image=''
engine=$(command -v podman || command -v docker) || { echo 'A container engine is required for runtime qualification.' >&2; exit 1; }
cleanup() {
    if [ -n "$image" ]; then "$engine" image rm --force "$image" >/dev/null 2>&1 || true; fi
    rm -rf "$workspace"
}
trap cleanup EXIT
export RUNNER_TEMP="$workspace"
# The runtime audit uses a Docker-compatible CLI; dev hosts provide Podman.
mkdir -p "$workspace/bin"
ln -s "$engine" "$workspace/bin/docker"
export PATH="$workspace/bin:$PATH"
image="localhost/terrascale-dashboard-qualified:$revision-$(basename "$workspace")"
{
    python3 scripts/dashboard/verify-sdk.py
    python3 scripts/dashboard/verify-e2e.py
    python3 scripts/dashboard/report-slow-failure.py --self-check
    python3 scripts/dashboard/ci/tree-proof.py --self-check
    bash scripts/dashboard/ci/check-release-result.sh
    node scripts/dashboard/check-document-changes.mjs
    # Preserve the checksum-qualified scanner and all dependency/runtime audits from the source CI.
    scanner_cache="$HOME/.cache/osv-scanner-bin/osv-scanner"
    if [ ! -f "$scanner_cache" ]; then
        mkdir -p "$workspace/scanner" "$(dirname "$scanner_cache")"
        GOBIN="$workspace/scanner" CGO_ENABLED=0 GOOS=linux GOARCH=amd64 GOAMD64=v1 GOFLAGS='' GOTOOLCHAIN=go1.27.3 \
            go install -trimpath github.com/google/osv-scanner/v2/cmd/osv-scanner@v2.7.0
        echo "1a7e2584c17db2e523c13f41ae4a4d1d13ee221dbc4abc19a6bba00aac3f40f2  $workspace/scanner/osv-scanner" | sha256sum --check
        cp "$workspace/scanner/osv-scanner" "$scanner_cache.$(basename "$workspace")"
        mv -n "$scanner_cache.$(basename "$workspace")" "$scanner_cache"
        rm -f "$scanner_cache.$(basename "$workspace")"
    fi
    # Copy then verify so concurrent qualification cannot change the checked executable.
    cp "$scanner_cache" "$workspace/osv-scanner"
    echo "1a7e2584c17db2e523c13f41ae4a4d1d13ee221dbc4abc19a6bba00aac3f40f2  $workspace/osv-scanner" | sha256sum --check
    chmod +x "$workspace/osv-scanner"
    python3 scripts/dashboard/ci/audit-dependencies.py "$workspace/osv-scanner"
    python3 scripts/dashboard/ci/audit-runtime-images.py "$workspace/osv-scanner"
    make -f scripts/dashboard/Makefile check
    test "$(grep '^FROM mcr.microsoft.com/dotnet/aspnet:' scripts/dashboard/Dockerfile)" = \
        "$(grep '^FROM mcr.microsoft.com/dotnet/aspnet:' scripts/dashboard/ci/Dockerfile.runtime)"
    dotnet publish src/TerraScale.Dashboard --configuration Release --no-build --no-restore \
        --output "$workspace/publish" -p:ContinuousIntegrationBuild=true -p:SourceRevisionId="$revision"
    # Private dev-host umasks must not make public application files unreadable to the image's non-root user.
    chmod -R a+rX "$workspace/publish"
    "$engine" build --file scripts/dashboard/ci/Dockerfile.runtime --tag "$image" "$workspace/publish"
    bash scripts/dashboard/ci/smoke.sh "$engine" "$image"
} 2>&1 | tee "$workspace/dashboard-qualification.log"
python3 - "$workspace" "$revision" <<'PY'
import hashlib, json, pathlib, sys, tarfile
root=pathlib.Path(sys.argv[1]); publish=root/'publish'; files={}
archive=root/'dashboard-publish.tar.gz'
with tarfile.open(archive,'w:gz') as package:
    for source in sorted(publish.rglob('*')):
        assert not source.is_symlink(), 'Release application must contain only regular files'
        if source.is_file():
            name=source.relative_to(publish).as_posix()
            files[name]=hashlib.sha256(source.read_bytes()).hexdigest()
            package.add(source,arcname=name,recursive=False)
assert 'TerraScale.Dashboard.dll' in files
manifest={'schema':'terrascale.dashboard-qualification/v1','revision':sys.argv[2],
          'archive':archive.name,'sha256':hashlib.sha256(archive.read_bytes()).hexdigest(),'files':files,
          'qualificationLogSha256':hashlib.sha256((root/'dashboard-qualification.log').read_bytes()).hexdigest()}
(root/'dashboard-qualification.json').write_text(json.dumps(manifest,indent=2)+'\n')
PY
gh release create "dashboard/qualified-$revision" --repo TerraScale/terrascale --target "$revision" \
  --prerelease --title "Dashboard qualified $revision" \
  --notes "Dev-host validation and source-bound portable application for $revision." \
  "$workspace/dashboard-publish.tar.gz" "$workspace/dashboard-qualification.json" "$workspace/dashboard-qualification.log"
gh api --method POST "repos/TerraScale/terrascale/statuses/$revision" \
  -f state=success -f context=dashboard-release/dashboard \
  -f description='Dashboard release checks and exact application bytes passed on a dev host' \
  -f "target_url=https://github.com/TerraScale/terrascale/releases/tag/dashboard/qualified-$revision" --silent
