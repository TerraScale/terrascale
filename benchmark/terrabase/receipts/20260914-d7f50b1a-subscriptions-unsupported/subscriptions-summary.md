# TerraBase reactive subscription fan-out benchmark

Status: **unsupported**

- Commit: `d7f50b1a4405da4298ce362f36102806b6160c29` (dirty: `True`)
- Artifact SHA-256: `790a1917fa67819da539150192d75b85db2f9ecab4fa09376a615e144229ae28`
- Runtime: .NET 10.0.12; build `Release`; NixOS 26.05 (Yarara); X64
- CPU: Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz; 46 logical processors; available memory 20937965568 bytes

Command: `/home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll --workload subscriptions --subscriber-counts 1,10,100,1000 --warmup-publications 2 --publications 5 --output bench/receipts/20260914-d7f50b1a-subscriptions-unsupported`

## Results

Subject: `ReactiveSubscriptionHub`; boundary: `durable commit -> one shared query maintenance -> subscriber frame delivery`; durable commit path: `False`.

| Subscribers | Status | Responsive | Delivered frames | Egress bytes | p50 µs | p95 µs | p99 µs | Allocated bytes/frame | CPU ms | RSS after bytes | Unsupported reason |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
|1 | unsupported | — | — | — | — | — | — | — | — | — | The CLI has no durable commit-to-reactive source callback; use the embedding API to supply one. |
|10 | unsupported | — | — | — | — | — | — | — | — | — | The CLI has no durable commit-to-reactive source callback; use the embedding API to supply one. |
|100 | unsupported | — | — | — | — | — | — | — | — | — | The CLI has no durable commit-to-reactive source callback; use the embedding API to supply one. |
|1000 | unsupported | — | — | — | — | — | — | — | — | — | The CLI has no durable commit-to-reactive source callback; use the embedding API to supply one. |

Unsupported reason: `The CLI has no durable commit-to-reactive source callback; use the embedding API to supply one.`
