# RustFS tiered segment qualification

| Measurement | Result |
| --- | --- |
| Endpoint | `http://127.0.0.1:19000/` |
| Bucket | `terrabase-ingest-audit-20260922` |
| Source provenance | `run-receipt.json` (emitted by the benchmark runner) |
| Durability | Synced WAL, one device barrier per batch of 1000 |
| Seed records (exact bodies and versions verified) | 100,000 |
| Final live records (exact bodies and versions verified) | 100,000 |
| Repeated-character payload bytes per record | 128 |
| Minimum logical seed payload bytes | 12,800,000 |
| Observed cgroup memory.max | max |
| Observed cgroup memory.peak | 23258378240 |
| Observed cgroup memory.swap.max | max |
| Initial published segments | 1 |
| Initial compaction records | 100,000 |
| Updates applied | 0 |
| Deletes applied and verified absent | 0 |
| Final published segments | 1 |
| Final compaction records | 0 |
| Synced seed time | 11.565 s |
| Synced writes/sec | 8647 |
| Initial compaction and object publication | 17.421 s |
| Initial remote reopen and exact scan after SSD cache deletion | 18.303 s |
| Final remote reopen and exact scan (after final cache deletion when mutations run) | 16.794 s |
| Update time | 0.000 s |
| Delete time | 0.000 s |
| Final compaction and object publication | 0.000 s |
| Remote objects under prefix | 1 (41,888,939 bytes) |
| Peak local engine bytes sampled during compaction/reopen | 87,379,321 |
| Peak SSD cache bytes sampled during compaction/reopen | 0 |
| Peak staging bytes sampled during compaction/reopen | 0 |
| SSD cache bytes after final verification | 0 |
| SSD cache bytes evicted before initial reopen | 0 |
| Total interval | 64.635 s |
| Peak working set | 432,115,712 bytes |
| Idle SSD cache budget (active leases require working space) | 16,777,216 bytes |

The segment manifest was committed only after the RustFS object upload, then
the local SSD copies were removed. Reopen therefore verified the read-through
cache refill and segment checksum validation against RustFS.
The direct StorageEngine surface exercised here provides point reads and
collection scans; indexed query qualification remains in E2eVolumeWorkload,
which owns the automatic-index lifecycle.
Current document, physical sequence, tombstone, and receipt indexes use
disk-backed derived state in this source. Snapshot and backup/restore paths
still have their own working buffers and artifact limits, so this segment run
does not claim arbitrary bounded memory for those paths. The resource envelope
and checkpoint stream are recorded separately. Backup and restore are
qualified by E2eVolumeWorkload; this segment workload intentionally does not
materialize a second corpus-sized backup artifact.