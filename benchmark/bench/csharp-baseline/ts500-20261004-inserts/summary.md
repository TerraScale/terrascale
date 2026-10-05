# C# TerraBase acceptance baseline

Release: `ci-ff256067015433c8c40b59879f1282d2a40480ce-run-13258` · SHA-256: `e07d173fc2bbab19dcb1fde8b121b0207b07d55452a5b19a406871d220e05e13`

| Workload | Concurrency | Attempts | Errors | ops/s | p50 ms | p95 ms | p99 ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| point_read | 1 | 100 | 0 | 1024.53 | 0.527 | 0.836 | 3.353 |
| point_write | 1 | 100 | 0 | 1054.64 | 0.747 | 1.252 | 2.507 |
| query_filter_sort | 1 | 100 | 0 | 556.52 | 1.391 | 2.174 | 4.894 |
| transactions | 1 | 100 | 0 | 1301.54 | 0.656 | 0.921 | 1.601 |
| mixed_90_10 | 1 | 100 | 0 | 2248.33 | 0.408 | 0.654 | 0.719 |
| point_read | 4 | 400 | 0 | 7927.50 | 0.432 | 0.710 | 1.638 |
| point_write | 4 | 400 | 0 | 2047.91 | 0.643 | 5.044 | 12.599 |
| query_filter_sort | 4 | 400 | 0 | 1220.62 | 2.987 | 4.487 | 5.387 |
| transactions | 4 | 400 | 0 | 1767.62 | 0.743 | 5.091 | 19.017 |
| mixed_90_10 | 4 | 400 | 0 | 7307.86 | 0.486 | 0.729 | 1.758 |
| point_read | 16 | 1600 | 0 | 13710.33 | 0.972 | 2.128 | 4.424 |
| point_write | 16 | 1600 | 0 | 2268.13 | 1.493 | 11.654 | 79.196 |
| query_filter_sort | 16 | 1600 | 0 | 519.09 | 29.297 | 45.842 | 55.040 |
| transactions | 16 | 1600 | 0 | 2462.61 | 1.042 | 14.801 | 78.919 |
| mixed_90_10 | 16 | 1600 | 0 | 15685.92 | 0.600 | 2.319 | 6.298 |

Final readback checks: 6510; errors: 0. [JSON evidence](receipt.json) · [Raw samples](samples.jsonl).

Process baseline; CPU priority is recorded in environment.json; timings are observations, never CI thresholds or SQLite qualification.
