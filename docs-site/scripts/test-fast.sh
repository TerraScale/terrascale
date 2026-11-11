#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
npm ci --no-audit --no-fund
node scripts/generate-llms.mjs
npx vitepress build docs
node scripts/check-public.mjs &
check_public=$!
node scripts/check-links.mjs
wait "$check_public"
node scripts/build-worker.mjs
node --test scripts/*.test.mjs
