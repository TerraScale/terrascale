# RustFS tiered segment qualification

| Measurement | Result |
| --- | --- |
| Endpoint | `http://127.0.0.1:19000/` |
| Bucket | `terrabase-tiered` |
| Subject project | `/home/mariogk/Projects/TerraScale/terrabase/src/TerraBase/TerraBase.csproj` |
| Subject commit | `67e35f443565c54eb731d6ea2dd989cb5c87b7be` (tree `69203400f13c7e311cf5fb569a081eeab36e8cbc`, dirty `True`, worktree `a132bd6ab8be3805af67ffbe99f1fe3a3921c833a22036bdb519b64a023d8fca`) |
| Benchmark build | `Release`; `11.0.0`; `.NET 11.0.0-rc.1.26425.128` |
| Benchmark assembly | `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/binaries/state-map-flush-candidate-integrity-fix-20260923T193500Z/TerraBase.Benchmarks.dll` (sha256 `ae5930effd8d32c1e2e25b6f85a2e8703a2abd7124f2e158e46e138fcefa985a`) |
| Source provenance | `run-receipt.json` (emitted by the benchmark runner) |
| Durability | Remote commit journal before synced WAL, one device barrier per batch of 65536 |
| Remote acknowledged commit journal | 19 objects (428,192,838 bytes) under `tiered-rustfs-strict-1m-integrity-fix-20260923T2004Z-commits/01M37XYYT1KJYJBH48ZHXNBCPG/journal/v1/us-east-1/frames/` |
| Remote acknowledgement batches | 19 (1,002,004 records; 424,182,694 encoded record bytes) |
| Remote acknowledgement latency | 32259.712 ms total; 1697.880 ms/batch |
| Remote acknowledgement throughput | 31061 records/s; 13148992 encoded bytes/s |
| Full local-root loss before initial compaction | verified exact scan, point reads, tombstone boundaries, and retry status in 248.736 s |
| Full local-root loss after mutations | verified exact scan, point reads, tombstone boundaries, and retry status in 235.850 s |
| Seed records (exact bodies and versions verified) | 1,000,000 |
| Final live records (exact bodies and versions verified) | 999,000 |
| Payload mode | `per-key-incompressible` |
| Payload generator | `sha256-base64url-per-key` |
| Payload seed | 1337 |
| Payload bytes per record | 128 |
| Minimum logical seed payload bytes | 128,000,000 |
| Observed cgroup memory.max | 4294967296 |
| Observed cgroup memory.peak | 1552277504 |
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
| Synced seed time | 118.079 s |
| Synced writes/sec | 8469 |
| Initial compaction and object publication | 143.849 s |
| Initial remote reopen and exact scan after SSD cache deletion (engine disposal excluded) | 236.105 s |
| Final remote reopen and exact scan after final cache deletion (engine disposal excluded when mutations run) | 114.662 s |
| Update time | 4.715 s |
| Delete time | 1.677 s |
| Final compaction and object publication | 139.500 s |
| Remote objects under prefix | 8 (853,771,212 bytes) |
| Peak local engine bytes sampled during compaction/reopen | 1,153,389,915 |
| Maximum local engine storage cap | unlimited |
| Output filesystem mount | `/` (1,098,436,055,040 bytes total) |
| Minimum free-space floor | 107,374,182,400 bytes |
| Minimum free space observed | 335,196,655,616 bytes |
| Free-space floor violation | none |
| Peak SSD cache bytes sampled during compaction/reopen | 134,217,589 |
| Peak staging bytes sampled during compaction/reopen | 201,326,592 |
| Periodic resource samples | 2,661 at 500 ms; `resource-samples.jsonl` |
| State-map flush trace | disabled |
| SSD cache bytes after final verification | 0 |
| SSD cache bytes after initial remote verification | 0 |
| SSD cache bytes evicted before initial reopen | 0 |
| Total interval | 1331.472 s |
| Peak RSS (process working set) | 751,915,008 bytes |
| Idle SSD cache budget (active leases require working space) | 16,777,216 bytes |

## Phase measurements

| Phase | Completed records | Duration (s) | RSS at end (B) | Peak RSS (B) | Local SSD at sample (B) | SSD cache at sample (B) | Staging at sample (B) | RustFS objects | RustFS bytes |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| seed | 1,000,000 | 118.079 | 477,593,600 | 751,915,008 | 682,083,083 | 0 | 0 | unknown | unknown |
| seed-root-loss-recovery | 1,000,000 | 248.736 | 442,482,688 | 751,915,008 | 682,819,149 | 0 | 0 | unknown | unknown |
| initial-compaction-upload | 1,000,000 | 143.849 | 426,872,832 | 751,915,008 | 489,437,400 | 0 | 0 | 4 | 426,417,196 |
| initial-remote-open | 1,000,000 | 220.537 | 399,818,752 | 751,915,008 | 485,525,947 | 0 | 0 | 4 | 426,417,196 |
| initial-exact-scan | 1,000,000 | 14.535 | 373,534,720 | 751,915,008 | 486,261,097 | 0 | 0 | 4 | 426,417,196 |
| initial-remote-reopen-scan | 1,000,000 | 236.105 | 381,050,880 | 751,915,008 | 486,261,097 | 0 | 0 | 4 | 426,417,196 |
| updates | 1,000 | 4.715 | 363,909,120 | 751,915,008 | 553,369,961 | 0 | 0 | 4 | 426,417,196 |
| deletes | 1,000 | 1.677 | 400,830,464 | 751,915,008 | 554,253,950 | 0 | 0 | 4 | 426,417,196 |
| mutation-root-loss-recovery | 999,000 | 235.850 | 514,457,600 | 751,915,008 | 685,744,502 | 0 | 0 | unknown | unknown |
| final-compaction-upload | 1,000,000 | 139.500 | 491,147,264 | 751,915,008 | 491,923,487 | 0 | 0 | 8 | 853,771,212 |
| final-remote-open | 1,000,000 | 100.099 | 444,944,384 | 751,915,008 | 491,825,282 | 0 | 0 | 8 | 853,771,212 |
| final-exact-scan | 999,000 | 13.755 | 332,709,888 | 751,915,008 | 491,825,282 | 0 | 0 | 8 | 853,771,212 |
| final-remote-reopen-scan | 999,000 | 114.662 | 396,103,680 | 751,915,008 | 491,825,234 | 0 | 0 | 8 | 853,771,212 |

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