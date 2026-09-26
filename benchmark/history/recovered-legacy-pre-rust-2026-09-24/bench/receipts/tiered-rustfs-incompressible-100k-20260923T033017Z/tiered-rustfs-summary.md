# RustFS tiered segment qualification

| Measurement | Result |
| --- | --- |
| Endpoint | `http://127.0.0.1:19000/` |
| Bucket | `terrabase-tiered` |
| Subject project | `/home/mariogk/Projects/TerraScale/terrabase/src/TerraBase/TerraBase.csproj` |
| Subject commit | `67e35f443565c54eb731d6ea2dd989cb5c87b7be` (tree `69203400f13c7e311cf5fb569a081eeab36e8cbc`, dirty `True`, worktree `9e89a6d3f9eae9cbb3af1720cac12d3cf6bf2d7e97a7503265613ba81c35688a`) |
| Benchmark build | `Release`; `11.0.0`; `.NET 11.0.0-rc.1.26425.128` |
| Benchmark assembly | `/home/mariogk/Projects/TerraScale/ts-benchmark/terrabase/TerraBase.Benchmarks/bin/Release/net11.0/TerraBase.Benchmarks.dll` (sha256 `197f4d1f5c6c930430ba5ce256c4943ca3eef138253fbd92db17fba9a1a84960`) |
| Source provenance | `run-receipt.json` (emitted by the benchmark runner) |
| Durability | Synced WAL, one device barrier per batch of 1000 |
| Seed records (exact bodies and versions verified) | 100,000 |
| Final live records (exact bodies and versions verified) | 99,000 |
| Payload mode | `per-key-incompressible` |
| Payload generator | `sha256-base64url-per-key` |
| Payload seed | 1337 |
| Payload bytes per record | 2,048 |
| Minimum logical seed payload bytes | 204,800,000 |
| Observed cgroup memory.max | 4294967296 |
| Observed cgroup memory.peak | 1240461312 |
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
| Synced seed time | 255.232 s |
| Synced writes/sec | 392 |
| Initial compaction and object publication | 72.462 s |
| Initial remote reopen and exact scan after SSD cache deletion | 208.476 s |
| Final remote reopen and exact scan (after final cache deletion when mutations run) | 192.649 s |
| Update time | 22.862 s |
| Delete time | 2.533 s |
| Final compaction and object publication | 181.494 s |
| Remote objects under prefix | 4 (469,867,866 bytes) |
| Peak local engine bytes sampled during compaction/reopen | 1,073,249,049 |
| Maximum local engine storage cap | 8,589,934,592 |
| Peak SSD cache bytes sampled during compaction/reopen | 134,217,132 |
| Peak staging bytes sampled during compaction/reopen | 134,217,132 |
| Periodic resource samples | 1,162 at 1000 ms; `resource-samples.jsonl` |
| SSD cache bytes after final verification | 0 |
| SSD cache bytes after initial remote verification | 0 |
| SSD cache bytes evicted before initial reopen | 0 |
| Total interval | 1108.437 s |
| Peak RSS (process working set) | 454,189,056 bytes |
| Idle SSD cache budget (active leases require working space) | 16,777,216 bytes |

## Phase measurements

| Phase | Completed records | Duration (s) | RSS at end (B) | Peak RSS (B) | Local SSD at sample (B) | SSD cache at sample (B) | Staging at sample (B) | RustFS objects | RustFS bytes |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| seed | 100,000 | 255.232 | 273,534,976 | 423,690,240 | 678,790,185 | 0 | 0 | unknown | unknown |
| initial-compaction-upload | 100,000 | 72.462 | 254,132,224 | 423,690,240 | 583 | 0 | 0 | 2 | 233,888,988 |
| initial-remote-reopen-scan | 100,000 | 208.476 | 207,753,216 | 454,189,056 | 583 | 0 | 0 | 2 | 233,888,988 |
| updates | 1,000 | 22.862 | 305,709,056 | 454,189,056 | 502,036,100 | 0 | 0 | 2 | 233,888,988 |
| deletes | 1,000 | 2.533 | 250,630,144 | 454,189,056 | 502,036,100 | 0 | 0 | 2 | 233,888,988 |
| final-compaction-upload | 100,000 | 181.494 | 273,063,936 | 454,189,056 | 586 | 0 | 0 | 4 | 469,867,866 |
| final-remote-reopen-scan | 99,000 | 192.649 | 272,568,320 | 454,189,056 | 586 | 0 | 0 | 4 | 469,867,866 |

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