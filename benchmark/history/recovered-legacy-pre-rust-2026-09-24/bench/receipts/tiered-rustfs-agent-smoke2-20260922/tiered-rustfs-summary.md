# RustFS tiered segment qualification

| Measurement | Result |
| --- | --- |
| Endpoint | `http://127.0.0.1:19000/` |
| Bucket | `terrabase-tiered` |
| Source provenance | `run-receipt.json` (emitted by the benchmark runner) |
| Durability | Synced WAL, one device barrier per batch of 32 |
| Seed records (exact bodies and versions verified) | 100 |
| Final live records (exact bodies and versions verified) | 96 |
| Repeated-character payload bytes per record | 16 |
| Minimum logical seed payload bytes | 1,600 |
| Observed cgroup memory.max | max |
| Observed cgroup memory.peak | 23258378240 |
| Observed cgroup memory.swap.max | max |
| Initial published segments | 1 |
| Initial compaction records | 100 |
| Updates applied | 4 |
| Deletes applied and verified absent | 4 |
| Final published segments | 1 |
| Final compaction records | 100 |
| Synced seed time | 0.326 s |
| Synced writes/sec | 307 |
| Initial compaction and object publication | 0.472 s |
| Initial remote reopen and exact scan after SSD cache deletion | 0.209 s |
| Final remote reopen and exact scan (after final cache deletion when mutations run) | 0.073 s |
| Update time | 0.011 s |
| Delete time | 0.012 s |
| Final compaction and object publication | 0.097 s |
| Remote objects under prefix | 2 (61,102 bytes) |
| Peak local engine bytes sampled during compaction/reopen | 67,174,784 |
| Peak SSD cache bytes sampled during compaction/reopen | 30,663 |
| Peak staging bytes sampled during compaction/reopen | 0 |
| SSD cache bytes after final verification | 30,663 |
| SSD cache bytes evicted before initial reopen | 30,439 |
| Total interval | 1.805 s |
| Peak working set | 124,280,832 bytes |
| Idle SSD cache budget (active leases require working space) | 1,048,576 bytes |

The segment manifest was committed only after the RustFS object upload, then
the local SSD copies were removed. Reopen therefore verified the read-through
cache refill and segment checksum validation against RustFS.
The direct StorageEngine surface exercised here provides point reads and
collection scans; indexed query qualification remains in E2eVolumeWorkload,
which owns the automatic-index lifecycle.
Current document and receipt bodies use disk-backed derived indexes. Revision
and history metadata still scale with the corpus; general snapshot and backup
metadata are not yet bounded. The resource envelope and checkpoint stream are
recorded separately. Backup and restore are qualified by E2eVolumeWorkload;
this segment workload intentionally does not materialize a second corpus-sized
backup artifact.