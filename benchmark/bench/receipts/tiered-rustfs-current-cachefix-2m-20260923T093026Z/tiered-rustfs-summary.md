# RustFS tiered segment qualification

| Measurement | Result |
| --- | --- |
| Endpoint | `http://127.0.0.1:19000/` |
| Bucket | `terrabase-tiered` |
| Subject project | `/home/mariogk/Projects/TerraScale/terrabase/src/TerraBase/TerraBase.csproj` |
| Subject commit | `67e35f443565c54eb731d6ea2dd989cb5c87b7be` (tree `69203400f13c7e311cf5fb569a081eeab36e8cbc`, dirty `True`, worktree `4261d18c22e84109dfa304e8a8836b4b0f76875920f54096eedde3e82b4cdbbc`) |
| Benchmark build | `Release`; `11.0.0`; `.NET 11.0.0-rc.1.26425.128` |
| Benchmark assembly | `/tmp/terrabase-current-artifacts-20260923T092735Z/bin/TerraBase.Benchmarks/release/TerraBase.Benchmarks.dll` (sha256 `6c66af4d7be540dde31d305e25bb7122a746a90e0185ab7e4e1831f27f53ede3`) |
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
| Observed cgroup memory.peak | 2247114752 |
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
| Synced seed time | 179.830 s |
| Synced writes/sec | 11122 |
| Initial compaction and object publication | 288.175 s |
| Initial remote reopen and exact scan after SSD cache deletion (engine disposal excluded) | 298.970 s |
| Final remote reopen and exact scan after final cache deletion (engine disposal excluded when mutations run) | 261.985 s |
| Update time | 3.461 s |
| Delete time | 0.091 s |
| Final compaction and object publication | 279.000 s |
| Remote objects under prefix | 14 (1,681,948,356 bytes) |
| Peak local engine bytes sampled during compaction/reopen | 2,012,415,120 |
| Maximum local engine storage cap | 17,179,869,184 |
| Peak SSD cache bytes sampled during compaction/reopen | 134,217,609 |
| Peak staging bytes sampled during compaction/reopen | 134,217,609 |
| Periodic resource samples | 1,345 at 1000 ms; `resource-samples.jsonl` |
| SSD cache bytes after final verification | 0 |
| SSD cache bytes after initial remote verification | 0 |
| SSD cache bytes evicted before initial reopen | 0 |
| Total interval | 1313.036 s |
| Peak RSS (process working set) | 303,431,680 bytes |
| Idle SSD cache budget (active leases require working space) | 16,777,216 bytes |

## Phase measurements

| Phase | Completed records | Duration (s) | RSS at end (B) | Peak RSS (B) | Local SSD at sample (B) | SSD cache at sample (B) | Staging at sample (B) | RustFS objects | RustFS bytes |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| seed | 2,000,000 | 179.830 | 205,033,472 | 252,264,448 | 1,365,591,125 | 0 | 0 | unknown | unknown |
| initial-compaction-upload | 2,000,000 | 288.175 | 254,255,104 | 289,501,184 | 1,403 | 0 | 0 | 7 | 840,889,233 |
| initial-remote-open | 2,000,000 | 240.199 | 234,160,128 | 303,431,680 | 964,522,179 | 0 | 0 | 7 | 840,889,233 |
| initial-exact-scan | 2,000,000 | 57.818 | 235,200,512 | 303,431,680 | 964,522,179 | 0 | 0 | 7 | 840,889,233 |
| initial-remote-reopen-scan | 2,000,000 | 298.970 | 264,790,016 | 303,431,680 | 964,522,179 | 0 | 0 | 7 | 840,889,233 |
| updates | 1,000 | 3.461 | 252,010,496 | 303,431,680 | 1,031,631,043 | 0 | 0 | 7 | 840,889,233 |
| deletes | 1,000 | 0.091 | 256,241,664 | 303,431,680 | 1,031,631,043 | 0 | 0 | 7 | 840,889,233 |
| final-compaction-upload | 2,000,000 | 279.000 | 272,818,176 | 303,431,680 | 1,406 | 0 | 0 | 14 | 1,681,948,356 |
| final-remote-open | 2,000,000 | 214.185 | 238,977,024 | 303,431,680 | 961,926,317 | 0 | 0 | 14 | 1,681,948,356 |
| final-exact-scan | 1,999,000 | 46.761 | 239,009,792 | 303,431,680 | 961,926,317 | 0 | 0 | 14 | 1,681,948,356 |
| final-remote-reopen-scan | 1,999,000 | 261.985 | 277,803,008 | 303,431,680 | 1,406 | 0 | 0 | 14 | 1,681,948,356 |

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