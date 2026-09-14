# Recovery and capacity benchmark harness

The benchmark project has two measured boundaries that are separate from the
provider point-operation runner:

| Boundary | Subject | Measured path | Claim boundary |
| --- | --- | --- | --- |
| Replication lag | `LeaderlessReplicationEngine` | Source apply, source delta export, destination apply | In-memory process-local replication only; no network, durable acknowledgement, repair, or multi-region claim |
| Restore time | `BackupManager` | Manifest chain load, checksum verification, and transactional in-memory target activation | In-memory object store and target only; no filesystem reopen, object-store network, crash recovery, or ZoneTree restore adapter claim |
| Reactive fan-out | `ReactiveSubscriptionHub` | `StorageEngine.ApplyBatchAsync` -> `StorageChangeFeedWorker` -> hub -> subscriber frames | Local durable WAL and feed publication; no network or multi-region claim |

Every measured receipt records the git revision and dirty state, runner DLL
hash, TerraBase and ZoneTree assembly hashes, .NET runtime, OS and
architecture, CPU model, logical processor count, available memory, cgroup
limits when visible, GC mode, command, and raw JSONL samples. Percentiles in
the generated summary are medians of per-sample percentiles. A missing
measurement remains `null`/`—`.

## Commands

Use a Release artifact and the repository's lowest available CPU priority:

```text
nice -n 19 dotnet build bench/TerraBase.Benchmarks/TerraBase.Benchmarks.csproj --configuration Release
nice -n 19 dotnet bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll \
  --workload recovery --samples 5 --operations 100 --log-entries 100 \
  --payload-size 256 --output bench/receipts/<utc-run>
```

`--workload replication-lag` and `--workload restore` run one boundary. The
minimum sample count is five. `--operations` is the measured operation count
per sample; `--log-entries` fixes the incremental restore chain size; and
`--payload-size` fixes the snapshot and log payload size. The replication
fixture uses one distinct document key per operation so the result measures
the apply/export boundary rather than unbounded same-document CRDT candidate
merge growth. An exploratory same-key run on the same artifact did not finish
within a 15-second bound at seven operations; that is an unresolved local
merge-path regression signal, not a result this harness suppresses. A separate
same-key workload is required before making a CRDT merge claim.

The provider runner compares the TerraBase storage engine with the ZoneTree
fork. Run the same workload, dataset, payload, sample, warmup, and concurrency
values for both:

```text
nice -n 19 dotnet bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll \
  --storage storage-engine --workload point-write --temperature warm \
  --dataset-size 1000 --payload-size 256 --samples 5 --warmup 500 \
  --operations 1000 --concurrency 1 --output bench/receipts/<storage-engine-run>
nice -n 19 dotnet bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll \
  --storage zonetree --workload point-write --temperature warm \
  --dataset-size 1000 --payload-size 256 --samples 5 --warmup 500 \
  --operations 1000 --concurrency 1 --output bench/receipts/<zonetree-run>
```

The provider receipt contains p50/p95/p99 latency, allocations per operation,
process CPU and RSS, and write amplification when the selected backend exposes
a WAL byte delta. ZoneTree's write amplification remains `—` until its
provider exposes a comparable durable byte boundary. Vary `--concurrency` for
vertical scaling. The subscription runner uses a real local durable commit and
change-feed worker; vary its `--subscriber-counts` for horizontal fan-out
scaling:

```text
nice -n 19 dotnet bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll \
  --workload subscriptions --subscriber-counts 1,10,100,1000 \
  --warmup-publications 2 --publications 5 \
  --output bench/receipts/<fanout-run>
```

Do not call a host-memory run larger-than-memory evidence. Use an explicit
cgroup memory limit with the larger-than-memory workload and retain the limit
in the receipt. A missing cgroup limit is recorded as unavailable.

## First harness receipt

The following receipt was generated on 2026-09-14 with the command above,
using five samples, 100 measured operations per sample, 100 incremental log
entries, 256-byte payloads, .NET 10.0.12, NixOS 26.05 x64, Intel Xeon E5-2660
v4, 46 logical processors, interactive non-server GC, and no visible cgroup
memory or CPU limit.

| Boundary | Status | Samples | Throughput | p50 | p95 | p99 | Allocated bytes/op | CPU % | RSS after bytes | Backlog / log entries | Runner artifact SHA-256 |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| Replication lag | complete | 5 | 12,915.06 ops/s | 62.20 µs | 114.50 µs | 297.40 µs | 6,777.36 | 2.29 | 68,829,184 | 0 ops | `ea33602170aa0159efd87a042ce7f4c5276ec13f59bdf76231c0536bc1f45fb3` |
| Restore time | complete | 5 | 407.51 restores/s | 1.96 ms | 3.17 ms | 4.31 ms | 378,557.60 | 2.40 | 80,539,648 | 100 entries | `ea33602170aa0159efd87a042ce7f4c5276ec13f59bdf76231c0536bc1f45fb3` |
| Reactive fan-out | complete | 1/10/100/1000 | — | see receipt | — | — | see receipt | see receipt | see receipt | — | local durable feed | [`subscriptions-receipt.json`](receipts/20260914-durable-subscriptions-final/subscriptions-receipt.json) |

Receipt directory: `/tmp/terrascale-recovery-final.Mz2fEv` during the initial
measurement. Future admitted numbers belong in a retained `bench/receipts/`
directory and must keep their raw JSONL files beside the receipt and summary.

The durable subscription receipt is retained separately at
`bench/receipts/20260914-durable-subscriptions/`. It reports p50/p95/p99
delivery latency, origin evaluations, shared maintenance updates, allocations,
CPU, RSS, egress, bounded backpressure, and resumable delivery for 1, 10, 100,
and 1,000 subscribers.

## Verification and limits

The benchmark project compiled with:

```text
nice -n 19 dotnet build bench/TerraBase.Benchmarks/TerraBase.Benchmarks.csproj \
  --configuration Release --no-restore /p:BuildProjectReferences=false
```

That focused command passed with zero warnings and errors. A full dependency
build also attempted the same Release build but was blocked by the existing
unrelated `Utf16ValueStringBuilder` error in
`src/TerraBase/Observability/Metrics/PrometheusExposition.cs`; the broad build
owner must rerun the normal command after that source issue is resolved.

The measured replication lag is a useful regression signal for the local
engine seam, but it cannot answer network or durable replication questions.
The smallest next measurement for that question is an authenticated loopback
through the real MagicOnion service with two durable engine directories,
retaining send timestamp, durable acknowledgement, applied frontier, and
destination frontier separately. The smallest next restore measurement is a
StorageEngine snapshot/restore target on a fixed filesystem and a matching
ZoneTree target once that adapter exists.
