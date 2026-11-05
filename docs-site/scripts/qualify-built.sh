#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
if [[ -n $(git status --porcelain) ]]; then
  echo 'Commit all source changes before qualifying a deployment.' >&2
  exit 1
fi
revision=$(git rev-parse HEAD)
python3 - "$revision" <<'CHECK'
import json, pathlib, sys
info = json.loads(pathlib.Path('docs/.vitepress/dist/build-info.json').read_text())
assert info['commit'] == sys.argv[1], 'The site must be built from this exact revision'
CHECK
output=$(mktemp -d /tmp/terrascale-docs-qualified.XXXXXX)
trap 'rm -rf "$output"' EXIT
while IFS= read -r -d '' lock; do
  npm audit --prefix "$(dirname "$lock")" --package-lock-only --ignore-scripts --audit-level=high --registry=https://registry.npmjs.org
done < <(git ls-files -z -- '**/package-lock.json' 'package-lock.json')
npm test
cf deploy --prebuilt --dry-run
tar -czf "$output/site.tar.gz" .cloudflare/output docs/.vitepress/dist
python3 - "$output" "$revision" <<'PY'
import hashlib
import json
from pathlib import Path
import sys
output = Path(sys.argv[1])
manifest = {"revision": sys.argv[2], "sourceDirectory": "docs-site", "sha256": hashlib.sha256((output / "site.tar.gz").read_bytes()).hexdigest()}
(output / "site-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
PY
tag="ci/qualified-$revision"
if ! python3 ../scripts/ci/proof_release.py publish "$revision" docs "$output/site.tar.gz" "$output/site-manifest.json"; then
  mkdir "$output/existing"
  python3 ../scripts/ci/proof_release.py download "$revision" docs "$output/existing" site.tar.gz site-manifest.json
  gh api --paginate --slurp "repos/TerraScale/terrascale/commits/$revision/statuses?per_page=100" > "$output/existing/statuses.json"
  python3 - "$output" "$revision" <<'PY'
import hashlib, json, pathlib, sys, tarfile
output = pathlib.Path(sys.argv[1])
existing = output / 'existing'
manifest = json.loads((existing / 'site-manifest.json').read_text())
assert manifest['revision'] == sys.argv[2] and manifest['sourceDirectory'] == 'docs-site'
assert hashlib.sha256((existing / 'site.tar.gz').read_bytes()).hexdigest() == manifest['sha256']
statuses = [status for page in json.loads((existing / 'statuses.json').read_text()) for status in page]
proof = next(status for status in statuses if status['context'] == 'docs-release/docs')
assert proof['state'] == 'success' and proof['creator']['login'].lower() == 'mariogk'
assert proof['target_url'] == f'https://github.com/TerraScale/terrascale/releases/tag/ci/qualified-{sys.argv[2]}'
with tarfile.open(existing / 'site.tar.gz') as package:
    names = set()
    for member in package.getmembers():
        path = pathlib.PurePosixPath(member.name)
        assert not path.is_absolute() and '..' not in path.parts
        assert member.isdir() or member.isfile(), 'Only regular site files are permitted'
        assert any(path == prefix or prefix in path.parents for prefix in (
            pathlib.PurePosixPath('.cloudflare/output'), pathlib.PurePosixPath('docs/.vitepress/dist')))
        assert member.name not in names, 'Duplicate site entry'
        names.add(member.name)
    for name in ('docs/.vitepress/dist/build-info.json', '.cloudflare/output/v0/workers/default/assets/build-info.json'):
        assert json.load(package.extractfile(name))['commit'] == sys.argv[2]
print('Verified existing qualified site and its successful exact-revision proof')
PY
fi
gh api "repos/TerraScale/terrascale/statuses/$revision" -f state=success \
  -f context=docs-release/docs -f description='Dev-host site, examples and locked dependency audits qualified' \
  -f "target_url=https://github.com/TerraScale/terrascale/releases/tag/$tag" >/dev/null
printf 'QUALIFIED %s https://github.com/TerraScale/terrascale/releases/tag/%s\n' "$revision" "$tag"
