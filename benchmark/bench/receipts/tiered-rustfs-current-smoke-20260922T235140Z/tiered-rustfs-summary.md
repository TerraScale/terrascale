# RustFS tiered segment qualification

| Measurement | Result |
| --- | --- |
| Endpoint | `http://127.0.0.1:19000/` |
| Bucket | `terrabase-tiered` |
| Source provenance | `run-receipt.json` (emitted by the benchmark runner) |
| Durability | Synced WAL, one device barrier per batch of 100 |
| Seed records (exact bodies and versions verified) | 1,000 |
| Final live records (exact bodies and versions verified) | 990 |
| Repeated-character payload bytes per record | 128 |
| Minimum logical seed payload bytes | 128,000 |
| Observed cgroup memory.max | max |
| Observed cgroup memory.peak | 23258378240 |
| Observed cgroup memory.swap.max | max |
| Initial published segments | 1 |
| Initial compaction records | 1,000 |
| Updates applied | 10 |
| Deletes applied and verified absent | 10 |
| Final published segments | 1 |
| Final compaction records | 1,000 |
| Synced seed time | 0.456 s |
| Synced writes/sec | 2192 |
| Initial compaction and object publication | 0.770 s |
| Initial remote reopen and exact scan after SSD cache deletion | 0.345 s |
| Final remote reopen and exact scan (after final cache deletion when mutations run) | 0.407 s |
| Update time | 0.016 s |
| Delete time | 0.011 s |
| Final compaction and object publication | 0.471 s |
| Remote objects under prefix | 2 (835,558 bytes) |
| Peak local engine bytes sampled during compaction/reopen | 67,538,251 |
| Peak SSD cache bytes sampled during compaction/reopen | 418,619 |
| Peak staging bytes sampled during compaction/reopen | 0 |
| SSD cache bytes after final verification | 418,619 |
| SSD cache bytes evicted before initial reopen | 416,939 |
| Total interval | 3.303 s |
| Peak working set | 131,121,152 bytes |
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