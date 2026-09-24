# RustFS tiered segment qualification

| Measurement | Result |
| --- | --- |
| Endpoint | `http://127.0.0.1:19000/` |
| Bucket | `terrabase-tiered` |
| Subject project | `/home/mariogk/Projects/TerraScale/terrabase/src/TerraBase/TerraBase.csproj` |
| Subject commit | `67e35f443565c54eb731d6ea2dd989cb5c87b7be` (tree `69203400f13c7e311cf5fb569a081eeab36e8cbc`, dirty `True`, worktree `cc183cd9873f32944df492f73b64ef45f05d48893654e905a0fbe070f2afb795`) |
| Benchmark build | `Release`; `11.0.0`; `.NET 11.0.0-rc.1.26425.128` |
| Benchmark assembly | `/home/mariogk/Projects/TerraScale/ts-benchmark/terrabase/TerraBase.Benchmarks/bin/Release/net11.0/TerraBase.Benchmarks.dll` (sha256 `562b2ea0e9adc21fba1439dc8fed9047755503f210180daf5b42260ffca23a3b`) |
| Source provenance | `run-receipt.json` (emitted by the benchmark runner) |
| Durability | Synced WAL, one device barrier per batch of 1000 |
| Seed records (exact bodies and versions verified) | 100,000 |
| Final live records (exact bodies and versions verified) | 99,000 |
| Payload mode | `per-key-incompressible` |
| Payload generator | `sha256-base64url-per-key` |
| Payload seed | 1337 |
| Payload bytes per record | 2,048 |
| Minimum logical seed payload bytes | 204,800,000 |
| Observed cgroup memory.max | 2147483648 |
| Observed cgroup memory.peak | 1325789184 |
| Observed cgroup memory.swap.max | 0 |
| Isolated cgroup required | True |
| Useful isolated cgroup observed | True |
| Initial published segments | 2 |
| Initial compaction records | 100,000 |
| Optional periodic compaction interval | disabled |
| Updates applied | 1,000 |
| Deletes applied and verified absent | 1,000 |
| Final published segments | 2 |
| Final compaction records | 100,000 |
| Synced seed time | 39.720 s |
| Synced writes/sec | 2518 |
| Initial compaction and object publication | 50.923 s |
| Initial remote reopen and exact scan after SSD cache deletion | 54.596 s |
| Final remote reopen and exact scan (after final cache deletion when mutations run) | 89.143 s |
| Update time | 3.216 s |
| Delete time | 0.423 s |
| Final compaction and object publication | 55.890 s |
| Remote objects under prefix | 4 (469,867,866 bytes) |
| Peak local engine bytes sampled during compaction/reopen | 1,043,165,259 |
| Maximum local engine storage cap | 8,589,934,592 |
| Peak SSD cache bytes sampled during compaction/reopen | 134,217,132 |
| Peak staging bytes sampled during compaction/reopen | 134,217,132 |
| Periodic resource samples | 384 at 1000 ms; `resource-samples.jsonl` |
| SSD cache bytes after final verification | 0 |
| SSD cache bytes after initial remote verification | 0 |
| SSD cache bytes evicted before initial reopen | 0 |
| Total interval | 330.118 s |
| Peak RSS (process working set) | 426,000,384 bytes |
| Idle SSD cache budget (active leases require working space) | 16,777,216 bytes |

## Phase measurements

| Phase | Completed records | Duration (s) | RSS at end (B) | Peak RSS (B) | Local SSD at sample (B) | SSD cache at sample (B) | Staging at sample (B) | RustFS objects | RustFS bytes |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| seed | 100,000 | 39.720 | 269,586,432 | 354,697,216 | 678,742,527 | 0 | 0 | unknown | unknown |
| initial-compaction-upload | 100,000 | 50.923 | 273,559,552 | 364,494,848 | 601 | 0 | 0 | 2 | 233,888,988 |
| initial-remote-reopen-scan | 100,000 | 54.596 | 234,721,280 | 365,654,016 | 601 | 0 | 0 | 2 | 233,888,988 |
| updates | 1,000 | 3.216 | 249,815,040 | 412,463,104 | 501,988,605 | 0 | 0 | 2 | 233,888,988 |
| deletes | 1,000 | 0.423 | 258,646,016 | 412,463,104 | 501,988,605 | 0 | 0 | 2 | 233,888,988 |
| final-compaction-upload | 100,000 | 55.890 | 279,588,864 | 412,463,104 | 604 | 0 | 0 | 4 | 469,867,866 |
| final-remote-reopen-scan | 99,000 | 89.143 | 283,906,048 | 426,000,384 | 604 | 0 | 0 | 4 | 469,867,866 |

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