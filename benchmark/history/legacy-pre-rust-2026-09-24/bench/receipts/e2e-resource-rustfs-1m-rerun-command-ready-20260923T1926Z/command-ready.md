# Strict 1M RustFS E2E launch sheet

Status: gated; launch only after the active strict tier calibration and the
current source build/test gates finish.

## Runtime boundary

- RustFS endpoint: `http://127.0.0.1:19000`
- Credentials: source privately from `/run/user/1000/terrabase-rustfs-credentials.sh`; do not print values.
- RustFS data root: `/home/mariogk/.local/share/containers/storage/volumes/f4cd87ff0e1f288eeafc4a03a1f73feed02c53806d7406f24d124759daa769d7/_data`
- Unit memory: `MemoryMax=4294967296`, `MemorySwapMax=0`, `OOMPolicy=stop`
- Local accounting: `--max-local-storage-bytes 68719476736 --min-free-space-bytes 107374182400`

## Command shape

Use fresh timestamped `<unit>`, `<binary-dir>`, `<temp-root>`, and
`<output-root>` values. The runner creates a fresh authenticated RustFS
bucket per process.

```bash
mkdir -p <temp-root>
systemd-run --user --unit=<unit> \
  --property=MemoryMax=4294967296 \
  --property=MemorySwapMax=0 \
  --property=OOMPolicy=stop \
  --working-directory=/home/mariogk/Projects/TerraScale/ts-benchmark \
  /run/current-system/sw/bin/bash -lc '
    set -a
    source /run/user/1000/terrabase-rustfs-credentials.sh
    set +a
    export TERRABASE_S3_ENDPOINT=http://127.0.0.1:19000 TERRABASE_S3_REGION=us-east-1
    export TMPDIR=<temp-root> TMP=<temp-root> TEMP=<temp-root>
    exec /run/current-system/sw/bin/nice -n 19 dotnet <binary-dir>/TerraBase.Benchmarks.dll \
  --workload e2e-volume --object-tier s3 --documents 1000000 \
  --batch-size 8192 --point-reads 10000 --updates 1000 --deletes 1000 \
  --queries 1000 --relationships 10000 --checkpoint-records 500000 \
  --resource-sample-ms 10000 --cache-bytes 16777216 \
  --max-local-storage-bytes 68719476736 \
  --min-free-space-bytes 107374182400 \
  --rustfs-data-path /home/mariogk/.local/share/containers/storage/volumes/f4cd87ff0e1f288eeafc4a03a1f73feed02c53806d7406f24d124759daa769d7/_data \
  --require-isolated-cgroup --root-loss-recovery \
  --temp-root <temp-root> --output <output-root>'
```

## Required evidence

The result is a pass only when the receipt reaches incremental replay,
source-root deletion and S3 journal recovery, full/PITR/final restore,
indexed queries, compaction, and cold remote reopen with exact live/deleted
counts. Preserve the command, binary hashes, `run-receipt.json`,
`e2e-volume-receipt.json`, phase summary, resource envelope, progress and
resource samples, root-loss checkpoint, bucket identity, and systemd journal.

Previous 1M diagnostic runs reached full backup at about 23 GB local and
incremental backup at about 23.14 GB local; the 64 GiB cap is retained for
restore and compaction headroom. Previous 1K strict E2E proved all function
phases and the SSD-root-loss journal path against the same RustFS endpoint.
