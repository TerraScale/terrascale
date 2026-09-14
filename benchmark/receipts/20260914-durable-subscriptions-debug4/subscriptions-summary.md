# TerraBase reactive subscription fan-out benchmark

Status: **complete**

- Commit: `9e84cc83827d1603bb307d23c6722761f1d029b4` (dirty: `True`)
- Artifact SHA-256: `363e2062da6da779f310226bdc1535612c7d4fc6f33ccb587b7b0c02d2a7c315`
- Runtime: .NET 10.0.12; build `Release`; NixOS 26.05 (Yarara); X64
- CPU: Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz; 46 logical processors; available memory 20937965568 bytes

Command: `/home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll --workload subscriptions --subscriber-counts 1 --warmup-publications 0 --publications 1 --output bench/receipts/20260914-durable-subscriptions-debug4`

## Results

Subject: `ReactiveSubscriptionHub`; boundary: `durable commit -> one shared query maintenance -> subscriber frame delivery`; durable commit path: `True`.

| Subscribers | Status | Responsive | Delivered frames | Egress bytes | p50 µs | p95 µs | p99 µs | Allocated bytes/frame | CPU ms | RSS after bytes | Unsupported reason |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
|1 | complete | 1 | 1 | 650 | 33926.90 | 33926.90 | 33926.90 | 1233752.00 | 166.24 | 72,519,680 | — |
