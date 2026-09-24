# Snapshot construction comparison

Historical durability limitation: this subject used AsyncCompressed index WALs. Later SIGKILL tests lost acknowledged snapshot batches and completion metadata. These numbers do not qualify durable construction; the later synchronous-WAL run supersedes the durability claims while preserving this performance baseline.

Same immutable snapshot, 2048 documents (1 KiB padding each), 5 alternating trials per lane after one warmup. Source snapshot capture: 84.883 ms, excluded from both lanes.

| Construction | Median ms | p95 ms | Median allocated bytes | Median sampled peak RSS bytes | Median sampled peak managed bytes | Median index bytes | Spool bytes | Rows verified |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| durable-batches | 213.353 | 441.119 | 56852344 | 138481664 | 36595360 | 521179 | 2290783 | 2048 |
| atomic-materialization | 170.013 | 592.138 | 40245736 | 147116032 | 40063392 | 639504 | 0 | 2048 |

Both lanes create the same physical definition, source watermark and query results. Every built source document and entry is verified against the snapshot outside the timed section. Ingestion and initial empty-generation creation are excluded. Atomic materialization includes scan, collection buffering and one transaction. Durable batches include ordered capture, SHA-256 verification, file/directory sync, bounded transactions and durable cursors. These different durability/recovery costs are intentional and disclosed.

The batched lane limits pages to 128 documents and 1 MiB of charged encoded rows; it may halve pages for entry expansion. The source snapshot still retains collection-wide metadata. RSS/managed peaks are absolute observations sampled every 2 ms in a shared warm process, not isolated process peaks; they include source storage and prior runtime/cache state. Allocation deltas include the sampler and process background work. Disk sizes are immediate logical file lengths, not compacted physical allocation. This small local run does not establish larger-than-memory behavior or a general performance advantage. Raw trials and the build/source/host provenance are retained beside this report.

This recorded run overlapped the TerraBase API test suite on the same development host. Treat elapsed time and memory as shared-host observations.
