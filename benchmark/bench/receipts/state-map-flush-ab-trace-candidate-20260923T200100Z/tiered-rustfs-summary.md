# RustFS tiered segment ingest-only qualification

| Measurement | Result |
| --- | --- |
| Qualification mode | `ingest-only` (seed writes and remote durability validation) |
| Endpoint | `http://127.0.0.1:19000/` |
| Bucket | `terrabase-tiered` |
| Segment prefix | `state-map-flush-ab-trace-candidate-20260923T200100Z/` |
| Journal prefix | `state-map-flush-ab-trace-candidate-20260923T200100Z-commits/01M37XQT7R3SVY597XDE6885VA/journal/v1/us-east-1/frames/` |
| Subject project | `/home/mariogk/Projects/TerraScale/terrabase/src/TerraBase/TerraBase.csproj` |
| Subject commit | `67e35f443565c54eb731d6ea2dd989cb5c87b7be` (tree `69203400f13c7e311cf5fb569a081eeab36e8cbc`, dirty `True`, worktree `21b9219e50ac790824ef424d4fdb54715ba1816d15a2d84eac8bb45acab4efc6`) |
| Benchmark build | `Release`; `11.0.0`; `.NET 11.0.0-rc.1.26425.128` |
| Benchmark assembly | `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/binaries/state-map-flush-candidate-integrity-fix-20260923T193500Z/TerraBase.Benchmarks.dll` (sha256 `ae5930effd8d32c1e2e25b6f85a2e8703a2abd7124f2e158e46e138fcefa985a`) |
| Engine root | `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/receipts/state-map-flush-ab-trace-candidate-20260923T200100Z/engine` |
| Local WAL bytes after engine close | 201,326,592 |
| Durability ordering | Every acknowledged batch was verified in the remote commit journal before its synced local WAL append |
| Seed records requested | 1,000,000 |
| Remote acknowledged physical records | 1,000,001 |
| Remote acknowledged batches | 16 |
| Remote acknowledged encoded record bytes | 422,416,985 |
| Remote acknowledgement latency | 35007.768 ms total; 2187.986 ms/batch |
| Remote acknowledgement throughput | 28565 physical records/s |
| Validated remote frontier | 1,000,001 |
| Frontier equals acknowledged physical record count | True |
| Remote frontier validation | 18.916 s |
| Seed write time | 129.396 s |
| Seed writes/sec | 7728 |
| Segment objects under segment prefix | 0 (0 bytes) |
| Journal objects under journal prefix | 16 (426,418,781 bytes) |
| Payload mode | `per-key-incompressible` |
| Payload generator | `sha256-base64url-per-key` |
| Payload seed | 1337 |
| Payload bytes per record | 128 |
| Minimum logical seed payload bytes | 128,000,000 |
| Observed cgroup memory.max | 4294967296 |
| Observed cgroup memory.peak | 1367691264 |
| Observed cgroup memory.swap.max | 0 |
| Isolated cgroup required | True |
| Useful isolated cgroup observed | True |
| Peak local engine bytes sampled | 690,766,220 |
| Local engine bytes at completion | 690,766,220 |
| Peak SSD cache bytes sampled | 0 |
| SSD cache bytes at completion | 0 |
| Peak staging bytes sampled | 0 |
| Minimum free space observed | 338,215,624,704 bytes |
| Free-space floor violation | none |
| Periodic resource samples | 310 at 500 ms; `resource-samples.jsonl` |
| State-map flush trace | state-map-flush-trace.json |
| Total interval | 152.420 s |
| Peak RSS (process working set) | 769,843,200 bytes |
| Idle SSD cache budget | 16,777,216 bytes |

## Phase measurements

| Phase | Completed records | Duration (s) | RSS at end (B) | Peak RSS (B) | Local SSD at sample (B) | SSD cache at sample (B) | Staging at sample (B) | RustFS objects | RustFS bytes |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| seed | 1,000,000 | 129.396 | 511,950,848 | 769,843,200 | 682,088,595 | 0 | 0 | unknown | unknown |
| ingest-only-remote-validation | 1,000,000 | 18.916 | 442,404,864 | 769,843,200 | 690,766,220 | 0 | 0 | 16 | 426,418,781 |

This receipt is scoped to the seed ingestion path. It proves the requested seed writes reached the synced local WAL only after remote commit journal publication and that the complete remote journal chain validated through its final physical frontier after the engine closed.

The following phases were skipped: `seed-root-loss-recovery`, `initial-compaction`, `initial-cache-eviction`, `initial-remote-reopen-scan`, `point-reads`, `updates`, `deletes`, `mutation-root-loss-recovery`, `final-compaction`, `final-cache-eviction`, `final-remote-reopen-scan`, `indexed-queries`, `backup`, `restore`.
Indexed queries and backup/restore require the separate E2eVolumeWorkload qualification.