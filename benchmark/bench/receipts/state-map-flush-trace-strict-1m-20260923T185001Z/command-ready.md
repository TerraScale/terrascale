# State map flush trace strict 1M ingest baseline

Status: prepared; this command has not been launched.

| Item | Value |
| --- | --- |
| Frozen benchmark binary | `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/binaries/state-map-flush-trace-baseline-20260923T185001Z/TerraBase.Benchmarks.dll` |
| Benchmark assembly SHA-256 | `8b0440bccc452feae4f4b3e56baff1d6c0a55e516e489aac0e7c41d2f3762f21` |
| TerraBase commit | `67e35f443565c54eb731d6ea2dd989cb5c87b7be` |
| TerraBase tree | `69203400f13c7e311cf5fb569a081eeab36e8cbc` |
| TerraBase working tree digest | `928df81e9f40ced6bc92fff87b4e5619a5a5194d00a8fdd8eebce958562257c4` |
| Benchmark repository commit | `0359e5c1efb3ed38d9e3c8255e39334e0b88f0d7` |
| Benchmark working tree digest | `4665fe7005133bc95fc873c1b21a62a92d2b8de29ad0a89f49939cc2cc6c949c` |
| Required memory envelope | 4 GiB cgroup, zero swap, isolated from the active strict run |
| Output directory | `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/receipts/state-map-flush-trace-strict-1m-20260923T185001Z` |
| Trace output | `state-map-flush-trace.json` (bounded aggregate by map) |
| S3 credentials | Read from `TERRABASE_S3_ACCESS_KEY` and `TERRABASE_S3_SECRET_KEY`; never place them in the command or receipt |

## Ready command

Run only after the current strict RustFS 1M process has exited and its evidence is preserved. The caller must provide the two credential environment variables from the local credentials file and enforce the isolated cgroup envelope.

```text
dotnet /home/mariogk/Projects/TerraScale/ts-benchmark/bench/binaries/state-map-flush-trace-baseline-20260923T185001Z/TerraBase.Benchmarks.dll --workload tiered-segment --records 1000000 --batch 65536 --payload-bytes 128 --payload-mode per-key-incompressible --updates 0 --deletes 0 --point-reads 0 --checkpoint-records 250000 --cache-bytes 16777216 --min-free-space-bytes 107374182400 --resource-sample-ms 500 --ingest-only --require-isolated-cgroup --state-map-flush-trace --s3-endpoint http://127.0.0.1:19000 --s3-bucket terrabase-tiered --prefix state-map-flush-trace-strict-1m-20260923T185001Z --output /home/mariogk/Projects/TerraScale/ts-benchmark/bench/receipts/state-map-flush-trace-strict-1m-20260923T185001Z
```

The run should produce `tiered-rustfs-receipt.json`, `tiered-rustfs-summary.md`, `run-receipt.json`, and `state-map-flush-trace.json`. The trace is diagnostic evidence for map flush and merge timing; it does not change storage behavior or the durability contract.
