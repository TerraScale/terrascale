# State map flush A/B comparison

This comparison covers the matched 1,000,000-record RustFS ingest-only runs. The
ingest-only workload includes append writes, state-map flushes, remote journal
acknowledgement, and remote frontier validation. It does not invoke the direct
compaction workload that exercises `OrderedMerge` and
`StorageCompactionCurrentRecordIndex.Add`; that workload is required before the
deletion-free merge fast path is finally accepted.

| Measurement | Baseline | Candidate | Candidate delta |
| --- | ---: | ---: | ---: |
| Records | 1,000,000 | 1,000,000 | 0 |
| Seed duration | 178.008 s | 129.396 s | -27.3% |
| Seed throughput | 5,618 records/s | 7,728 records/s | +37.6% |
| Remote frontier validation | 18.777 s | 18.916 s | +0.7% |
| Validated remote frontier | 1,000,001 | 1,000,001 | equal |
| Peak process RSS | 933,978,112 B | 769,843,200 B | -17.6% |
| Peak sampled local engine bytes | 690,782,831 B | 690,766,220 B | -0.002% |
| Remote journal objects | 16 | 16 | equal |
| Remote journal bytes | 426,418,784 B | 426,418,781 B | -3 B |
| Flush measurements | 262 | 262 | equal |
| Dropped flush measurements | 0 | 0 | equal |

## Flush trace totals

| Map | Flushes | Baseline merge wait | Candidate merge wait | Baseline total | Candidate total |
| --- | ---: | ---: | ---: | ---: | ---: |
| `physical-records` | 82 | 25,985.5 ms | 12,154.3 ms | 27,062.1 ms | 12,597.2 ms |
| `applied-sequences` | 43 | 5,388.8 ms | 3,451.4 ms | 5,913.4 ms | 3,733.8 ms |
| `watermarks` | 43 | 4,366.5 ms | 2,861.5 ms | 4,769.1 ms | 3,076.7 ms |
| `documents` | 20 | 4,760.2 ms | 2,588.6 ms | 5,061.9 ms | 2,696.7 ms |
| `durable-record-sequences` | 33 | 5,239.5 ms | 2,445.9 ms | 5,544.2 ms | 2,585.2 ms |
| `durable-feed-positions` | 33 | 3,067.1 ms | 1,228.8 ms | 3,320.0 ms | 1,349.1 ms |

The candidate reduced total time for every map with repeated flushes. The
physical-records total fell by 53.5%, and its merge wait fell by 53.2%.

## Durability and correctness

Both receipts completed successfully. Each reports 1,000,001 acknowledged
physical records, a validated remote frontier of 1,000,001, 16 remote journal
objects, and no free-space-floor violation. The candidate therefore preserved
the tested remote acknowledgement and frontier invariants while reducing the
observed seed and flush time.

The candidate's benchmark assembly includes local segment compatibility
validation (manifest length, file bounds, header magic/version, and record
count). Its SHA-256 is
`ae5930effd8d32c1e2e25b6f85a2e8703a2abd7124f2e158e46e138fcefa985a`.
The baseline assembly SHA-256 is
`8b0440bccc452feae4f4b3e56baff1d6c0a55e516e489aac0e7c41d2f3762f21`.
Both assemblies were Release builds targeting `net11.0` with
`.NET 11.0.0-rc.1.26425.128`.

## Resource envelope and limitation

Both runs used a required isolated cgroup with `memory.max=4,294,967,296` and
`memory.swap.max=0`. The baseline cgroup memory peak was 1,082,068,992 B; the
candidate cgroup memory peak was 1,367,691,264 B. Neither run reached its memory
limit or recorded an OOM event. The candidate process RSS was lower even though
its cgroup peak was higher, so these values should be reported separately.

The host was not fully quiet: Roslyn/API activity overlapped the benchmark
window. This makes the absolute seed-duration difference unsuitable as a clean
isolated speed claim. The equal flush-event count, zero dropped measurements,
matching frontier, matching remote object count, and consistent per-map
reductions support retaining the candidate for the next qualification stage.

## Decision

**Keep the candidate fast path for direct-compaction qualification.** The A/B
evidence shows lower state-map flush costs with preserved tested durability and
frontier correctness. This is a provisional decision: run the bounded direct
compaction A/B workload before final acceptance, because this ingest-only run
does not execute the deletion-free `OrderedMerge` path itself.

## Receipts

- Baseline: `state-map-flush-ab-trace-baseline-20260923T195800Z/`
- Candidate: `state-map-flush-ab-trace-candidate-20260923T200100Z/`
- Baseline binary provenance: `bench/binaries/state-map-flush-trace-baseline-20260923T185001Z/`
- Candidate binary provenance: `bench/binaries/state-map-flush-candidate-integrity-fix-20260923T193500Z/`
