# Relationship path: decoded constraint index improvement record

Paired runs of `--workload=relationship` before and after replacing the
constraint keyspace's per-query full scan-and-decode with the engine-owned
decoded index (`EngineConstraintIndex`). The before receipt was captured on
the pre-change tree (Debug, net10, serial run); the after receipts were
captured on the changed tree under the same serial conditions (Debug, net11).
Both trees carried the same concurrent in-flight restore work, so the
comparison isolates the constraint-index change plus the runtime flip.

- Before: `bench/receipts/relationship-before-2000/`
- After: `bench/receipts/relationship-after-2000/`, `bench/receipts/relationship-after-10000/`

## Headline comparison at 2,000 edges, 100 parents

| Metric | Before | After | Change |
| --- | --- | --- | --- |
| Corpus load (2,000 FK writes) | 148.71 s | 12.24 s | 12.1x |
| FK update p50 | 218.5 ms | 8.8 ms | 24.8x |
| Reverse one-hop (planner range read) p50 | 34.8 ms | 6.4 ms | 5.4x |
| Restrict delete p50 | 64.1 ms | 6.3 ms | 10.1x |
| Child write p50, last window (1,750–2,000 edges) | 113.3 ms | 6.0 ms | 19x |

## At 10,000 edges (five times the corpus)

| Metric | Before (extrapolated linearly) | After | Change |
| --- | --- | --- | --- |
| FK update p50 | ~1,090 ms | 50.4 ms | ~21x |
| One-hop read API p50 | (no such read existed) | 0.24 ms | new |
| Bounded traversal p50 | (no such read existed) | 0.35 ms | new |
| No-op rebuild pass over the corpus | (no such pass existed) | 0.53 s | new |

The before numbers grow linearly with the tenant's total constraint records
because every planner range read and every commit revalidation decoded the
entire constraints keyspace. After the change, reads resolve through the
endpoint's bucket (degree-bounded) and commits revalidate against exact
records, so the decode term is gone from every row above.

## New surfaces

- One-hop forward and reverse reads through `DocumentStore` as
  `IRelationshipReader` (`ListRelatedAsync`).
- Bounded multi-hop traversal (`TraverseRelationshipsAsync`) with fail-closed
  depth, result, byte, work, and deadline limits mirroring the public
  contract ceilings.
- `RebuildConstraintRecordsAsync`, the post-restore catch-up pass that
  re-derives edge and unique-owner records from restored documents; the
  no-op time above is the full-corpus maintenance cost.

## Remaining ceiling

Write latency still grows slowly with corpus size because every serializable
transaction eagerly copies the engine's state dictionaries in
`BeginSnapshotAsync`. At 10,000 edges that copy is most of the remaining
50 ms write p50. It predates this change, bounds every snapshot-taking path
equally, and is marked at the source with the upgrade path
(copy-on-write snapshot state fenced by a state version).
