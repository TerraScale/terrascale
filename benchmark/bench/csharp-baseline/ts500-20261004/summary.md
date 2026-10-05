# C# TerraBase acceptance baseline

Release: `ci-ff256067015433c8c40b59879f1282d2a40480ce-run-13258` · SHA-256: `e07d173fc2bbab19dcb1fde8b121b0207b07d55452a5b19a406871d220e05e13`

| Workload | Concurrency | Attempts | Errors | ops/s | p50 ms | p95 ms | p99 ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| point_read | 1 | 100 | 0 | 1064.10 | 0.508 | 0.888 | 3.031 |
| point_write | 1 | 100 | 0 | 1360.51 | 0.665 | 1.018 | 2.067 |
| query_filter_sort | 1 | 100 | 0 | 746.77 | 1.022 | 1.245 | 4.533 |
| transactions | 1 | 100 | 0 | 1081.23 | 0.817 | 1.215 | 1.309 |
| mixed_90_10 | 1 | 100 | 0 | 1508.55 | 0.613 | 0.858 | 1.004 |
| point_read | 4 | 400 | 0 | 5856.32 | 0.585 | 1.189 | 3.210 |
| point_write | 4 | 400 | 0 | 1543.23 | 0.857 | 7.013 | 19.591 |
| query_filter_sort | 4 | 400 | 0 | 2573.68 | 1.434 | 2.344 | 3.630 |
| transactions | 4 | 400 | 0 | 1451.29 | 0.761 | 5.027 | 13.199 |
| mixed_90_10 | 4 | 400 | 0 | 6121.44 | 0.535 | 0.743 | 1.897 |
| point_read | 16 | 1600 | 0 | 10111.29 | 1.284 | 3.410 | 5.737 |
| point_write | 16 | 1600 | 0 | 2258.81 | 1.768 | 19.020 | 56.616 |
| query_filter_sort | 16 | 1600 | 0 | 3188.18 | 4.512 | 8.231 | 12.568 |
| transactions | 16 | 1600 | 0 | 2181.50 | 2.472 | 19.860 | 80.745 |
| mixed_90_10 | 16 | 1600 | 0 | 8562.38 | 0.783 | 3.696 | 7.946 |

Final readback errors: 0. [JSON evidence](receipt.json) · [Raw samples](samples.jsonl).

Process baseline; CPU priority is recorded in environment.json; timings are observations, never CI thresholds or SQLite qualification.
