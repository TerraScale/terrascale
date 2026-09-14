# TerraBase reactive subscription fan-out benchmark

Status: **unsupported**

- Commit: `8cde04fec00ba23b31ebb2391a5bea2e45215a87` (dirty: `True`)
- Artifact SHA-256: `c7d202a8b3d790d92711c864a42c47a2ca65862465560ce7fda0bcb274ec66ac`
- Runtime: .NET 10.0.12; build `Release`; NixOS 26.05 (Yarara); X64
- CPU: Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz; 46 logical processors; available memory 20937965568 bytes

Command: `/home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll --workload subscriptions --subscriber-counts 1,10,50 --warmup-publications 2 --publications 5 --output /tmp/terrabase-bench-subscriptions-1789365890`

## Results

Subject: `ReactiveSubscriptionHub`; boundary: `durable commit -> one shared query maintenance -> subscriber frame delivery`; durable commit path: `False`.

| Subscribers | Status | Responsive | Delivered frames | Egress bytes | p50 µs | p95 µs | p99 µs | Allocated bytes/frame | CPU ms | RSS after bytes | Unsupported reason |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
|1 | unsupported | — | — | — | — | — | — | — | — | — | The CLI has no durable commit-to-reactive source callback; use the embedding API to supply one. |
|10 | unsupported | — | — | — | — | — | — | — | — | — | The CLI has no durable commit-to-reactive source callback; use the embedding API to supply one. |
|50 | unsupported | — | — | — | — | — | — | — | — | — | The CLI has no durable commit-to-reactive source callback; use the embedding API to supply one. |

Unsupported reason: `The CLI has no durable commit-to-reactive source callback; use the embedding API to supply one.`

Raw samples: [`raw-samples.jsonl`](raw-samples.jsonl) (unsupported cases contain no measured latency or resource values).
