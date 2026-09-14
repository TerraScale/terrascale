# Larger-than-memory storage workload

`LargerThanMemoryWorkload` is a bounded workload for the benchmark runner. It
opens the real `StorageEngine` by default and can use the `ZoneTreeStorageProvider`
when the runner selects `ZoneTree`. Both providers retain their normal durable
write settings: the workload does not disable WAL sync, bypass a provider, or
turn a write into an in-memory operation.

The runner must supply an explicit memory cap and must run the process inside a
matching cgroup when it wants to claim that the fixture is larger than the
available memory. The workload checks that the estimated logical dataset is
larger than the configured cap, but that check is not a cgroup limit and does
not prove that the process stayed below the cap.

## Bounded starting profile

Use this profile for the first approved run:

| Field | Value |
| --- | ---: |
| Backend | `StorageEngine` |
| Logical dataset | 32,768 documents × 2,048-byte payload |
| Estimated logical bytes | about 67 MiB, excluding provider metadata and indexes |
| Target memory cap | 256 MiB (requires an externally enforced cgroup limit) |
| Samples | 5 post-warmup samples |
| Operations per warm/cold phase | 2,000 |
| Sustained writes per sample | 2,000 |
| Warmup operations | 1,000 |
| Concurrency | 1 |
| Key seed | 1,337 |
| Zipf exponent | 1.2 |

The host inspected for this workload had 33,614,626,816 bytes of RAM,
18,411,892,736 bytes available, and 398 GiB free on the XFS data filesystem.
The benchmark shell did not expose a root cgroup `memory.max` or `cpu.max`, so
those values are host observations only. Do not call a host run a capped
larger-than-memory result. Start a larger profile only after the parent runner
has approved its memory and disk budget.

## Phases and output

Each sample records a warm-cache operation phase, a close/reopen interval, a
cold-cache operation phase, one configured distribution/mix shift, sustained
writes, and StorageEngine compaction. The shift list alternates uniform and
Zipf key selection and includes a Zipf write-only phase. ZoneTree reports the
compaction phase as skipped because its provider contract does not expose
`StorageEngine.CompactAsync`.

The runner writes one JSON object per sample to
`raw-samples-larger-than-memory-<backend>.jsonl`. Each operation phase includes
its deterministic seed metadata, operation count, distribution, p50/p95/p99,
nanosecond latency histogram, throughput, process CPU time/percent, RSS before
and after, allocated bytes, returned/logical bytes, total storage bytes, WAL
bytes, log-byte delta, and write amplification. The reopen phase records the
close plus durable open interval. The raw file is the evidence path; a summary
must link it rather than reconstructing pooled percentiles.

`StorageEngine` write amplification is reported as the observed WAL byte delta
divided by logical UTF-8 payload bytes in that phase. Compaction has no logical
operation denominator and therefore reports no amplification ratio. Missing
filesystem measurements are `null`, never zero.

## Runner interface

The parent runner should map its command-line values into
`LargerThanMemoryWorkload.Configuration`, call:

```csharp
var result = await LargerThanMemoryWorkload.RunAsync(
    configuration,
    options.OutputDirectory,
    cancellationToken);
```

The returned `RunResult` can be included in the normal receipt. A failed run
must retain its raw path and error. The workload should be invoked with
`nice -n 19` and a Release build for any performance receipt. A Debug build,
an uncapped host run, and this implementation's default profile are not speed
claims.
