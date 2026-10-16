#!/usr/bin/env bash
set -euo pipefail

# No rebuild: CI publishes this commit's image only after check succeeds.
sha=${SOURCE_REVISION:?The tested main commit is required}
[[ "$sha" =~ ^[0-9a-f]{40}$ ]] || { echo 'Invalid source revision' >&2; exit 1; }
image="registry.fly.io/ts-dashboard:$sha"
merged=$(gh api "repos/TerraScale/ts-dashboard/commits/$sha" --jq .commit.committer.date)
merged_epoch=$(date -u -d "$merged" +%s)

for region in gru sjc fra; do
  # The initial gru deploy creates the first machine; scale copies its config into each subsequent region.
  if [[ "$region" != gru ]]; then
    flyctl scale count 1 --app ts-dashboard --region "$region" --max-per-region 1 --yes
  fi
  flyctl deploy --app ts-dashboard --config fly.toml --image "$image" --regions "$region" --ha=false
  response=$(curl --fail --silent --show-error --max-time 30 \
    --header "fly-prefer-region: $region" https://dash.terrascale.tech/health/ready)
  python3 - "$sha" "$region" "$response" <<'PY'
import json, sys
sha, region, response = sys.argv[1:]
body = json.loads(response)
assert body.get('status') == 'ok', 'Dashboard is not ready'
assert body.get('version', '').endswith('+' + sha), 'Wrong image revision'
assert body.get('region') == region, 'Fly answered from the wrong region'
print(f'LIVE region={region} sha={sha}')
PY
  printf 'MERGE_TO_LIVE region=%s seconds=%s sha=%s\n' "$region" "$(( $(date -u +%s) - merged_epoch ))" "$sha"
done

flyctl machines list --app ts-dashboard --json | python3 -c '
import json, sys
machines = [m for m in json.load(sys.stdin) if m.get("state") != "destroyed"]
assert len(machines) == 3, "Expected exactly three machines"
for region in ("gru", "sjc", "fra"):
    serving = [m for m in machines if m.get("region") == region and m.get("state") == "started"]
    assert len(serving) == 1, f"Expected one running machine in {region}"
print("PASS: one running machine each in gru, sjc and fra")
'
