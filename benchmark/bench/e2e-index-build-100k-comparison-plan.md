# E2E 100K automatic-index snapshot batch comparison

Status: prepared, not launched.

This is a controlled comparison of the `IndexSnapshotBuildLimits.MaximumDocuments`
setting. Both lanes use the same frozen Release benchmark binary, source
identity, runtime, fixture seed, document count, batch size, and workload
phases. Only the snapshot build batch limit changes (`256` versus `1024`).
The run is local tier only so S3 traffic does not add noise to this index
construction comparison.

Do not launch while another TerraBase benchmark is using the shared disk,
including the active 1M RustFS E2E service.

## Fixed run contract

| Control | Value |
| --- | ---: |
| Documents | `100,000` |
| Seed | `1337` |
| Write batch | `4,096` |
| Point reads | `0` |
| Updates / deletes | `1 / 1` |
| Relationships | `0` |
| Verified indexed queries | `1` |
| Object tier | `local` |
| Snapshot batch limits | `256`, then `1024` |
| CPU priority | `nice -n 19` |

The single query exists to require an active automatic index and therefore
measure the complete index build. The receipt's `configuration` object records
`snapshotBuildMaximumDocuments`; the Markdown receipt repeats the selected
value. Compare `phases.indexBuild` after both runs complete. Verify that both
receipts have `status=complete`, `volume.documentsWritten=100000`,
`volume.queriesVerified=1`, and an active indexed query result.

## Invocation

Set `BENCH_DLL` to the same frozen assembly for both lanes. Run each command
sequentially from `ts-benchmark` after the source-matched Release build has
been frozen:

```bash
set -Eeuo pipefail

BENCH_DLL="${BENCH_DLL:?Set BENCH_DLL to the frozen TerraBase.Benchmarks.dll}"
DOTNET_BIN="${DOTNET_BIN:-/home/mariogk/.local/share/mise/dotnet-root/dotnet}"
BASE="bench/receipts/e2e-index-build-100k-comparison"

test -f "$BENCH_DLL"
test -x "$DOTNET_BIN"
test ! -e "${BASE}-256"
test ! -e "${BASE}-1024"

for LIMIT in 256 1024; do
    OUTPUT="${BASE}-${LIMIT}"
    nice -n 19 "$DOTNET_BIN" "$BENCH_DLL" \
        --workload e2e-volume \
        --object-tier local \
        --documents 100000 \
        --batch-size 4096 \
        --point-reads 0 \
        --updates 1 \
        --deletes 1 \
        --queries 1 \
        --relationships 0 \
        --seed 1337 \
        --snapshot-build-maximum-documents "$LIMIT" \
        --output "$OUTPUT"
done
```

The commands intentionally create two fresh output roots. Do not reuse a
receipt directory because the E2E workload deletes owned source, object-store,
and target children at startup.

## Comparison table

After the sequential runs, copy the measured values into this table and keep
the raw JSON/Markdown receipts beside it:

| Snapshot batch documents | Status | Index build ms | Indexed query ms | Documents written | Queries verified | Peak RSS B | Source commit / binary SHA-256 |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | --- |
| 256 | pending | — | — | — | — | — | — |
| 1024 | pending | — | — | — | — | — | — |

Extract the fields without hand-editing the JSON:

```bash
for LIMIT in 256 1024; do
    jq -r '[
        .configuration.snapshotBuildMaximumDocuments,
        .status,
        .phases.indexBuild,
        .phases.queries,
        .volume.documentsWritten,
        .volume.queriesVerified,
        (.resources.peakRssBytes // "unavailable")
    ] | @tsv' "bench/receipts/e2e-index-build-100k-comparison-${LIMIT}/e2e-volume-receipt.json"
done
```

The `TimeSpan` columns are the exact receipt values; use the elapsed
milliseconds shown in each `e2e-volume-summary.md` for the human-readable
table. Read source commit and benchmark assembly SHA-256 from each adjacent
`run-receipt.json`. This comparison does not establish 1M or 100M capacity,
S3 durability, or SSD failure recovery; those are covered by the separate
qualification runs.
