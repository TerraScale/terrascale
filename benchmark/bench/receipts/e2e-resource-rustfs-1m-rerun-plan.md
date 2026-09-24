# Fresh 1M RustFS E2E rerun plan

Status: **gated; not started**.

Do not start this run until the strict 1M calibration completes and the
incremental replay fix is validated on the frozen failure path. The stopped
diagnostic and its bucket remain preserved at
`receipts/e2e-resource-rustfs-1m-fixed-cfd8d1b6-20260923T151400Z/`.

## Release gate

1. Strict 1M calibration receipt is complete and records its source identity.
2. The replay fix passes the targeted incremental read/replay check, including
   the old 1M barrier path that stalled after full backup.
3. Build a new Release benchmark assembly from that validated source. Record
   the SHA-256 values for `TerraBase.Benchmarks.dll`, `TerraBase.dll`, and
   `TerraBase.ZoneTree.dll` before starting the run.
4. Generate a new RustFS bucket and use new source, temporary, and output
   roots. Do not reuse the stopped bucket or roots.

## Build command after the gate

Run from `/home/mariogk/Projects/TerraScale/ts-benchmark` with the lowest
priority:

```bash
nice -n 19 dotnet build terrabase/TerraBase.Benchmarks -c Release --no-restore --artifacts-path /tmp/terrabase-e2e-resource-rustfs-1m-rerun-<utc>
```

Copy the resulting Release dependency closure into a new immutable directory
under `bench/binaries/e2e-resource-rustfs-1m-rerun-<utc>/`, then hash it. The
build must not overwrite the frozen diagnostic directory.

## Run shape after the gate

Use a fresh timestamped receipt root and a fresh harness-generated RustFS
bucket. Keep the authenticated endpoint and cgroup envelope from the
diagnostic:

- RustFS endpoint: `http://127.0.0.1:19000`.
- `MemoryMax=4294967296`, `MemorySwapMax=0`, `OOMPolicy=stop`.
- Local accounting cap: `68719476736` B; free-space floor: `1073741824` B.
- `--object-tier s3 --documents 1000000 --batch-size 8192`.
- `--point-reads 10000 --updates 1000 --deletes 1000 --queries 1000`.
- `--relationships 10000 --checkpoint-records 500000`.
- `--cache-bytes 16777216 --resource-sample-ms 10000`.
- `--require-isolated-cgroup --root-loss-recovery`.

The final command must substitute distinct values for `<binary-dir>`,
`<source-root>`, `<temp-root>`, `<output-root>`, and the harness-generated
bucket. Capture the complete command, source/binary hashes, bucket identity,
systemd journal, resource samples, normal run receipt, and phase summary.

## Required completion checks

The rerun is a pass only if it completes incremental replay, root-loss
recovery, full/PITR/final restore, indexed queries, compaction, and cold
RustFS reopen with exact live/deleted counts. Also verify zero OOM events,
zero swap, the storage cap, the final RustFS object inventory, and every
artifact hash. A normal `e2e-volume-receipt.json` and `run-receipt.json` are
required; an interrupted or manually stopped run remains diagnostic.
