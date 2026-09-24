# RustFS tiered segment qualification

| Measurement | Result |
| --- | --- |
| Endpoint | `http://127.0.0.1:19000/` |
| Bucket | `terrabase-tiered` |
| Subject project | `/home/mariogk/Projects/TerraScale/terrabase/src/TerraBase/TerraBase.csproj` |
| Subject commit | `67e35f443565c54eb731d6ea2dd989cb5c87b7be` (tree `69203400f13c7e311cf5fb569a081eeab36e8cbc`, dirty `True`, worktree `d97472c59926aa48f005fccfb8ff2cfeefdd28b718877976a8cf89216311c94f`) |
| Benchmark build | `Release`; `11.0.0`; `.NET 11.0.0-rc.1.26425.128` |
| Benchmark assembly | `/home/mariogk/Projects/TerraScale/ts-benchmark/terrabase/TerraBase.Benchmarks/bin/Release/net11.0/TerraBase.Benchmarks.dll` (sha256 `f8bee08cb410c98bb94e86933cb2240d3f8b4d2a99aaca35176281536b2094a0`) |
| Source provenance | `run-receipt.json` (emitted by the benchmark runner) |
| Durability | Remote commit journal before synced WAL, one device barrier per batch of 8192 |
| Remote acknowledged commit journal | 16 objects (249,170,163 bytes) under `tiered-rustfs-remote-journal-100k-20260923T141346Z-commits/01M379Z0AS4WFP2XR7GAVDYBN8/journal/v1/us-east-1/frames/` |
| Remote acknowledgement batches | 16 (102,004 records; 248,760,355 encoded record bytes) |
| Remote acknowledgement latency | 14519.856 ms total; 907.491 ms/batch |
| Remote acknowledgement throughput | 7025 records/s; 17132425 encoded bytes/s |
| Full local-root loss before initial compaction | verified exact scan, point reads, tombstone boundaries, and retry status in 95.094 s |
| Full local-root loss after mutations | verified exact scan, point reads, tombstone boundaries, and retry status in 99.801 s |
| Seed records (exact bodies and versions verified) | 100,000 |
| Final live records (exact bodies and versions verified) | 99,000 |
| Payload mode | `per-key-incompressible` |
| Payload generator | `sha256-base64url-per-key` |
| Payload seed | 1337 |
| Payload bytes per record | 2,048 |
| Minimum logical seed payload bytes | 204,800,000 |
| Observed cgroup memory.max | 4294967296 |
| Observed cgroup memory.peak | 1408761856 |
| Observed cgroup memory.swap.max | 0 |
| Isolated cgroup required | True |
| Useful isolated cgroup observed | True |
| Initial published segments | 2 |
| Initial compaction records | 100,001 |
| Optional periodic compaction interval | disabled |
| Updates applied | 1,000 |
| Deletes applied and verified absent | 1,000 |
| Final published segments | 2 |
| Final compaction records | 100,004 |
| Synced seed time | 55.872 s |
| Synced writes/sec | 1790 |
| Initial compaction and object publication | 62.281 s |
| Initial remote reopen and exact scan after SSD cache deletion (engine disposal excluded) | 66.640 s |
| Final remote reopen and exact scan after final cache deletion (engine disposal excluded when mutations run) | 20.835 s |
| Update time | 6.381 s |
| Delete time | 1.269 s |
| Final compaction and object publication | 65.835 s |
| Remote objects under prefix | 4 (482,289,372 bytes) |
| Peak local engine bytes sampled during compaction/reopen | 1,152,349,338 |
| Maximum local engine storage cap | unlimited |
| Output filesystem mount | `/` (1,098,436,055,040 bytes total) |
| Minimum free-space floor | 1,073,741,824 bytes |
| Minimum free space observed | 367,367,258,112 bytes |
| Free-space floor violation | none |
| Peak SSD cache bytes sampled during compaction/reopen | 134,216,468 |
| Peak staging bytes sampled during compaction/reopen | 268,435,456 |
| Periodic resource samples | 1,019 at 500 ms; `resource-samples.jsonl` |
| SSD cache bytes after final verification | 0 |
| SSD cache bytes after initial remote verification | 0 |
| SSD cache bytes evicted before initial reopen | 0 |
| Total interval | 489.548 s |
| Peak RSS (process working set) | 764,538,880 bytes |
| Idle SSD cache budget (active leases require working space) | 67,108,864 bytes |

## Phase measurements

| Phase | Completed records | Duration (s) | RSS at end (B) | Peak RSS (B) | Local SSD at sample (B) | SSD cache at sample (B) | Staging at sample (B) | RustFS objects | RustFS bytes |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| seed | 100,000 | 55.872 | 498,614,272 | 748,548,096 | 692,640,415 | 0 | 0 | unknown | unknown |
| seed-root-loss-recovery | 100,000 | 95.094 | 319,995,904 | 748,548,096 | 707,160,177 | 0 | 0 | unknown | unknown |
| initial-compaction-upload | 100,000 | 62.281 | 288,092,160 | 748,548,096 | 446,242,288 | 0 | 0 | 2 | 237,796,793 |
| initial-remote-open | 100,000 | 56.456 | 320,016,384 | 748,548,096 | 439,163,074 | 0 | 0 | 2 | 237,796,793 |
| initial-exact-scan | 100,000 | 10.098 | 275,099,648 | 748,548,096 | 439,191,096 | 0 | 0 | 2 | 237,796,793 |
| initial-remote-reopen-scan | 100,000 | 66.640 | 275,705,856 | 748,548,096 | 439,191,096 | 0 | 0 | 2 | 237,796,793 |
| updates | 1,000 | 6.381 | 317,444,096 | 748,548,096 | 511,795,971 | 0 | 0 | 2 | 237,796,793 |
| deletes | 1,000 | 1.269 | 319,115,264 | 748,548,096 | 511,795,971 | 0 | 0 | 2 | 237,796,793 |
| mutation-root-loss-recovery | 99,000 | 99.801 | 315,420,672 | 764,538,880 | 710,705,262 | 0 | 0 | unknown | unknown |
| final-compaction-upload | 100,000 | 65.835 | 274,661,376 | 764,538,880 | 458,861,178 | 0 | 0 | 4 | 482,289,372 |
| final-remote-open | 100,000 | 10.990 | 248,569,856 | 764,538,880 | 458,848,397 | 0 | 0 | 4 | 482,289,372 |
| final-exact-scan | 99,000 | 9.759 | 248,659,968 | 764,538,880 | 458,848,397 | 0 | 0 | 4 | 482,289,372 |
| final-remote-reopen-scan | 99,000 | 20.835 | 251,138,048 | 764,538,880 | 458,848,355 | 0 | 0 | 4 | 482,289,372 |

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