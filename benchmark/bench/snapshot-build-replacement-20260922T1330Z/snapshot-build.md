# Snapshot construction comparison

Same immutable snapshot, 16 documents (1 KiB padding each), 3 alternating trials per lane after one warmup. Source snapshot capture: 41.043 ms, excluded from both lanes.

| Construction | Median ms | p95 ms | Median allocated bytes | Median sampled peak RSS bytes | Median sampled peak managed bytes | Median index bytes | Spool bytes | Rows verified |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| durable-batches | 26.650 | 29.864 | 974264 | 87580672 | 4122784 | 34018 | 18243 | 16 |
| atomic-materialization | 91.180 | 92.030 | 1196144 | 87314432 | 4339240 | 32362 | 0 | 16 |

Both lanes use synchronous index WALs. Atomic materialization synchronizes each append, including transaction-control records. Durable batches synchronize bounded history, write-stamp, data and commit phases; it does not flush every row individually. The earlier AsyncCompressed run lost acknowledged checkpoints under SIGKILL and is a historical baseline, not equivalent durability evidence.

Both lanes create the same physical definition, source watermark and query results. Every built source document and entry is verified against the snapshot outside the timed section. Ingestion and initial empty-generation creation are excluded. Atomic materialization includes scan, collection buffering and one transaction. Durable batches include ordered capture, SHA-256 verification, file/directory sync, bounded transactions and durable cursors. These different durability/recovery costs are intentional and disclosed.

The batched lane limits pages to 128 documents and 1 MiB of charged encoded rows; it may halve pages for entry expansion. The source snapshot still retains collection-wide metadata. RSS/managed peaks are absolute observations sampled every 2 ms in a shared warm process, not isolated process peaks; they include source storage and prior runtime/cache state. Allocation deltas include the sampler and process background work. Disk sizes are immediate logical file lengths, not compacted physical allocation. This small local run does not establish larger-than-memory behavior or a general performance advantage. Raw trials and the build/source/host provenance are retained beside this report.
