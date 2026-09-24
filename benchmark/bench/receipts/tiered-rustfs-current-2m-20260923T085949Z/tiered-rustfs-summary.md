# RustFS tiered segment qualification

| Measurement | Result |
| --- | --- |
| Endpoint | `http://127.0.0.1:19000/` |
| Bucket | `terrabase-tiered` |
| Subject project | `/home/mariogk/Projects/TerraScale/terrabase/src/TerraBase/TerraBase.csproj` |
| Subject commit | `67e35f443565c54eb731d6ea2dd989cb5c87b7be` (tree `69203400f13c7e311cf5fb569a081eeab36e8cbc`, dirty `True`, worktree `69b23cc3019f11d85435af1f3697e16cc1cc4b49927110b1a35a8e0d26689284`) |
| Benchmark build | `Release`; `11.0.0`; `.NET 11.0.0-rc.1.26425.128` |
| Benchmark assembly | `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/binaries/tiered-current-20260923T085858Z/TerraBase.Benchmarks.dll` (sha256 `35739f36cd01e0d256a48368272107d49f22dbe2b28bca5d350e3914339a21a0`) |
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
| Observed cgroup memory.peak | 2213838848 |
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
| Synced seed time | 252.300 s |
| Synced writes/sec | 7927 |
| Initial compaction and object publication | 351.188 s |
| Initial remote reopen and exact scan after SSD cache deletion (engine disposal excluded) | 361.531 s |
| Final remote reopen and exact scan after final cache deletion (engine disposal excluded when mutations run) | 349.047 s |
| Update time | 3.991 s |
| Delete time | 0.082 s |
| Final compaction and object publication | 351.870 s |
| Remote objects under prefix | 14 (1,681,948,356 bytes) |
| Peak local engine bytes sampled during compaction/reopen | 2,012,380,993 |
| Maximum local engine storage cap | 17,179,869,184 |
| Peak SSD cache bytes sampled during compaction/reopen | 134,217,609 |
| Peak staging bytes sampled during compaction/reopen | 134,217,609 |
| Periodic resource samples | 1,699 at 1000 ms; `resource-samples.jsonl` |
| SSD cache bytes after final verification | 0 |
| SSD cache bytes after initial remote verification | 0 |
| SSD cache bytes evicted before initial reopen | 0 |
| Total interval | 1671.485 s |
| Peak RSS (process working set) | 390,901,760 bytes |
| Idle SSD cache budget (active leases require working space) | 16,777,216 bytes |

## Phase measurements

| Phase | Completed records | Duration (s) | RSS at end (B) | Peak RSS (B) | Local SSD at sample (B) | SSD cache at sample (B) | Staging at sample (B) | RustFS objects | RustFS bytes |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| seed | 2,000,000 | 252.300 | 247,336,960 | 344,059,904 | 1,365,284,182 | 0 | 0 | unknown | unknown |
| initial-compaction-upload | 2,000,000 | 351.188 | 279,015,424 | 344,059,904 | 1,403 | 0 | 0 | 7 | 840,889,233 |
| initial-remote-open | 2,000,000 | 310.636 | 263,458,816 | 381,116,416 | 964,500,201 | 0 | 0 | 7 | 840,889,233 |
| initial-exact-scan | 2,000,000 | 47.286 | 263,385,088 | 381,116,416 | 964,500,201 | 0 | 0 | 7 | 840,889,233 |
| initial-remote-reopen-scan | 2,000,000 | 361.531 | 264,613,888 | 381,116,416 | 964,500,201 | 0 | 0 | 7 | 840,889,233 |
| updates | 1,000 | 3.991 | 264,941,568 | 381,116,416 | 1,031,609,065 | 0 | 0 | 7 | 840,889,233 |
| deletes | 1,000 | 0.082 | 269,185,024 | 381,116,416 | 1,031,609,065 | 0 | 0 | 7 | 840,889,233 |
| final-compaction-upload | 2,000,000 | 351.870 | 305,831,936 | 390,901,760 | 1,406 | 0 | 0 | 14 | 1,681,948,356 |
| final-remote-open | 2,000,000 | 294.953 | 263,864,320 | 390,901,760 | 961,906,578 | 0 | 0 | 14 | 1,681,948,356 |
| final-exact-scan | 1,999,000 | 49.827 | 264,777,728 | 390,901,760 | 961,906,578 | 0 | 0 | 14 | 1,681,948,356 |
| final-remote-reopen-scan | 1,999,000 | 349.047 | 270,499,840 | 390,901,760 | 1,406 | 0 | 0 | 14 | 1,681,948,356 |

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