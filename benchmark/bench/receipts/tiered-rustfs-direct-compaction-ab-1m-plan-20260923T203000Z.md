# Direct compaction A/B launch plan

Prepared read-only while the strict 1M current-source candidate run is active.
Do not launch either command until that service is inactive. Do not rebuild the
assemblies. Run the baseline and candidate sequentially with the same host
conditions and fresh RustFS prefixes.

## Frozen inputs

| Variant | Assembly | SHA-256 |
| --- | --- | --- |
| Baseline | `bench/binaries/state-map-flush-trace-baseline-20260923T185001Z/TerraBase.Benchmarks.dll` | `8b0440bccc452feae4f4b3e56baff1d6c0a55e516e489aac0e7c41d2f3762f21` |
| Candidate | `bench/binaries/state-map-flush-candidate-integrity-fix-20260923T193500Z/TerraBase.Benchmarks.dll` | `ae5930effd8d32c1e2e25b6f85a2e8703a2abd7124f2e158e46e138fcefa985a` |

Both commands use the strict 1M input: 1,000,000 records, batch 65,536,
128-byte per-key incompressible payloads, payload seed 1337, 16 MiB cache,
100 GiB free-space floor, 500 ms resource sampling, and the local RustFS
endpoint `http://127.0.0.1:19000` in bucket `terrabase-tiered`.

The direct path is selected by omitting `--ingest-only` and setting
`--updates 0 --deletes 0`. This runs seed, local-root-loss recovery, and the
initial `CompactAsync` upload followed by remote reopen and exact scan. With no
mutations, the workload intentionally skips the final compaction path.

## Preflight (read-only)

```bash
if systemctl --user is-active --quiet terrabase-tiered-strict-1m-integrity-fix.service; then
  echo 'strict 1M runner is still active; wait before launching direct A/B' >&2
  exit 1
fi
curl -fsS http://127.0.0.1:19000/minio/health/live >/dev/null
sha256sum \
  /home/mariogk/Projects/TerraScale/ts-benchmark/bench/binaries/state-map-flush-trace-baseline-20260923T185001Z/TerraBase.Benchmarks.dll \
  /home/mariogk/Projects/TerraScale/ts-benchmark/bench/binaries/state-map-flush-candidate-integrity-fix-20260923T193500Z/TerraBase.Benchmarks.dll
test -r /run/user/1000/terrabase-rustfs-credentials.sh
```

The first command must report that the strict service is inactive before the
run is admitted. The credentials file must continue to be sourced privately;
do not copy credentials into this plan or a receipt.

## Baseline command

```bash
systemd-run --user --wait --collect \
  --unit=terrabase-direct-compaction-1m-baseline.service \
  -p MemoryMax=4294967296 -p MemorySwapMax=0 -p OOMPolicy=stop -- \
  /run/current-system/sw/bin/bash -c '
    set -a
    . /run/user/1000/terrabase-rustfs-credentials.sh
    set +a
    exec /run/current-system/sw/bin/nice -n 19 \
      /home/mariogk/.local/share/mise/dotnet-root/dotnet \
      /home/mariogk/Projects/TerraScale/ts-benchmark/bench/binaries/state-map-flush-trace-baseline-20260923T185001Z/TerraBase.Benchmarks.dll \
      --workload tiered-segment \
      --records 1000000 --batch 65536 --payload-bytes 128 \
      --payload-mode per-key-incompressible --seed 1337 \
      --updates 0 --deletes 0 --point-reads 0 \
      --checkpoint-records 250000 --cache-bytes 16777216 \
      --min-free-space-bytes 107374182400 --resource-sample-ms 500 \
      --require-isolated-cgroup \
      --s3-endpoint http://127.0.0.1:19000 --s3-bucket terrabase-tiered \
      --prefix tiered-rustfs-direct-1m-baseline-20260923T203000Z \
      --output /home/mariogk/Projects/TerraScale/ts-benchmark/bench/receipts/tiered-rustfs-direct-1m-baseline-20260923T203000Z
  '
```

## Candidate command

Run only after the baseline service exits successfully. Keep every option and
the host state identical; only the assembly, unit, prefix, and output path
change.

```bash
systemd-run --user --wait --collect \
  --unit=terrabase-direct-compaction-1m-candidate.service \
  -p MemoryMax=4294967296 -p MemorySwapMax=0 -p OOMPolicy=stop -- \
  /run/current-system/sw/bin/bash -c '
    set -a
    . /run/user/1000/terrabase-rustfs-credentials.sh
    set +a
    exec /run/current-system/sw/bin/nice -n 19 \
      /home/mariogk/.local/share/mise/dotnet-root/dotnet \
      /home/mariogk/Projects/TerraScale/ts-benchmark/bench/binaries/state-map-flush-candidate-integrity-fix-20260923T193500Z/TerraBase.Benchmarks.dll \
      --workload tiered-segment \
      --records 1000000 --batch 65536 --payload-bytes 128 \
      --payload-mode per-key-incompressible --seed 1337 \
      --updates 0 --deletes 0 --point-reads 0 \
      --checkpoint-records 250000 --cache-bytes 16777216 \
      --min-free-space-bytes 107374182400 --resource-sample-ms 500 \
      --require-isolated-cgroup \
      --s3-endpoint http://127.0.0.1:19000 --s3-bucket terrabase-tiered \
      --prefix tiered-rustfs-direct-1m-candidate-20260923T203000Z \
      --output /home/mariogk/Projects/TerraScale/ts-benchmark/bench/receipts/tiered-rustfs-direct-1m-candidate-20260923T203000Z
  '
```

## Expected duration

The strict 1M receipt measured a 128.4 s seed, 255.2 s seed root-loss
recovery, 178.4 s initial compaction upload, and 248.2 s initial remote reopen
and exact scan. The direct no-mutation run should therefore take approximately
14–18 minutes per variant, or 30–40 minutes for both sequentially, depending
on host contention and RustFS I/O. It should not be started with a short
timeout.

## Pass criteria

Each receipt must satisfy all of the following:

1. `run-receipt.json` has `status=complete` and `exitCode=0`.
2. `tiered-rustfs-receipt.json` has `Status=complete`, `CompletedRecords=1000000`,
   and no `Error` or `FreeSpaceLimitViolation`.
3. `seed-root-loss-recovery` completes and the initial remote reopen/exact scan
   reports 1,000,000 records. This is the SSD-loss correctness gate.
4. `initial-compaction-upload` reports 1,000,000 records and publishes the
   same nonzero segment object count in both variants (the prior strict 1M
   run published four objects).
5. The final remote object listing and local segment compatibility validation
   succeed; no manifest length, bounds, magic/version, or record-count error is
   present.
6. The final cgroup reports `MemoryMax=4294967296`, `SwapMax=0`, and no OOM
   events. The receipt must report a useful isolated cgroup.
7. `RemoteJournalObjects` is nonzero and the remote journal acknowledged all
   1,000,000 seed records before the root-loss recovery. Journal object count
   and segment object count must be compared between variants.

Performance is secondary to these correctness gates. Compare
`PhaseMetrics[].Name == initial-compaction-upload` duration, records written,
published object count/bytes, peak RSS, peak local bytes, and cgroup memory
peak. Do not reject a correct candidate on one noisy wall-clock sample; retain
the receipt and record host contention. The fast path is accepted only if the
candidate passes every correctness gate and does not introduce a material
resource regression.
