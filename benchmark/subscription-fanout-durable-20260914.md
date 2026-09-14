# TerraBase reactive subscription fan-out: durable commit path

This run measures the local durable boundary
`StorageEngine.ApplyBatchAsync -> StorageChangeFeedWorker -> ReactiveSubscriptionHub -> subscriber frames`.
The benchmark commits each mutation to a checksummed WAL, waits for the
durable change-feed worker to publish that commit, and then records delivery
latency for responsive subscribers. The harness does not call
`ReactiveSubscriptionHub.PublishAsync` for this path.

The Release artifact was built with zero warnings and zero errors. The source
tree was dirty because the repository contains other in-progress work, and the
host did not provide an enforced CPU or memory cgroup limit. These values are
local regression evidence, not a multi-region or network delivery claim.

| Field | Value |
| --- | --- |
| Runtime | .NET 10.0.12; NixOS 26.05; x64 |
| CPU | Intel Xeon E5-2660 v4; 46 logical processors |
| Build | `nice -n 19 dotnet build bench/TerraBase.Benchmarks/TerraBase.Benchmarks.csproj -c Release --no-restore -m:1 --nologo` |
| Command | `TerraBase.Benchmarks.dll --workload subscriptions --subscriber-counts 1,10,100,1000 --warmup-publications 2 --publications 5` |
| Artifact SHA-256 | `bb2dde0ca26db0c408039d16ff5d8ebaf50dcf31b3ceb7d583ee5a720819dbdb` |
| Receipt | [`subscriptions-receipt.json`](receipts/20260914-durable-subscriptions-final/subscriptions-receipt.json) |
| Raw samples | [`subscriptions-raw-samples.jsonl`](receipts/20260914-durable-subscriptions-final/subscriptions-raw-samples.jsonl) |

One subscriber is deliberately held back for cases with more than one
subscriber. `Responsive` excludes that subscriber; its reset and resume
observations are reported separately. Every subscriber shares one operation,
parameter set, tenant, authorization scope, and document dependency.

| Subscribers | Status | Responsive | Origin evaluations | Shared maintenance updates | Delivered frames | Egress bytes | Allocated bytes/frame | CPU ms | RSS after bytes | p50 µs | p95 µs | p99 µs | Backpressure resets | Reconnect frame |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| 1 | complete | 1 | 5 | 5 | 5 | 3,248 | 161,332.80 | 35.88 | 73,711,616 | 5,291.90 | 7,815.96 | 8,287.91 | 0 | — |
| 10 | complete | 9 | 5 | 5 | 45 | 30,268 | 49,809.07 | 71.22 | 77,225,984 | 3,951.90 | 9,651.24 | 10,048.64 | 1 | Delta |
| 100 | complete | 99 | 5 | 5 | 495 | 326,386 | 7,112.63 | 82.56 | 81,121,280 | 4,688.10 | 11,617.01 | 11,700.33 | 1 | Delta |
| 1,000 | complete | 999 | 5 | 5 | 4,995 | 3,285,388 | 3,488.53 | 301.61 | 84,348,928 | 12,575.30 | 18,038.34 | 18,450.61 | 1 | Delta |

The probe observed exactly one origin query evaluation per measured durable
publication in every case, while delivery scaled with responsive subscriber
count. The 1,000-subscriber case shows the expected tail-latency and CPU rise;
the data should be repeated under an enforced resource envelope before using
it for capacity planning.
