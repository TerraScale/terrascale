# LZ4 fast live-backup comparison — 2026-09-15

Status: retained-evidence comparison. No build or benchmark was run for this
document. The candidate receipt was already verified as `PASS` for its bounded
build, selector checks, and requested measurement. This is a component caller
measurement; it does not establish TerraBase database throughput, service
capacity, WAN capacity, or a production compression default.

## Compared case and boundary

Both rows are `live-backup-record-batch / 4MiB-incompressible / LZ4 /
warm-write`, with five warm observations, 20 operations per observation, and
concurrency 1. The candidate uses LZ4 level 0; the retained HC3 row uses LZ4
level 3. The differing level is an explicit comparison condition, so this table
does not isolate a source-only change.

The candidate receipt calls the boundary `worker-operation-window`: the window
starts at the first worker operation and ends at the last worker operation. The
per-operation percentiles are inside that window. Worker scheduling and worker
join are reported separately. Input and stored bytes are case-level values for
the eight-record batch; the candidate measured 33,554,800 input bytes and
33,555,176 stored bytes.

## Source, build, and environment identity

The candidate is the v2 evidence bundle
[`verification-receipt.md`](../../terrabase/.codex/evidence/compression-lz4-fast-selected-20260915-v2/verification-receipt.md), derived from immutable v1. V1 identifies canonical base commit
`641b71a14b245f645e3db75c9987c5cc2a5c90aa`; the v2 source overlay retained 837
files and changed only
`bench/TerraBase.Benchmarks/CompressionCallerWorkload.cs`. The overlay hash is
`a556bc4415f89e9046327ada99a9a62e1611fa58cf24755c640a1e41e2daf700` (v1
overlay hash `1faa8fcef234aed75ea0851fffb030c7b548feedc9de5a7181de32014d311fa9`).
The candidate Release artifacts are benchmark
`cff8e4835e3c0310eba3322667e1b8c0bfdfdbb577759deb94058d8e5bc7a764`, TerraBase
`1fea3fe2697c1650bdd3aef6c67bf95baf2815a92c98011f9aed8292fb79cd4e`, and
ZoneTree `b8ce66445b44d89e968de2d33fd242d8ce740a217c822d448a11d53dd5e043ae`.
The candidate receipt is from a non-Git evidence directory (`gitCommit=null`);
its `gitDirty=false` field does not prove that a current live worktree was
clean.

The retained HC3 row is the exact filter
`live-backup-record-batch/4MiB-incompressible/LZ4/warm-write/n=5` from
[`warm-median-summary.tsv`](../../terrabase/.codex/evidence/compression-callers-timing-v2-smoke-20260915-sdk10/warm-median-summary.tsv). Its source bundle identifies the same canonical base commit and tree
`125ccc5d61c24f083fa2ccb3120299bb57c44e97`; its overlay hashes are
`CompressionWorkload.cs=72f96d3dfff73860f1bbfa71f125af5ea22887e75d17aabb68aff7103f136cb3`
and `CompressionCallerWorkload.cs=53fd0a50a89da110ac3c86e6905fb2ab44ad91d4a4ec1e7d2c61033d06f9062e`.
Its retained Release artifacts are benchmark
`ec0702f349ce0e1a5688fe944859440e72a5fce5d869f66a6ebf938eb938d135`, TerraBase
`672412a2f49e6a2689f41d4f8c403f040dbff1726c22ffeba3c4417c1f6c2349`, and
ZoneTree `a76823196958fed90e26a07de89d7211293c5a4c2a0652eef7abdf9be7dda258`.

The candidate receipt SHA-256 is
`cc9d39ab30bdf08bec725a559b5807cb8542128c7e223eacd5ee663b5ee687e6` after
copying. The original HC3 full receipt SHA-256 is
`e2110b1859c53579cb017315f66d07fd6314ed0eba2066f1ba81830890be95f0`; the
comparison uses only its exact selected warm row and does not copy the larger
raw receipt.

Both runs used .NET `10.0.12`, NixOS 26.05 (Yarara), X64, Intel Xeon E5-2660
v4, `DOTNET_PROCESSOR_COUNT=2`, and 20,937,965,568 available bytes. The
candidate measurement was pinned to CPUs `6,7`; HC3 was pinned to CPUs `8,9`.
Both used `nice -n 19` and concurrency 1. The host exposed 46 logical
processors. No cgroup CPU or memory cap is recorded in either receipt, so host
memory and two visible benchmark processors are not a production resource
floor.

## Retained median fields

The candidate values are the medians across its five warm observations. HC3
values are the exact retained median row or the corresponding median fields from
the retained HC3 receipt. A dash means the old HC3 receipt did not record that
field; it is not a zero measurement.

| Field | Candidate: LZ4 level 0 | HC3 retained: LZ4 level 3 | Candidate − HC3 | Candidate / HC3 |
| --- | ---: | ---: | ---: | ---: |
| Window elapsed (ms) | 1,979.6591 | 30,377.4794 | −28,397.8203 | 0.065168643 |
| Throughput (ops/s) | 10.10274951 | 0.6584 | +9.44434951 | 15.344394760 |
| p50 operation latency (µs) | 95,727.205 | 1,505,954.75 | −1,410,227.545 | 0.063565791 |
| p95 operation latency (µs) | 135,315.19 | 1,569,561.72 | −1,434,246.53 | 0.086212086 |
| p99 operation latency (µs) | 153,017.132 | 1,622,091.20 | −1,469,074.068 | 0.094333248 |
| Allocated bytes/operation | 135,273,914 | 135,272,355.6 | +1,558.4 | 1.000011520 |
| Allocated bytes/record | 16,909,239.25 | 16,909,044.45 | +194.8 | 1.000011520 |
| Allocated bytes/window | 2,705,478,280 | 2,705,447,112 | +31,168 | 1.000011520 |
| Process CPU ms/window | 2,126.580 | 29,701.902 | −27,575.322 | 0.071597435 |
| Process CPU ms/operation | 106.329 | 1,485.0951 | −1,378.7661 | 0.071597435 |
| Process CPU percent | 54.44511443 | 48.88603727 | +5.55907716 percentage points | 1.113715029 |
| Working set before (bytes) | 564,318,208 | — | — | — |
| Working set after (bytes) | 525,045,760 | — | — | — |
| Working set delta (bytes) | −117,239,808 | — | — | — |
| Peak working set, process lifetime (bytes) | 752,226,304 | — | — | — |
| Worker scheduling (ms) | 1.2962 | 0.6342 | +0.6620 | 2.043834752 |
| Worker join (ms) | 1.8469 | 0.0209 | +1.8260 | 88.368421053 |
| Input bytes | 33,554,800 | 33,554,800 | 0 | 1.000000000 |
| Stored bytes | 33,555,176 | 33,555,176 | 0 | 1.000000000 |

The candidate's actual stored-size arithmetic is
`33,555,176 / 33,554,800 = 1.00001120555`, or 0.00112056% above input. The
retained HC3 case has the same input and stored byte values, so the size ratio is
unchanged. LZ4 did not reduce this incompressible batch.

The speed arithmetic is direct division. For example,
`10.10274951 / 0.6584 = 15.344394760` candidate ops/s per HC3 ops/s, while
`1,505,954.75 / 95,727.205 = 15.731732165` HC3 p50 microseconds per candidate
p50. The latency ratios in the table are candidate/HC3; their reciprocal means
the candidate is about 15.73x faster at p50, 11.60x at p95, and 10.60x at p99.
The candidate window is 15.3448x shorter by the reciprocal
`30,377.4794 / 1,979.6591`.

Allocation is effectively unchanged: `1.000011520` candidate/HC3, or
`+1,558.4 B/op` (`+0.001152%`). CPU time per operation is 13.967x lower by
the reciprocal `1,485.0951 / 106.329`, but process CPU percentage is higher by
5.559 percentage points, or 1.113715x relative. The scheduling and join ratios
are large only because their absolute values are milliseconds while the windows
are about 2.0 s and 30.4 s. They are not the source of the multi-second gap.

## RAM field boundary

The candidate receipt records process-level `processWorkingSetBytesBefore`,
`processWorkingSetBytesAfter`, signed `processWorkingSetDeltaBytes`, and
`processPeakWorkingSetBytes`; the values above are the median fields. The peak
is the process lifetime high-water mark and includes setup, warm-up, GC/JIT, and
other process activity. It is diagnostic, not per-operation memory or a capacity
limit. Working-set before/after samples are outside the latency interval.

The retained HC3 warm row and its receipt schema contain no working-set before,
after, delta, or peak field. HC3 RAM ratios are therefore intentionally
unsupported. The comparison does not use host available memory as a substitute
for process RSS and does not infer a missing HC3 peak.

## Measurement, bottleneck, and confidence

**Measured:** at the declared local caller boundary, level 0 has 10.10274951
ops/s versus the retained level-3 HC3 row's 0.6584 ops/s, with lower operation
percentiles and nearly identical allocation per operation. Both runs use the
same payload shape, batch byte counts, warm sample count, concurrency, runtime,
and two-CPU process setting, but different CPU affinity and compression level.

**Bottleneck location:** the old HC3 window is dominated by the live-backup
record-batch warm-write path under level 3 for this incompressible payload. The
candidate's worker scheduling and join are only 1.2962 ms and 1.8469 ms, so they
cannot explain the roughly 28.4 s window difference. The retained fields do not
separate compression, record batching, checksum, allocation/GC, or lower storage
I/O costs. The precise internal bottleneck is therefore unmeasured.

**Hypothesis:** the large gap is most consistent with compression work under the
two explicit LZ4 levels, with the incompressible input producing no byte saving;
this remains a hypothesis because level and source overlay differ and no profiler
was run. Confidence is high for the arithmetic and declared caller boundary,
medium for the observed level-specific cost difference, and low for a component
hotspot inside the writer.

## Evidence copies and provenance

The following small files were copied byte-for-byte into
[`bench/receipts/20260915-compression-lz4-fast-comparison-v2/`](receipts/20260915-compression-lz4-fast-comparison-v2/):

| File | SHA-256 | Size |
| --- | --- | ---: |
| [`compression-real-receipt.json`](receipts/20260915-compression-lz4-fast-comparison-v2/compression-real-receipt.json) | `cc9d39ab30bdf08bec725a559b5807cb8542128c7e223eacd5ee663b5ee687e6` | 8,009 B |
| [`hc3-comparison.tsv`](receipts/20260915-compression-lz4-fast-comparison-v2/hc3-comparison.tsv) | `cb8929a1605a0171590a0389232d064112466472a1521bcd745fd702c4b6ac2e` | 1,123 B |
| [`median-boundaries.tsv`](receipts/20260915-compression-lz4-fast-comparison-v2/median-boundaries.tsv) | `b9ccd409dc4712b773f62df754cb6e8b73e51ce97a53545105201e58d15e1882` | 840 B |
| [`hc3-warm-median-summary.tsv`](receipts/20260915-compression-lz4-fast-comparison-v2/hc3-warm-median-summary.tsv) | `29e869218c89d721d3cf5986d7b14062952284acb00554541fe6b407fd949978` | 7,140 B |
| [`verification-receipt.md`](receipts/20260915-compression-lz4-fast-comparison-v2/verification-receipt.md) | `0ddf350f1b566612d0189e769b24b48cc18ef17cee92aa4d228ab57786605a84` | 3,744 B |

The candidate verification recorded the following commands and results before
this document was written:

```text
taskset -c 6,7 nice -n 19 env DOTNET_PROCESSOR_COUNT=2 dotnet build bench/TerraBase.Benchmarks/TerraBase.Benchmarks.csproj --configuration Release -m:1 /p:BuildInParallel=false /p:EnableNETAnalyzers=true /p:RunAnalyzers=true /p:RunAnalyzersDuringBuild=true /p:AnalysisLevel=latest-recommended /p:Nullable=enable /p:TreatWarningsAsErrors=true
=> exit 0; 0 warnings; 0 errors; benchmark artifact cff8e4835e3c0310eba3322667e1b8c0bfdfdbb577759deb94058d8e5bc7a764

taskset -c 6,7 nice -n 19 env DOTNET_PROCESSOR_COUNT=2 dotnet bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll --workload compression --caller live-backup --profile 4MiB-incompressible --method lz4 --phase warm-write --samples 5 --warmup 5 --operations 20 --concurrency 1 --seed 1337 --level 0 --output measurement
=> exit 0; receipt complete; one case; five warm observations
```

Unknown and empty profile selector checks both exited 2 before execution. The
HC3 row was retained from the prior source-bound smoke evidence and was not
rerun. No raw large artifact or raw-sample bundle was copied into `bench`.

## Decision boundary and next measurement

This table does not change the current default compression selection and does
not recommend a production default from one level-confounded case. It records a
strong local level-specific caller observation for the selected incompressible
batch while preserving the HC3 row and its missing RAM fields.

The smallest useful follow-up is a source-matched matrix on the same pinned
two-CPU envelope with LZ4 levels 0 and 3 in the same process order, at least five
post-warm-up observations per level, plus a profiler or lower-level timing split
for compression, record-batch assembly, checksum, and I/O. Repeat at concurrency
2 only if vertical scaling is required; add independent processes and a declared
cgroup memory/CPU floor before making horizontal or production capacity claims.
