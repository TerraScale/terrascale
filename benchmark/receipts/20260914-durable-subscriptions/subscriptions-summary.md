# TerraBase reactive subscription fan-out benchmark

Status: **complete**

- Commit: `9e84cc83827d1603bb307d23c6722761f1d029b4` (dirty: `True`)
- Artifact SHA-256: `52ee19a95a0cd75774800f2b15733656186a80630222615ca63cf2e7975626e1`
- Runtime: .NET 10.0.12; build `Release`; NixOS 26.05 (Yarara); X64
- CPU: Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz; 46 logical processors; available memory 20937965568 bytes

Command: `/home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll --workload subscriptions --subscriber-counts 1,10,100,1000 --warmup-publications 2 --publications 5 --output bench/receipts/20260914-durable-subscriptions`

## Results

Subject: `ReactiveSubscriptionHub`; boundary: `durable commit -> one shared query maintenance -> subscriber frame delivery`; durable commit path: `True`.

| Subscribers | Status | Responsive | Delivered frames | Egress bytes | p50 µs | p95 µs | p99 µs | Allocated bytes/frame | CPU ms | RSS after bytes | Unsupported reason |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
|1 | complete | 1 | 5 | 3,250 | 2883.50 | 4248.20 | 4467.08 | 138804.80 | 21.85 | 73,646,080 | — |
|10 | complete | 9 | 45 | 30,286 | 3350.90 | 3744.52 | 4722.39 | 44308.98 | 53.76 | 76,881,920 | — |
|100 | complete | 99 | 495 | 326,386 | 3968.10 | 4552.44 | 5326.32 | 6611.09 | 70.06 | 80,957,440 | — |
|1000 | complete | 999 | 4,995 | 3,287,386 | 7914.70 | 18255.05 | 18668.13 | 3490.34 | 276.49 | 84,578,304 | — |
