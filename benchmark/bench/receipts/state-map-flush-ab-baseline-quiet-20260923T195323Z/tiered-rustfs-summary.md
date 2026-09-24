# RustFS tiered segment ingest-only qualification

| Measurement | Result |
| --- | --- |
| Qualification mode | `ingest-only` (seed writes and remote durability validation) |
| Endpoint | `http://127.0.0.1:19000/` |
| Bucket | `terrabase-tiered` |
| Segment prefix | `state-map-flush-ab-baseline-quiet-20260923T195323Z/` |
| Journal prefix | `state-map-flush-ab-baseline-quiet-20260923T195323Z-commits/01M37X9B0XHJBT89EQ7HWWM67J/journal/v1/us-east-1/frames/` |
| Subject project | `/home/mariogk/Projects/TerraScale/terrabase/src/TerraBase/TerraBase.csproj` |
| Subject commit | `67e35f443565c54eb731d6ea2dd989cb5c87b7be` (tree `69203400f13c7e311cf5fb569a081eeab36e8cbc`, dirty `True`, worktree `e23c4f6194f6e391bf5bc40e53fef9e7f31a9bf35f6bb20bcd50e9184ad96925`) |
| Benchmark build | `Release`; `11.0.0`; `.NET 11.0.0-rc.1.26425.128` |
| Benchmark assembly | `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/binaries/state-map-flush-trace-baseline-20260923T185001Z/TerraBase.Benchmarks.dll` (sha256 `8b0440bccc452feae4f4b3e56baff1d6c0a55e516e489aac0e7c41d2f3762f21`) |
| Engine root | `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/receipts/state-map-flush-ab-baseline-quiet-20260923T195323Z/engine` |
| Local WAL bytes after engine close | 201,326,592 |
| Durability ordering | Every acknowledged batch was verified in the remote commit journal before its synced local WAL append |
| Seed records requested | 1,000,000 |
| Remote acknowledged physical records | 1,000,001 |
| Remote acknowledged batches | 16 |
| Remote acknowledged encoded record bytes | 422,416,973 |
| Remote acknowledgement latency | 32319.245 ms total; 2019.953 ms/batch |
| Remote acknowledgement throughput | 30941 physical records/s |
| Validated remote frontier | 1,000,001 |
| Frontier equals acknowledged physical record count | True |
| Remote frontier validation | 20.507 s |
| Seed write time | 130.781 s |
| Seed writes/sec | 7646 |
| Segment objects under segment prefix | 0 (0 bytes) |
| Journal objects under journal prefix | 16 (426,418,769 bytes) |
| Payload mode | `per-key-incompressible` |
| Payload generator | `sha256-base64url-per-key` |
| Payload seed | 1337 |
| Payload bytes per record | 128 |
| Minimum logical seed payload bytes | 128,000,000 |
| Observed cgroup memory.max | 4294967296 |
| Observed cgroup memory.peak | 1488891904 |
| Observed cgroup memory.swap.max | 0 |
| Isolated cgroup required | True |
| Useful isolated cgroup observed | True |
| Peak local engine bytes sampled | 690,763,868 |
| Local engine bytes at completion | 690,763,856 |
| Peak SSD cache bytes sampled | 0 |
| SSD cache bytes at completion | 0 |
| Peak staging bytes sampled | 0 |
| Minimum free space observed | 340,685,283,328 bytes |
| Free-space floor violation | none |
| Periodic resource samples | 319 at 500 ms; `resource-samples.jsonl` |
| State-map flush trace | disabled |
| Total interval | 155.743 s |
| Peak RSS (process working set) | 1,053,282,304 bytes |
| Idle SSD cache budget | 16,777,216 bytes |

## Phase measurements

| Phase | Completed records | Duration (s) | RSS at end (B) | Peak RSS (B) | Local SSD at sample (B) | SSD cache at sample (B) | Staging at sample (B) | RustFS objects | RustFS bytes |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| seed | 1,000,000 | 130.781 | 646,483,968 | 1,053,282,304 | 682,083,843 | 0 | 0 | unknown | unknown |
| ingest-only-remote-validation | 1,000,000 | 20.507 | 390,586,368 | 1,053,282,304 | 690,763,856 | 0 | 0 | 16 | 426,418,769 |

This receipt is scoped to the seed ingestion path. It proves the requested seed writes reached the synced local WAL only after remote commit journal publication and that the complete remote journal chain validated through its final physical frontier after the engine closed.

The following phases were skipped: `seed-root-loss-recovery`, `initial-compaction`, `initial-cache-eviction`, `initial-remote-reopen-scan`, `point-reads`, `updates`, `deletes`, `mutation-root-loss-recovery`, `final-compaction`, `final-cache-eviction`, `final-remote-reopen-scan`, `indexed-queries`, `backup`, `restore`.
Indexed queries and backup/restore require the separate E2eVolumeWorkload qualification.