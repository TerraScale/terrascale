#!/usr/bin/env bash
set -euo pipefail
work=${1:?directory}
repo=TerraScale/ts-orchestrator
binary=terrascale-orchestrator-linux-amd64
mkdir -p "$work"
tag=$(gh api graphql -f query='{repository(owner: "TerraScale", name: "ts-orchestrator") {releases(first: 30, orderBy: {field: CREATED_AT, direction: DESC}) {nodes {tagName createdAt isDraft releaseAssets(name: "qualification.json") {totalCount}}}}}' \
  --jq '[.data.repository.releases.nodes[] | select((.tagName | startswith("ci-")) and (.isDraft | not) and .releaseAssets.totalCount > 0)] | sort_by(.createdAt) | last | .tagName // ""')
[[ -n $tag ]] || { echo "No published ci- release of $repo carries qualification.json" >&2; exit 1; }
gh release download "$tag" --repo "$repo" --dir "$work" --pattern "$binary" --pattern qualification.json --clobber
sha256=$(sha256sum "$work/$binary" | cut -d ' ' -f 1)
jq -e --arg name "$binary" --arg sha256 "$sha256" 'any(.artifacts[]; .name == $name and .sha256 == $sha256)' \
  "$work/qualification.json" > /dev/null || { echo "$repo $tag: $binary (sha256 $sha256) is not the binary its qualification.json lists" >&2; exit 1; }
curl -fsSL --max-time 30 https://raw.githubusercontent.com/TerraScale/ts-orchestrator/main/deploy/m1/regions.json -o "$work/regions.json"
chmod +x "$work/$binary"
echo "$tag"
