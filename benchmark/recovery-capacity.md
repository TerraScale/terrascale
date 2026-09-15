# Recovery and capacity benchmark harness

The benchmark project has these measured boundaries separate from the provider
point-operation runner:

| Boundary | Subject | Measured path | Claim boundary |
| --- | --- | --- | --- |
| Replication lag | `LeaderlessReplicationEngine` | Source apply, source delta export, destination apply | In-memory process-local replication only; no network, durable acknowledgement, repair, or multi-region claim |
| Restore time | `BackupManager` | Manifest chain load, checksum verification, and transactional in-memory target activation | In-memory object store and target only; no filesystem reopen, object-store network, crash recovery, or ZoneTree restore adapter claim |
| Reactive fan-out | `ReactiveSubscriptionHub` | `StorageEngine.ApplyBatchAsync` -> `StorageChangeFeedWorker` -> hub -> subscriber frames | Local durable WAL and feed publication; no network or multi-region claim |

The source-matched v3 recovery receipt and the retained durable subscription
receipt record git revision and dirty state, runner DLL hash, assembly hashes
where applicable, runtime, host, resource observations, command, and raw JSONL
samples. The separately retained MagicOnion and native-backup receipts are
complete runs but omit git and benchmark-artifact identity; those gaps remain
explicit below. Percentiles in the source-matched generated summary are medians
of per-sample percentiles. A missing measurement remains `null`/`—`.

## Retained receipt identities

These identity fields describe the named dated receipts used by this document;
they do not make a dirty-tree or unbound fixture a release qualification.

| Boundary | Receipt | Status | Git identity | Benchmark artifact identity |
| --- | --- | --- | --- | --- |
| Replication lag and in-memory restore | [`source-matched-v3 recovery`](receipts/20260914-source-matched-v3-recovery/recovery-receipt.json) | complete | present: `9e84cc83827d1603bb307d23c6722761f1d029b4`, dirty | present: `9a4fd71e58057954c8b0840d2688c61bb89eb043a13fd5078dd3494e53962e3b` |
| Local durable subscription fan-out | [`durable subscriptions`](receipts/20260914-durable-subscriptions-final/subscriptions-receipt.json) | complete | present: `9e84cc83827d1603bb307d23c6722761f1d029b4`, dirty | present: `bb2dde0ca26db0c408039d16ff5d8ebaf50dcf31b3ceb7d583ee5a720819dbdb` |
| MagicOnion loopback replication | [`MagicOnion final`](receipts/20260914-magic-onion-final/receipt.json) | complete | missing | missing |
| Native filesystem restore | [`native backup`](receipts/native-backup-20260914/native-backup-receipt.json) | complete | missing | missing |

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

For provider rows, `warm` means that the seeded engine receives the configured
warmup operations before measured samples while remaining open. `cold` means
that the seeded durable engine is disposed and reopened before measured samples;
the reopen is timed separately and the operating-system page cache is not
cleared. These labels describe local process and durable-path phases, not a host
restart or a guarantee about cache state.

Do not call a host-memory run larger-than-memory evidence. Use an explicit
cgroup memory limit with the larger-than-memory workload and retain the limit
in the receipt. A missing cgroup limit is recorded as unavailable.

## Source-matched harness receipt

The retained source-matched v3 receipt was generated on 2026-09-14 with the command above,
using five samples, 100 measured operations per sample, 100 incremental log
entries, 256-byte payloads, .NET 10.0.12, NixOS 26.05 x64, Intel Xeon E5-2660
v4, 46 logical processors, interactive non-server GC, and no visible cgroup
memory or CPU limit. It uses HEAD `9e84cc83827d1603bb307d23c6722761f1d029b4`
with a dirty tree and runner artifact
`9a4fd71e58057954c8b0840d2688c61bb89eb043a13fd5078dd3494e53962e3b`.

| Boundary | Status | Samples | Throughput | p50 | p95 | p99 | Allocated bytes/op | CPU % | RSS after bytes | Backlog / log entries | Runner artifact SHA-256 |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| Replication lag | complete | 5 | 12,248.15 ops/s | 67.30 µs | 133.00 µs | 205.80 µs | 6,452.40 | 2.30 | 69,169,152 | 0 ops | `9a4fd71e58057954c8b0840d2688c61bb89eb043a13fd5078dd3494e53962e3b` |
| Restore time | complete | 5 | 319.78 restores/s | 2.93 ms | 5.19 ms | 7.39 ms | 516,785.20 | 2.59 | 81,612,800 | 100 entries | `9a4fd71e58057954c8b0840d2688c61bb89eb043a13fd5078dd3494e53962e3b` |
| Reactive fan-out | complete | 1/10/100/1000 | — | see retained receipt | — | — | see retained receipt | see retained receipt | see retained receipt | — | `bb2dde0ca26db0c408039d16ff5d8ebaf50dcf31b3ceb7d583ee5a720819dbdb` |

The durable subscription receipt is retained separately at
[`subscriptions-summary.md`](receipts/20260914-durable-subscriptions-final/subscriptions-summary.md)
and records delivery latency, origin evaluations, shared maintenance updates,
allocations, CPU, RSS, egress, bounded backpressure, and resumable delivery for
1, 10, 100, and 1,000 subscribers. It is the complete local durable receipt
listed above; the earlier [source-matched v3 subscription receipt](receipts/20260914-source-matched-v3-subscriptions/subscriptions-receipt.json)
remains an explicit unsupported attempt.

## Verification and limits

The benchmark project compiled with:

```text
nice -n 19 dotnet build bench/TerraBase.Benchmarks/TerraBase.Benchmarks.csproj \
  --configuration Release --no-restore /p:BuildProjectReferences=false
```

That focused command passed with zero warnings and errors. A full dependency
build was also attempted against the moving dirty checkout and remains blocked
by current source/analyzer errors; see
`../.codex/evidence/operation-context-terrabase-build-20260914.log`. The
focused benchmark artifact is therefore a local fixture receipt, not a clean
solution qualification.

The measured replication lag is a useful regression signal for the local
engine seam, but it cannot answer network or durable replication questions.
The retained [MagicOnion loopback receipt](receipts/20260914-magic-onion-final/summary.md)
is complete and covers two HTTPS/mTLS hosts with separate durable directories,
duplicate/reverse delivery, durable acknowledgements, deferred responses, and
final durable/applied frontiers; its git and artifact identity are missing, and
it does not qualify WAN, crash, partition-healing, or saturation behavior. The
retained [native restore receipt](receipts/native-backup-20260914/native-backup-summary.md)
is complete and covers a local filesystem target and clean reopen; its git and
artifact identity are missing, and it does not qualify remote object stores,
power loss, or production RPO/RTO.
