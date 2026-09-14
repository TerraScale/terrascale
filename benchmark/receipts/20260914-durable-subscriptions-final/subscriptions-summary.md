# TerraBase reactive subscription fan-out benchmark

Status: **complete**

- Commit: `9e84cc83827d1603bb307d23c6722761f1d029b4` (dirty: `True`)
- Artifact SHA-256: `bb2dde0ca26db0c408039d16ff5d8ebaf50dcf31b3ceb7d583ee5a720819dbdb`
- Runtime: .NET 10.0.12; build `Release`; NixOS 26.05 (Yarara); X64
- CPU: Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz; 46 logical processors; available memory 20937965568 bytes

Command: `/home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll --workload subscriptions --subscriber-counts 1,10,100,1000 --warmup-publications 2 --publications 5 --output bench/receipts/20260914-durable-subscriptions-final`

## Results

Subject: `ReactiveSubscriptionHub`; boundary: `durable commit -> one shared query maintenance -> subscriber frame delivery`; durable commit path: `True`.

| Subscribers | Status | Responsive | Delivered frames | Egress bytes | p50 µs | p95 µs | p99 µs | Allocated bytes/frame | CPU ms | RSS after bytes | Unsupported reason |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
|1 | complete | 1 | 5 | 3,248 | 5291.90 | 7815.96 | 8287.91 | 161332.80 | 35.88 | 73,711,616 | — |
|10 | complete | 9 | 45 | 30,268 | 3951.90 | 9651.24 | 10048.64 | 49809.07 | 71.22 | 77,225,984 | — |
|100 | complete | 99 | 495 | 326,386 | 4688.10 | 11617.01 | 11700.33 | 7112.63 | 82.56 | 81,121,280 | — |
|1000 | complete | 999 | 4,995 | 3,285,388 | 12575.30 | 18038.34 | 18450.61 | 3488.53 | 301.61 | 84,348,928 | — |
