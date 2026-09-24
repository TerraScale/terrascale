# RustFS tiered storage evidence — 2026-09-24

The 40,000-record, 8 KiB RustFS run completed its durability and larger-than-memory diagnostic path. It is recorded as diagnostic evidence because it used an immutable frozen benchmark closure whose dirty source provenance has since drifted from the live checkout; it is not a qualification of the merged source.

| Evidence | Result | Scope or interpretation |
| --- | ---: | --- |
| 40K × 8 KiB seed | 40,000 records in 93.112 s (430 records/s) | `per-key-incompressible`, 327,680,000 B minimum logical payload |
| Complete run interval | 868.739 s | Release benchmark, RustFS at `127.0.0.1:19000` |
| Root-loss recovery | 141.977 s before compaction; 149.822 s after mutations | Exact scans, point reads, tombstone boundaries, and retry status verified after each local-root loss |
| Mutations | 100 updates in 0.945 s; 1,000 deletes in 5.300 s | Final exact scan verified 39,000 live records and deleted-key absence |
| Compaction and cold remote reopen | 113.703 s / 91.347 s compaction; 152.336 s / 59.482 s remote reopen scans | Segment upload completed before local SSD copies were removed; RustFS read-through refill and checksums verified |
| Remote durability | 644 acknowledged journal batches; 41,104 physical records; 350,719,536 B journal | Every acknowledged batch was published to the remote commit journal before its synced local WAL append |
| Final RustFS segment store | 6 objects, 681,166,690 B | Published after the final mutation and compaction generation |
| Local storage | 1,738,484,427 B peak engine bytes | 4,294,967,296 B local engine cap; free-space floor was not violated |
| Memory envelope | 268,435,456 B `memory.max`; 282,312,704 B peak; 0 swap; 0 OOM kills | cgroup v2 reclaim pressure produced a brief peak overshoot; final current memory was within the cap. The peak is reported as pressure evidence, not treated as a hard-failure gate |
| Direct workload coverage | Point reads, collection scans, updates, deletes, compaction, S3 publication, cache eviction, cold reopen, and two root-loss recoveries | Indexed query planning and backup/restore remain covered by the separate E2E workload |
| Provenance | Frozen assembly `2b2b6dd9…f78db`; subject commit `47b235d…ffcba9`; dirty source; live-checkout drift | See the [run summary](receipts/tiered-rustfs-40k-8k-256m-20260924T062106053021948Z-4159684/tiered-rustfs-summary.md), [run receipt](receipts/tiered-rustfs-40k-8k-256m-20260924T062106053021948Z-4159684/run-receipt.json), and [provenance verification](receipts/tiered-rustfs-40k-8k-256m-20260924T062106053021948Z-4159684/provenance-verification.json) |

## Earlier 10M planning evidence

The completed 10M ingest-only receipt is older-binary planning evidence. It measured 4,845 records/s overall, 3,461 records/s in the last million-record interval, about 6.86 GB of local engine storage, and 4.22 GB of remote journal objects under a 4 GiB, zero-swap cgroup. It did not run compaction, cold reopen, updates, deletes, indexed queries, backup, restore, or root-loss recovery. See the [10M summary](receipts/tiered-rustfs-strict-10m-ingest-20260923T152728Z-rerun/tiered-rustfs-summary.md) and [calibration analysis](receipts/tiered-rustfs-strict-10m-ingest-20260923T152728Z-rerun/calibration-analysis.md).

## 100M status

The 100M ingest and exact-recovery run has not started and is not qualified. It remains scheduled for a later run from the settled merged source with a fresh frozen provenance record.
