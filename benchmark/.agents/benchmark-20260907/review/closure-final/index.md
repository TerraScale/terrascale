# Astra correction packet: storage closure and owned identity batches

Batch: `benchmark-20260907-closure-final`
Source authority: `/tmp/terrabase-qual-20260907.7lF9` only. Copied inputs live
under `source/`; no live repository files were used to construct this packet.

The storage trait closure is complete in the copied source: `StoragePort`
declares advance, register, and retire methods; SQLite implements all three,
while its test helper directly calls the renamed
`advance_consumer_frontier_inner`. SQL `MAX(seq, excluded.seq)` preserves
monotonic frontiers. The shard split route lookup uses `self.engine`, matching
the `ScopedShardEngine` receiver and preserving source generation ordering.

The delegated identity fixer changes borrowed `tenants.chunks(32)` into owned
`Vec<Vec<String>>` batches before `buffer_unordered(8)`. This removes the
higher-ranked borrowed slice from the async future across the exchange await;
the five second timeout, permit bound, and all-batch error collection remain.
The exact fixer delta is [delegated_identity.patch](diffs/delegated_identity.patch).

## Immutable source identities

| Copied input | SHA-256 |
| --- | --- |
| `source/storage-mod.rs` | `c80a9361a00e552255353a7631026b968436259fa07422b1f8e0a81e20e89f2c` |
| `source/storage-sqlite-mod.rs` | `bd609e5ef402c3bc00266776d35a0f5a2424d93d20817239e0e9015b9f6f88fa` |
| `source/engine-shard-split.rs` | `03ce44d6e92c8d16025f1b35a3967ea81c555964d485c8dd9c297bac2c4a98c3` |
| `source/server-delegated-identity.rs` | `3891b609ca68d92b7dbc5b1b26f5eef44d339aa1de40adbcd3b1e0e9bd77065f` |
| `source/storage-sqlite-tests.rs` | `3a3d421b4ec7fe407474609a7e931857dfc07294dc70a33cb369c563bb9e641e` |

Before/after fixer hashes are preserved in `diffs/before.sha256` and
`diffs/after.sha256`; the after identity is the copied delegated source above.

## Callers and tests

Production frontier calls are in the copied engine lifecycle path, with
retention bounded by the minimum consumer frontier. Frontier tests in the
copied SQLite test source cover registration, monotonic advancement, and
pruning bounds (`1173-1210`, `1548-1584`). The delegated identity production
caller is the copied server source around `473-496`, and its owned batches feed
the bounded concurrent exchange.

## Verification

`diffs/server-build.log` is the exact fixer build log. It records warnings but
does not contain a terminal success marker or exit status, so compile evidence
is pending/unknown and must not be treated as green. Benchmark acceptance is
also pending; prior release results remain stale diagnostic evidence. Astra may
review the immutable source packet now, with final acceptance limited until the
build owner records a successful command against these hashes.
