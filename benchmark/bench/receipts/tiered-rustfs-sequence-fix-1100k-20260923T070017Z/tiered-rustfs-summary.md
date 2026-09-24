# RustFS tiered segment qualification

| Measurement | Result |
| --- | --- |
| Endpoint | `http://127.0.0.1:19000/` |
| Bucket | `terrabase-tiered` |
| Subject project | `/home/mariogk/Projects/TerraScale/terrabase/src/TerraBase/TerraBase.csproj` |
| Subject commit | `67e35f443565c54eb731d6ea2dd989cb5c87b7be` (tree `69203400f13c7e311cf5fb569a081eeab36e8cbc`, dirty `True`, worktree `bfbb97c3dc6331de50ba1f516113cd3e7b3b3016d5a3f1dffefd936903972c3a`) |
| Benchmark build | `Release`; `11.0.0`; `.NET 11.0.0-rc.1.26425.128` |
| Benchmark assembly | `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/receipts/tiered-rustfs-sequence-fix-1100k-20260923T070017Z/bin/TerraBase.Benchmarks.dll` (sha256 `e5534eeb25ee0367c7787cbeb592a19f4b08c2ff506de9d3632ad3c686679861`) |
| Source provenance | `run-receipt.json` (emitted by the benchmark runner) |
| Durability | Synced WAL, one device barrier per batch of 1000 |
| Seed records (exact bodies and versions verified) | 1,100,000 |
| Final live records (exact bodies and versions verified) | 1,099,000 |
| Payload mode | `per-key-incompressible` |
| Payload generator | `sha256-base64url-per-key` |
| Payload seed | 1337 |
| Payload bytes per record | 128 |
| Minimum logical seed payload bytes | 140,800,000 |
| Observed cgroup memory.max | 4294967296 |
| Observed cgroup memory.peak | 1441931264 |
| Observed cgroup memory.swap.max | 0 |
| Isolated cgroup required | True |
| Useful isolated cgroup observed | True |
| Initial published segments | 4 |
| Initial compaction records | 1,100,000 |
| Optional periodic compaction interval | disabled |
| Updates applied | 1,000 |
| Deletes applied and verified absent | 1,000 |
| Final published segments | 4 |
| Final compaction records | 1,100,000 |
| Synced seed time | 102.869 s |
| Synced writes/sec | 10693 |
| Initial compaction and object publication | 165.704 s |
| Initial remote reopen and exact scan after SSD cache deletion (engine disposal excluded) | 151.406 s |
| Final remote reopen and exact scan after final cache deletion (engine disposal excluded when mutations run) | 140.368 s |
| Update time | 5.460 s |
| Delete time | 4.906 s |
| Final compaction and object publication | 162.399 s |
| Remote objects under prefix | 8 (924,148,062 bytes) |
| Peak local engine bytes sampled during compaction/reopen | 1,270,509,224 |
| Maximum local engine storage cap | 17,179,869,184 |
| Peak SSD cache bytes sampled during compaction/reopen | 134,217,539 |
| Peak staging bytes sampled during compaction/reopen | 134,217,539 |
| Periodic resource samples | 789 at 1000 ms; `resource-samples.jsonl` |
| SSD cache bytes after final verification | 0 |
| SSD cache bytes after initial remote verification | 0 |
| SSD cache bytes evicted before initial reopen | 0 |
| Total interval | 734.193 s |
| Peak RSS (process working set) | 385,134,592 bytes |
| Idle SSD cache budget (active leases require working space) | 16,777,216 bytes |

## Phase measurements

| Phase | Completed records | Duration (s) | RSS at end (B) | Peak RSS (B) | Local SSD at sample (B) | SSD cache at sample (B) | Staging at sample (B) | RustFS objects | RustFS bytes |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| seed | 1,100,000 | 102.869 | 258,490,368 | 325,857,280 | 797,850,136 | 0 | 0 | unknown | unknown |
| initial-compaction-upload | 1,100,000 | 165.704 | 237,031,424 | 325,857,280 | 923 | 0 | 0 | 4 | 461,989,086 |
| initial-remote-reopen-scan | 1,100,000 | 151.406 | 240,644,096 | 367,915,008 | 529,415,351 | 0 | 0 | 4 | 461,989,086 |
| updates | 1,000 | 5.460 | 240,033,792 | 367,915,008 | 596,848,354 | 0 | 0 | 4 | 461,989,086 |
| deletes | 1,000 | 4.906 | 253,046,784 | 367,915,008 | 597,279,349 | 0 | 0 | 4 | 461,989,086 |
| final-compaction-upload | 1,100,000 | 162.399 | 267,509,760 | 367,915,008 | 926 | 0 | 0 | 8 | 924,148,062 |
| final-remote-reopen-scan | 1,099,000 | 140.368 | 236,347,392 | 385,134,592 | 926 | 0 | 0 | 8 | 924,148,062 |

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