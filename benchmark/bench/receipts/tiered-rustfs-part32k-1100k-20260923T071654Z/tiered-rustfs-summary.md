# RustFS tiered segment qualification

| Measurement | Result |
| --- | --- |
| Endpoint | `http://127.0.0.1:19000/` |
| Bucket | `terrabase-tiered` |
| Subject project | `/home/mariogk/Projects/TerraScale/terrabase/src/TerraBase/TerraBase.csproj` |
| Subject commit | `67e35f443565c54eb731d6ea2dd989cb5c87b7be` (tree `69203400f13c7e311cf5fb569a081eeab36e8cbc`, dirty `True`, worktree `029dd1e567c7b7857f0c594e256e27d50a8d7ff14bdef55b49b910e03cf874ed`) |
| Benchmark build | `Release`; `11.0.0`; `.NET 11.0.0-rc.1.26425.128` |
| Benchmark assembly | `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/receipts/tiered-rustfs-part32k-1100k-20260923T071654Z/bin/TerraBase.Benchmarks.dll` (sha256 `8845dff9857eb7ded0521462aa7e28c79dcfe41061f46ae69b4bd6111c91e0ae`) |
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
| Observed cgroup memory.peak | 1471598592 |
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
| Synced seed time | 93.802 s |
| Synced writes/sec | 11727 |
| Initial compaction and object publication | 144.783 s |
| Initial remote reopen and exact scan after SSD cache deletion (engine disposal excluded) | 158.005 s |
| Final remote reopen and exact scan after final cache deletion (engine disposal excluded when mutations run) | 153.102 s |
| Update time | 3.602 s |
| Delete time | 1.100 s |
| Final compaction and object publication | 152.797 s |
| Remote objects under prefix | 8 (924,148,062 bytes) |
| Peak local engine bytes sampled during compaction/reopen | 1,270,492,989 |
| Maximum local engine storage cap | 17,179,869,184 |
| Peak SSD cache bytes sampled during compaction/reopen | 134,217,539 |
| Peak staging bytes sampled during compaction/reopen | 134,217,539 |
| Periodic resource samples | 761 at 1000 ms; `resource-samples.jsonl` |
| SSD cache bytes after final verification | 0 |
| SSD cache bytes after initial remote verification | 0 |
| SSD cache bytes evicted before initial reopen | 0 |
| Total interval | 708.367 s |
| Peak RSS (process working set) | 395,309,056 bytes |
| Idle SSD cache budget (active leases require working space) | 16,777,216 bytes |

## Phase measurements

| Phase | Completed records | Duration (s) | RSS at end (B) | Peak RSS (B) | Local SSD at sample (B) | SSD cache at sample (B) | Staging at sample (B) | RustFS objects | RustFS bytes |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| seed | 1,100,000 | 93.802 | 245,833,728 | 331,276,288 | 797,842,139 | 0 | 0 | unknown | unknown |
| initial-compaction-upload | 1,100,000 | 144.783 | 265,031,680 | 346,812,416 | 923 | 0 | 0 | 4 | 461,989,086 |
| initial-remote-reopen-scan | 1,100,000 | 158.005 | 277,696,512 | 377,794,560 | 529,407,354 | 0 | 0 | 4 | 461,989,086 |
| updates | 1,000 | 3.602 | 284,672,000 | 377,794,560 | 596,840,477 | 0 | 0 | 4 | 461,989,086 |
| deletes | 1,000 | 1.100 | 286,375,936 | 377,794,560 | 597,270,878 | 0 | 0 | 4 | 461,989,086 |
| final-compaction-upload | 1,100,000 | 152.797 | 287,539,200 | 377,794,560 | 926 | 0 | 0 | 8 | 924,148,062 |
| final-remote-reopen-scan | 1,099,000 | 153.102 | 295,796,736 | 395,309,056 | 926 | 0 | 0 | 8 | 924,148,062 |

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