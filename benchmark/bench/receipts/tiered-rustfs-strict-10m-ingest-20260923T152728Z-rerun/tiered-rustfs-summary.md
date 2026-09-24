# RustFS tiered segment ingest-only qualification

| Measurement | Result |
| --- | --- |
| Qualification mode | `ingest-only` (seed writes and remote durability validation) |
| Endpoint | `http://127.0.0.1:19000/` |
| Bucket | `terrabase-tiered` |
| Segment prefix | `tiered-rustfs-strict-10m-ingest-20260923T152728Z-rerun/` |
| Journal prefix | `tiered-rustfs-strict-10m-ingest-20260923T152728Z-rerun-commits/01M37PFSYQXK5340H2C4NV4C8N/journal/v1/us-east-1/frames/` |
| Subject project | `/home/mariogk/Projects/TerraScale/terrabase/src/TerraBase/TerraBase.csproj` |
| Subject commit | `67e35f443565c54eb731d6ea2dd989cb5c87b7be` (tree `69203400f13c7e311cf5fb569a081eeab36e8cbc`, dirty `True`, worktree `04b5f83dbd7aa06823ce6d597a8cbe44891b60b58d2ece329e064f6140f44260`) |
| Benchmark build | `Release`; `11.0.0`; `.NET 11.0.0-rc.1.26425.128` |
| Benchmark assembly | `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/binaries/tiered-rustfs-strict-1m-20260923T152728Z/TerraBase.Benchmarks.dll` (sha256 `2b7c4c92bed0a4555903630139f1031628e006091498121c37d148726897243c`) |
| Engine root | `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/receipts/tiered-rustfs-strict-10m-ingest-20260923T152728Z-rerun/engine` |
| Local WAL bytes after engine close | 2,013,265,920 |
| Durability ordering | Every acknowledged batch was verified in the remote commit journal before its synced local WAL append |
| Seed records requested | 10,000,000 |
| Remote acknowledged physical records | 10,000,001 |
| Remote acknowledged batches | 153 |
| Remote acknowledged encoded record bytes | 4,183,756,852 |
| Remote acknowledgement latency | 307223.175 ms total; 2007.995 ms/batch |
| Remote acknowledgement throughput | 32550 physical records/s |
| Validated remote frontier | 10,000,001 |
| Frontier equals acknowledged physical record count | True |
| Remote frontier validation | 180.813 s |
| Seed write time | 2063.505 s |
| Seed writes/sec | 4846 |
| Segment objects under segment prefix | 0 (0 bytes) |
| Journal objects under journal prefix | 153 (4,223,773,992 bytes) |
| Payload mode | `per-key-incompressible` |
| Payload generator | `sha256-base64url-per-key` |
| Payload seed | 1337 |
| Payload bytes per record | 128 |
| Minimum logical seed payload bytes | 1,280,000,000 |
| Observed cgroup memory.max | 4294967296 |
| Observed cgroup memory.peak | 4295979008 |
| Observed cgroup memory.swap.max | 0 |
| Isolated cgroup required | True |
| Useful isolated cgroup observed | True |
| Peak local engine bytes sampled | 6,860,884,443 |
| Local engine bytes at completion | 6,860,884,431 |
| Peak SSD cache bytes sampled | 0 |
| SSD cache bytes at completion | 0 |
| Peak staging bytes sampled | 0 |
| Minimum free space observed | 350,359,121,920 bytes |
| Free-space floor violation | none |
| Periodic resource samples | 4,271 at 500 ms; `resource-samples.jsonl` |
| Total interval | 2280.712 s |
| Peak RSS (process working set) | 1,335,169,024 bytes |
| Idle SSD cache budget | 16,777,216 bytes |

## Phase measurements

| Phase | Completed records | Duration (s) | RSS at end (B) | Peak RSS (B) | Local SSD at sample (B) | SSD cache at sample (B) | Staging at sample (B) | RustFS objects | RustFS bytes |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| seed | 10,000,000 | 2063.505 | 767,991,808 | 1,335,169,024 | 6,846,170,731 | 0 | 0 | unknown | unknown |
| ingest-only-remote-validation | 10,000,000 | 180.813 | 349,614,080 | 1,335,169,024 | 6,860,884,431 | 0 | 0 | 153 | 4,223,773,992 |

This receipt is scoped to the seed ingestion path. It proves the requested seed writes reached the synced local WAL only after remote commit journal publication and that the complete remote journal chain validated through its final physical frontier after the engine closed.

The following phases were skipped: `seed-root-loss-recovery`, `initial-compaction`, `initial-cache-eviction`, `initial-remote-reopen-scan`, `point-reads`, `updates`, `deletes`, `mutation-root-loss-recovery`, `final-compaction`, `final-cache-eviction`, `final-remote-reopen-scan`, `indexed-queries`, `backup`, `restore`.
Indexed queries and backup/restore require the separate E2eVolumeWorkload qualification.