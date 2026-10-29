#!/usr/bin/env bash
set -euo pipefail

[ "$#" -eq 1 ] || { echo "usage: qualify-release.sh <external-output-directory>" >&2; exit 2; }
cd "$(dirname "${BASH_SOURCE[0]}")/.."
repository=$(git rev-parse --show-toplevel)
output=$(realpath -m "$1")
case "$output/" in "$repository/"*) echo 'Release output must be outside the checkout' >&2; exit 2 ;; esac
mkdir -p "$output"
[ -z "$(find "$output" -mindepth 1 -maxdepth 1 -print -quit)" ] || { echo 'Release output must be empty' >&2; exit 2; }

version=$(node -p 'require("./package.json").version')
if [[ ! "$version" =~ ^0\.0\.[0-9]+$ ]]; then
  echo "TS-528: 0.0.x is required until the owner lifts it; got $version" >&2
  exit 1
fi
test "$(node --version)" = v24.21.0
workspace=$(mktemp -d)
trap 'rm -rf "$workspace"' EXIT
export RUNNER_TEMP="$workspace" GITHUB_ENV="$workspace/npm.env" GITHUB_PATH="$workspace/npm.path"
node scripts/provision-reviewed-npm.js
export PATH="$(cat "$GITHUB_PATH"):$PATH"
export TERRASCALE_NPM_ARCHIVE_INTEGRITY="$(sed -n 's/^TERRASCALE_NPM_ARCHIVE_INTEGRITY=//p' "$GITHUB_ENV")"
node --input-type=module - "$output/npm-cli-evidence.json" <<'NODE'
import { writeFile } from "node:fs/promises";
import { inspectNpmCli } from "./scripts/npm-cli-evidence.js";
const { evidence } = await inspectNpmCli({ archiveIntegrity: process.env.TERRASCALE_NPM_ARCHIVE_INTEGRITY });
await writeFile(process.argv[2], JSON.stringify(evidence, null, 2) + "\n", { flag: "wx" });
NODE
npm run check:vulnerabilities
npm ci
npm run typecheck
npm run build
npm run check:v1-route-manifest
npm test
npm pack --json --pack-destination "$output" > "$output/npm-pack.json"
