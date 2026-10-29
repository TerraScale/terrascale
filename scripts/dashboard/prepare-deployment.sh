#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."
[ -z "$(git status --porcelain)" ] || { echo 'Commit the dashboard changes before qualifying a deployment.' >&2; exit 1; }
revision=$(git rev-parse HEAD)
export GITHUB_SHA="$revision"
workspace=$(mktemp -d /tmp/dashboard-qualified.XXXXXX)
trap 'rm -rf "$workspace"' EXIT
export RUNNER_TEMP="$workspace"
python3 scripts/dashboard/verify-sdk.py
python3 scripts/dashboard/verify-e2e.py
python3 scripts/dashboard/report-slow-failure.py --self-check
python3 scripts/dashboard/ci/tree-proof.py --self-check
bash scripts/dashboard/ci/check-release-result.sh
node scripts/dashboard/check-document-changes.mjs
make -f scripts/dashboard/Makefile check
dotnet publish src/TerraScale.Dashboard --configuration Release --no-build --no-restore \
  --output "$workspace/publish" -p:ContinuousIntegrationBuild=true -p:SourceRevisionId="$revision"
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
          'archive':archive.name,'sha256':hashlib.sha256(archive.read_bytes()).hexdigest(),'files':files}
(root/'dashboard-qualification.json').write_text(json.dumps(manifest,indent=2)+'\n')
PY
gh release create "dashboard/qualified-$revision" --repo TerraScale/terrascale --target "$revision" \
  --prerelease --title "Dashboard qualified $revision" \
  --notes "Dev-host validation and source-bound portable application for $revision." \
  "$workspace/dashboard-publish.tar.gz" "$workspace/dashboard-qualification.json"
gh api --method POST "repos/TerraScale/terrascale/statuses/$revision" \
  -f state=success -f context=dashboard-release/dashboard \
  -f description='Dashboard release checks and exact application bytes passed on a dev host' \
  -f "target_url=https://github.com/TerraScale/terrascale/releases/tag/dashboard/qualified-$revision" --silent
