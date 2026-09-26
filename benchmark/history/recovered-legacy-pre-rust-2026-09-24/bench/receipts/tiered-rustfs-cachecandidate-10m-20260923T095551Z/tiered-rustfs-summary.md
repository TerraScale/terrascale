# RustFS tiered segment qualification

| Measurement | Result |
| --- | --- |
| Endpoint | `http://127.0.0.1:19000/` |
| Bucket | `terrabase-tiered` |
| Subject project | `/home/mariogk/Projects/TerraScale/terrabase/src/TerraBase/TerraBase.csproj` |
| Subject commit | `67e35f443565c54eb731d6ea2dd989cb5c87b7be` (tree `69203400f13c7e311cf5fb569a081eeab36e8cbc`, dirty `True`, worktree `6117fd05f286dde09f610cc450a48ee81af4c73537e2f4ff201dd4304d9a9706`) |
| Benchmark build | `Release`; `11.0.0`; `.NET 11.0.0-rc.1.26425.128` |
| Benchmark assembly | `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/binaries/tiered-cachecandidate-20260923T095551Z/TerraBase.Benchmarks.dll` (sha256 `6c66af4d7be540dde31d305e25bb7122a746a90e0185ab7e4e1831f27f53ede3`) |
| Source provenance | `run-receipt.json` (emitted by the benchmark runner) |
| Durability | Synced WAL, one device barrier per batch of 1000 |
| Seed records (exact bodies and versions verified) | 10,000,000 |
| Final live records (exact bodies and versions verified) | 9,999,000 |
| Payload mode | `per-key-incompressible` |
| Payload generator | `sha256-base64url-per-key` |
| Payload seed | 1337 |
| Payload bytes per record | 128 |
| Minimum logical seed payload bytes | 1,280,000,000 |
| Observed cgroup memory.max | 4294967296 |
| Observed cgroup memory.peak | 4296261632 |
| Observed cgroup memory.swap.max | 0 |
| Isolated cgroup required | True |
| Useful isolated cgroup observed | True |
| Initial published segments | 32 |
| Initial compaction records | 10,000,000 |
| Optional periodic compaction interval | disabled |
| Updates applied | 1,000 |
| Deletes applied and verified absent | 1,000 |
| Final published segments | 32 |
| Final compaction records | 10,000,000 |
| Synced seed time | 1315.050 s |
| Synced writes/sec | 7604 |
| Initial compaction and object publication | 2277.694 s |
| Initial remote reopen and exact scan after SSD cache deletion (engine disposal excluded) | 1951.312 s |
| Final remote reopen and exact scan after final cache deletion (engine disposal excluded when mutations run) | 1960.453 s |
| Update time | 46.274 s |
| Delete time | 3.441 s |
| Final compaction and object publication | 2235.587 s |
| Remote objects under prefix | 64 (8,417,950,806 bytes) |
| Peak local engine bytes sampled during compaction/reopen | 9,540,806,297 |
| Maximum local engine storage cap | 17,179,869,184 |
| Peak SSD cache bytes sampled during compaction/reopen | 134,217,609 |
| Peak staging bytes sampled during compaction/reopen | 134,217,609 |
| Periodic resource samples | 9,345 at 1000 ms; `resource-samples.jsonl` |
| SSD cache bytes after final verification | 0 |
| SSD cache bytes after initial remote verification | 0 |
| SSD cache bytes evicted before initial reopen | 0 |
| Total interval | 9793.655 s |
| Peak RSS (process working set) | 557,187,072 bytes |
| Idle SSD cache budget (active leases require working space) | 16,777,216 bytes |

## Phase measurements

| Phase | Completed records | Duration (s) | RSS at end (B) | Peak RSS (B) | Local SSD at sample (B) | SSD cache at sample (B) | Staging at sample (B) | RustFS objects | RustFS bytes |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| seed | 10,000,000 | 1315.050 | 336,801,792 | 453,312,512 | 6,841,262,169 | 0 | 0 | unknown | unknown |
| initial-compaction-upload | 10,000,000 | 2277.694 | 465,838,080 | 517,468,160 | 5,405 | 0 | 0 | 32 | 4,208,890,458 |
| initial-remote-open | 10,000,000 | 1685.076 | 361,717,760 | 518,901,760 | 4,829,987,972 | 0 | 0 | 32 | 4,208,890,458 |
| initial-exact-scan | 10,000,000 | 261.433 | 349,945,856 | 518,901,760 | 4,829,987,972 | 0 | 0 | 32 | 4,208,890,458 |
| initial-remote-reopen-scan | 10,000,000 | 1951.312 | 345,989,120 | 518,901,760 | 4,829,987,972 | 0 | 0 | 32 | 4,208,890,458 |
| updates | 1,000 | 46.274 | 371,146,752 | 518,901,760 | 4,899,684,867 | 0 | 0 | 32 | 4,208,890,458 |
| deletes | 1,000 | 3.441 | 363,327,488 | 518,901,760 | 4,899,684,867 | 0 | 0 | 32 | 4,208,890,458 |
| final-compaction-upload | 10,000,000 | 2235.587 | 488,984,576 | 557,187,072 | 5,409 | 0 | 0 | 64 | 8,417,950,806 |
| final-remote-open | 10,000,000 | 1715.129 | 383,623,168 | 557,187,072 | 4,825,394,233 | 0 | 0 | 64 | 8,417,950,806 |
| final-exact-scan | 9,999,000 | 240.678 | 353,075,200 | 557,187,072 | 4,825,394,233 | 0 | 0 | 64 | 8,417,950,806 |
| final-remote-reopen-scan | 9,999,000 | 1960.453 | 373,018,624 | 557,187,072 | 5,409 | 0 | 0 | 64 | 8,417,950,806 |

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