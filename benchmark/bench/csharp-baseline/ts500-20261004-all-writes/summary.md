# C# TerraBase acceptance baseline

Release: `ci-ff256067015433c8c40b59879f1282d2a40480ce-run-13258` · SHA-256: `e07d173fc2bbab19dcb1fde8b121b0207b07d55452a5b19a406871d220e05e13`

| Workload | Concurrency | Attempts | Errors | ops/s | p50 ms | p95 ms | p99 ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| point_read | 1 | 100 | 0 | 1104.96 | 0.486 | 0.799 | 2.949 |
| point_write | 1 | 100 | 0 | 1397.58 | 0.638 | 1.115 | 1.224 |
| query_filter_sort | 1 | 100 | 0 | 593.27 | 1.293 | 2.213 | 2.359 |
| transactions | 1 | 100 | 0 | 1006.88 | 0.811 | 1.389 | 3.201 |
| mixed_90_10 | 1 | 100 | 0 | 2262.61 | 0.415 | 0.558 | 0.632 |
| point_read | 4 | 400 | 0 | 8094.39 | 0.429 | 0.605 | 2.345 |
| point_write | 4 | 400 | 0 | 1765.84 | 0.628 | 5.168 | 12.759 |
| query_filter_sort | 4 | 400 | 0 | 1314.75 | 2.823 | 4.314 | 4.950 |
| transactions | 4 | 400 | 0 | 1708.40 | 0.744 | 5.218 | 12.358 |
| mixed_90_10 | 4 | 400 | 0 | 7045.07 | 0.492 | 0.740 | 2.088 |
| point_read | 16 | 1600 | 0 | 12868.63 | 0.992 | 3.064 | 4.638 |
| point_write | 16 | 1600 | 0 | 2011.80 | 1.742 | 12.222 | 54.906 |
| query_filter_sort | 16 | 1600 | 0 | 642.04 | 23.555 | 36.620 | 43.136 |
| transactions | 16 | 1600 | 0 | 2379.89 | 1.568 | 11.069 | 54.631 |
| mixed_90_10 | 16 | 1600 | 0 | 13573.96 | 0.587 | 1.970 | 5.994 |

Final readback checks: 6510; errors: 0. [JSON evidence](receipt.json) · [Raw samples](samples.jsonl).

Process baseline; CPU priority is recorded in environment.json; timings are observations, never CI thresholds or SQLite qualification.
