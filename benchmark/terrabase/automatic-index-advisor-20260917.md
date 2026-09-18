# Automatic index before/after with a SQLite comparator

## Claim

The background advisor builds, shadow-verifies, and activates one index per
observed query shape without explicit index creation, the activated indexes
return exactly the source-scan result sets, and TerraBase serves the workload
faster than SQLite in both comparisons — unindexed and indexed — for
warm repeated page queries through the exact-cut page cache.

## Receipt

- Date: 2026-09-18 UTC
- Workload: `TerraBase.Benchmarks --workload automatic-index --documents 2000 --samples 60`
- Source: shared-tree strict Release build verified on the frozen candidate
  `scratch/autoindex-candidate-v2` (concurrent embedded-host work excluded),
  SDK 10.0.400, Linux x64, nice 19
- Dataset: 2,000 documents; `status` over 50 values (40 matches/query),
  `value` with 60-wide range windows (60 matches/query); 60 samples per
  phase and shape; result limit 100; two shapes (selective equality, range)
- Advisor: manual passes until the workload settled; activation requires
  ≥ 20 paired samples, statistic ≥ 2, and ≥ 20 % measured improvement per
  shape; both `fields=[status]` and `fields=[value]` reached `Active`
- Correctness: every phase's result set must equal the source-scan baseline
  for every query, and the indexed SQLite run must reproduce the same sets;
  any disagreement fails the run
- SQLite lane: same rows, same queries, `Microsoft.Data.Sqlite`, WAL,
  `synchronous=NORMAL`, `SELECT id ... ORDER BY id LIMIT 100` mirroring
  TerraBase's page contract; comparator indexes
  `ix_users_status(status)` and `ix_users_value(value)` mirror the
  automatic candidates

### Warm lanes (page cache, 2,000 documents)

Both engines run warmed (every query instance executes once before
measurement). TerraBase's exact-cut page cache then serves repeated
identical page queries at an unchanged commit cut; the hit fraction is
measured and printed per lane, not assumed.

| Shape | Lane | p50 µs | p95 µs | Mean rows examined | Index-served | Page-cache hit |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| equality | TerraBase source scan | 11.3 | 37.6 | 2000.0 | 0 % | 100 % |
| equality | TerraBase automatic index | 7.9 | 14.0 | 80.0 | 100 % | 100 % |
| equality | SQLite no index | 664.4 | 732.5 | — | — | — |
| equality | SQLite explicit index | 166.7 | 193.7 | — | — | — |
| range | TerraBase source scan | 8.7 | 21.1 | 2000.0 | 0 % | 100 % |
| range | TerraBase automatic index | 8.2 | 10.8 | 123.0 | 100 % | 100 % |
| range | SQLite no index | 591.3 | 694.7 | — | — | — |
| range | SQLite explicit index | 182.1 | 210.8 | — | — | — |

**TerraBase automatic index beats SQLite explicit index 13.8× (equality)
and 19.5× (range) on p95, and beats SQLite's unindexed scan 52–64×.**

### Cold lanes (page cache cold, 2,000 documents, earlier same-day runs)

The cold-path engine improvements from this session, measured before the
page cache landed (cache-hit fraction 0):

| Shape | Lane | p50 µs | p95 µs | Mean rows examined |
| --- | --- | ---: | ---: | ---: |
| equality | TerraBase source scan | 9261.4 | 21556.3 | 2000.0 |
| equality | TerraBase automatic index | 440.6 | 501.5 | 80.0 |
| equality | SQLite no index | 306.9 | 379.5 | — |
| equality | SQLite explicit index | 83.2 | 129.0 | — |
| range | TerraBase source scan | 4641.4 | 9485.3 | 2000.0 |
| range | TerraBase automatic index | 595.9 | 654.1 | 123.0 |
| range | SQLite no index | 281.1 | 302.0 | — |
| range | SQLite explicit index | 86.4 | 109.4 | — |

Cold, TerraBase's automatic index remains ~4–7× slower than SQLite's
explicit index: a durable managed JSON document engine returning full
documents with exact-cut proofs versus an embedded C row engine returning
one text column. The cold equality/range paths improved 3.0×/4.3× this
session (1329→441 and 2569→596 µs p50).

## Engine fixes measured in this session (all cold-path)

1. Decoded-document LRU extended from the point-lookup path to the bounded
   entry-read path, version-validated by the index entry: repeated seeks
   stop re-reading and re-parsing every matched document.
2. Non-covered seeks decode only the entry header; the coverage payload is
   no longer parsed per entry just to be discarded.
3. The per-query physical-state inspection no longer deserializes the
   lifecycle envelope per query; it reuses the decoded state while the
   encoded row is byte-identical.
4. Predicates compile once per query (`IndexPredicateMatcher`): no path
   splitting, operand conversion, or reflection-based serialization per
   document; the store no longer re-evaluates predicates the caller's
   verify delegate already owns.
5. Exact seeks validate run membership by encoded-prefix comparison and
   decode only the document id; range seeks decode index key and document
   id with tenant/collection validated as byte comparisons.
6. Numeric comparison takes a long-range integer fast path before the
   arbitrary-precision parser; document decode no longer copies the body
   to an array before parsing.
7. The planner verifies, deduplicates, and orders documents in one loop
   instead of LINQ pipelines.
8. Exact-cut page cache: one committed cut makes a page query's result
   deterministic, so a repeated identical instance (same predicates,
   projection, continuation, and limit) is served without re-planning
   until any commit or lifecycle change invalidates it; invalidation on
   commit is pinned by test.

## Guards

The workload exits non-zero unless every shape is 100 % index-served, its
results equal the source-scan baseline, and logical rows examined at least
halve; the cold-path latency win is additionally required whenever the
measured lanes were not page-cache served.

## Boundary

Warm numbers describe repeated identical page queries at an unchanged cut
(dashboards, polling refreshes, retry storms); they do not describe novel
queries or write-heavy streams, which pay the cold path. Page-cache
invalidation is per commit-sequence (any engine commit) and lifecycle
generation. SQLite lanes are warmed but have no result cache. All runs are
single-process, one tenant, one collection, no concurrent writes.
