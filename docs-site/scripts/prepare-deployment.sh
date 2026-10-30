#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
npm ci --no-audit --no-fund
npm run build
bash scripts/qualify-built.sh
