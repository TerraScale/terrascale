# TerraBase reactive subscription fan-out receipt

Status: **unsupported**

This template is intentionally not a performance claim. Replace `unsupported`
with `complete` only after the benchmark is connected to the real durable
commit-to-reactive path and the retained machine-readable receipt is available.

## Source and host

| Field | Value |
| --- | --- |
| Git commit | `<commit>` |
| Dirty tree | `<true-or-false>` |
| Artifact SHA-256 | `<sha256>` |
| .NET runtime / SDK | `<runtime-and-sdk>` |
| OS / architecture | `<os-and-architecture>` |
| GC mode | `<server-gc-and-latency-mode>` |
| CPU / logical processors | `<cpu-and-count>` |
| Memory envelope | `<bytes-or-limit>` |
| Build command | `nice -n 19 dotnet build ... --configuration Release` |
| Benchmark command | `nice -n 19 dotnet run ... --configuration Release` |
| Receipt directory | `<path>` |

## Operation boundary and workload

| Field | Value |
| --- | --- |
| Subject | `ReactiveSubscriptionHub` |
| Operation boundary | `durable commit -> one shared query maintenance -> subscriber frame delivery` |
| Durable commit-to-reactive path | `<available-or-unsupported>` |
| Query operation identity | `<operation-id>` |
| Tenant / authorization scope | `<tenant-and-scope-digest>` |
| Subscriber counts | `1, 10, 100, 1000` |
| Warm-up publications | `<count>` |
| Measured publications | `<count>` |
| Payload / result bytes | `<bytes>` |
| Queue / backpressure policy | `<normal-and-slow-subscriber-policy>` |
| Workload digest | `<sha256>` |

## Measurements

Percentiles are calculated per subscriber-count case from responsive subscriber
frames and summarized without pooling. A missing value is `—`; it is not zero.
When one subscriber is deliberately held back, `Responsive` excludes that
subscriber and the separate backpressure/reconnect columns describe it.

| Subscribers | Responsive | Status | Origin evaluations | Shared maintenance updates | Incremental updates | Changed documents | Delivered frames | Egress bytes | Allocated bytes | Allocated bytes/frame | CPU ms | Working-set before | Working-set after | Working-set delta | p50 delivery µs | p95 delivery µs | p99 delivery µs | Backpressure resets | Backpressure disconnects | Reconnect frame | Reconnect reset | Reconnect egress bytes | Error / unsupported reason |
| ---: | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- | --- | ---: | --- |
| 1 | `<responsive>` | unsupported | — | — | — | — | — | — | — | — | — | — | — | — | — | — | — | — | — | — | — | `<reason>` |
| 10 | `<responsive>` | unsupported | — | — | — | — | — | — | — | — | — | — | — | — | — | — | — | — | — | — | — | `<reason>` |
| 100 | `<responsive>` | unsupported | — | — | — | — | — | — | — | — | — | — | — | — | — | — | — | — | — | — | — | `<reason>` |
| 1000 | `<responsive>` | unsupported | — | — | — | — | — | — | — | — | — | — | — | — | — | — | — | — | — | — | — | `<reason>` |

## Correctness and limitations

- All subscribers in a case must use one identical query operation, parameter
  value, tenant, and authorization scope so that the hub can share one origin
  materialization.
- The commit callback must return the durable commit's `ReactiveChangeSet`. A
  durable scenario supplies `PublishCommittedAsync` to wait for the
  `StorageChangeFeedWorker` (or equivalent durable publisher); the harness
  then avoids publishing the same commit a second time. A scenario without
  that hook may use the harness's direct hub publication only when its commit
  boundary is explicitly marked as non-durable.
- `Origin evaluations` comes from the query delegate's
  `SubscriptionFanoutProbe`. If no probe is supplied, the field remains `—`.
- `Shared maintenance updates` and `Incremental updates` count one shared delta
  per measured publication; delivered frames count each responsive subscriber
  frame.
- A synthetic change callback, an in-memory-only path, or a missing durable
  feed remains `unsupported` and must not be promoted to a throughput result.
- Reconnect reset is recorded only when the old checkpoint is outside the
  hub's retained history. The hub's retention policy is configured when the
  hub is created.

## Retained artifacts

| Artifact | Path |
| --- | --- |
| Machine-readable receipt | `<receipt.json>` |
| Raw samples | `<raw-samples-subscription-fanout.jsonl>` |
| Summary | `<summary.md>` |
