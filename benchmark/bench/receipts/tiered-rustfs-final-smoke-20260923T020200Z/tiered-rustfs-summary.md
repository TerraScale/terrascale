# RustFS tiered segment qualification

| Measurement | Result |
| --- | --- |
| Endpoint | `http://127.0.0.1:19000/` |
| Bucket | `terrabase-tiered-final-smoke` |
| Subject project | `/home/mariogk/Projects/TerraScale/terrabase/src/TerraBase/TerraBase.csproj` |
| Subject commit | `67e35f443565c54eb731d6ea2dd989cb5c87b7be` (tree `69203400f13c7e311cf5fb569a081eeab36e8cbc`, dirty `True`, worktree `659419e6f0064bc7d7a225f79dec840c7e1d168ba85848b5524d5d948b734cb2`) |
| Benchmark build | `Release`; `11.0.0`; `.NET 11.0.0-rc.1.26425.128` |
| Benchmark assembly | `/home/mariogk/Projects/TerraScale/ts-benchmark/terrabase/TerraBase.Benchmarks/bin/Release/net11.0/TerraBase.Benchmarks.dll` (sha256 `98622f0e93b8d1b0bbd99d500f2e51dc0181e491e65f73b8826d6ab2421029da`) |
| Source provenance | `run-receipt.json` (emitted by the benchmark runner) |
| Durability | Synced WAL, one device barrier per batch of 20 |
| Seed records (exact bodies and versions verified) | 100 |
| Final live records (exact bodies and versions verified) | 90 |
| Payload mode | `per-key-incompressible` |
| Payload generator | `sha256-base64url-per-key` |
| Payload seed | 1337 |
| Payload bytes per record | 256 |
| Minimum logical seed payload bytes | 25,600 |
| Observed cgroup memory.max | 536870912 |
| Observed cgroup memory.peak | 61939712 |
| Observed cgroup memory.swap.max | 0 |
| Isolated cgroup required | True |
| Useful isolated cgroup observed | True |
| Initial published segments | 1 |
| Initial compaction records | 100 |
| Optional periodic compaction interval | disabled |
| Updates applied | 10 |
| Deletes applied and verified absent | 10 |
| Final published segments | 1 |
| Final compaction records | 100 |
| Synced seed time | 0.320 s |
| Synced writes/sec | 313 |
| Initial compaction and object publication | 0.359 s |
| Initial remote reopen and exact scan after SSD cache deletion | 0.132 s |
| Final remote reopen and exact scan (after final cache deletion when mutations run) | 0.078 s |
| Update time | 0.011 s |
| Delete time | 0.012 s |
| Final compaction and object publication | 0.106 s |
| Remote objects under prefix | 2 (111,838 bytes) |
| Peak local engine bytes sampled during compaction/reopen | 67,177,935 |
| Maximum local engine storage cap | 536,870,912 |
| Peak SSD cache bytes sampled during compaction/reopen | 111,838 |
| Peak staging bytes sampled during compaction/reopen | 54,439 |
| Periodic resource samples | 36 at 100 ms; `resource-samples.jsonl` |
| SSD cache bytes after final verification | 57,399 |
| SSD cache bytes after initial remote verification | 54,439 |
| SSD cache bytes evicted before initial reopen | 54,439 |
| Total interval | 1.621 s |
| Peak RSS (process working set) | 123,265,024 bytes |
| Idle SSD cache budget (active leases require working space) | 16,777,216 bytes |

## Phase measurements

| Phase | Completed records | Duration (s) | RSS at end (B) | Peak RSS (B) | Local SSD at sample (B) | SSD cache at sample (B) | Staging at sample (B) | RustFS objects | RustFS bytes |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| seed | 100 | 0.320 | 110,235,648 | 110,235,648 | 67,121,871 | 0 | 0 | unknown | unknown |
| initial-compaction-upload | 100 | 0.359 | 119,881,728 | 119,881,728 | 54,829 | 54,439 | 0 | 1 | 54,439 |
| initial-remote-reopen-scan | 100 | 0.132 | 120,553,472 | 120,553,472 | 54,829 | 54,439 | 0 | 1 | 54,439 |
| updates | 10 | 0.011 | 122,404,864 | 122,404,864 | 67,176,693 | 54,439 | 0 | 1 | 54,439 |
| deletes | 10 | 0.012 | 122,425,344 | 122,425,344 | 67,176,693 | 54,439 | 0 | 1 | 54,439 |
| final-compaction-upload | 100 | 0.106 | 122,671,104 | 122,671,104 | 57,789 | 57,399 | 0 | 2 | 111,838 |
| final-remote-reopen-scan | 90 | 0.078 | 123,219,968 | 123,219,968 | 57,789 | 57,399 | 0 | 2 | 111,838 |

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