# RustFS tiered segment qualification

| Measurement | Result |
| --- | --- |
| Endpoint | `http://127.0.0.1:19000/` |
| Bucket | `terrabase-tiered` |
| Subject project | `/home/mariogk/Projects/TerraScale/terrabase/src/TerraBase/TerraBase.csproj` |
| Subject commit | `67e35f443565c54eb731d6ea2dd989cb5c87b7be` (tree `69203400f13c7e311cf5fb569a081eeab36e8cbc`, dirty `True`, worktree `f4cbba5c3615a3d07ef93081f2451f0ea45e7c933646c26f0a083b07562fc759`) |
| Benchmark build | `Release`; `11.0.0`; `.NET 11.0.0-rc.1.26425.128` |
| Benchmark assembly | `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/receipts/tiered-rustfs-part32k-1100k-20260923T071654Z/bin/TerraBase.Benchmarks.dll` (sha256 `8845dff9857eb7ded0521462aa7e28c79dcfe41061f46ae69b4bd6111c91e0ae`) |
| Source provenance | `run-receipt.json` (emitted by the benchmark runner) |
| Durability | Synced WAL, one device barrier per batch of 1000 |
| Seed records (exact bodies and versions verified) | 2,000,000 |
| Final live records (exact bodies and versions verified) | 1,999,000 |
| Payload mode | `per-key-incompressible` |
| Payload generator | `sha256-base64url-per-key` |
| Payload seed | 1337 |
| Payload bytes per record | 128 |
| Minimum logical seed payload bytes | 256,000,000 |
| Observed cgroup memory.max | 4294967296 |
| Observed cgroup memory.peak | 2313854976 |
| Observed cgroup memory.swap.max | 0 |
| Isolated cgroup required | True |
| Useful isolated cgroup observed | True |
| Initial published segments | 7 |
| Initial compaction records | 2,000,000 |
| Optional periodic compaction interval | disabled |
| Updates applied | 1,000 |
| Deletes applied and verified absent | 1,000 |
| Final published segments | 7 |
| Final compaction records | 2,000,000 |
| Synced seed time | 255.875 s |
| Synced writes/sec | 7816 |
| Initial compaction and object publication | 365.948 s |
| Initial remote reopen and exact scan after SSD cache deletion (engine disposal excluded) | 359.025 s |
| Final remote reopen and exact scan after final cache deletion (engine disposal excluded when mutations run) | 362.362 s |
| Update time | 3.789 s |
| Delete time | 0.078 s |
| Final compaction and object publication | 369.390 s |
| Remote objects under prefix | 14 (1,681,948,356 bytes) |
| Peak local engine bytes sampled during compaction/reopen | 2,013,127,935 |
| Maximum local engine storage cap | 17,179,869,184 |
| Peak SSD cache bytes sampled during compaction/reopen | 134,217,609 |
| Peak staging bytes sampled during compaction/reopen | 134,217,609 |
| Periodic resource samples | 1,790 at 1000 ms; `resource-samples.jsonl` |
| SSD cache bytes after final verification | 0 |
| SSD cache bytes after initial remote verification | 0 |
| SSD cache bytes evicted before initial reopen | 0 |
| Total interval | 1718.114 s |
| Peak RSS (process working set) | 463,527,936 bytes |
| Idle SSD cache budget (active leases require working space) | 16,777,216 bytes |

## Phase measurements

| Phase | Completed records | Duration (s) | RSS at end (B) | Peak RSS (B) | Local SSD at sample (B) | SSD cache at sample (B) | Staging at sample (B) | RustFS objects | RustFS bytes |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| seed | 2,000,000 | 255.875 | 274,944,000 | 334,737,408 | 1,365,805,200 | 0 | 0 | unknown | unknown |
| initial-compaction-upload | 2,000,000 | 365.948 | 297,787,392 | 347,303,936 | 1,403 | 0 | 0 | 7 | 840,889,233 |
| initial-remote-reopen-scan | 2,000,000 | 359.025 | 264,155,136 | 369,221,632 | 965,022,939 | 0 | 0 | 7 | 840,889,233 |
| updates | 1,000 | 3.789 | 264,179,712 | 369,221,632 | 1,032,131,803 | 0 | 0 | 7 | 840,889,233 |
| deletes | 1,000 | 0.078 | 265,977,856 | 369,221,632 | 1,032,131,803 | 0 | 0 | 7 | 840,889,233 |
| final-compaction-upload | 2,000,000 | 369.390 | 302,256,128 | 369,221,632 | 1,406 | 0 | 0 | 14 | 1,681,948,356 |
| final-remote-reopen-scan | 1,999,000 | 362.362 | 273,780,736 | 463,527,936 | 1,406 | 0 | 0 | 14 | 1,681,948,356 |

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