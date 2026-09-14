# Source matched performance measurements

This report records measurements made against the benchmark artifact built from
commit `9e84cc83827d1603bb307d23c6722761f1d029b4`. The worktree was dirty
(`git status --porcelain` was non-empty), so these numbers are tied to the
recorded artifact rather than to a clean release claim.

The benchmark project was built with:

```text
nice -n 19 dotnet build bench/TerraBase.Benchmarks/TerraBase.Benchmarks.csproj --configuration Release --no-restore /p:BuildProjectReferences=false
```

The focused benchmark build passed with zero warnings and errors. The runner
artifact SHA-256 for the provider and recovery batch is
`9a4fd71e58057954c8b0840d2688c61bb89eb043a13fd5078dd3494e53962e3b`.
The executed dependency copies were `TerraBase.dll`
`8af72765d5716dfc4fbb01afe2abe5623f700fdd5429567a39380f44ce614be9` and
`TerraBase.ZoneTree.dll`
`0b6cbd5a58e7c30c2ab8a9f60b7851cc432b67697f6eb0678e2fcc613076a92b`.
The normal dependency build was also attempted against the moving dirty
checkout and did not produce a current release receipt. The retained failure
includes source/analyzer errors in the storage transaction, restore metadata,
replication contract, and transport slices; see the current build evidence in
`../.codex/evidence/operation-context-terrabase-build-20260914.log`. This does
not invalidate the retained focused benchmark artifact, but it does mean these
numbers must not be presented as a clean solution qualification.

The host was NixOS 26.05 x64 with .NET 10.0.12, an Intel Xeon E5-2660 v4,
46 logical processors, 20,937,965,568 bytes available to the runtime, and
interactive non-server GC. No cgroup `memory.max` or `cpu.max` was visible.
RSS is therefore a process observation, and the larger-than-memory runs below
are configured-cap exercises rather than host-memory-capacity claims.

All result rows below use five post-warmup samples. Percentiles and scalar
metrics are medians of the five per-sample values; raw samples remain beside
each receipt. Blank write-amplification values mean that the provider did not
expose a comparable durable WAL byte boundary.

## Provider throughput and latency

The point workload used a deterministic seed of 1337. Point reads used 1,000
documents with 256-byte JSON payloads and 1,000 measured operations per
sample. Point writes used 100 documents with 128-byte payloads and 100 measured
operations per sample. StorageEngine and ZoneTree used both warm and cold
durable phases. In-memory storage has no reopen boundary and was measured warm
only.

| Provider | Workload | Phase | Ops/s | p50 (µs) | p95 (µs) | p99 (µs) | Allocated B/op | CPU % | RSS after (B) | Bytes written | Write amplification | Receipt |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| Memory | point read | warm | 109,536.22 | 3.99 | 19.09 | 33.42 | 1,167.18 | 1.49 | 69,705,728 | — | — | [receipt](receipts/20260914-source-matched-v3-memory-point-read-warm/receipt.json) |
| StorageEngine | point read | warm | 177,600.97 | 4.06 | 7.72 | 17.15 | 1,172.60 | 2.37 | 89,034,752 | 0 | — | [receipt](receipts/20260914-source-matched-v3-storage-engine-point-read-both/receipt.json) |
| StorageEngine | point read | cold | 64,969.24 | 3.35 | 14.71 | 31.89 | 1,173.24 | 0.99 | 108,838,912 | 0 | — | [receipt](receipts/20260914-source-matched-v3-storage-engine-point-read-both/receipt.json) |
| StorageEngine | point write | warm | 365.28 | 2,052.22 | 6,044.68 | 9,041.93 | 79,466.24 | 0.94 | 79,626,240 | 134,314 | 9.46 | [receipt](receipts/20260914-source-matched-v3-storage-engine-point-write-both/receipt.json) |
| StorageEngine | point write | cold | 253.20 | 2,824.70 | 8,775.58 | 11,658.83 | 77,780.24 | 1.52 | 86,941,696 | 134,303 | 9.46 | [receipt](receipts/20260914-source-matched-v3-storage-engine-point-write-both/receipt.json) |
| ZoneTree | point read | warm | 42,394.44 | 16.43 | 27.56 | 62.30 | 9,101.06 | 2.04 | 89,300,992 | — | — | [receipt](receipts/20260914-source-matched-v3-zonetree-point-read-both/receipt.json) |
| ZoneTree | point read | cold | 38,511.16 | 19.16 | 29.70 | 43.63 | 9,101.73 | 3.92 | 95,080,448 | — | — | [receipt](receipts/20260914-source-matched-v3-zonetree-point-read-both/receipt.json) |
| ZoneTree | point write | warm | 62.37 | 14,689.84 | 26,542.42 | 36,898.19 | 13,863.68 | 0.37 | 84,230,144 | — | — | [receipt](receipts/20260914-source-matched-v3-zonetree-point-write-both/receipt.json) |
| ZoneTree | point write | cold | 55.35 | 14,822.02 | 30,605.66 | 36,429.44 | 13,897.84 | 0.23 | 90,218,496 | — | — | [receipt](receipts/20260914-source-matched-v3-zonetree-point-write-both/receipt.json) |

StorageEngine point writes are the measured bottleneck in this matrix: they
are roughly two orders of magnitude slower than in-memory point reads, have
millisecond tails, and emit 134 KB for 14.2 KB of logical payload in each
sample (9.46x observed WAL amplification). This locates the cost at the
durable write boundary exposed by `StorageEngine`; it does not identify which
internal stage dominates.

## Vertical scaling check

The same warm StorageEngine point-read workload was run at concurrency 1, 2,
and 4 in the earlier `a1da5903…` artifact batch from the same commit. A
repeat of the concurrency-4 command under the current `9a4fd71e…` artifact
did not produce a receipt before the shared host stalled, so the earlier
completed sweep is retained as a separately identified measurement.

| Concurrency | Ops/s | p50 (µs) | p95 (µs) | p99 (µs) | Allocated B/op | CPU % | RSS after (B) | Receipt |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| 1 | 173,412.41 | 4.45 | 7.18 | 18.03 | 1,172.50 | 2.47 | 88,023,040 | [receipt](receipts/20260914-source-matched-v2-storage-engine-point-read-both/receipt.json) |
| 2 | 220,589.86 | 6.94 | 10.47 | 24.85 | 1,170.86 | 4.67 | 88,879,104 | [receipt](receipts/20260914-source-matched-v2-storage-engine-point-read-concurrency-2/receipt.json) |
| 4 | 168,947.46 | 12.29 | 73.21 | 143.18 | 1,177.94 | 6.61 | 88,645,632 | [receipt](receipts/20260914-source-matched-v2-storage-engine-point-read-concurrency-4/receipt.json) |

Two workers improved median throughput by about 27% over one; four workers
fell below the one-worker throughput while p99 rose to 143.18 µs. This is
evidence of a concurrency ceiling in this operation/provider/host fixture.
The measurement does not distinguish lock contention, storage scheduling, or
CPU placement.

## Replication lag and restore

The recovery runner uses explicit local boundaries. Replication is source
apply, source delta export, and destination apply in one process. Restore is
manifest-chain verification and activation through an in-memory object store
and in-memory target. Neither row is a network, multi-region, filesystem, or
object-store RTO claim.

| Boundary | Ops/s | p50 | p95 | p99 | Allocated B/op | CPU % | RSS after (B) | Backlog / entries | Receipt |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| Replication lag | 12,248.15 | 67.30 µs | 133.00 µs | 205.80 µs | 6,452.40 | 2.30 | 69,169,152 | 0 backlog ops | [receipt](receipts/20260914-source-matched-v3-recovery/recovery-receipt.json) |
| Restore time | 319.78 restores/s | 2.93 ms | 5.19 ms | 7.39 ms | 516,785.20 | 2.59 | 81,612,800 | 100 log entries | [receipt](receipts/20260914-source-matched-v3-recovery/recovery-receipt.json) |

## Subscription fan-out

The horizontal subscriber-count matrix was executed for 1, 10, 100, and 1,000
subscribers. Every case is `unsupported`, with no latency, allocation, CPU,
RSS, or egress value emitted. The CLI cannot supply the durable commit to
reactive source callback required by this boundary, so it is not valid to
replace these missing values with a synthetic in-memory publication result.

[Unsupported receipt](receipts/20260914-source-matched-v3-subscriptions/subscriptions-receipt.json)

The smallest next fan-out measurement is an embedding or service harness that
commits one real durable transaction, captures its `ReactiveChangeSet`, and
publishes it exactly once while retaining per-subscriber frame timestamps and
egress bytes at counts 1, 10, 100, and 1,000.

The separate durable commit-to-feed harness now has a complete retained
receipt. It commits through `StorageEngine.ApplyBatchAsync`, waits for
`StorageChangeFeedWorker`, and delivers the shared maintenance result to
responsive subscribers:

| Subscribers | Responsive | Delivered frames | Egress bytes | p50 (µs) | p95 (µs) | p99 (µs) | Allocated B/frame | Receipt |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| 1 | 1 | 5 | 3,248 | 5,291.90 | 7,815.96 | 8,287.91 | 161,332.80 | [receipt](receipts/20260914-durable-subscriptions-final/subscriptions-receipt.json) |
| 10 | 9 | 45 | 30,268 | 3,951.90 | 9,651.24 | 10,048.64 | 49,809.07 | [receipt](receipts/20260914-durable-subscriptions-final/subscriptions-receipt.json) |
| 100 | 99 | 495 | 326,386 | 4,688.10 | 11,617.01 | 11,700.33 | 7,112.63 | [receipt](receipts/20260914-durable-subscriptions-final/subscriptions-receipt.json) |
| 1,000 | 999 | 4,995 | 3,285,388 | 12,575.30 | 18,038.34 | 18,450.61 | 3,488.53 | [receipt](receipts/20260914-durable-subscriptions-final/subscriptions-receipt.json) |

The durable receipt is local commit-to-hub evidence on a dirty tree with no
enforced cgroup envelope. It does not qualify public HTTP/CDN delivery,
multi-Region fan-out, or capacity planning.

## Native restore and authenticated loopback

The separate native restore receipt ran five full-plus-incremental PITR cases
against local filesystem object-store and restore directories. Restore and
activation had p50/p95 of 68.820/180.010 ms; activation plus a fresh
`StorageEngine` reopen had p50/p95 of 76.270/189.350 ms. See
[native-backup-summary.md](receipts/native-backup-20260914/native-backup-summary.md).
This is a one-host filesystem fixture, not a production RTO, remote object-store,
crash, or multi-Region recovery claim.

The retained MagicOnion loopback receipt used two HTTPS/mTLS hosts, three
32-publication samples, duplicate/reverse delivery, and separate durable
directories. Every sample sent 64 pushes and received 64 durable acknowledgements;
the destination finished at durable/applied frontier 32 after 62 deferred
responses. See [summary.md](receipts/20260914-magic-onion-final/summary.md).
The run does not qualify WAN latency, packet loss, crash recovery, partition
healing, or saturation. Its retained receipt has no environment identity, so
it is not an admitted source-bound performance claim until rerun with the
current metadata-producing workload.

## Larger-than-memory exercise

The runner requires an explicit memory cap and checks that the estimated logical
dataset exceeds it. A configured `memoryCapBytes` threshold is not itself a
cgroup limit. The receipts below separate unbound fixture observations from a
run with a measured cgroup limit; neither proves a dataset larger than host
memory.

| Backend | Rows × payload | Logical dataset (B) | Configured cap (B) | Samples | Status | Receipt |
| --- | ---: | ---: | ---: | ---: | --- | --- |
| StorageEngine | 8,192 × 2,048 B | 17,006,592 | 8,388,608 | 5 | complete, cgroup unavailable | [receipt](receipts/20260914-source-matched-larger-memory-storage-engine-8192/larger-memory-receipt.json) |
| ZoneTree | 8,192 × 2,048 B | 17,006,592 | 4,194,304 | 5 | complete, cgroup unavailable | [receipt](receipts/20260914-source-matched-larger-memory-zonetree-8192/larger-memory-receipt.json) |

The two receipts above have no embedded commit or artifact identity and belong
to an earlier benchmark artifact batch. Keep them as unbound fixture
observations only; their generated summaries contain phase-level p50/p95/p99,
allocations, CPU, RSS, WAL deltas, and write amplification where available:

- [StorageEngine summary](receipts/20260914-source-matched-larger-memory-storage-engine-8192/larger-memory-summary.md)
- [ZoneTree summary](receipts/20260914-source-matched-larger-memory-zonetree-8192/larger-memory-summary.md)

The retained 32,768-row StorageEngine receipt has status `failed` and no
measured samples. A separate retry left an empty raw-sample file and has no
receipt; no successful 32,768-row result exists.

A current source-bound StorageEngine run under the `cgroup256` directory also
failed before the first sample with `OutOfMemoryException` during WAL recovery;
the receipt records no cgroup limit and must not be treated as capacity proof:
[receipt](receipts/20260914-cgroup256-larger-memory-storage-engine-8192-run1/larger-memory-receipt.json).

An explicit cgroup run was completed for ZoneTree under a user systemd scope
with `MemoryMax=256M` (`memory.max=268,435,456` bytes). Its 8,192-row logical
fixture is 17,006,592 bytes and the CLI logical cap is 4,194,304 bytes. The
fixture exceeds the CLI cap but remains smaller than the enforced cgroup limit,
so this is a cgroup-constrained capacity exercise rather than evidence of a
dataset larger than cgroup memory. The receipt records the launcher, cgroup
limit, cold/warm phases, latency percentiles, allocations, and RSS; the scope
probe is retained at [cgroup256-scope-probe.log](../.codex/evidence/cgroup256-scope-probe.log).

- [ZoneTree cgroup receipt](receipts/20260914-cgroup256-larger-memory-zonetree-8192-run3/larger-memory-receipt.json)
- [ZoneTree cgroup summary](receipts/20260914-cgroup256-larger-memory-zonetree-8192-run3/larger-memory-summary.md)

## Bottleneck, confidence, and next measurement

The highest-confidence measured bottleneck is the StorageEngine durable point
write boundary: the result is repeated across five samples in both warm and
cold phases, includes a matching WAL byte delta, and has millisecond p95/p99
latency. Confidence is medium for the local fixture and low for a production
multi-region claim because the host has no enforced resource floor and the
worktree is dirty.

The vertical sweep provides medium-confidence evidence of a read concurrency
ceiling at four workers. It is a symptom-level result; profiling is required
before attributing it to a lock or scheduler. The repeat stall is itself an
unresolved environment limit, not a zero or a fabricated throughput value.

The smallest next measurements are:

1. Run the StorageEngine point-write workload with the same fixture under a
   known cgroup CPU and memory envelope, then repeat with WAL sync mode and
   filesystem I/O counters retained. This separates the durable path from
   host scheduling and verifies whether the 9.46x WAL ratio persists.
2. Capture a short `dotnet-trace` or equivalent CPU/lock profile during the
   concurrency-4 point-read case. The current plateau does not justify a code
   change by itself.
3. Connect the subscription runner to the real durable commit callback and
   repeat the horizontal subscriber matrix.
4. Restore a fixed backup chain into a filesystem-backed target and object
   store, retaining bytes read and replayed, to turn the in-memory restore
   result into an RTO measurement.
