# Astra supplement: production futures dependency classification

Batch: `benchmark-20260907-manifest-futures-normal`
Source commit: `ad62f9d36b81c4efee7e1637e57954d7023f51ee`
Current reviewed revision: `4a2c36aabdc5cbd663d20f4dbe8d5cedb1a48fc7`
Prior chain: [harness correction](correction-index.md)

## Scope

The exact committed manifest change is [manifest-futures-normal.diff](manifest-futures-normal.diff).
It adds `object_store`, `tokio`, and `futures-util` to the root package's normal
dependencies because production code uses them, and adds the `rustix/fs`
feature required by filesystem code. This is a committed source change rather
than a current dirty overlay; the current worktree has no Cargo manifest diff.

## Real production callers

* `src/backup/object_store.rs:13` imports `futures_util::StreamExt` for the
  production object store stream path.
* `crates/terrabase-server/src/delegated_identity.rs:475-476` uses
  `futures_util::stream::iter` and `StreamExt` to process authorization lease
  batches in production identity control work.
* `crates/terrabase-server/src/native_backup_runtime.rs:476` exposes a
  production `futures_util::stream::BoxStream` from the native backup store.
* `crates/terrabase-server/src/production_backup.rs:8-10` constructs the
  native backup runtime caller.

The workspace lockfile is current at SHA-256
`3bd5352f35cd859ae3611ddaf3a4917f2fa2f4821ff57e3123f2535ed70b6077`; root
manifest SHA-256 is
`d2e45b7853d86a3276ba2150569a9f588763410aae5ea5b9ff19f94428d0caf1`.
Relevant caller hashes are recorded in [SHA256SUMS-manifest](SHA256SUMS-manifest).

## Verification state

This supplement does not claim a fresh build or benchmark result. The prior
release benchmark evidence remains diagnostic/stale as documented in the
harness packets. The build owner must record a fresh check against the current
revision before treating this manifest correction as verified.

## Reading order

1. [manifest-futures-normal.diff](manifest-futures-normal.diff)
2. `Cargo.toml` `[dependencies]`
3. The three production callers above and `production_backup.rs`
4. [correction-index.md](correction-index.md)
