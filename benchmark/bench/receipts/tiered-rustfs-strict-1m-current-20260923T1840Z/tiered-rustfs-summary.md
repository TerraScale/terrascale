# RustFS tiered segment qualification

| Measurement | Result |
| --- | --- |
| Endpoint | `http://127.0.0.1:19000/` |
| Bucket | `terrabase-tiered` |
| Subject project | `/home/mariogk/Projects/TerraScale/terrabase/src/TerraBase/TerraBase.csproj` |
| Subject commit | `67e35f443565c54eb731d6ea2dd989cb5c87b7be` (tree `69203400f13c7e311cf5fb569a081eeab36e8cbc`, dirty `True`, worktree `5a3435aca02f90bc25ad0fef4f06d5143301f29a6cab5c0a7048c1aa142cb438`) |
| Benchmark build | `Release`; `11.0.0`; `.NET 11.0.0-rc.1.26425.128` |
| Benchmark assembly | `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/binaries/tiered-rustfs-strict-1m-current-20260923T1833Z/TerraBase.Benchmarks.dll` (sha256 `27efd1eaaf31751f1e0f686039f1eba872c13ae4152b15fe3c1b9d37e35b1e96`) |
| Source provenance | `run-receipt.json` (emitted by the benchmark runner) |
| Durability | Remote commit journal before synced WAL, one device barrier per batch of 65536 |
| Remote acknowledged commit journal | 19 objects (428,192,872 bytes) under `tiered-rustfs-strict-1m-current-20260923T1840Z-commits/01M37RX6Q6GB4CCZNPTBXBXV61/journal/v1/us-east-1/frames/` |
| Remote acknowledgement batches | 19 (1,002,004 records; 424,182,728 encoded record bytes) |
| Remote acknowledgement latency | 31547.938 ms total; 1660.418 ms/batch |
| Remote acknowledgement throughput | 31761 records/s; 13445656 encoded bytes/s |
| Full local-root loss before initial compaction | verified exact scan, point reads, tombstone boundaries, and retry status in 255.172 s |
| Full local-root loss after mutations | verified exact scan, point reads, tombstone boundaries, and retry status in 227.440 s |
| Seed records (exact bodies and versions verified) | 1,000,000 |
| Final live records (exact bodies and versions verified) | 999,000 |
| Payload mode | `per-key-incompressible` |
| Payload generator | `sha256-base64url-per-key` |
| Payload seed | 1337 |
| Payload bytes per record | 128 |
| Minimum logical seed payload bytes | 128,000,000 |
| Observed cgroup memory.max | 4294967296 |
| Observed cgroup memory.peak | 1505280000 |
| Observed cgroup memory.swap.max | 0 |
| Isolated cgroup required | True |
| Useful isolated cgroup observed | True |
| Initial published segments | 4 |
| Initial compaction records | 1,000,001 |
| Optional periodic compaction interval | disabled |
| Updates applied | 1,000 |
| Deletes applied and verified absent | 1,000 |
| Final published segments | 4 |
| Final compaction records | 1,000,004 |
| Synced seed time | 128.401 s |
| Synced writes/sec | 7788 |
| Initial compaction and object publication | 178.449 s |
| Initial remote reopen and exact scan after SSD cache deletion (engine disposal excluded) | 248.222 s |
| Final remote reopen and exact scan after final cache deletion (engine disposal excluded when mutations run) | 121.477 s |
| Update time | 3.506 s |
| Delete time | 1.636 s |
| Final compaction and object publication | 182.085 s |
| Remote objects under prefix | 8 (853,771,292 bytes) |
| Peak local engine bytes sampled during compaction/reopen | 1,082,374,544 |
| Maximum local engine storage cap | unlimited |
| Output filesystem mount | `/` (1,098,436,055,040 bytes total) |
| Minimum free-space floor | 107,374,182,400 bytes |
| Minimum free space observed | 342,936,125,440 bytes |
| Free-space floor violation | none |
| Peak SSD cache bytes sampled during compaction/reopen | 134,217,635 |
| Peak staging bytes sampled during compaction/reopen | 201,326,592 |
| Periodic resource samples | 6,489 at 500 ms; `resource-samples.jsonl` |
| SSD cache bytes after final verification | 0 |
| SSD cache bytes after initial remote verification | 0 |
| SSD cache bytes evicted before initial reopen | 0 |
| Total interval | 3279.355 s |
| Peak RSS (process working set) | 970,850,304 bytes |
| Idle SSD cache budget (active leases require working space) | 16,777,216 bytes |

## Phase measurements

| Phase | Completed records | Duration (s) | RSS at end (B) | Peak RSS (B) | Local SSD at sample (B) | SSD cache at sample (B) | Staging at sample (B) | RustFS objects | RustFS bytes |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| seed | 1,000,000 | 128.401 | 551,792,640 | 970,850,304 | 682,362,219 | 0 | 0 | unknown | unknown |
| seed-root-loss-recovery | 1,000,000 | 255.172 | 421,568,512 | 970,850,304 | 682,825,487 | 0 | 0 | unknown | unknown |
| initial-compaction-upload | 1,000,000 | 178.449 | 325,943,296 | 970,850,304 | 489,446,859 | 0 | 0 | 4 | 426,417,242 |
| initial-remote-open | 1,000,000 | 235.088 | 466,771,968 | 970,850,304 | 485,533,711 | 0 | 0 | 4 | 426,417,242 |
| initial-exact-scan | 1,000,000 | 12.388 | 354,684,928 | 970,850,304 | 486,268,854 | 0 | 0 | 4 | 426,417,242 |
| initial-remote-reopen-scan | 1,000,000 | 248.222 | 354,725,888 | 970,850,304 | 486,268,854 | 0 | 0 | 4 | 426,417,242 |
| updates | 1,000 | 3.506 | 350,273,536 | 970,850,304 | 553,377,718 | 0 | 0 | 4 | 426,417,242 |
| deletes | 1,000 | 1.636 | 353,128,448 | 970,850,304 | 554,261,568 | 0 | 0 | 4 | 426,417,242 |
| mutation-root-loss-recovery | 999,000 | 227.440 | 426,487,808 | 970,850,304 | 685,752,912 | 0 | 0 | unknown | unknown |
| final-compaction-upload | 1,000,000 | 182.085 | 377,593,856 | 970,850,304 | 491,931,374 | 0 | 0 | 8 | 853,771,292 |
| final-remote-open | 1,000,000 | 107.053 | 370,946,048 | 970,850,304 | 491,833,169 | 0 | 0 | 8 | 853,771,292 |
| final-exact-scan | 999,000 | 13.452 | 270,090,240 | 970,850,304 | 491,833,169 | 0 | 0 | 8 | 853,771,292 |
| final-remote-reopen-scan | 999,000 | 121.477 | 324,677,632 | 970,850,304 | 491,833,121 | 0 | 0 | 8 | 853,771,292 |

Every acknowledged batch was published to the immutable remote commit journal
before its local WAL append. The two root-loss drills rebuilt the WAL from
that journal and verified exact reads, scans, tombstone boundaries, and retry
receipts before compaction. Segment manifests were committed only after the
RustFS object upload, then the local SSD copies were removed. Reopen therefore
verified the read-through cache refill and segment checksum validation against
RustFS.
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