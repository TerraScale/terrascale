# Automatic index advisor: before/after with a SQLite comparator

## Claim

The background advisor builds, shadow-verifies, and activates one index per
observed query shape without explicit index creation, the activated indexes
return exactly the source-scan result sets, and they materially speed up the
same workload. SQLite running the identical data and queries with and without
a matching explicit index is the reference lane.

## Receipt

- Date: 2026-09-17 (local), 2026-09-18 UTC
- Workload: `TerraBase.Benchmarks --workload automatic-index --documents 2000 --samples 60`
- Source: shared tree strict Release build, SDK 10.0.400, Linux x64, nice 19
- Dataset: 2,000 documents; `status` over 50 values (40 matches/query),
  `value` with 60-wide range windows (60 matches/query); 60 samples per
  phase and shape; result limit 100; two shapes (selective equality, range)
- Advisor: manual passes until the workload settled (2 passes; 2.1 s wall at
  2,000 documents, 3.7 s at 10,000); activation requires ≥ 20 paired samples,
  statistic ≥ 2, and ≥ 20 % measured improvement per shape
- Advisor outcome: `fields=[status]` and `fields=[value]` both `Active`
- Correctness: every phase's result set must equal the source-scan baseline
  for every query; any disagreement fails the run
- SQLite lane: same rows, same queries, `Microsoft.Data.Sqlite`, WAL,
  `synchronous=NORMAL`, `SELECT id ... ORDER BY id LIMIT 100` mirroring
  TerraBase's deterministic document-id page contract; comparator indexes
  `ix_users_status(status)` and `ix_users_value(value)` mirror the automatic
  candidates

### 2,000 documents

| Shape | Lane | p50 µs | p95 µs | p99 µs | Mean rows examined | Index-served |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| equality | TerraBase source scan | 9604.9 | 10991.8 | 11204.6 | 2000.0 | 0 % |
| equality | TerraBase automatic index | 1328.9 | 1547.0 | 2397.9 | 81.0 | 100 % |
| equality | SQLite no index | 303.4 | 370.2 | 384.2 | — | — |
| equality | SQLite explicit index | 76.2 | 101.5 | 121.6 | — | — |
| range | TerraBase source scan | 12665.4 | 17698.2 | 20439.2 | 2000.0 | 0 % |
| range | TerraBase automatic index | 2569.2 | 3658.7 | 4212.2 | 123.0 | 100 % |
| range | SQLite no index | 278.0 | 308.3 | 313.4 | — | — |
| range | SQLite explicit index | 78.2 | 104.2 | 156.0 | — | — |

### 10,000 documents (scale check)

| Shape | Lane | p50 µs | p95 µs | p99 µs | Mean rows examined | Index-served |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| equality | TerraBase source scan | 17720.6 | 21642.4 | 24292.5 | 10000.0 | 0 % |
| equality | TerraBase automatic index | 1480.1 | 2529.7 | 2886.0 | 202.0 | 100 % |
| equality | SQLite no index | 676.2 | 697.2 | 720.9 | — | — |
| equality | SQLite explicit index | 167.2 | 200.7 | 285.7 | — | — |
| range | TerraBase source scan | 28551.4 | 30848.6 | 34217.7 | 10000.0 | 0 % |
| range | TerraBase automatic index | 1100.4 | 2298.5 | 2402.0 | 123.0 | 100 % |
| range | SQLite no index | 1161.7 | 1245.3 | 1522.9 | — | — |
| range | SQLite explicit index | 75.7 | 97.1 | 101.7 | — | — |

| Scale | Shape | Auto vs scan p95 | Auto vs scan rows | SQLite scan / auto p95 | Auto / SQLite indexed p95 |
| --- | --- | ---: | ---: | ---: | ---: |
| 2k | equality | 85.9 % | 96.0 % | 0.24x | 15.24x |
| 2k | range | 79.3 % | 93.9 % | 0.08x | 35.11x |
| 10k | equality | 88.3 % | 98.0 % | 0.28x | 12.60x |
| 10k | range | 92.5 % | 98.8 % | 0.54x | 23.67x |

## Reading

- **The auto index improves this workload decisively at both scales**: p95
  −79 % to −92 % per shape and scale; logical rows examined −94 % to −99 %;
  100 % of after-phase queries served by the automatic indexes; result sets
  verified equal to the source-scan baseline and to SQLite's pages.
- **Against SQLite the absolute gap is large and honest**: SQLite's C table
  scan beats TerraBase's *indexed* path (0.08–0.54x, narrowing with scale),
  and SQLite's explicit index is ~12–35x faster than the automatic-index
  lane. TerraBase is a durable JSON document engine doing authorization,
  change feed, and document deserialization per query; SQLite is an embedded
  C RDBMS returning a text column. The relative before/after claim is the
  point of this receipt; the absolute engine comparison lives in
  [engine-sqlite-parity-20260917.md](engine-sqlite-parity-20260917.md).

## Fixes this run forced

1. The advisor built only one candidate per pass (`MaxBuildsPerTick=1`), so
   a two-shape workload left the second shape on scans forever. Default is
   now 2, and the benchmark ticks until the workload settles.
2. A range query (`value >= lo AND value < hi`) produced two predicates on
   one path; the runtime's shape key kept both while the analyzer's template
   deduplicated them, so the advisor could never replay the instance and the
   range index stalled in Shadowing. `ToShape` now applies the analyzer's
   exact dedup normalization.
3. At 10,000 documents the equality matches exceeded the page limit: the
   SQLite comparator returned all matches while TerraBase returned its
   deterministic first page. The comparator now applies the same
   `ORDER BY id LIMIT` page contract, so the guard compares identical pages.

## Guards

The workload exits non-zero unless every shape is 100 % index-served, its
results equal the source-scan baseline, p95 improves, and mean rows examined
at least halve — a CI run is a qualification receipt, not a sample dump.

## Boundary

Single process, warm cache, one tenant and collection, no concurrent writes,
and no measured write-path cost added by the two live indexes. Regression
rollback, retirement, and revival are exercised by the API test suite, not
by this receipt.
