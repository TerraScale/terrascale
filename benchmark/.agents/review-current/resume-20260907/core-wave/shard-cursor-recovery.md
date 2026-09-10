# Snapshot-to-journal restart correction

The supervisor now persists the actual original-publication replay cursor (zero) when final-state snapshot copying completes. Previously fresh catchup started at zero but restart began at the persisted snapshot boundary, skipping original history and causal tombstones. Removed the unused source_boundary helper.

The actual Engine close/reopen regression also exposed a separate production restart blocker: ownership validation rejected the engine-created parent/shards directory. Ownership validation now permits only that reserved non-symlink directory when the parent owner and SQLite file already exist. Each selected child remains independently validated at Engine open; interrupted child staging does not prevent parent service. Unowned adoption, a file named shards, and a symlink named shards remain rejected.

## Verification

Exclusive rust_checks worker reported passing:

- engine-final2-supervisor_restart_after_snapshot_replays_original_history_from_zero.log: 1 passed. Encrypted source put/delete history yields an empty snapshot, all Engine handles close, source reopens, and target retains the exact original put and causal tombstone publications.
- engine-final2-shard_staging_does_not_prevent_parent_reopen_or_allow_foreign_adoption.log: 1 passed. Incomplete child staging permits parent reopen while foreign adoption/file/symlink cases fail.

Logs and before/after source maps are under /tmp/terrabase-rust-checks-20260907/. The broader supervisor_ rerun is owned by rust_checks. No Cargo was run by the implementation worker.

Current source hashes:

| File | SHA-256 |
| --- | --- |
| src/engine/shard_supervisor.rs | cf08787344821ed5401c30514f3f5212cc7e8e7de8d289640c275901cc7c10eb |
| src/engine/shard_split_tests.rs | 8a2b8eab4265f24e07215d8459b724cb95a8dca06a454f9150b52053273e5a3b |
| src/storage/ownership.rs | 591f6ff3237687c19046e7398c00f9d852c8616856db8b40e8f467e51f2ae3ed |

The shared shard test file includes serving-lane changes beyond the new cursor regression. Prior accepted cohort evidence remains unchanged in shard-correction.md. The next full-copy/stable-revision source brief is shard-copy-proposal.md; it explicitly requires retained-baseline support under normal pruning, provenance and original request outcomes, real target RF2 admission, and actual routing before automatic activation.
