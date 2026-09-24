# RustFS tiered segment qualification

| Measurement | Result |
| --- | --- |
| Endpoint | `http://127.0.0.1:19000/` |
| Bucket | `terrabase-tiered-final-debug` |
| Subject project | `/home/mariogk/Projects/TerraScale/terrabase/src/TerraBase/TerraBase.csproj` |
| Subject commit | `67e35f443565c54eb731d6ea2dd989cb5c87b7be` (tree `69203400f13c7e311cf5fb569a081eeab36e8cbc`, dirty `True`, worktree `44b4dfcb054ea1a841ed395bde5d31a2a39d73a1aa6e698e9ddc08c1afb2196f`) |
| Benchmark build | `Release`; `11.0.0`; `.NET 11.0.0-rc.1.26425.128` |
| Benchmark assembly | `/home/mariogk/Projects/TerraScale/ts-benchmark/terrabase/TerraBase.Benchmarks/bin/Release/net11.0/TerraBase.Benchmarks.dll` (sha256 `23fc578d1fc8820d346d3d0c26d7fff96d311a83eda472b7b13d9ffacaf974ed`) |
| Source provenance | `run-receipt.json` (emitted by the benchmark runner) |
| Durability | Synced WAL, one device barrier per batch of 5 |
| Seed records (exact bodies and versions verified) | 10 |
| Final live records (exact bodies and versions verified) | 9 |
| Payload mode | `per-key-incompressible` |
| Payload generator | `sha256-base64url-per-key` |
| Payload seed | 1337 |
| Payload bytes per record | 64 |
| Minimum logical seed payload bytes | 640 |
| Observed cgroup memory.max | max |
| Observed cgroup memory.peak | 23258378240 |
| Observed cgroup memory.swap.max | max |
| Isolated cgroup required | False |
| Useful isolated cgroup observed | False |
| Initial published segments | 1 |
| Initial compaction records | 10 |
| Optional periodic compaction interval | disabled |
| Updates applied | 1 |
| Deletes applied and verified absent | 1 |
| Final published segments | 1 |
| Final compaction records | 10 |
| Synced seed time | 0.300 s |
| Synced writes/sec | 33 |
| Initial compaction and object publication | 0.352 s |
| Initial remote reopen and exact scan after SSD cache deletion | 0.115 s |
| Final remote reopen and exact scan (after final cache deletion when mutations run) | 0.071 s |
| Update time | 0.007 s |
| Delete time | 0.009 s |
| Final compaction and object publication | 0.075 s |
| Remote objects under prefix | 2 (7,222 bytes) |
| Peak local engine bytes sampled during compaction/reopen | 67,127,292 |
| Maximum local engine storage cap | 536,870,912 |
| Peak SSD cache bytes sampled during compaction/reopen | 7,222 |
| Peak staging bytes sampled during compaction/reopen | 3,559 |
| Periodic resource samples | 34 at 100 ms; `resource-samples.jsonl` |
| SSD cache bytes after final verification | 3,663 |
| SSD cache bytes after initial remote verification | 3,559 |
| SSD cache bytes evicted before initial reopen | 3,559 |
| Total interval | 1.551 s |
| Peak RSS (process working set) | 123,899,904 bytes |
| Idle SSD cache budget (active leases require working space) | 16,777,216 bytes |

## Phase measurements

| Phase | Completed records | Duration (s) | RSS at end (B) | Peak RSS (B) | Local SSD at sample (B) | SSD cache at sample (B) | Staging at sample (B) | RustFS objects | RustFS bytes |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| seed | 10 | 0.300 | 110,968,832 | 110,968,832 | 67,121,871 | 0 | 0 | unknown | unknown |
| initial-compaction-upload | 10 | 0.352 | 118,927,360 | 118,927,360 | 3,948 | 3,559 | 0 | 1 | 3,559 |
| initial-remote-reopen-scan | 10 | 0.115 | 119,455,744 | 119,455,744 | 3,948 | 3,559 | 0 | 1 | 3,559 |
| updates | 1 | 0.007 | 119,566,336 | 119,566,336 | 67,125,812 | 3,559 | 0 | 1 | 3,559 |
| deletes | 1 | 0.009 | 119,590,912 | 119,590,912 | 67,125,812 | 3,559 | 0 | 1 | 3,559 |
| final-compaction-upload | 10 | 0.075 | 121,208,832 | 121,208,832 | 4,052 | 3,663 | 0 | 2 | 7,222 |
| final-remote-reopen-scan | 9 | 0.071 | 123,850,752 | 123,850,752 | 4,052 | 3,663 | 0 | 2 | 7,222 |

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