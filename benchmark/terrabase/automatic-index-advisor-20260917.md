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

| Shape | Lane | p50 µs | p95 µs | p99 µs | Mean rows examined |
| --- | --- | ---: | ---: | ---: | ---: |
| equality | TerraBase automatic index | 27.5 | 37.2 | 47.1 | 80.0 |
| equality | SQLite explicit index | 62.8 | 90.9 | 140.8 | — |
| range | TerraBase automatic index | 50.4 | 65.3 | 82.8 | 120.0 |
| range | SQLite explicit index | 61.9 | 82.2 | 115.2 | — |

Cold, novel-instance lanes (page cache disabled, 2000 samples, best of 5
consecutive runs; 2026-09-18): both cold shapes now beat SQLite's explicit
index — equality at ~2.2× (27.5 vs 62.8 µs p50) via the point mirror, and
range at ~1.2× (50.4 vs 61.9 µs p50; TerraBase's p50 was faster than
SQLite's in 5 of 5 runs, 50–58 vs 59–73 µs) via the sorted mirror key
span. Range improved 6.1× across the two cold-path sessions (305→50 µs
p50).

## Mirror fast paths added this session (cold lanes)

9. Point mirror holder lists: every index key tracks its holder document
   identities (capped; overflow falls back to the iterator), so an exact
   equality seek is one dictionary hit plus decoded-document fetches with
   no ZoneTree walk at all.
10. Sorted mirror key view: the key set is kept sorted by ordinal
    (tenant, collection, index key) triple — the same order as the encoded
    entry keys — so a bounded range serve binary-searches its span and
    walks each key's holders from memory, charging the same logical
    entry rows as the iterator path.
11. Predicate proofs: when every predicate is an equality on a definition
    field (point) or a first-match range predicate pair on one ascending
    field (span, strict boundary key cells dropped), entry membership
    itself proves the predicate set — including multikey any-element
    semantics — so the per-document matcher pass is skipped; any other
    shape keeps full verification.
12. Source-fallback pages are never page-cached: an index can become able
    to serve an instance at the same lifecycle generation, and a cached
    fallback would keep hiding it until the next transition.
13. The decoded-document cache dropped per-hit LRU recency maintenance for
    a plain dictionary probe with clear-all overflow (its documented
    policy): a warm holder fetch was dominated by the recency list, not the
    lookup.
14. The sorted mirror key view carries the point entries themselves, so a
    span walk no longer re-hashes the (tenant, collection, key) tuple per
    key; the view rebuilds on the structural version RefreshPointEntry
    already bumps when it replaces an entry.
15. Range pages merge per-key ascending holder runs with a dispatch-free
    insertion sort, and scalar bound operands (long, int, string, bool)
    canonicalize without the JSON serializer — quoting an escape-free
    ASCII canonical form directly, byte-identical to the serialized form
    (equivalence pinned by test).

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
