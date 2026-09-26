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

## Payload integrity mode

The default payload is the historical repeated `x` string. For a physical-size
qualification, set `TERRABASE_LARGER_MEMORY_PAYLOAD_MODE=per-key-incompressible`.
This generates a fixed length base64url body from SHA-256 blocks keyed by the
configured seed and document ID. Every key therefore receives a reproducible
payload while adjacent records do not share a compressible body. The result
receipt records `payloadMode`, `payloadGenerator`, `seededStorageBytes`, and
`seededStorageExceedsMemoryCap`; use those fields to distinguish logical size
from the measured post-seed filesystem footprint.

The environment override is optional and leaves existing invocations on the
repeated payload mode. The runner also accepts
`--payload-mode repeated|per-key-incompressible`; generated Markdown exposes the
effective mode and generator from the detailed result.

For a 256 MiB zero-swap cgroup run, build Release first and then use a private
output directory. This command is a proposal for parent approval; it is not
started by the fixture validation:

```bash
mkdir -p /tmp/terrabase-larger-memory-256m-incompressible
systemd-run --user --scope --collect --quiet \
  -p MemoryMax=256M -p MemorySwapMax=0 -p OOMPolicy=stop \
  env TERRABASE_LARGER_MEMORY_PAYLOAD_MODE=per-key-incompressible \
  nice -n 19 dotnet run --project terrabase/TerraBase.Benchmarks/TerraBase.Benchmarks.csproj \
    -c Release --no-build -- \
    --workload larger-memory --backend storage-engine \
    --memory-cap-bytes 268435456 --dataset-size 150000 --payload-size 2048 \
    --samples 5 --operations 2000 --writes 2000 --warmup 1000 \
    --output /tmp/terrabase-larger-memory-256m-incompressible
```

If the host does not provide a user systemd scope, run the same process under
an equivalent cgroup v2 launcher that reports `memory.max=268435456` and
`memory.swap.max=0` in the receipt evidence.

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

## Sample count and interpretation

The default remains five post-warmup samples. The runner accepts
`--samples 1` for a constrained qualification run when completing the full
workload is more important than estimating a latency distribution. One sample
still runs every configured correctness, durable reopen, resource, and storage
phase, so it can establish that the workload completed and that the data and
disk checks passed. It must be reported as a correctness/resource check rather
than a stable latency benchmark. Use five or more samples when latency
statistics are part of the qualification; the sample loop and its calculations
are unchanged for every requested count.

## Bounded delete qualification

Pass `--deletes N` to exercise a deterministic set of at most one million
document deletes after the final sample's sustained upserts. The set is spread
through the ordered key space so the run checks tombstones across the dataset
without retaining a dataset-sized delete map. The default is zero, preserving
existing runs.

The `StorageEngine` backend measures the delete phase, verifies every deleted
key is absent and every surviving body is exact, then cold reopens the engine
and repeats both checks. The detailed receipt records `requestedCount`,
`deletedCount`, survivor and absence verification counts, and delete plus cold
reopen elapsed milliseconds under each sample's `deletes` object. The generated
Markdown includes the same counts and timings in its Delete qualification
table. ZoneTree accepts the option for command compatibility and records the
qualification as skipped because this gate targets StorageEngine.

## Phases and output

Each sample records a warm-cache operation phase, a close/reopen interval, a
cold-cache operation phase, one configured distribution/mix shift, sustained
writes, and StorageEngine compaction. When `--deletes` is positive, the final
sample also records the bounded delete phase and its post-delete cold reopen.
The shift list alternates uniform and
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
