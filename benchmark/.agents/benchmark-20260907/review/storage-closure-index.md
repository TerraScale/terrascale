# Astra packet: storage frontier closure and delegated identity batch

Batch: `benchmark-20260907-storage-closure`
Reviewed source: HEAD `2b445f71` plus the copied qualification sources under
`snapshot-source/`. The copied files are the review input; live worktree edits
are excluded from identity claims.

## Covered changes

The storage closure adds `advance_consumer_frontier`, registration, and retire
methods to `StoragePort`, moves SQLite's implementation behind that trait, and
keeps the test helper calling the renamed `advance_consumer_frontier_inner`
directly. The SQL update uses `MAX(seq, excluded.seq)`, preserving monotonic
frontiers. `src/engine/mod.rs:2762-2786` is the production caller for admission
and lifecycle updates; retention uses the minimum consumer frontier.

The shard split caller at `src/engine/shard_split.rs:151-158` uses
`self.engine.load_shard_router`, required by the `ScopedShardEngine` receiver,
before deriving the target generation. The exact earlier caller correction is
linked in [snapshot-compile-correction.diff](snapshot-compile-correction.diff).

The delegated identity refresh at
`crates/terrabase-server/src/delegated_identity.rs:473-493` processes tenants
in chunks of 32 with bounded `buffer_unordered(8)`, a permit per batch, and a
five second exchange timeout. It collects every result and returns an error if
any batch fails, preserving fail closed authorization refresh behavior.

## Copied source identities

| Source | SHA-256 |
| --- | --- |
| `snapshot-source/storage-mod.rs` | `0bacf7878b6a71054f983a82ca2483d0cb120413214c7004502d38c225f50e29` |
| `snapshot-source/storage-sqlite-mod.rs` | `bd609e5ef402c3bc00266776d35a0f5a2424d93d20817239e0e9015b9f6f88fa` |
| `snapshot-source/engine-shard-split.rs` | `03ce44d6e92c8d16025f1b35a3967ea81c555964d485c8dd9c297bac2c4a98c3` |
| `snapshot-source/server-delegated-identity.rs` | `0f8761e7ec90650d987c10538d9b243c724452ffbcfac5c23130762d845e888e` |
| `snapshot-source/storage-sqlite-tests.rs` | `3a3d421b4ec7fe407474609a7e931857dfc07294dc70a33cb369c563bb9e641e` |

## Tests and limits

Relevant frontier tests are `src/storage/sqlite/tests.rs:1173-1210` and
`1548-1584`, covering registration, monotonic advancement, and pruning bounds.
Delegated identity coverage is the focused server `delegated_identity` suite;
fresh build output for this exact copied cohort is pending from the build owner.
Earlier release benchmark output is stale diagnostic evidence and does not
qualify benchmark acceptance. This packet makes no correctness approval.

## Reading order

1. Copied `snapshot-source/storage-mod.rs` trait contract
2. Copied `snapshot-source/storage-sqlite-mod.rs` implementation/helper
3. Copied `snapshot-source/storage-sqlite-tests.rs` frontier tests
4. Copied `snapshot-source/server-delegated-identity.rs` batch refresh
5. Copied `snapshot-source/engine-shard-split.rs` caller and prior diff
