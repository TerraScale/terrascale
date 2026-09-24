# Snapshot construction comparison

Same immutable snapshot, 2048 documents (1 KiB padding each), 5 alternating trials per lane after one warmup. Source snapshot capture: 150.627 ms, excluded from both lanes.

| Construction | Median ms | p95 ms | Median allocated bytes | Median sampled peak RSS bytes | Median sampled peak managed bytes | Median index bytes | Spool bytes | Rows verified |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| durable-batches | 8158.418 | 9066.522 | 120097392 | 120541184 | 27490032 | 3276951 | 2290783 | 2048 |
| atomic-materialization | 8843.788 | 14033.169 | 106117856 | 114855936 | 27752680 | 3261728 | 0 | 2048 |

Both lanes use synchronous index WALs with file synchronization at append, including transaction control records. The earlier AsyncCompressed run lost acknowledged checkpoints under SIGKILL and is a historical baseline, not equivalent durability evidence.

Both lanes create the same physical definition, source watermark and query results. Every built source document and entry is verified against the snapshot outside the timed section. Ingestion and initial empty-generation creation are excluded. Atomic materialization includes scan, collection buffering and one transaction. Durable batches include ordered capture, SHA-256 verification, file/directory sync, bounded transactions and durable cursors. These different durability/recovery costs are intentional and disclosed.

The batched lane limits pages to 128 documents and 1 MiB of charged encoded rows; it may halve pages for entry expansion. The source snapshot still retains collection-wide metadata. RSS/managed peaks are absolute observations sampled every 2 ms in a shared warm process, not isolated process peaks; they include source storage and prior runtime/cache state. Allocation deltas include the sampler and process background work. Disk sizes are immediate logical file lengths, not compacted physical allocation. This small local run does not establish larger-than-memory behavior or a general performance advantage. Raw trials and the build/source/host provenance are retained beside this report.

This recorded run overlapped the API suite and migration checks on the same development host. The large regression relative to the earlier asynchronous run includes synchronizing each row/control append; that earlier run failed checkpoint durability under SIGKILL.
