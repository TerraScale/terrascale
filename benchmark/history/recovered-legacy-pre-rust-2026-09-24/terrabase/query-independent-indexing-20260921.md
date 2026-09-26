# Query-independent indexing comparison

Generated: 2026-09-21T16:15:18.3650702+00:00; documents: 200; samples: 10; equal policy: one index, status path; evaluation query was not supplied to candidate selection.

| Strategy | First p50 µs | Steady p50 µs | No-index p95 µs | Steady p95 µs | Rows examined | Active index | Result coverage | Profile ms | Build ms | Inference ms | Inference cost USD | Storage bytes | Advice |
| --- | ---: | ---: | ---: | ---: | ---: | --- | --- | ---: | ---: | ---: | ---: | ---: | --- |
| No secondary index | 416.8 | 471.5 | 922.2 | 922.2 | 200.0 | False | True | 0.0 | 0.0 | 0.0 | 0.00000000 | 0 | not-run |
| Deterministic schema statistics | 756.1 | 11.2 | 854.3 | 1724.4 | 4.0 | True | True | 822.4 | 585.4 | 3.3 | 0.00000000 | 67316137 | schema-statistics:query-independent-coverage-hypothesis |

Jev is only a measured external strategy when `--jev` and `TERRABASE_JEV_API_KEY` are explicitly supplied. Otherwise its row is omitted; no fallback run is relabeled as Jev. Model probabilities/confidence are not treated as measured index benefit.
