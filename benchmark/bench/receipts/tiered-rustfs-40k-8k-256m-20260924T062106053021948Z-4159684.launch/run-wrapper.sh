#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'
OUTPUT=/home/mariogk/Projects/TerraScale/ts-benchmark/bench/receipts/tiered-rustfs-40k-8k-256m-20260924T062106053021948Z-4159684
DOTNET_BIN=/home/mariogk/.local/share/mise/dotnet-root/dotnet
BENCH_DLL=/home/mariogk/Projects/TerraScale/ts-benchmark/bench/binaries/terrabase-final-source-matched-release-20260924T061758Z/TerraBase.Benchmarks.dll
UNIT=terrabase-tiered-rustfs-40k-8k-256m-20260924T062106053021948Z-4159684.service
NICE_BIN=/run/current-system/sw/bin/nice

# Keep credentials out of the console log and all receipt metadata. The path
# is injected into this private systemd unit and is intentionally not written
# into the launch manifest or wrapper artifact.
: "${TB_TIERED_CREDENTIALS_FILE:?RustFS credential environment is missing}"
source "$TB_TIERED_CREDENTIALS_FILE"
source "$OUTPUT/launch.env"
export TERRABASE_S3_ENDPOINT="$S3_ENDPOINT"
export TERRABASE_S3_BUCKET="$S3_BUCKET"
export TERRABASE_S3_REGION="$S3_REGION"
exec > "$OUTPUT/console.log" 2>&1

run_status=1
if "$NICE_BIN" -n 19 "$DOTNET_BIN" "$BENCH_DLL" \
    --workload tiered-segment \
    --records 40000 \
    --batch 64 \
    --payload-bytes 8192 \
    --payload-mode per-key-incompressible \
    --updates 100 \
    --deletes 1000 \
    --point-reads 32 \
    --checkpoint-records 4096 \
    --cache-bytes 4194304 \
    --max-local-storage-bytes 4294967296 \
    --min-free-space-bytes 107374182400 \
    --resource-sample-ms 500 \
    --require-isolated-cgroup \
    --s3-endpoint "$S3_ENDPOINT" \
    --s3-bucket "$S3_BUCKET" \
    --s3-region "$S3_REGION" \
    --prefix "$PREFIX" \
    --output "$OUTPUT"; then
    run_status=0
else
    run_status=$?
fi
printf 'exitCode=%s\n' "$run_status" > "$OUTPUT/benchmark-exit-status.tmp"
mv -- "$OUTPUT/benchmark-exit-status.tmp" "$OUTPUT/benchmark-exit-status"
exit "$run_status"
