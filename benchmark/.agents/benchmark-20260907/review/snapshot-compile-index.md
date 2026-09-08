# Astra supplement: qualification snapshot compile correction

Batch: `benchmark-20260907-snapshot-compile-correction`
Immutable build snapshot: `/tmp/terrabase-qual-20260907.7lF9` (copied into
`snapshot-source/` in this packet before review)
Snapshot source hashes: SQLite `bd609e5ef402c3bc00266776d35a0f5a2424d93d20817239e0e9015b9f6f88fa`,
shard split `03ce44d6e92c8d16025f1b35a3967ea81c555964d485c8dd9c297bac2c4a98c3`,
storage trait `0bacf7878b6a71054f983a82ca2483d0cb120413214c7004502d38c225f50e29`.

## Delta

The exact minimal caller correction is [snapshot-compile-correction.diff](snapshot-compile-correction.diff): within `ScopedShardEngine`, the route lookup must use its owned `self.engine` field. The live worktree currently contains this corrected call and hashes are recorded in [SHA256SUMS-snapshot](SHA256SUMS-snapshot).

The copied SQLite snapshot exposes the test helper as a direct call to
`advance_consumer_frontier_inner`; it does not delegate through the trait.
The production `StoragePort` implementation provides
`advance_consumer_frontier`,
`register_consumer_frontier`, and `retire_consumer_frontier` through the
trait-owned contract. The test-only inherent helper calls
`advance_consumer_frontier_inner` keeps the frontier monotonic with SQL
`MAX(seq, excluded.seq)`. No SQLite
source delta exists in this snapshot correction packet; any prior “inner” rename
is therefore already represented by the snapshot source identity.

## Callers and behavioral contract

`src/engine/shard_split.rs:151-154` obtains the source route generation before
installing the target catalog, and `self.engine` is required because the method
receiver is `ScopedShardEngine`. `src/engine/mod.rs:2762-2786` calls the storage
frontier trait methods during engine admission/lifecycle. Retention uses the
minimum registered consumer frontier, so lower updates must never move a
frontier backwards. Tests at `src/storage/sqlite/tests.rs:1173-1210` and
`1548-1584` exercise registration, monotonic advancement, and pruning bounds.

## Verification state

The snapshot is immutable for this packet. The live source hash was read twice
with the same result after the concurrent worker settled. Build owner evidence
is still pending for this exact cohort; earlier release benchmark artifacts are
stale diagnostic evidence and do not qualify performance results. This packet
does not assert correctness or benchmark success.

## Reading order

1. [snapshot-compile-correction.diff](snapshot-compile-correction.diff)
2. `src/engine/shard_split.rs:145-158`
3. `src/storage/mod.rs:1435-1470` and `src/storage/sqlite/mod.rs:1818-1830, 2115-2165`
4. `src/storage/sqlite/tests.rs:1173-1210, 1548-1584`
