#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
if [[ -n $(git status --porcelain) ]]; then
  echo 'Commit all source changes before qualifying a deployment.' >&2
  exit 1
fi
revision=$(git rev-parse HEAD)
output=$(mktemp -d /tmp/terrascale-docs-qualified.XXXXXX)
trap 'rm -rf "$output"' EXIT
npm ci --no-audit --no-fund
while IFS= read -r -d '' lock; do
  npm audit --prefix "$(dirname "$lock")" --package-lock-only --ignore-scripts --audit-level=high --registry=https://registry.npmjs.org
done < <(git ls-files -z -- '**/package-lock.json' 'package-lock.json')
npm run build
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
tag="docs/qualified-$revision"
gh release create "$tag" "$output/site.tar.gz" "$output/site-manifest.json" \
  --repo TerraScale/terrascale --target "$revision" --prerelease \
  --title "Documentation qualified $revision" \
  --notes "Dev-host audit, build, example/starter tests and prebuilt dry-run passed for $revision."
gh api "repos/TerraScale/terrascale/statuses/$revision" -f state=success \
  -f context=docs-release/docs -f description='Dev-host site, examples and locked dependency audits qualified' \
  -f "target_url=https://github.com/TerraScale/terrascale/releases/tag/$tag" >/dev/null
printf 'QUALIFIED %s https://github.com/TerraScale/terrascale/releases/tag/%s\n' "$revision" "$tag"
