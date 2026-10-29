#!/usr/bin/env bash
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
. "$here/../turso-pin.env"
bin=$here/../.build/bin
out=${1:-$here/../.build/results.json}
cd "$here"
npm ci --ignore-scripts --no-audit --no-fund >/dev/null
status=0
node qualify.mjs "$bin/ts-sync-revision-host" "$bin/tursodb" "$out" "${2:-3}" || status=$?
RECEIPT_DATE=$(date -u +%F) \
RECEIPT_COMMIT=$(git -C "$here" rev-parse HEAD) \
RECEIPT_HOST=$(sha256sum "$bin/ts-sync-revision-host" | cut -d' ' -f1) \
RECEIPT_CLI=$(sha256sum "$bin/tursodb" | cut -d' ' -f1) \
RECEIPT_CPU=$(grep -m1 'model name' /proc/cpuinfo | cut -d: -f2- | xargs) \
RECEIPT_CPUS=$(nproc) \
RECEIPT_KERNEL=$(uname -r) \
RECEIPT_MACHINE=$(hostname -s) \
TURSO_COMMIT=$TURSO_COMMIT TURSO_SHA256=$TURSO_SHA256 RUST_TOOLCHAIN=$RUST_TOOLCHAIN \
node -e '
const e = process.env;
const client = require("./node_modules/@tursodatabase/sync/package.json").version;
require("fs").writeFileSync(process.argv[1], JSON.stringify({
  date: e.RECEIPT_DATE, terrascale_commit: e.RECEIPT_COMMIT, turso_commit: e.TURSO_COMMIT,
  turso_sha256: e.TURSO_SHA256, rust: e.RUST_TOOLCHAIN, host_sha256: e.RECEIPT_HOST,
  cli_sha256: e.RECEIPT_CLI, client, machine: e.RECEIPT_MACHINE, cpu: e.RECEIPT_CPU,
  cpus: Number(e.RECEIPT_CPUS), kernel: e.RECEIPT_KERNEL,
}, null, 2));
' "${out%.json}-receipt.json"
exit "$status"
