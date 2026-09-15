# LZ4 fast selected v2 verification receipt

Status: **PASS** for the bounded build, selector rejection checks, and single
requested measurement.

Candidate: `.codex/evidence/compression-lz4-fast-selected-20260915-v2`

Source identity:

- Baseline: immutable v1 at `.codex/evidence/compression-lz4-fast-selected-20260915-v1`.
- The candidate retained 837 source/config files from v1.
- The only source change is `bench/TerraBase.Benchmarks/CompressionCallerWorkload.cs`.
- Its `SelectedMethods` return type is `CompressionMethod[]`.
- v1 hash: `1faa8fcef234aed75ea0851fffb030c7b548feedc9de5a7181de32014d311fa9`.
- Candidate/live hash: `a556bc4415f89e9046327ada99a9a62e1611fa58cf24755c640a1e41e2daf700`.
- The live overlay hash remained unchanged before copy, after copy, and after
  the measurement.

Strict build:

```text
taskset -c 6,7 nice -n 19 env DOTNET_PROCESSOR_COUNT=2 dotnet build bench/TerraBase.Benchmarks/TerraBase.Benchmarks.csproj --configuration Release -m:1 /p:BuildInParallel=false /p:EnableNETAnalyzers=true /p:RunAnalyzers=true /p:RunAnalyzersDuringBuild=true /p:AnalysisLevel=latest-recommended /p:Nullable=enable /p:TreatWarningsAsErrors=true
```

Exit `0`; 0 warnings and 0 errors. Benchmark artifact SHA-256:
`cff8e4835e3c0310eba3322667e1b8c0bfdfdbb577759deb94058d8e5bc7a764`.
ZoneTree assembly SHA-256:
`b8ce66445b44d89e968de2d33fd242d8ce740a217c822d448a11d53dd5e043ae`.

Selector checks:

- Unknown profile: exit `2`; rejected with `Unknown --profile
  'unknown-profile'` before execution.
- Empty profile: exit `2`; rejected with `--profile must not be empty` before
  execution.

Measurement command:

```text
taskset -c 6,7 nice -n 19 env DOTNET_PROCESSOR_COUNT=2 dotnet bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll --workload compression --caller live-backup --profile 4MiB-incompressible --method lz4 --phase warm-write --samples 5 --warmup 5 --operations 20 --concurrency 1 --seed 1337 --level 0 --output measurement
```

Exit `0`. Receipt status is `complete`, methodology is
`real-caller-warm-window-v2`, and exactly one case was emitted:
`live-backup-record-batch / 4MiB-incompressible / LZ4 level 0 / warm-write`.
It contains exactly five warm observations, each with 20 operations at
concurrency 1. Cold writer/reader and warm reader arrays are empty as required
by the phase selector. No None or Zstd cases ran.

Candidate warm medians across the five observations:

| Window ms | Ops/s | p50/p95/p99 µs | Alloc/op | CPU ms/op | CPU % | WS before/after/peak B |
| ---: | ---: | --- | ---: | ---: | ---: | --- |
| 1,979.6591 | 10.10274951 | 95,727.205 / 135,315.19 / 153,017.132 | 135,273,914 | 106.329 | 54.4451 | 564,318,208 / 525,045,760 / 752,226,304 |

Input bytes were 33,554,800 and stored bytes were 33,555,176 (ratio
1.0000112056). The raw receipt, generated summary, per-sample boundaries, and
validation are under `measurement/`.

The direct HC3 comparison uses only the exact retained row
`live-backup-record-batch / 4MiB-incompressible / LZ4 / warm-write / n=5` from
`compression-callers-timing-v2-smoke-20260915-sdk10/warm-median-summary.tsv`.
HC3 medians were 30,377.4794 ms, 0.6584 ops/s, and p50/p95/p99
1,505,954.75 / 1,569,561.72 / 1,622,091.20 µs. The candidate is level 0 while
HC3 is level 3; the direct field comparison is retained in
`hc3-comparison.tsv` and makes no production claim.

Evidence files include `strict-build.log`, `strict-build.exit`,
`strict-build.command`, `artifact-before.txt`, `artifact-after.sha256`,
`immutable-v1-source-manifest.tsv`, `candidate-pre-overlay-manifest.tsv`,
`candidate-post-overlay-manifest.tsv`, `candidate-source-overlay-check.txt`,
`source-post-run-check.txt`, selector logs/exits, and the measurement receipt.
