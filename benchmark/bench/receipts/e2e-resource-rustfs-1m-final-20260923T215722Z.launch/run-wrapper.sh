#!/usr/bin/env bash
set -euo pipefail
source /run/user/1000/terrabase-rustfs-credentials.sh
export TERRABASE_S3_ENDPOINT=http://127.0.0.1:19000 TERRABASE_S3_REGION=us-east-1
export TMPDIR=/home/mariogk/Projects/TerraScale/ts-benchmark/bench/receipts/e2e-resource-rustfs-1m-final-20260923T215722Z-tmp TMP=/home/mariogk/Projects/TerraScale/ts-benchmark/bench/receipts/e2e-resource-rustfs-1m-final-20260923T215722Z-tmp TEMP=/home/mariogk/Projects/TerraScale/ts-benchmark/bench/receipts/e2e-resource-rustfs-1m-final-20260923T215722Z-tmp
exec /run/current-system/sw/bin/nice -n 19 dotnet /home/mariogk/Projects/TerraScale/ts-benchmark/bench/binaries/terrabase-final-source-matched-release-20260923T215453Z/TerraBase.Benchmarks.dll \
  --workload \
  e2e-volume \
  --object-tier \
  s3 \
  --documents \
  1000000 \
  --batch-size \
  8192 \
  --point-reads \
  10000 \
  --updates \
  1000 \
  --deletes \
  1000 \
  --queries \
  1000 \
  --relationships \
  10000 \
  --seed \
  1337 \
  --checkpoint-records \
  500000 \
  --resource-sample-ms \
  10000 \
  --cache-bytes \
  16777216 \
  --max-local-storage-bytes \
  68719476736 \
  --min-free-space-bytes \
  107374182400 \
  --rustfs-data-path \
  /home/mariogk/.local/share/containers/storage/volumes/f4cd87ff0e1f288eeafc4a03a1f73feed02c53806d7406f24d124759daa769d7/_data \
  --require-isolated-cgroup \
  --root-loss-recovery \
  --temp-root \
  /home/mariogk/Projects/TerraScale/ts-benchmark/bench/receipts/e2e-resource-rustfs-1m-final-20260923T215722Z-tmp \
  --output \
  /home/mariogk/Projects/TerraScale/ts-benchmark/bench/receipts/e2e-resource-rustfs-1m-final-20260923T215722Z \

