# RustFS tiered segment qualification

| Measurement | Result |
| --- | --- |
| Endpoint | `http://127.0.0.1:19000/` |
| Bucket | `terrabase-tiered` |
| Durability | Synced WAL, one device barrier per batch of 64 |
| Seed records (exact bodies and versions verified) | 1,000 |
| Final live records (exact bodies and versions verified) | 990 |
| Repeated-character payload bytes per record | 16 |
| Minimum logical seed payload bytes | 16,000 |
| Observed cgroup memory.max | max |
| Observed cgroup memory.peak | 23258378240 |
| Observed cgroup memory.swap.max | max |
| Initial published segments | 1 |
| Initial compaction records | 1,000 |
| Updates applied | 10 |
| Deletes applied and verified absent | 10 |
| Final published segments | 1 |
| Final compaction records | 1,000 |
| Synced seed time | 0.425 s |
| Synced writes/sec | 2353 |
| Initial compaction and object publication | 0.558 s |
| Initial remote reopen and exact scan after SSD cache deletion | 0.338 s |
| Final remote reopen and exact scan after SSD cache deletion | 0.245 s |
| Update time | 0.010 s |
| Delete time | 0.011 s |
| Final compaction and object publication | 0.220 s |
| Remote objects under prefix | 2 (610,438 bytes) |
| Peak local engine bytes sampled during compaction/reopen | 67,694,217 |
| Peak SSD cache bytes sampled during compaction/reopen | 305,499 |
| Peak staging bytes sampled during compaction/reopen | 0 |
| SSD cache bytes after final verification | 305,499 |
| SSD cache bytes evicted before initial reopen | 304,939 |
| Total interval | 2.503 s |
| Peak working set | 130,486,272 bytes |
| Idle SSD cache budget (active leases require working space) | 1,048,576 bytes |

The segment manifest was committed only after the RustFS object upload, then
the local SSD copies were removed. Reopen therefore verified the read-through
cache refill and segment checksum validation against RustFS.
Current document and receipt bodies use disk-backed derived indexes. Revision
and history metadata still scale with the corpus; general snapshot and backup
metadata are not yet bounded. The resource envelope and checkpoint stream are
recorded separately. Backup and restore are qualified by E2eVolumeWorkload;
this segment workload intentionally does not materialize a second corpus-sized
backup artifact.