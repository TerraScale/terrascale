# RustFS tiered segment qualification

| Measurement | Result |
| --- | --- |
| Endpoint | `http://127.0.0.1:19000/` |
| Bucket | `terrabase-tiered` |
| Subject project | `/home/mariogk/Projects/TerraScale/terrabase/src/TerraBase/TerraBase.csproj` |
| Subject commit | `47b235dcc78240a9eb5330e454a7bf3273ffcba9` (tree `1808109fb3bee474a93829a76c9d17d5d22328c3`, dirty `True`, worktree `3796c76bb7bfeaffb5ee077a4f10151ac24f0a5f080d8b1eb2b91580cadf7cbe`) |
| Benchmark build | `Release`; `11.0.0`; `.NET 11.0.0-rc.1.26425.128` |
| Benchmark assembly | `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/binaries/terrabase-final-source-matched-release-20260924T061758Z/TerraBase.Benchmarks.dll` (sha256 `2b2b6dd9ddf0f7d0a8766dd51b98744ad47c4d13b9858ce055886f4a259f78db`) |
| Source provenance | `run-receipt.json` (emitted by the benchmark runner) |
| Durability | Remote commit journal before synced WAL, one device barrier per batch of 64 |
| Remote acknowledged commit journal | 644 objects (350,719,536 bytes) under `tiered-rustfs-40k-8k-256m-20260924T062106053021948Z-4159684-commits/01M3916BD13M5NZNH9XFAKKJ50/journal/v1/us-east-1/frames/` |
| Remote acknowledgement batches | 644 (41,104 records; 350,482,992 encoded record bytes) |
| Remote acknowledgement latency | 33712.838 ms total; 52.349 ms/batch |
| Remote acknowledgement throughput | 1219 records/s; 10396128 encoded bytes/s |
| Full local-root loss before initial compaction | verified exact scan, point reads, tombstone boundaries, and retry status in 141.977 s |
| Full local-root loss after mutations | verified exact scan, point reads, tombstone boundaries, and retry status in 149.822 s |
| Seed records (exact bodies and versions verified) | 40,000 |
| Final live records (exact bodies and versions verified) | 39,000 |
| Payload mode | `per-key-incompressible` |
| Payload generator | `sha256-base64url-per-key` |
| Payload seed | 1337 |
| Payload bytes per record | 8,192 |
| Minimum logical seed payload bytes | 327,680,000 |
| Observed cgroup memory.max | 268435456 |
| Observed cgroup memory.peak | 282312704 |
| Observed cgroup memory.swap.max | 0 |
| Isolated cgroup required | True |
| Useful isolated cgroup observed | True |
| Initial published segments | 3 |
| Initial compaction records | 40,001 |
| Optional periodic compaction interval | disabled |
| Updates applied | 100 |
| Deletes applied and verified absent | 1,000 |
| Final published segments | 3 |
| Final compaction records | 40,004 |
| Synced seed time | 93.112 s |
| Synced writes/sec | 430 |
| Initial compaction and object publication | 113.703 s |
| Initial remote reopen and exact scan after SSD cache deletion (engine disposal excluded) | 152.336 s |
| Final remote reopen and exact scan after final cache deletion (engine disposal excluded when mutations run) | 59.482 s |
| Update time | 0.945 s |
| Delete time | 5.300 s |
| Final compaction and object publication | 91.347 s |
| Remote objects under prefix | 6 (681,166,690 bytes) |
| Peak local engine bytes sampled during compaction/reopen | 1,738,484,427 |
| Maximum local engine storage cap | 4,294,967,296 |
| Output filesystem mount | `/` (1,098,436,055,040 bytes total) |
| Minimum free-space floor | 107,374,182,400 bytes |
| Minimum free space observed | 312,505,511,936 bytes |
| Free-space floor violation | none |
| Peak SSD cache bytes sampled during compaction/reopen | 134,213,624 |
| Peak staging bytes sampled during compaction/reopen | 402,653,184 |
| Periodic resource samples | 1,807 at 500 ms; `resource-samples.jsonl` |
| State-map flush trace | disabled |
| SSD cache bytes after final verification | 0 |
| SSD cache bytes after initial remote verification | 0 |
| SSD cache bytes evicted before initial reopen | 0 |
| Total interval | 868.739 s |
| Peak RSS (process working set) | 283,598,848 bytes |
| Idle SSD cache budget (active leases require working space) | 4,194,304 bytes |

## Phase measurements

| Phase | Completed records | Duration (s) | RSS at end (B) | Peak RSS (B) | Local SSD at sample (B) | SSD cache at sample (B) | Staging at sample (B) | RustFS objects | RustFS bytes |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| seed | 40,000 | 93.112 | 229,871,616 | 255,410,176 | 1,055,607,612 | 0 | 0 | unknown | unknown |
| seed-root-loss-recovery | 40,000 | 141.977 | 195,801,088 | 278,147,072 | 1,067,577,992 | 0 | 0 | unknown | unknown |
| initial-compaction-upload | 40,000 | 113.703 | 199,725,056 | 278,147,072 | 671,903,081 | 0 | 0 | 3 | 339,850,125 |
| initial-remote-open | 40,000 | 137.677 | 223,694,848 | 283,598,848 | 653,479,919 | 0 | 0 | 3 | 339,850,125 |
| initial-exact-scan | 40,000 | 14.438 | 225,431,552 | 283,598,848 | 665,479,461 | 0 | 0 | 3 | 339,850,125 |
| initial-remote-reopen-scan | 40,000 | 152.336 | 198,021,120 | 283,598,848 | 665,479,461 | 0 | 0 | 3 | 339,850,125 |
| updates | 100 | 0.945 | 200,470,528 | 283,598,848 | 732,588,325 | 0 | 0 | 3 | 339,850,125 |
| deletes | 1,000 | 5.300 | 209,915,904 | 283,598,848 | 755,866,297 | 0 | 0 | 3 | 339,850,125 |
| mutation-root-loss-recovery | 39,000 | 149.822 | 183,623,680 | 283,598,848 | 1,082,515,864 | 0 | 0 | unknown | unknown |
| final-compaction-upload | 40,000 | 91.347 | 192,471,040 | 283,598,848 | 673,899,382 | 0 | 0 | 6 | 681,166,690 |
| final-remote-open | 40,000 | 45.518 | 190,332,928 | 283,598,848 | 673,889,355 | 0 | 0 | 6 | 681,166,690 |
| final-exact-scan | 39,000 | 13.813 | 177,405,952 | 283,598,848 | 673,889,355 | 0 | 0 | 6 | 681,166,690 |
| final-remote-reopen-scan | 39,000 | 59.482 | 171,601,920 | 283,598,848 | 673,889,317 | 0 | 0 | 6 | 681,166,690 |

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