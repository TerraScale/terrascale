#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
npm ci --no-audit --no-fund
npm run build
node --test scripts/*.test.mjs

python3 scripts/check-main.test.py
