# FullTextSearch performance evidence

This is a focused local Release benchmark for the cloned
`TerraBase.ZoneTree.FullTextSearch` read/write paths. The source tree was
already dirty from the parent task; the source revision was
`7846bce1fab7a12db26b704fd7d37a365f72c932`. The benchmark ran on .NET
10.0.12 / SDK 10.0.401, NixOS 26.05 x64, Intel Xeon E5-2660 v4, 46 logical
processors, 20.9 GB available memory, workstation GC, and interactive latency
mode.

The workload uses 1,000 seeded records, eight generated tokens per record,
seed `1337`, five measured samples, 100 warm-up operations, and 200 measured
operations per sample. `AddRecord` runs against the mutable index before the
explicit flush. `SimpleSearch("token1 token2", respectTokenOrder: false)` runs
after the flush and returns 2,100 records. The command was:

```text
nice -n 19 dotnet bench/TerraBase.FullTextBenchmarks/bin/Release/net10.0/TerraBase.FullTextBenchmarks.dll --records 1000 --tokens 8 --samples 5 --warmup 100 --operations 200 --seed 1337
```

## A/B result

The baseline uses the original `EnumerateSlices(...).ToArray()` loops. The
candidate streams the same slices directly; tokenizer construction, hashing,
token order, and analyzer settings are unchanged.

| Compression | Operation | Baseline source | Candidate source | Baseline p50 / p95 / p99 (µs) | Candidate p50 / p95 / p99 (µs) | Baseline allocated B/op | Candidate allocated B/op | Allocation change |
| --- | --- | --- | --- | ---: | ---: | ---: | ---: | ---: |
| Zstd | AddRecord | `009341452470c1ceffd7f1e7c0e563a77ef5a52a5b1c8233095b5dfdfa9ad96f` | `bec760ab139a258c128685090a2f25b23946c21d3618fd3837b2b6ab77d0b131` | 40.250 / 59.920 / 80.865 | 24.550 / 35.910 / 46.116 | 577.0 | 489.1 | -15.2% |
| None | AddRecord | `009341452470c1ceffd7f1e7c0e563a77ef5a52a5b1c8233095b5dfdfa9ad96f` | `bec760ab139a258c128685090a2f25b23946c21d3618fd3837b2b6ab77d0b131` | 36.350 / 53.615 / 63.242 | 35.600 / 50.500 / 84.101 | 577.1 | 489.0 | -15.3% |
| Zstd | SimpleSearch | `009341452470c1ceffd7f1e7c0e563a77ef5a52a5b1c8233095b5dfdfa9ad96f` | `bec760ab139a258c128685090a2f25b23946c21d3618fd3837b2b6ab77d0b131` | 7,068.300 / 10,780.440 / 13,265.892 | 6,634.350 / 11,067.515 / 12,316.315 | 1,484,096.6 | 1,484,096.6 | 0.0% |
| None | SimpleSearch | `009341452470c1ceffd7f1e7c0e563a77ef5a52a5b1c8233095b5dfdfa9ad96f` | `bec760ab139a258c128685090a2f25b23946c21d3618fd3837b2b6ab77d0b131` | 5,148.750 / 7,553.595 / 11,295.360 | 6,235.700 / 10,557.505 / 14,869.239 | 1,484,096.6 | 1,484,096.6 | 0.0% |

The candidate benchmark executable was
`cd2a791161c0929fe3d3f25e90c491aa343cf411fa2d6fa0579524acddac2004`; the
baseline executable was `5f77bbae2c517bbd73d6da06fa449b881a2e94acd0ca732de3b87a84555d10b2`.

## Compression and resource observations

The FullTextSearch ZoneTree disk segments use the existing default Zstd
level (`Zstd0`). With the same corpus and the same explicit `EvictToDisk`
boundary, the candidate run measured 15,527 bytes for Zstd and 145,165 bytes
for `CompressionMethod.None`, an 89.3% reduction in segment bytes. Flush time
was 127.557 ms for Zstd and 129.137 ms for None in the combined run; this
sample is too small to claim a latency difference. Compression is therefore a
storage-size trade-off, not a demonstrated CPU bottleneck here.

The measured read bottleneck is candidate-set processing: the broad two-token
query allocates about 1.48 MB per operation and returns 2,100 records. The
allocation is unchanged by the write-only patch, so the search algorithm was
left intact. The smallest next measurement is a query-shape sweep using the
same corpus (single selective token, two-token AND with low selectivity, and
NOT/OR) while recording result cardinality and allocations; that distinguishes
posting traversal from result materialization before any read optimization.

## Validation

```text
nice -n 19 dotnet build bench/TerraBase.FullTextBenchmarks/TerraBase.FullTextBenchmarks.csproj -c Release --no-restore
nice -n 19 dotnet test tests/TerraBase.ZoneTree.FullTextSearch.Tests/TerraBase.ZoneTree.FullTextSearch.UnitTests.csproj -c Release --no-restore
```

The build passed with zero warnings and errors. The FullTextSearch unit suite
passed 72/72 tests. No analyzer settings or compression defaults were changed.
